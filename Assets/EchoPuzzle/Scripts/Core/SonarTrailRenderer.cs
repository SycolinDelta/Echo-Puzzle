using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace EchoPuzzle
{
    /// <summary>
    /// 一条声波射线（曳光弹）。
    /// 位置/方向/长度由 SonarSystem 在反射那一刻算好，登记一次就一直留在表里，
    /// 直到整条彻底暗掉才被淘汰 —— 这就是"光带持续存在并慢慢淡出"的实现方式。
    /// </summary>
    public struct SonarTrail
    {
        public Vector3 origin;      // 该段的起点（世界坐标）
        public Vector3 dir;         // 单位方向
        public float   length;      // 该段长度（米）
        public float   startTime;   // 波前离开**声源**的时刻
        public float   spawnTime;   // **这一小段**诞生的时刻（每反射一次就更新一次）
        public float   speed;       // 声速
        public float   travelBefore;// 波前到达 origin 时已经走过的路程（用来算 origin 处的年龄）
        public float   initAlpha;   // 初始不透明度（已含反射衰减）
        public float   spacing;     // 采样间距
        public float   lifetime;    // 本段波前存在时长（秒）：到点后停止推进，形状钉住
        public float   tailLinger;  // 波前停止后，尾巴还能继续残留多久（秒）
        public bool    holdAlpha;   // true = **波前处**维持不透明度（去掉"冻住后整体变暗"）；扫过的轨迹照常渐隐
        public float   width;       // 本段光带宽度（米）。<= 0 = 沿用渲染器的全局宽度
        public Vector3 source;      // 这一次发声的**声源位置**（怪物用来判断"去哪个产生点"）
        public bool    isSneak;     // true = 潜行产生的，怪物不会捕捉
        public bool    ignoreMonster; // true = 环境 / 机关声波，怪物不会捕捉（但照常显示）
        public bool    isKillSonar;   // true = 怪物被刺杀时发出的声波
        public Color   color;
    }

    /// <summary>
    /// 光带的"落点提示缓存"。
    ///
    /// <see cref="ZoneSystem.Find"/> 会遍历所有特殊空间，而它原本是在
    /// 铺几何的**最内层采样循环**里被调用的 —— 每条光带的每个采样步都要全表扫一遍。
    /// 光带长度 10m、采样间距 0.4m 时每条就要查 25 次以上，光带上千条就是几万次
    /// 属性访问 + Bounds 构造。（Profiler 实测：SonarTrailRenderer.LateUpdate 占了 85% 帧时间。）
    ///
    /// 做法：一条光带通常整条都落在同一块空间里（或都在外面），
    /// 所以缓存上一次的查询结果，只有"这个点已经不在缓存的空间里了"才重新查。
    /// 一条光带因此从 ~25 次查找降到 2 次（进 + 出）。
    /// </summary>
    internal struct ZoneProbe
    {
        public SpecialZone   zone;
        public TriggerVolume trigger;
        public bool          zoneValid;      // false = 本次还没查过，必须查一次
        public bool          triggerValid;
    }

    /// <summary>
    /// 把所有声波射线铺成连续的光带，一个动态 Mesh、一次 DrawCall。
    ///
    /// 每条射线被切成若干四边形；每个四边形"面向相机 45°"构造 ——
    /// 一条边垂直向上，一条边指向相机。两者都不是相机的右向量，
    /// 所以光带不会随视角变化而忽宽忽窄，远处的声波也不会缩成一颗点。
    ///
    /// 每段长度落在 [尾, 头] 之间：尾巴是"波前扫过最久"的地方，头部是波前当前所在，
    /// 所以整条光带看起来就是一颗发光的弹头拖着一条渐隐的尾巴在往前飞。
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class SonarTrailRenderer : MonoBehaviour
    {
        public const int MaxQuads = 160000;

        [Header("渲染")]
        [Tooltip("声波光带材质。留空则用 Resources/EchoRuntime/EchoSonarTrail。\n" +
                 "**必须来自资产**，否则 shader 不会被打进包，exe 里光带不显示")]
        public Material material;

        [Tooltip("整体发光强度。声波太暗先加大这个")]
        public float glowIntensity = 1.8f;
        [Tooltip("光带宽度（米）")]
        public float trailWidth    = 0.09f;
        [Tooltip("光带实际铺出来的长度（米）")]
        public float maxTrailLength = 34f;
        [Tooltip("采样间距（米）。越小越顺滑，几何量越大")]
        public float trailSpacing   = 0.4f;
        [Tooltip("声波速度的参考值（米/秒），用来把『衰减速度』换算成『尾巴长度』。\n" +
                 "四种声波的 speed 都设成这个值就行。")]
        public float referenceSpeed = 8f;
        [Tooltip("不透明度衰减速度（1/秒），全局统一。越大尾巴越短、消失越快")]
        public float fadeSpeed      = 3f;
        [Tooltip("当前不透明度低于这个值就不画了")]
        public float visibleThreshold = 0.2f;
        [Tooltip("单条射线最长铺多远（米）。这是几何量的硬上限，别调太大")]
        public float maxVisibleLength = 10f;
        [Tooltip("声波最前端额外加一个发光弹头")]
        public bool  drawHead = false;

        [Tooltip("缓存落点查询（性能优化）。关掉就退回原本的每步全表查找。\n" +
                 "用来验证某类异常是否由缓存引起")]
        public bool  useZoneProbeCache = true;

        private Mesh         m_Mesh;
        private Material     m_Mat;
        private bool         m_OwnsMat;   // true = 这份材质是实例，OnDestroy 时要销毁
        private MeshFilter   m_Filter;
        private MeshRenderer m_Renderer;

        private Vector3[] m_Verts;
        private Vector2[] m_Uvs;
        private Color[]   m_Cols;
        private int[]     m_Indices;

        private readonly List<SonarTrail> m_Trails = new List<SonarTrail>(4096);
        private bool m_Ready;

        private void Awake()
        {
            m_Verts   = new Vector3[MaxQuads * 4];
            m_Uvs     = new Vector2[MaxQuads * 4];
            m_Cols    = new Color[MaxQuads * 4];
            m_Indices = new int[MaxQuads * 6];

            for (int q = 0; q < MaxQuads; q++)
            {
                int v = q * 4;
                int t = q * 6;
                m_Indices[t + 0] = v + 0;
                m_Indices[t + 1] = v + 1;
                m_Indices[t + 2] = v + 2;
                m_Indices[t + 3] = v + 0;
                m_Indices[t + 4] = v + 2;
                m_Indices[t + 5] = v + 3;
            }

            m_Mesh = new Mesh { name = "EchoSonarTrails" };
            m_Mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m_Mesh.MarkDynamic();

            m_Filter = GetComponent<MeshFilter>();
            m_Filter.sharedMesh = m_Mesh;

            // ★ 材质必须来自**资产**（Resources 下的 EchoSonarTrail.mat），
            //   不能只靠 Shader.Find 现场建。
            //
            //   原因：工程里如果没有资产引用 EchoPuzzle/SonarTrail，
            //   Unity 打包时会把这个 shader 整体剔除，于是打包后
            //   Shader.Find 返回 null、组件直接 enabled = false ——
            //   光带连网格都不建，表现就是"完全不生成"。
            //   材质资产引用 shader ⇒ shader 进包，编辑器与打包行为才一致。
            // 用**资产**作为模板克隆一份实例。
            //
            // 为什么要克隆：ApplyMaterialProperties 会写 _Intensity，
            // 如果直接改资产本体，编辑器里一跑 Play 就把 .mat 文件改掉了
            // （而且 .mat 会被标记为 dirty）。克隆一份既能带上资产里的参数，
            // 又不污染工程文件 —— 同时资产引用保证了 shader 进包。
            Material src = material != null ? material : EchoRuntimeMaterials.SonarTrail;

            if (src == null)
            {
                Debug.LogError("[EchoPuzzle] 拿不到声波光带材质（Resources/EchoRuntime/EchoSonarTrail）。\n" +
                               "请执行菜单：EchoPuzzle → 生成运行时材质资产");
                enabled = false;
                return;
            }

            m_Mat = new Material(src) { name = "EchoSonarTrailMat", hideFlags = HideFlags.DontSave };
            m_OwnsMat = true;

            ApplyMaterialProperties();

            m_Renderer = GetComponent<MeshRenderer>();
            m_Renderer.sharedMaterial             = m_Mat;
            m_Renderer.shadowCastingMode          = UnityEngine.Rendering.ShadowCastingMode.Off;
            m_Renderer.receiveShadows             = false;
            m_Renderer.lightProbeUsage            = UnityEngine.Rendering.LightProbeUsage.Off;
            m_Renderer.reflectionProbeUsage       = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            m_Renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;

            // 这个 Mesh 每帧整体重建。关键：必须给出确定的包围盒，否则 Unity 会拿
            // 上一帧的旧包围盒做视锥剔除 —— 表现就是"某个视角看得到声波，
            // 一转动相机全部同时消失，转回来又同时出现"。
            // 直接用 localBounds 覆盖，最省事也最稳（渲染器就在世界原点）。
            m_Renderer.localBounds = new Bounds(Vector3.zero, Vector3.one * 600f);
            m_Renderer.allowOcclusionWhenDynamic = false;

            m_Ready = true;
        }

        private void OnValidate() => ApplyMaterialProperties();

        private void ApplyMaterialProperties()
        {
            if (m_Mat == null) return;
            m_Mat.SetFloat("_Intensity", glowIntensity);
        }

        private void OnDestroy()
        {
            // 只销毁自己克隆出来的实例；Resources 里的材质资产本体不能碰
            if (m_OwnsMat && m_Mat != null) Destroy(m_Mat);
            if (m_Mesh != null) Destroy(m_Mesh);
        }

        /// <summary>
        /// 登记一条声波射线（反射那一刻调用一次）。
        /// 登记后就一直留在表里，由 LateUpdate 按寿命淘汰 —— **不要**每帧清空。
        /// </summary>
        /// <param name="lifetime">
        /// 本段的波前存在时长（秒），从 <paramref name="spawnTime"/> 起算。到点后停止推进。
        /// 每反射一次这个值会被 SonarSystem 扣掉一截（bounceLifeLoss），
        /// 所以越绕越远的声波能推进的时间越短。
        /// </param>
        /// <param name="spawnTime">
        /// 本段诞生的时刻，**不是**声源发声的时刻。
        /// 每次反射都会重新赋值，波前的推进从这里起算。
        /// </param>
        /// <param name="tailLinger">
        /// 波前停止之后，尾巴还能继续残留多久（秒）。
        /// 这段时间里整条光带会在原地继续变暗，直到彻底看不见才被淘汰。
        /// 填 &lt;= 0 表示波前一到就立刻一起消失。
        /// </param>
        /// <param name="holdAlpha">
        /// true = **波前处**维持初始不透明度：去掉"波前冻住之后整条一起变暗"这一层衰减，
        /// 但保留"被波前扫过之后沿光带渐隐"—— 所以刚生成的光带不透明度不变，
        /// 生成后的轨迹仍会渐渐变淡消失。用来做"常亮波前"的特殊声波。
        /// </param>
        /// <param name="width">
        /// 本段光带的宽度（米）。填 &lt;= 0 表示沿用渲染器上的全局
        /// <see cref="trailWidth"/>。
        /// </param>
        public void AddTrail(Vector3 origin, Vector3 dir, float length,
                             float startTime, float speed, float travelBefore,
                             float initAlpha, float spacing, float spawnTime,
                             float lifetime, float tailLinger, bool holdAlpha,
                             float width, Vector3 source, bool isSneak, Color color,
                             bool ignoreMonster = false, bool isKillSonar = false)
        {
            if (!m_Ready || length <= 0.0005f) return;

            m_Trails.Add(new SonarTrail
            {
                origin        = origin,
                dir           = dir,
                length        = length,
                startTime     = startTime,
                spawnTime     = spawnTime,
                speed         = Mathf.Max(0.01f, speed),
                travelBefore  = Mathf.Max(0f, travelBefore),
                initAlpha     = initAlpha,
                spacing       = spacing > 0.01f ? spacing : trailSpacing,
                lifetime      = lifetime,
                tailLinger    = tailLinger,
                holdAlpha     = holdAlpha,
                width         = width,
                source        = source,
                isSneak       = isSneak,
                ignoreMonster = ignoreMonster,
                isKillSonar   = isKillSonar,
                color         = color,
            });
        }

        // ---------------------------------------------------------------- 击杀声波查询
        //
        // 击杀声波 = 怪物被匕首刺杀时在那个位置炸开的声波（isKillSonar = true）。
        // 用途：
        //   1) 其它怪物捕捉到它 -> 进入受惊状态
        //   2) 只要场上还有它，怪物就不会因"匕首收回"而受惊

        /// <summary>
        /// 场上是否存在**还在显示**的击杀声波光带。
        /// </summary>
        public bool AnyKillSonar()
        {
            float now = Time.timeSinceLevelLoad;

            for (int i = 0; i < m_Trails.Count; i++)
            {
                SonarTrail tr = m_Trails[i];
                if (!tr.isKillSonar) continue;

                // 已经暗到看不见就当作不存在
                float age = now - tr.spawnTime;
                float bodyAlpha = tr.initAlpha * Mathf.Exp(-age * fadeSpeed);
                if (bodyAlpha > visibleThreshold) return true;
            }

            return false;
        }

        /// <summary>
        /// 找一条**已经可见**的击杀声波，返回它的声源位置与产生时刻。
        /// 多条时取最新的。给怪物用来"被同类被杀的声音吓到"。
        /// </summary>
        /// <param name="senseCenter">怪物的感知圆柱中心。</param>
        /// <param name="senseRadius">感知圆柱的水平半径。</param>
        /// <param name="senseHeight">感知圆柱的竖直总高。</param>
        /// <param name="skipTrailIndex">可选：忽略下标为它的那条光带（本怪物自己的）。</param>
        public bool TryCaptureKillSonar(out Vector3 source, out float birthTime,
                                        Vector3 senseCenter, float senseRadius, float senseHeight)
        {
            source    = Vector3.zero;
            birthTime = float.NegativeInfinity;

            float now = Time.timeSinceLevelLoad;
            bool  found = false;

            float r2  = senseRadius * senseRadius;
            float cyLo = senseCenter.y - senseHeight * 0.5f;
            float cyHi = senseCenter.y + senseHeight * 0.5f;

            for (int i = 0; i < m_Trails.Count; i++)
            {
                SonarTrail tr = m_Trails[i];
                if (!tr.isKillSonar) continue;

                // 必须已经"画出来了"（波前推进出了可见长度）
                float age       = now - tr.spawnTime;
                float maxTravel = tr.lifetime > 0f ? tr.lifetime * tr.speed : float.MaxValue;
                float travelled = age * tr.speed;
                float frontAbs  = Mathf.Min(travelled, maxTravel);
                float segEnd    = Mathf.Min(tr.travelBefore + tr.length, maxTravel);
                float visibleLen = Mathf.Min(frontAbs, segEnd) - tr.travelBefore;

                if (visibleLen <= 1e-4f) continue;

                // 已经暗到看不见也跳过
                float bodyAlpha = tr.initAlpha * Mathf.Exp(-age * fadeSpeed);
                if (bodyAlpha <= visibleThreshold) continue;

                // ★ **必须等可见的那一段真正进入感知范围**才捕捉。
                //   和 TryCaptureNearest 用完全相同的"线段 vs 圆柱"判定：
                //   只看已经铺出来的部分（visibleLen），不是整条。
                Vector3 seg = tr.dir * visibleLen;

                float y0 = tr.origin.y;
                float y1 = tr.origin.y + seg.y;
                if (y0 > y1) { float tmp = y0; y0 = y1; y1 = tmp; }
                if (y1 < cyLo || y0 > cyHi) continue;

                float tLo, tHi;
                if (y1 > y0)
                {
                    tLo = Mathf.Clamp01((cyLo - y0) / (y1 - y0));
                    tHi = Mathf.Clamp01((cyHi - y0) / (y1 - y0));
                }
                else
                {
                    tLo = 0f;
                    tHi = 1f;
                }

                Vector2 o = new Vector2(tr.origin.x, tr.origin.z);
                Vector2 s = new Vector2(seg.x, seg.z);
                Vector2 c = new Vector2(senseCenter.x, senseCenter.z);

                float sLen2 = s.sqrMagnitude;
                float t;
                if (sLen2 <= 1e-10f)
                {
                    t = tLo;
                }
                else
                {
                    t = Mathf.Clamp(Vector2.Dot(c - o, s) / sLen2, tLo, tHi);
                }

                Vector2 closest2 = o + s * t;
                if ((closest2 - c).sqrMagnitude > r2) continue;   // 还没进入感知范围

                if (tr.startTime <= birthTime) continue;

                birthTime = tr.startTime;
                source    = tr.source;
                found     = true;
            }

            return found;
        }

        /// <summary>
        /// 收集所有**波前**落在以 center 为心、半径 radiusXZ 的圆内的光带，
        /// 把波前在 xz 平面上的位置追加到 outPoints。
        ///
        /// 给"变色球红线"用：只有波前经过的地方才该留下痕迹，
        /// 已经扫过去的轨迹不算。
        ///
        /// ★ 只看 xz，完全忽略 y。
        /// ★ 只算**还看得见**的波前（alpha >= visibleThreshold）。
        /// </summary>
        public void CollectWavefrontsXZ(Vector3 center, float radiusXZ,
                                        List<Vector2> outPoints,
                                        bool sneakOnly = true)
        {
            CollectWavefrontsXZ(center, radiusXZ, outPoints, sneakOnly, false);
        }

        /// <summary>
        /// 同上，但可以选择判定区域是**圆**还是**方块**。
        ///
        /// ★ useBox 只影响"波前落没落在区域内"这一判断：
        ///   触发区是方形空间，所以用方块（|dx| 和 |dz| 分别比较）；
        ///   怪物的变色球是球形，用圆。
        /// </summary>
        public void CollectWavefrontsXZ(Vector3 center, float radiusXZ,
                                        List<Vector2> outPoints,
                                        bool sneakOnly,
                                        bool useBox)
        {
            if (outPoints == null) return;

            float r  = Mathf.Max(0.01f, radiusXZ);
            float r2 = r * r;

            Vector2 c = new Vector2(center.x, center.z);

            float now = Time.timeSinceLevelLoad;
            float fadeSpd = Mathf.Max(0.01f, fadeSpeed);

            for (int i = 0; i < m_Trails.Count; i++)
            {
                SonarTrail tr = m_Trails[i];

                // ★★ sneakOnly = true 时，只认**潜行声波** ★★
                //
                //   isSneak = true 表示这条光带是玩家潜行时踩出来的
                //   （PlayerController 里 sneakSilent 为 true 的那些）。
                //
                //   谁用哪种模式：
                //     · 怪物的变色球  -> sneakOnly = true （只有潜行才有红线）
                //     · 触发区        -> sneakOnly = false（任何声波都有竖线）
                if (sneakOnly && !tr.isSneak) continue;
                if (tr.initAlpha <= 0f) continue;

                float sndSpd    = Mathf.Max(0.01f, tr.speed);
                float maxTravel = tr.lifetime > 0f ? tr.lifetime * tr.speed : float.MaxValue;

                // 这一段的有效长度
                float segmentEnd = Mathf.Min(tr.travelBefore + tr.length, maxTravel);
                if (segmentEnd <= tr.travelBefore) continue;

                // ---- 波前在**这一段**里的位置 ----
                float frontAbs = Mathf.Min((now - tr.spawnTime) * sndSpd, maxTravel);

                if (frontAbs <= tr.travelBefore) continue;   // 还没走到这一段
                if (frontAbs >  segmentEnd)      continue;   // 已经越过这一段

                // ---- 波前处的 alpha（和渲染同一套公式）----
                float headAge    = Mathf.Max(0f, (now - tr.spawnTime) - frontAbs / sndSpd);
                float headFrozen = Mathf.Max(0f, (now - tr.spawnTime) - maxTravel / sndSpd);

                float headAlpha = tr.holdAlpha
                    ? tr.initAlpha * Mathf.Exp(-headAge * fadeSpd) * LingerFade01(headFrozen, tr.tailLinger)
                    : tr.initAlpha * Mathf.Exp(-headAge * fadeSpd) * Mathf.Exp(-headFrozen * fadeSpd);

                if (headAlpha < Mathf.Max(0f, visibleThreshold)) continue;

                // ---- 波前的世界坐标（xz）----
                float local = frontAbs - tr.travelBefore;
                Vector2 p   = new Vector2(tr.origin.x, tr.origin.z)
                            + new Vector2(tr.dir.x, tr.dir.z) * local;

                // ★ 区域判定：方块 or 圆
                Vector2 d = p - c;

                bool inside = useBox
                    ? (Mathf.Abs(d.x) <= r && Mathf.Abs(d.y) <= r)
                    : (d.sqrMagnitude <= r2);

                if (!inside) continue;

                outPoints.Add(p);
            }
        }

        /// <summary>
        /// 收集所有**穿过以 center 为心、半径 radiusXZ 的圆**的光带，
        /// 以 **xz 平面上的线段**（世界坐标）形式追加到 outSegments。
        ///
        /// 给"变色球"用：每条返回的线段代表"这一列被这条光带照亮了"——
        ///   球面上与这条线段同 (x, z) 的所有点都会发红。
        ///
        /// ★ 只看 xz，**完全忽略 y**（地牢模式的光带贴地走，和球心高度差很多）。
        /// ★ 只收集**已经铺出来的、还在显示**的那部分光带。
        /// ★ 线段会按圆的包围盒裁一下，避免整条很长的时候逐顶点判断太浪费。
        /// </summary>
        /// <param name="center">球心（世界坐标）。</param>
        /// <param name="radiusXZ">球的半径（xz 平面）。</param>
        /// <param name="outSegments">输出：世界坐标 xz 平面上的线段。</param>
        /// <param name="pad">向外扩张多少米（做柔和边缘用）。</param>
        public void CollectTrailSegmentsXZ(Vector3 center, float radiusXZ,
                                           List<Vector2> outSegments,
                                           float pad = 0f)
        {
            if (outSegments == null) return;

            float r  = Mathf.Max(0.01f, radiusXZ) + Mathf.Max(0f, pad);
            float r2 = r * r;

            Vector2 c = new Vector2(center.x, center.z);

            float now = Time.timeSinceLevelLoad;

            for (int i = 0; i < m_Trails.Count; i++)
            {
                SonarTrail tr = m_Trails[i];

                if (tr.isSneak || tr.ignoreMonster) continue;
                if (tr.initAlpha <= 0f) continue;

                // ---- 这一段**真正可见**的区间 ----
                //
                // ★ 必须和 LateUpdate 里画光带的算法完全一致，
                //   否则会出现"屏幕上光带已经不见了，变色球上还留着竖线"。
                //
                //   关键：可见的不是整段 [travelBefore, travelBefore+length]，
                //   而是波前后面 fadeDistance 那一截尾巴：
                //     [max(travelBefore, frontAbs - fadeDistance), frontAbs]
                float age       = now - tr.spawnTime;
                float maxTravel = tr.lifetime > 0f ? tr.lifetime * tr.speed : float.MaxValue;
                float travelled = age * tr.speed;
                float frontAbs  = Mathf.Min(travelled, maxTravel);
                float segmentEnd = Mathf.Min(tr.travelBefore + tr.length, maxTravel);

                if (segmentEnd <= tr.travelBefore) continue;   // 这一段无效
                if (frontAbs <= tr.travelBefore) continue;     // 波前还没走到这一段

                // ★★ 只让**还看得见**的光带产生红线 ★★
                //
                //   在**可见区间后端**（也就是最亮的那一处，靠近波前）算一下
                //   这条光带此刻的不透明度；低于阈值就直接跳过。
                //
                //   阈值用 visibleThreshold（默认 0.2）—— 和渲染那边
                //   "暗到看不见就不画"用的是同一个数，
                //   所以"屏幕上没有光带了"和"变色球上没有红线了"会同步。
                {
                    float fadeSpd = Mathf.Max(0.01f, fadeSpeed);
                    float sndSpd  = Mathf.Max(0.01f, tr.speed);

                    // 取"最亮的那一端"（靠近波前）来代表整段
                    float headAbs = Mathf.Min(frontAbs, segmentEnd);

                    // ★ 和渲染（BuildTrail）完全同一套公式：
                    //     headAge    = (now - spawnTime) - 已走路程 / 声速
                    //     headFrozen = (now - spawnTime) - 最大里程 / 声速
                    float headAge    = Mathf.Max(0f, (now - tr.spawnTime) - headAbs / sndSpd);
                    float headFrozen = Mathf.Max(0f, (now - tr.spawnTime) - maxTravel / sndSpd);

                    float headAlpha = tr.holdAlpha
                        ? tr.initAlpha * Mathf.Exp(-headAge * fadeSpd) * LingerFade01(headFrozen, tr.tailLinger)
                        : tr.initAlpha * Mathf.Exp(-headAge * fadeSpd) * Mathf.Exp(-headFrozen * fadeSpd);

                    // ★ 只有 alpha >= visibleThreshold（默认 0.2）才产生红线
                    if (headAlpha < Mathf.Max(0f, visibleThreshold)) continue;
                }

                // 尾巴长度（和 LateUpdate 里的 fadeDistance 同一公式）
                float fadeDist = Mathf.Log(1f / Mathf.Max(Mathf.Max(0f, visibleThreshold), 1e-4f))
                               / Mathf.Max(0.01f, fadeSpeed)
                               * Mathf.Max(0.01f, referenceSpeed);
                fadeDist = Mathf.Min(fadeDist, Mathf.Max(1f, maxVisibleLength));

                // 波前已经远远超过这一段末尾 -> 这段早就暗透了，不算
                if (frontAbs > segmentEnd + fadeDist) continue;

                float tailAbs = Mathf.Max(tr.travelBefore, frontAbs - fadeDist);

                float visFrom = Mathf.Max(tailAbs, tr.travelBefore);
                float visTo   = Mathf.Min(frontAbs, segmentEnd);

                float visibleLen = visTo - visFrom;
                if (visibleLen <= 1e-3f) continue;

                // ---- xz 平面上的可见线段 ----
                //   tr.origin 是 travelBefore 处的位置，
                //   往前推 (visFrom - travelBefore) 才是可见起点
                float startOffset = Mathf.Max(0f, visFrom - tr.travelBefore);

                Vector2 d = new Vector2(tr.dir.x, tr.dir.z);
                Vector2 a = new Vector2(tr.origin.x, tr.origin.z) + d * startOffset;
                Vector2 b = a + d * visibleLen;

                // ---- 圆的包围盒预筛 ----
                float minX = Mathf.Min(a.x, b.x);
                float maxX = Mathf.Max(a.x, b.x);
                float minY = Mathf.Min(a.y, b.y);
                float maxY = Mathf.Max(a.y, b.y);

                if (maxX < c.x - r || minX > c.x + r) continue;
                if (maxY < c.y - r || minY > c.y + r) continue;

                // ---- 线段与圆是否相交（粗筛，够用）----
                Vector2 ab = b - a;
                float len2 = ab.sqrMagnitude;

                float t = len2 <= 1e-10f
                        ? 0f
                        : Mathf.Clamp01(Vector2.Dot(c - a, ab) / len2);

                Vector2 closest = a + ab * t;

                if ((closest - c).sqrMagnitude > r2) continue;

                // ---- 裁到圆的范围内（两端各夹一下） ----
                //   粗裁：直接按"线段到圆心距离 <= r"的那一段来算。
                //   这里用简单的参数截取 —— 只要整段在包围盒里就整段收下，
                //   因为逐顶点判定本身会按距离再做一次精确过滤。
                outSegments.Add(a);
                outSegments.Add(b);
            }
        }

        /// <summary>
        /// 找出**感知区域接触到的声波光带**，并返回它的两个标签：
        /// 产生点（声源位置）与产生时间（波前离开声源的时刻）。
        ///
        /// 给怪物用：光带**整条带身**（起点到波前之间已铺出的任意一点）进入感知区域
        /// 就算接触，不要求波前正好在区域里。
        /// 感知区域是**圆柱**：以 center 为轴心、水平半径 radius、
        /// 竖直范围 [center.y - height/2, center.y + height/2]。
        /// 同一帧扫到多条时取**产生时间最晚**的那条 —— 怪物只赶最新的声音。
        /// 潜行产生的光带不算（<see cref="SonarTrail.isSneak"/>）。
        ///
        /// 注意：感知（圆柱）和"光带变色区域"（球，见 PuzzleMonster.SenseContains）
        /// 是两套独立判定，半径相同但形状不同，互不影响。
        /// </summary>
        public bool TryCaptureNearest(Vector3 center, float radius, float height,
                                      out Vector3 source, out float birthTime)
        {
            source    = Vector3.zero;
            birthTime = float.NegativeInfinity;

            float r2  = radius * radius;
            float now = Time.timeSinceLevelLoad;
            bool  found = false;

            // 圆柱的竖直范围（以 center 为中心，总高 height）
            float cyLo = center.y - height * 0.5f;
            float cyHi = center.y + height * 0.5f;

            for (int i = 0; i < m_Trails.Count; i++)
            {
                SonarTrail tr = m_Trails[i];

                if (tr.isSneak || tr.ignoreMonster) continue;   // 潜行 / 环境声波都不暴露



                // ★ 与 LateUpdate 完全一致的"可见末端"计算 —— 这是关键。
                //
                //   渲染里（LateUpdate）算的是：
                //       age       = now - spawnTime
                //       travelled = age * speed
                //       frontAbs  = Min(travelled, maxTravel)
                //       localHead = Min(frontAbs, segmentEnd) - travelBefore
                //
                //   旧代码用的是 segLen = Clamp(front - travelBefore, 0, tr.length)，
                //   其中 front = Min(travelBefore + travelled, maxTravel)。
                //   在 maxTravel 很小时（反射段 lifetime 被扣得很短），
                //   这个式子会立刻把 segLen 顶到 tr.length —— 也就是"整段瞬间可见"，
                //   于是波前还没画到，捕捉就已经成立，怪物提前出发。
                //
                //   现在直接用渲染的同一套公式：可见长度 = Min(frontAbs, segmentEnd) - travelBefore。
                float age       = now - tr.spawnTime;
                float maxTravel = tr.lifetime > 0f ? tr.lifetime * tr.speed : float.MaxValue;
                float travelled = age * tr.speed;
                float frontAbs  = Mathf.Min(travelled, maxTravel);

                // 本段的有效末端（钉住限制）
                float segmentEnd = Mathf.Min(tr.travelBefore + tr.length, maxTravel);

                // 本段可见的局部长度：必须 > 0 才算"画出来了"
                float visibleLen = Mathf.Min(frontAbs, segmentEnd) - tr.travelBefore;
                if (visibleLen <= 1e-4f) continue;        // 还没铺出来


                Vector3 seg = tr.dir * visibleLen;

                // ---- 整条带身 vs 圆柱的"线段-圆柱"判定 ----
                // 线段在 y 轴上的范围若完全够不到圆柱，直接跳过
                float y0 = tr.origin.y;
                float y1 = tr.origin.y + seg.y;
                if (y0 > y1) { float tmp = y0; y0 = y1; y1 = tmp; }
                if (y1 < cyLo || y0 > cyHi) continue;

                // 线段上 y 落在圆柱高度范围内的参数区间 [tLo, tHi]
                float tLo, tHi;
                if (y1 > y0)
                {
                    tLo = Mathf.Clamp01((cyLo - y0) / (y1 - y0));
                    tHi = Mathf.Clamp01((cyHi - y0) / (y1 - y0));
                }
                else
                {
                    tLo = 0f;
                    tHi = 1f;            // 水平段：整段同高，前面已确认落在范围内
                }

                // xz 平面上，该子区间内离圆柱轴线最近的点
                Vector2 o = new Vector2(tr.origin.x, tr.origin.z);
                Vector2 s = new Vector2(seg.x, seg.z);
                Vector2 c = new Vector2(center.x, center.z);

                float   sLen2 = s.sqrMagnitude;
                float   t;
                if (sLen2 <= 1e-10f)
                {
                    t = tLo;             // xz 上是一个点（纯竖直段）
                }
                else
                {
                    t = Mathf.Clamp(Vector2.Dot(c - o, s) / sLen2, tLo, tHi);
                }

                Vector2 closest2 = o + s * t;
                if ((closest2 - c).sqrMagnitude > r2) continue;

                // ★★ 检查"接触点"本身的真实不透明度 ★★
                //
                //   光带淡出之后不会立刻从 m_Trails 移除（holdAlpha 要等
                //   tailLinger 走完），所以列表里有很多**早已看不见**的残留。
                //   以前只看几何位置，于是"范围里明明没光带却开始追"。
                //
                //   这里直接算**线段上离怪物最近的那个点**有多亮 ——
                //   公式与渲染（EmitStep）完全一致，所以只认眼睛看得见的。
                //
                //   阈值 0.2：和渲染的 visibleThreshold 一致 ——
                //   不透明度低于 0.2 时渲染不会画出来，视为看不见。
                float hitAbs    = tr.travelBefore + visibleLen * t;
                float hitAge    = Mathf.Max(0f, (now - tr.spawnTime)
                                            - Mathf.Min(hitAbs, maxTravel) / tr.speed);
                float frozenAll = Mathf.Max(0f, (now - tr.spawnTime) - maxTravel / tr.speed);

                float hitAlpha = tr.holdAlpha
                    ? tr.initAlpha * Mathf.Exp(-hitAge * fadeSpeed)
                                  * LingerFade01(frozenAll, tr.tailLinger)
                    : tr.initAlpha * Mathf.Exp(-hitAge * fadeSpeed)
                                  * Mathf.Exp(-frozenAll * fadeSpeed);

                if (hitAlpha <= 0.2f) continue;    // 太暗看不见 -> 不算捕捉

                // 接触成立：产生时间 = startTime（波前离开声源的时刻）。
                // 多条同时接触时只保留产生时间最晚的那条。
                if (tr.startTime <= birthTime) continue;

                birthTime = tr.startTime;
                source    = tr.source;
                found     = true;
            }

            return found;
        }

        /// <summary>清空所有光带（换关卡 / 重置时用）。平时不要调用。</summary>
        /// <summary>清掉所有光带（重玩时用）。</summary>
        public void ClearTrails() => m_Trails.Clear();

        private void LateUpdate()
        {
            if (!m_Ready) return;

            float now = Time.timeSinceLevelLoad;

            Camera  cam = Camera.main;
            Vector3 camPos, camUp, camFwd;
            if (cam != null)
            {
                camPos = cam.transform.position;
                camUp  = cam.transform.up;
                camFwd = cam.transform.forward;
            }
            else
            {
                camPos = Vector3.zero;
                camUp  = Vector3.up;
                camFwd = Vector3.down;
            }

            float fade      = Mathf.Max(0.01f, fadeSpeed);

            float alphaFloor = Mathf.Max(0f, visibleThreshold);

            // 不透明度掉到 alphaFloor 需要飞多远（米）。这就是"尾巴"的自然长度：
            // 超过这个距离的部分早就看不见了，不用铺几何。
            // 用参考声速换算，这样改动声速时不用重调这个值。
            float fadeDistance = Mathf.Log(1f / Mathf.Max(alphaFloor, 1e-4f))
                               / fade * Mathf.Max(0.01f, referenceSpeed);
            fadeDistance = Mathf.Min(fadeDistance, Mathf.Max(1f, maxVisibleLength));

            int quads = 0;

            for (int i = m_Trails.Count - 1; i >= 0; i--)
            {
                SonarTrail tr = m_Trails[i];

                // ★ 逐条宽度：特殊声波（比如 TriggerVolume 触发的那种）可以自带宽度。
                //   tr.width <= 0 表示沿用渲染器上的全局 trailWidth。
                float trWidth = tr.width > 0f ? tr.width : trailWidth;
                float trHalfWidth = Mathf.Max(0.005f, trWidth) * 0.5f;

                // 本段从"自己诞生"起算的年龄。每次反射 spawnTime 都会被刷新，
                // 所以这里的 lifetime 就是"这一小段还能推进多久"。
                float age = now - tr.spawnTime;

                // 波前允许推进的路程上限。lifetime <= 0 表示不限时。
                float maxTravel = tr.lifetime > 0f
                    ? tr.lifetime * tr.speed
                    : float.MaxValue;

                // 实际能推进到的路程，被"钉住"限制住
                float travelled = age * tr.speed;
                float frontAbs  = Mathf.Min(travelled, maxTravel);

                // 整段的有效末端：不能超过它钉住的时刻波前所在的位置
                float segmentEnd = Mathf.Min(tr.travelBefore + tr.length, maxTravel);

                // 波前压根没推进到这一段 -> 整条丢弃
                if (segmentEnd <= tr.travelBefore)
                {
                    m_Trails[i] = m_Trails[m_Trails.Count - 1];
                    m_Trails.RemoveAt(m_Trails.Count - 1);
                    continue;
                }

                // ── 判定 A：波前已经钉住，而且尾巴的残留时间也过完了 -> 整条消失
                if (tr.lifetime > 0f && age > tr.lifetime)
                {
                    float linger = Mathf.Max(0f, tr.tailLinger);
                    if (linger <= 0f || age > tr.lifetime + linger)
                    {
                        m_Trails[i] = m_Trails[m_Trails.Count - 1];
                        m_Trails.RemoveAt(m_Trails.Count - 1);
                        continue;
                    }
                }

                // ── 淘汰判定
                //
                //   常规光带：靠"尾巴也彻底暗掉"淘汰。
                //   holdAlpha 光带：波前推进期间不衰减，所以"整段暗掉"不会立刻发生；
                //                  它改为等**残留淡出**（在 tailLinger 内降到 0）走完再淘汰，
                //                  这样波前消失后轨迹会钉在原地淡出，而不是一起消失。
                if (tr.holdAlpha)
                {
                    // 波前钉住之后过了多久。
                    // 注意这个变量在 LateUpdate 里没有现成的 ——
                    // BuildTrail 里那个是同名局部变量，作用域不重叠。
                    // maxTravel 为 float.MaxValue（lifetime <= 0）时这里算出 0，即"从未冻住"。
                    float frozenHere = Mathf.Max(0f, (now - tr.spawnTime) - maxTravel / tr.speed);

                    float lingerFade = LingerFade01(frozenHere, tr.tailLinger);

                    // 波前压根没动过（寿命令为 0 或极短）-> 没有轨迹可留，直接淘汰
                    if (frontAbs <= 0f) lingerFade = 0f;

                    if (lingerFade <= 0f)
                    {
                        m_Trails[i] = m_Trails[m_Trails.Count - 1];
                        m_Trails.RemoveAt(m_Trails.Count - 1);
                        continue;
                    }
                }
                else if (frontAbs > segmentEnd + fadeDistance)
                {
                    m_Trails[i] = m_Trails[m_Trails.Count - 1];
                    m_Trails.RemoveAt(m_Trails.Count - 1);
                    continue;
                }

                // 四边形预算用尽，本帧剩下的光带只能跳过
                if (quads >= MaxQuads) continue;

                if (frontAbs <= tr.travelBefore) continue;          // 还没走到这一段

                // 尾巴的绝对路程。
                // holdAlpha 的语义是"波前处不衰减"，而不是"整条都不衰减" ——
                // 所以尾巴长度照常按 fadeDistance 算，扫过的轨迹会正常淡出。
                float tailAbs = Mathf.Max(tr.travelBefore, frontAbs - fadeDistance);

                // 换算成"本段内部的局部距离"
                float localHead = Mathf.Min(frontAbs, segmentEnd) - tr.travelBefore;
                float localTail = tailAbs - tr.travelBefore;

                if (localHead - localTail < 1e-3f) continue;

                // 每条光带用一个**新的**落点缓存（在栈上，不分配）
                ZoneProbe probe = default;

                quads += BuildTrail(tr, localTail, localHead, trHalfWidth, fade,
                                    now, tr.spawnTime, maxTravel, camPos, camUp, camFwd, quads,
                                    ref probe);
            }

            m_Mesh.Clear(false);

            if (quads == 0) return;

            int vcount = quads * 4;

            // 顶点 / UV / 颜色：每帧都在变，必须传
            m_Mesh.SetVertices(m_Verts, 0, vcount);
            m_Mesh.SetUVs(0, m_Uvs, 0, vcount, UnityEngine.Rendering.MeshUpdateFlags.Default);
            m_Mesh.SetColors(m_Cols, 0, vcount);

            // 索引缓冲：每帧重新设置。
            //
            // 试过"只在数量变化时才传"来省带宽，两种写法都出问题
            // （先是不显示，再是间歇性闪烁）—— Clear(false) / SetVertices / SetIndices
            // 之间对 submesh 索引数量的处理有耦合，不值得为这点带宽去赌。
            // 索引数组内容恒定，真正贵的是顶点/UV/颜色那三个上传，省不掉。
            m_Mesh.SetIndices(m_Indices, 0, quads * 6, MeshTopology.Triangles, 0, true);
        }

        /// <summary>
        /// 波前钉住之后的"残留淡出系数"，从 1 线性降到 0。
        ///
        /// 常规光带（holdAlpha = false）不使用它 —— 那条路径靠 exp(-frozen·fade)
        /// 做整体淡出，效果等价。
        ///
        /// holdAlpha 光带必须用这个：因为它的波前要求"不透明度不变"，
        /// 所以不能靠 exp(-frozen·fade) 衰减，否则波前自己就先暗了。
        /// 但也正因为没了那个衰减，光带永远不会自己暗掉 ——
        /// 于是波前一消失整条就跟着没了，**没有"残留在原地淡出"这一步**。
        /// 这个系数就是补回来的那一步：在 tailLinger 秒内把已经画出来的轨迹整体淡到 0。
        /// </summary>
        private static float LingerFade01(float frozen, float tailLinger)
        {
            float linger = Mathf.Max(0.0001f, tailLinger);
            return Mathf.Clamp01(1f - frozen / linger);
        }

        /// <summary>
        /// 把一条射线的 [tail, head]（本段内部的局部距离）铺成连续的四边形。
        /// 返回新增的四边形数。
        /// </summary>
        private int BuildTrail(SonarTrail tr, float tail, float head, float halfWidth,
                               float fade, float now, float spawnTime, float maxTravel,
                               Vector3 camPos, Vector3 camUp, Vector3 camFwd, int quadsSoFar,
                               ref ZoneProbe probe)
        {
            Vector3 origin = tr.origin;
            Vector3 dir    = tr.dir;
            Vector3 flat    = origin + dir * tail;
            Vector3 flatEnd = origin + dir * head;

            Vector3 toCam = camPos - flat;
            if (toCam.sqrMagnitude < 1e-6f) toCam = camUp;
            toCam.Normalize();

            Vector3 up = Vector3.Cross(dir, camFwd);
            if (up.sqrMagnitude < 1e-4f) up = Vector3.Cross(dir, camUp);
            if (up.sqrMagnitude < 1e-4f) up = Vector3.up - dir * Vector3.Dot(Vector3.up, dir);
            if (up.sqrMagnitude < 1e-4f) up = Vector3.right;
            up.Normalize();

            Vector3 side = toCam - dir * Vector3.Dot(toCam, dir);
            if (side.sqrMagnitude < 1e-4f) side = Vector3.Cross(dir, up);
            side.Normalize();

            float spacing = Mathf.Max(0.05f, tr.spacing);
            int steps = Mathf.Clamp(Mathf.CeilToInt((head - tail) / spacing), 1, 400);
            float stepLen = (head - tail) / steps;

            // 特殊空间的查询交给 ZoneSystem（数量很少，线性遍历就够）
            float goalHalfWidth = 0f;

            int made = 0;

            // 上一步是否在终点空间内。用来在这一步跨越边界时插入一次切分。
            bool prevInside = false;

            for (int s = 0; s < steps; s++)
            {
                if (quadsSoFar + made >= MaxQuads) break;

                float t0 = tail + stepLen * s;
                float t1 = t0 + stepLen;

                Vector3 stepEnd = origin + dir * t1;

                // 落点查询。
                //
                // ★ 关于缓存的坑：
                //   光带是**逐帧变长**的。缓存成 null 之后如果直接信它，
                //   那么"上一帧还在区域外、这一帧已经进去"的点就会被漏掉 ——
                //   表现正好是"进入时延迟变大、离开时正常"（离开时缓存非 null，
                //   Contains 失败会触发重查，所以感觉没问题）。
                //
                //   所以这里只在"缓存有效 `>` 命中非 null"时才敢跳过查询；
                //   缓存为 null 时必须老实查 —— 这是有意的，
                //   因为一次全表查找远比"漏检导致颜色不更新"便宜。
                SpecialZone zone;

                if (useZoneProbeCache && probe.zoneValid && probe.zone != null &&
                    probe.zone.Contains(stepEnd))
                {
                    zone = probe.zone;                       // 确实还在同一块空间里
                }
                else
                {
                    zone            = ZoneSystem.Find(stepEnd);
                    probe.zone      = zone;
                    probe.zoneValid = true;
                }

                bool inGoal = zone != null && zone.kind == ZoneKind.Goal;
                if (inGoal) goalHalfWidth = Mathf.Max(0.001f, zone.goalWidth) * 0.5f;

                // 终点空间会改宽度，跨越它的边界时必须切一刀，边缘才是干净的直边。
                // （机关/陷阱只改颜色，沿用原来的细步长就够了）
                if (s > 0 && inGoal != prevInside)
                {
                    float cut = FindGoalBoundary(origin, dir, t0, t1);
                    if (cut > t0 + 1e-4f && cut < t1 - 1e-4f)
                    {
                        SpecialZone prevZone = ZoneSystem.Find(origin + dir * ((t0 + cut) * 0.5f));
                        made += EmitStep(tr, t0, cut, prevZone, goalHalfWidth, halfWidth,
                                         up, side, fade, now, spawnTime, maxTravel,
                                         origin, dir, quadsSoFar + made, ref probe);
                        t0 = cut;

                        // 切完这一刀，剩下部分重新确定落点，并同步缓存
                        zone            = ZoneSystem.Find(origin + dir * ((t0 + t1) * 0.5f));
                        probe.zone      = zone;
                        probe.zoneValid = true;

                        inGoal = zone != null && zone.kind == ZoneKind.Goal;
                        if (inGoal) goalHalfWidth = Mathf.Max(0.001f, zone.goalWidth) * 0.5f;
                    }
                }

                prevInside = inGoal;

                made += EmitStep(tr, t0, t1, zone, goalHalfWidth, halfWidth,
                                 up, side, fade, now, spawnTime, maxTravel,
                                 origin, dir, quadsSoFar + made, ref probe);
            }

            // 最前端加一个更亮的弹头，让"曳光弹"的头部有实感。
            // 波前一旦冻住（maxTravel 已到），弹头就不再跟着走，只在原地淡出。
            if (drawHead && quadsSoFar + made < MaxQuads)
            {
                float headPos  = tr.travelBefore + head;
                float headAge  = Mathf.Max(0f, (now - spawnTime) - headPos / tr.speed);
                float headFroz = Mathf.Max(0f, (now - spawnTime) - maxTravel / tr.speed);

                // 弹头（波前）的不透明度。
                // holdAlpha 光带在波前推进期间保持满亮度（这就是"波前不透明度不变"）；
                // 波前钉住之后才跟着残留淡出系数一起淡掉。
                float headAlpha = tr.holdAlpha
                                ? tr.initAlpha * LingerFade01(headFroz, tr.tailLinger)
                                : tr.initAlpha * Mathf.Exp(-headAge * fade) * Mathf.Exp(-headFroz * fade);
                float headR     = halfWidth * 2.6f;
                Color headColor = tr.color;

                // 弹头落在特殊空间里：终点"提亮 + 加宽"，机关/陷阱只换颜色
                SpecialZone headZone = ZoneSystem.Find(flatEnd);
                if (headZone != null)
                {
                    if (headZone.kind == ZoneKind.Goal)
                    {
                        float b = tr.initAlpha > 1e-4f ? Mathf.Clamp01(headAlpha / tr.initAlpha) : 0f;
                        headAlpha = Mathf.Max(headAlpha, headZone.goalAlpha * b);
                        headR     = Mathf.Max(0.001f, headZone.goalWidth) * 0.5f * 2.6f;
                    }
                    else if (headZone.kind == ZoneKind.Mechanism || headZone.kind == ZoneKind.Trap)
                    {
                        headColor = headZone.waveColor;
                    }
                }

                if (headAlpha > visibleThreshold)
                {
                    float r = headR;

                    int v = (quadsSoFar + made) * 4;
                    m_Verts[v + 0] = flatEnd - up * r - side * r;
                    m_Verts[v + 1] = flatEnd - up * r + side * r;
                    m_Verts[v + 2] = flatEnd + up * r + side * r;
                    m_Verts[v + 3] = flatEnd + up * r - side * r;

                    m_Uvs[v + 0] = new Vector2(0f, 0f);
                    m_Uvs[v + 1] = new Vector2(0f, 1f);
                    m_Uvs[v + 2] = new Vector2(1f, 1f);
                    m_Uvs[v + 3] = new Vector2(1f, 0f);

                    Color ch = new Color(headColor.r, headColor.g, headColor.b, Mathf.Clamp01(headAlpha * 1.6f));
                    m_Cols[v + 0] = ch;
                    m_Cols[v + 1] = ch;
                    m_Cols[v + 2] = ch;
                    m_Cols[v + 3] = ch;

                    made++;
                }
            }

            return made;
        }

        /// <summary>
        /// 铺一个四边形。[t0, t1] 是本段内部的局部距离。
        ///
        /// <paramref name="zone"/> 是这一段中点所在的特殊空间（没命中就传 null）：
        ///   Goal      —— 宽度换成 goalWidth，不透明度按比例提亮到 goalAlpha
        ///   Mechanism —— 只把颜色换成 zone.waveColor，粗细/不透明度不动
        ///   Trap      —— 同上，只是颜色是红的
        ///   Hint      —— 什么都不做
        /// </summary>
        private int EmitStep(SonarTrail tr, float t0, float t1, SpecialZone zone,
                             float goalHalfWidth, float halfWidth,
                             Vector3 up, Vector3 side, float fade, float now, float spawnTime,
                             float maxTravel, Vector3 origin, Vector3 dir, int quadsSoFar,
                             ref ZoneProbe probe)
        {
            if (t1 - t0 < 1e-5f) return 0;
            if (quadsSoFar >= MaxQuads) return 0;

            bool inGoal = zone != null && zone.kind == ZoneKind.Goal;

            // 终点空间内：宽度换成固定值；其余情况保持原本的细宽度
            float hw = inGoal ? goalHalfWidth : halfWidth;

            // 机关 / 陷阱：只换颜色
            Color baseColor = tr.color;
            if (zone != null && (zone.kind == ZoneKind.Mechanism || zone.kind == ZoneKind.Trap))
                baseColor = zone.waveColor;

            // 手搭的触发立方体：可以独立覆盖宽度 / 不透明度 / 颜色。
            //
            // ★ 判定点用这一步的**前沿 t1**，不是中点。
            //
            //   用中点的话：波前刚跨进区域时，最前面那个四边形的**边缘**已经在区内，
            //   但它的**中点**还在区外 —— 要等波前再推进半个步长，中点才越界。
            //   结果就是"波前已经进去了，颜色却还没变"，延迟约半个采样步长。
            //   站在区域内释放时每个中点都在区内，所以看不出问题；
            //   从区外反弹回来时就会明显。
            //
            //   用前沿判定，语义才和"声波扫到哪里亮到哪里"一致。
            Vector3 volProbePos = origin + dir * t1;

            // 同样只在"缓存命中且仍然包含该点"时才跳过查询。
            // 缓存为 null 时必须查 —— 光带逐帧变长，之前不在区域里的点这一帧可能就进去了。
            TriggerVolume vol;

            if (useZoneProbeCache && probe.triggerValid && probe.trigger != null &&
                probe.trigger.Contains(volProbePos))
            {
                vol = probe.trigger;
            }
            else
            {
                vol                = ZoneSystem.FindTrigger(volProbePos);
                probe.trigger      = vol;
                probe.triggerValid = true;
            }

            float alphaOverride = -1f;

            if (vol != null)
            {
                if (vol.overrideWidth) hw        = Mathf.Max(0.001f, vol.waveWidth) * 0.5f;
                if (vol.overrideColor) baseColor = vol.waveColor;
                if (vol.overrideAlpha) alphaOverride = vol.waveAlpha;
            }
            else
            {
                // 没有落在触发立方体里，再看看是否落在某个**怪物的变色球**里。
                // 怪物周围那一圈空间要求"和陷阱一样的观感"，所以走同一套覆盖逻辑。
                //
                // ★★ 地牢模式：只有**潜行**光带变色 ★★
                //
                //   需求原文："我希望地牢模式的潜行脚步光带在经过怪物变色球下方时
                //             会变色，和进入变色球时的变色一样。"
                //
                //   所以地牢模式下加一个条件：只处理 isSneak 的光带。
                //   走路 / 跑步 / 空格 / 子弹 的光带经过变色球时**保持原样**。
                //
                //   探秘模式不受影响 —— 那边所有光带都照常变色（原来就是这样）。
                bool dungeonSneakOnly = GameUI.IsDungeonMode && !tr.isSneak;

                if (!dungeonSneakOnly)
                {
                    PuzzleMonster mon = ZoneSystem.FindMonster(volProbePos);
                    if (mon != null)
                    {
                        if (mon.overrideWidth) hw        = Mathf.Max(0.001f, mon.waveWidth) * 0.5f;
                        if (mon.overrideColor) baseColor = mon.waveColor;
                        if (mon.overrideAlpha) alphaOverride = mon.waveAlpha;
                    }
                }
            }

            // ★ Gravel（碎石区）：射入其中的光带改成它指定的粗细 / 不透明度 / 颜色。
            //
            //   优先级放在最后 —— 只要这段光带落在 Gravel 里，就用 Gravel 的观感，
            //   不管它同时是不是也在某个触发区里。
            {
                Gravel gravel = ZoneSystem.FindGravel(volProbePos);

                if (gravel != null)
                {
                    hw            = Mathf.Max(0.001f, gravel.width) * 0.5f;
                    baseColor     = gravel.color;
                    alphaOverride = gravel.alpha;
                }
            }

            Vector3 p0 = origin + dir * t0;
            Vector3 p1 = origin + dir * t1;

            int v = quadsSoFar * 4;

            m_Verts[v + 0] = p0 + up * hw;
            m_Verts[v + 1] = p0 - up * hw;
            m_Verts[v + 2] = p1 - up * hw;
            m_Verts[v + 3] = p1 + up * hw;

            m_Uvs[v + 0] = new Vector2(0f, 0f);
            m_Uvs[v + 1] = new Vector2(0f, 1f);
            m_Uvs[v + 2] = new Vector2(1f, 1f);
            m_Uvs[v + 3] = new Vector2(1f, 0f);

            // 局部距离换算成"从声源算起的绝对路程"要加上 travelBefore
            float abs0 = tr.travelBefore + t0;
            float abs1 = tr.travelBefore + t1;

            // 钉住处：波前推进到 maxTravel 时所在的位置。
            // 超过它的点永远不会被扫到，它们是被一起冻住的，用 min 把过冲的年龄夹掉。
            float clamped0 = Mathf.Min(abs0, maxTravel);
            float clamped1 = Mathf.Min(abs1, maxTravel);

            // ① 被波前扫过之后过了多久 -> 沿光带的渐隐
            float age0 = Mathf.Max(0f, (now - spawnTime) - clamped0 / tr.speed);
            float age1 = Mathf.Max(0f, (now - spawnTime) - clamped1 / tr.speed);

            // ② 钉住之后过了多久 -> 波前停住后的残留淡出（整段统一，所以一起变暗）
            float frozen = Mathf.Max(0f, (now - spawnTime) - maxTravel / tr.speed);

            // ★ holdAlpha 的语义是"**波前处**维持不透明度"：
            //   保留 ①（刚生成后照常渐隐消失），把 ② 换成一个**有限的**残留淡出系数。
            //   这样波前消失后，已经画出来的轨迹会钉在原地、在 tailLinger 秒内淡出。
            float lingerFade = tr.holdAlpha ? LingerFade01(frozen, tr.tailLinger) : 1f;

            float a0 = tr.holdAlpha
                     ? tr.initAlpha * Mathf.Exp(-age0 * fade) * lingerFade
                     : tr.initAlpha * Mathf.Exp(-age0 * fade) * Mathf.Exp(-frozen * fade);
            float a1 = tr.holdAlpha
                     ? tr.initAlpha * Mathf.Exp(-age1 * fade) * lingerFade
                     : tr.initAlpha * Mathf.Exp(-age1 * fade) * Mathf.Exp(-frozen * fade);
            float initAlpha = tr.initAlpha;

            // ★ 终点空间：把不透明度"按比例提亮"，而不是"替换成常数"。
            //   替换成常数会把衰减整个关掉，波前冻住后那截粗白条就永远不淡出，
            //   像凝固在空气里。用归一化包络就没有这个问题。
            if (inGoal)
            {
                float b0 = initAlpha > 1e-4f ? Mathf.Clamp01(a0 / initAlpha) : 0f;
                float b1 = initAlpha > 1e-4f ? Mathf.Clamp01(a1 / initAlpha) : 0f;

                a0 = Mathf.Max(a0, zone.goalAlpha * b0);
                a1 = Mathf.Max(a1, zone.goalAlpha * b1);
            }

            // 触发立方体要求固定不透明度时：同样按比例提亮，不冻结衰减
            if (alphaOverride >= 0f)
            {
                float b0 = initAlpha > 1e-4f ? Mathf.Clamp01(a0 / initAlpha) : 0f;
                float b1 = initAlpha > 1e-4f ? Mathf.Clamp01(a1 / initAlpha) : 0f;

                a0 = Mathf.Max(a0, alphaOverride * b0);
                a1 = Mathf.Max(a1, alphaOverride * b1);
            }

            Color c0 = new Color(baseColor.r, baseColor.g, baseColor.b, Mathf.Clamp01(a0));
            Color c1 = new Color(baseColor.r, baseColor.g, baseColor.b, Mathf.Clamp01(a1));

            m_Cols[v + 0] = c0;
            m_Cols[v + 1] = c0;
            m_Cols[v + 2] = c1;
            m_Cols[v + 3] = c1;

            return 1;
        }

        /// <summary>
        /// 找出 [t0, t1] 区间内第一次进入/离开终点空间的位置（局部距离）。
        /// 用二分法：两端状态不同，边界必然夹在中间。
        /// </summary>
        private static float FindGoalBoundary(Vector3 origin, Vector3 dir, float t0, float t1)
        {
            float a = t0, b = t1;

            for (int i = 0; i < 12; i++)
            {
                float m = (a + b) * 0.5f;
                SpecialZone z = ZoneSystem.Find(origin + dir * m);
                if (z != null && z.kind == ZoneKind.Goal) b = m;
                else                                      a = m;
            }

            return b;
        }
    }
}
