using System.Collections.Generic;
using UnityEngine;

namespace EchoPuzzle.Dungeon
{
    /// <summary>
    /// 【地牢模式】爆炸圆形 / 光环圆形的**统一渲染器**（对象池，不反复创建销毁）。
    ///
    /// ── 为什么要有它 ────────────────────────────────────
    ///   需求："现在爆炸子弹会创建一个叫『爆炸圆形』的东西……
    ///         我希望建两个渲染器将它们分别容纳，免得大量创建和删除东西。"
    ///
    ///   ★ 以前每次子弹爆炸都 new GameObject + new Mesh + new Material，
    ///     0.45 秒后再 Destroy 掉。连发 + 爆炸弹头时一秒能建十几个，
    ///     每个都带一次网格/材质分配 —— 会产生 GC 垃圾和卡顿。
    ///
    ///   ✔ 现在：
    ///     · 网格**只有一份**（单位半径的圆盘），需要多大就改 Transform 缩放
    ///     · 材质**只有一份**，颜色/透明度实时写进去
    ///     · 物件从池里借，用完还回池，不销毁
    ///
    /// ── 用法 ────────────────────────────────────────────
    ///   BulletBlastRenderer.Spawn(位置, 半径, 颜色);          // 一次性爆炸圆
    ///   var r = BulletBlastRenderer.RentPersistent(...);      // 常驻圆（光环）
    ///   BulletBlastRenderer.Release(r);                       // 手动归还
    /// </summary>
    [AddComponentMenu("Echo Puzzle/Dungeon/Bullet Blast Renderer")]
    public sealed class BulletBlastRenderer : MonoBehaviour
    {
        public static BulletBlastRenderer Instance { get; private set; }

        // ---------------------------------------------------------------- 池

        /// <summary>池里每个圆盘的运行时状态。</summary>
        private sealed class Disc
        {
            public GameObject go;
            public MeshRenderer mr;
            public MaterialPropertyBlock mpb;

            public bool  inUse;
            public bool  persistent;

            /// <summary>出生时刻（Time.timeSinceLevelLoad）。</summary>
            public float bornAt;

            /// <summary>死亡时刻 = bornAt + fadeIn + hold + fadeOut。</summary>
            public float dieAt;

            public float startAlpha;

            /// <summary>这一帧算出来的、未经全局压缩的 alpha（第一遍求和用）。</summary>
            public float frameAlpha;

            // ★ 三段时长（秒）
            public float fadeInTime;
            public float holdTime;
            public float fadeOutTime;

            public Color color;

            public bool  followTarget;
            public Transform follow;
            public float followGroundY;
        }

        private readonly List<Disc> m_Pool = new List<Disc>(32);

        /// <summary>共用网格（单位半径的 XZ 圆盘）。</summary>
        private Mesh m_SharedMesh;

        /// <summary>共用材质。</summary>
        private Material m_SharedMat;

        /// <summary>圆盘细分数（改了要 Rebuild，运行期不动）。</summary>
        [Header("网格")]
        [Range(12, 128)] public int segments = 48;

        [Header("不透明度")]
        [Tooltip("★ 淡入结束时达到的不透明度（峰值）。\n\n" +
                 "★ 这是「一次爆炸看起来多亮」的直接旋钮。")]
        [Range(0f, 1f)] public float defaultStartAlpha = 0.2f;

        [Tooltip("★ **所有爆炸圆不透明度之和的上限**。\n\n" +
                 "★ 叠加方式是直接相加：每个圆按自己的 alpha 显示，\n" +
                 "  重叠处就是几个 alpha 加在一起。\n" +
                 "★ 只有「相加后的总和」超过这个值时，才把所有圆等比压低，\n" +
                 "  使总和刚好等于它。\n" +
                 "★ 单个圆永远不受影响（它自己的 alpha 就是峰值）。")]
        [Range(0.01f, 1f)] public float maxStackedAlpha = 0.25f;

        [Header("时长（秒）")]
        [Tooltip("★ 淡入时长：不透明度从 0 涨到 defaultStartAlpha 用多久")]
        [Min(0f)] public float defaultFadeInSeconds = 0.1f;

        [Tooltip("★ 维持时长：保持在 defaultStartAlpha 多久")]
        [Min(0f)] public float defaultHoldSeconds = 0.3f;

        [Tooltip("★ 淡出时长：不透明度从 defaultStartAlpha 掉回 0 用多久")]
        [Min(0f)] public float defaultFadeOutSeconds = 0.4f;

        [Header("位置")]
        [Tooltip("圆盘离地高度（米），避免和地面 z-fighting")]
        public float groundOffset = 0.06f;

        // ---------------------------------------------------------------- 生命周期

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;

            BuildSharedResources();
        }

        private void OnDestroy()
        {
            if (Instance != this) return;
            Instance = null;

            if (m_SharedMesh != null) Destroy(m_SharedMesh);
            if (m_SharedMat  != null) Destroy(m_SharedMat);
        }

        /// <summary>建一次共用网格 + 共用材质。</summary>
        private void BuildSharedResources()
        {
            if (m_SharedMesh == null)
            {
                int n = Mathf.Clamp(segments, 12, 128);

                var verts = new Vector3[n + 1];
                var tris  = new int[n * 3];

                verts[0] = Vector3.zero;

                for (int i = 0; i < n; i++)
                {
                    float ang = i * Mathf.PI * 2f / n;
                    verts[i + 1] = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang));
                }

                for (int i = 0; i < n; i++)
                {
                    tris[i * 3 + 0] = 0;
                    tris[i * 3 + 1] = i + 1;
                    tris[i * 3 + 2] = (i + 1) % n + 1;
                }

                m_SharedMesh = new Mesh { name = "BulletBlastDisc(shared)" };
                m_SharedMesh.SetVertices(new List<Vector3>(verts));
                m_SharedMesh.SetTriangles(tris, 0);
                m_SharedMesh.RecalculateBounds();
            }

            if (m_SharedMat == null)
            {
                m_SharedMat = EchoRuntimeMaterials.AimOverlay(Color.white);

                if (m_SharedMat != null)
                {
                    m_SharedMat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.LessEqual);
                    m_SharedMat.SetInt("_ZWrite", 0);
                    m_SharedMat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                }
            }
        }

        // ---------------------------------------------------------------- 借用 / 归还

        /// <summary>从池里拿一个空闲圆盘；没有就新建一个。</summary>
        private Disc Rent()
        {
            for (int i = 0; i < m_Pool.Count; i++)
            {
                if (!m_Pool[i].inUse)
                {
                    m_Pool[i].inUse = true;
                    m_Pool[i].go.SetActive(true);
                    return m_Pool[i];
                }
            }

            // 池里没有 -> 新建一个（之后会一直复用）
            BuildSharedResources();

            var go = new GameObject("[子弹] 爆炸圆形");
            go.hideFlags = HideFlags.DontSave;

            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = m_SharedMesh;

            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = m_SharedMat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows    = false;

            // 项目规矩：所有物体挂 Hide
            Hide hide = go.AddComponent<Hide>();
            hide.SetHiding(0);

            var d = new Disc
            {
                go  = go,
                mr  = mr,
                mpb = new MaterialPropertyBlock(),
                inUse = true,
            };

            // 挂在渲染器下面，Hierarchy 整洁
            go.transform.SetParent(transform, false);

            m_Pool.Add(d);
            return d;
        }

        /// <summary>
        /// 把一个圆盘还回池里（隐藏但不销毁）。
        ///
        /// ★ 静态方法：和 RentPersistent / SetFollow 一样，
        ///   调用方不需要先拿到 Instance（OnDestroy 时 Instance 可能已经是 null）。
        /// </summary>
        public static void ReleaseDisc(object token)
        {
            if (Instance == null) return;
            if (!(token is Disc d) || d == null) return;

            d.inUse        = false;
            d.persistent   = false;
            d.followTarget = false;
            d.follow       = null;

            if (d.go != null) d.go.SetActive(false);
        }

        // ---------------------------------------------------------------- 对外：一次性

        /// <summary>
        /// 在 <paramref name="center"/> 铺一个半径 <paramref name="radius"/> 的爆炸圆。
        ///
        /// ★ 只显示，不参与伤害结算。
        /// ★ 时长用本组件上的默认三段（淡入 / 维持 / 淡出）。
        /// </summary>
        public static void Spawn(Vector3 center, float radius, Color color)
        {
            if (Instance == null) return;
            if (radius <= 0.01f) return;

            Instance.SpawnInternal(center, radius, color,
                                   Instance.defaultStartAlpha,
                                   Instance.defaultFadeInSeconds,
                                   Instance.defaultHoldSeconds,
                                   Instance.defaultFadeOutSeconds);
        }

        /// <summary>带自定义峰值不透明度的版本（时长仍用默认三段）。</summary>
        public static void Spawn(Vector3 center, float radius, Color color, float startAlpha)
        {
            if (Instance == null) return;
            if (radius <= 0.01f) return;

            Instance.SpawnInternal(center, radius, color, startAlpha,
                                   Instance.defaultFadeInSeconds,
                                   Instance.defaultHoldSeconds,
                                   Instance.defaultFadeOutSeconds);
        }

        private void SpawnInternal(Vector3 center, float radius, Color color,
                                   float startAlpha,
                                   float fadeInSeconds, float holdSeconds, float fadeOutSeconds)
        {
            Disc d = Rent();

            float groundY = ProbeGroundY(center);

            d.go.transform.position = new Vector3(center.x, groundY + groundOffset, center.z);
            d.go.transform.rotation = Quaternion.identity;

            // ★ 网格顶点是**单位半径**（cos/sin 落在半径 1 的圆上），
            //   所以缩放直接用 radius，不能用 radius*2（那会变成 2 倍大）。
            d.go.transform.localScale = new Vector3(radius, 1f, radius);

            d.persistent   = false;
            d.followTarget = false;
            d.color        = color;  d.color.a = 1f;
            d.startAlpha   = Mathf.Clamp01(startAlpha);

            // ---- 三段时长 ----
            d.fadeInTime  = Mathf.Max(0f, fadeInSeconds);
            d.holdTime    = Mathf.Max(0f, holdSeconds);
            d.fadeOutTime = Mathf.Max(0f, fadeOutSeconds);

            // ★ 总时长为 0 会导致永远消失不掉，兜一个最小值
            float total = d.fadeInTime + d.holdTime + d.fadeOutTime;
            if (total < 0.02f) { d.fadeInTime = 0.02f; total = 0.02f; }

            d.bornAt = Time.timeSinceLevelLoad;
            d.dieAt  = d.bornAt + total;

            // ★★ 出生这一帧写"淡入起点"（= 0），不能直接写满值 ★★
            //
            //   如果出生瞬间就是满不透明，下一帧按淡入曲线才算出接近 0 的值，
            //   会出现"闪一下再淡入"的跳变。
            WriteAlpha(d, 0f);
        }

        // ---------------------------------------------------------------- 对外：常驻

        /// <summary>
        /// 拿一个**常驻**圆盘（不自动消失），返回一个 token，用完交给 <see cref="ReleaseDisc"/>。
        /// 给怪物光环这种"一直跟着怪物"的效果用。
        /// </summary>
        public static object RentPersistent(float radius, Color color, float alpha)
        {
            if (Instance == null) return null;

            Disc d = Instance.Rent();

            d.go.transform.rotation = Quaternion.identity;

            // ★ 同上：单位半径网格 -> 缩放 = radius
            d.go.transform.localScale = new Vector3(radius, 1f, radius);

            d.persistent    = true;
            d.followTarget  = false;
            d.color         = color;  d.color.a = 1f;
            d.startAlpha    = Mathf.Clamp01(alpha);

            Instance.WriteAlpha(d, d.startAlpha);

            return d;
        }

        /// <summary>让常驻圆盘跟着某个 Transform（每帧贴地面）。</summary>
        public static void SetFollow(object token, Transform target)
        {
            if (!(token is Disc d) || d == null) return;

            d.followTarget = target != null;
            d.follow       = target;
        }

        // ---------------------------------------------------------------- 每帧

        private void Update()
        {
            float now = Time.timeSinceLevelLoad;

            // ★★ 叠加方式：**不透明度直接相加，总和超过上限才压缩** ★★
            //
            //   需求："我说的加算的意思是将不透明度相加。"
            //
            //   ✔ 做法分两步：
            //     1) 每个圆先按自己的三段曲线算出 alpha
            //        —— 这就是它**独立显示时**的亮度，不做任何分摊
            //     2) 把所有一次性圆的 alpha **求和**；
            //        只有和超过 maxStackedAlpha 时，才把所有圆**等比**乘上
            //        (maxStackedAlpha / 总和)，让总和不超限
            //
            //   ★ 效果：
            //     · 只有 1 个圆时，总和 = 它自己，通常远低于上限 → **完全不压缩**
            //     · 圆越多越亮，直到总和碰到上限就整体停住
            //     · 重叠处就是各圆 alpha 的真正相加
            //
            //   ★ 注意：压缩系数按"所有圆的总和"算，不是按局部重叠算。
            //     这是纯 C# 能做到的加算 + 限幅，和逐像素相加的总量一致。

            // ---- 第一遍：算每个一次性圆当前的 alpha，并求和 ----
            float sumAlpha = 0f;

            for (int i = 0; i < m_Pool.Count; i++)
            {
                Disc dd = m_Pool[i];
                if (!dd.inUse || dd.persistent) continue;

                dd.frameAlpha = dd.startAlpha * LifeCurve01(dd, now);
                sumAlpha += dd.frameAlpha;
            }

            // ---- 总和超过上限时，算一个等比压缩系数 ----
            float scale = 1f;

            float cap = Mathf.Clamp01(maxStackedAlpha);
            if (sumAlpha > cap && sumAlpha > 0.0001f)
                scale = cap / sumAlpha;

            // ---- 第二遍：写颜色 / 回收 ----
            for (int i = 0; i < m_Pool.Count; i++)
            {
                Disc d = m_Pool[i];
                if (!d.inUse) continue;

                // ---- 常驻：跟位置 / 保持不透明度（不参与加算）----
                if (d.persistent)
                {
                    if (d.followTarget && d.follow != null)
                    {
                        float gy = ProbeGroundY(d.follow.position);

                        d.go.transform.position =
                            new Vector3(d.follow.position.x, gy + groundOffset, d.follow.position.z);
                    }

                    WriteAlpha(d, d.startAlpha);
                    continue;
                }

                // ---- 一次性：自己的 alpha × 全局压缩系数 ----
                WriteAlpha(d, d.frameAlpha * scale);

                if (now >= d.dieAt) ReleaseDisc(d);
            }
        }

        // ---------------------------------------------------------------- 三段曲线

        /// <summary>
        /// 爆炸圆的寿命曲线：返回 0~1 的亮度比例（尚未乘 startAlpha / 叠加系数）。
        ///
        ///   0 ──────────► fadeInTime ──► +holdTime ──► +fadeOutTime
        ///   0 ──涨到 1──►   保持 1     ──►   降回 0
        ///
        /// ★★ 淡入和淡出都是线性（匀速）★★
        ///
        ///   需求：全都改成线性
        ///
        ///   ★ 之前淡入用的是 smoothstep（x*x*(3-2x)），淡出用的是 1-y*y 二次曲线，
        ///     两者都不均匀：
        ///       · smoothstep 两头慢、中间快，最快/最慢相差 5.3 倍
        ///       · 1-y*y 开头几乎不动、结尾猛降，最快/最慢相差 19 倍
        ///     所以淡出后半段会感觉「刷一下就没了」。
        ///   ✔ 现在每段都按时间匀速变化：
        ///     淡入每帧固定增量涨到峰值，淡出每帧固定增量降回 0。
        /// </summary>
        private static float LifeCurve01(Disc d, float now)
        {
            float e = now - d.bornAt;              // 出生到现在过了多久

            // ---- 淡入：线性 0 -> 1 ----
            if (e <= d.fadeInTime)
            {
                if (d.fadeInTime <= 0.0001f) return 1f;

                return Mathf.Clamp01(e / d.fadeInTime);
            }

            e -= d.fadeInTime;

            // ---- 维持：恒为 1 ----
            if (e <= d.holdTime) return 1f;

            e -= d.holdTime;

            // ---- 淡出：线性 1 -> 0 ----
            if (d.fadeOutTime <= 0.0001f) return 0f;

            return 1f - Mathf.Clamp01(e / d.fadeOutTime);
        }

        private void WriteAlpha(Disc d, float a)
        {
            if (d.mr == null) return;

            Color c = d.color;
            c.a = Mathf.Clamp01(a);

            d.mpb.Clear();
            d.mpb.SetColor("_Color", c);

            if (m_SharedMat != null && m_SharedMat.HasProperty("_BaseColor"))
                d.mpb.SetColor("_BaseColor", c);

            d.mr.SetPropertyBlock(d.mpb);
        }

        /// <summary>从某点上方向下打一条射线找地面高度。找不到就用 y = 0。</summary>
        private static float ProbeGroundY(Vector3 around)
        {
            Vector3 probe = around + Vector3.up * 6f;

            if (Physics.Raycast(probe, Vector3.down, out RaycastHit hit, 12f,
                                ~((1 << 2) | (1 << 4) | (1 << 5)),
                                QueryTriggerInteraction.Ignore))
            {
                return hit.point.y;
            }

            return 0f;
        }
    }
}
