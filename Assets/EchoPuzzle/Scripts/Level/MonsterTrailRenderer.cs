using System.Collections.Generic;
using UnityEngine;

namespace EchoPuzzle
{
    /// <summary>
    /// 怪物光带的渲染器。和 <see cref="SonarTrailRenderer"/> 完全独立的一套。
    ///
    /// 与声波光带的区别：
    ///   · 波前**不反射**：撞到墙体就立即消失（由 PuzzleMonster 判定）
    ///   · 光带残留在原地淡出
    ///   · 波前的不透明度衰减 > 光带本体的不透明度衰减
    ///     —— 也就是"前沿先暗下去、尾巴拖着"的反向观感：
    ///        波前用 <see cref="headFadeSpeed"/>（快），
    ///        光带本体用 <see cref="bodyFadeSpeed"/>（慢）。
    ///
    /// 渲染方式照抄声波光带：一个动态 Mesh、一次 DrawCall、
    /// 面向相机 45° 的四边形（一条边向上、一条边指向相机）。
    /// </summary>
    public sealed class MonsterTrailRenderer : MonoBehaviour
    {
        public const int MaxQuads = 24000;

        /// <summary>一条怪物光带。</summary>
        public struct Trail
        {
            /// <summary>
            /// 稳定 ID（单调递增，永不复用）。
            ///
            /// ★★ 对外一律用 ID 操作，不要用列表下标 ★★
            ///
            ///   原来外部存的是 m_Trails 的下标，而淘汰时这里会把
            ///   "末尾元素搬到被删位置"（m_Trails[i] = m_Trails[last]）——
            ///   一次淘汰就让所有下标错位：
            ///     · 正在推进的光带 SetLength 到别人身上 -> 那条突然变长 / 变短
            ///     · 自己的长度停在半路 -> 看起来"这条光带很短"
            ///   这就是"怪物光带忽长忽短"的主因之一。
            ///
            ///   改成 ID 之后，淘汰只把槽标记成 dead，下标彻底不参与外部逻辑。
            /// </summary>
            public int id;

            /// <summary>是否还活着。false = 已淘汰，槽位可复用。</summary>
            public bool alive;

            public Vector3 origin;      // 波前起点（球心 + dir × headRadius）
            public Vector3 dir;         // 单位方向
            public float   length;      // 已经铺出去的长度（米）
            public float   spawnTime;   // 诞生时刻
            public float   headAge;     // 波前停止推进的时刻（撞墙那一刻），负数表示还在推进
            public Color   color;
            public float   width;
            public float   initAlpha;

            /// <summary>
            /// 折线路径（世界坐标）。非空时用它代替 origin+dir 直线。
            /// 石子那种走抛物线的光带靠它才能画出**连贯的弧**。
            /// </summary>
            public List<Vector3> path;
        }

        /// <summary>折线最多多少个点。超过就不再追加（避免无限增长）。</summary>
        public const int MaxPathPoints = 256;

        [Header("渲染")]
        [Tooltip("光带材质。留空则用 Resources/EchoRuntime/EchoSonarTrail")]
        public Material material;

        [Tooltip("整体发光强度")]
        public float glowIntensity = 1.8f;

        [Tooltip("采样间距（米）。越小越顺滑，几何量越大")]
        public float trailSpacing = 0.35f;

        [Header("淡出")]
        [Tooltip("光带**本体**（波前扫过的轨迹）的不透明度衰减速度（1/秒）。越小残留越久")]
        public float bodyFadeSpeed = 1.2f;

        [Tooltip("**波前**的不透明度衰减速度（1/秒）。\n" +
                 "应当大于上面的本体速度 —— 这样前沿会先暗下去，看起来像被吞掉")]
        public float headFadeSpeed = 3.5f;

        [Tooltip("不透明度低于这个值就不画了")]
        public float visibleThreshold = 0.02f;

        [Tooltip("单条光带最长铺多远（米），以及残留多久后被淘汰")]
        public float maxLength = 40f;

        // ---------------------------------------------------------------- 内部

        private readonly List<Trail> m_Trails = new List<Trail>(256);

        /// <summary>已淘汰、可复用的槽位下标（避免列表无限增长）。</summary>
        private readonly Stack<int> m_FreeSlots = new Stack<int>();

        /// <summary>下一个光带 ID。单调递增，永不复用。</summary>
        private int m_NextId = 1;

        private Mesh         m_Mesh;
        private Material     m_Mat;
        private bool         m_OwnsMat;
        private MeshFilter   m_Filter;
        private MeshRenderer m_Renderer;
        private bool         m_Ready;

        private Vector3[] m_Verts;
        private Vector2[] m_Uvs;
        private Color[]   m_Cols;
        private int[]     m_Indices;

        /// <summary>列表槽位总数（含已淘汰但还没被复用的槽）。</summary>
        public int TrailCount => m_Trails.Count;

        /// <summary>某个光带 ID 是否还活着（槽未被淘汰、也未被复用）。</summary>
        public bool IsTrailAlive(int id) => FindIndexById(id) >= 0;

        /// <summary>按 ID 找槽位下标；找不到返回 -1（已淘汰 / 已被复用）。</summary>
        private int FindIndexById(int id)
        {
            if (id <= 0) return -1;

            for (int i = 0; i < m_Trails.Count; i++)
            {
                Trail t = m_Trails[i];
                if (t.alive && t.id == id) return i;
            }

            return -1;
        }

        /// <summary>
        /// 借一个槽位：优先复用已淘汰的，没有就追加；到上限返回 -1。
        /// </summary>
        private int RentSlot()
        {
            if (m_FreeSlots.Count > 0) return m_FreeSlots.Pop();

            if (m_Trails.Count >= MaxQuads) return -1;

            m_Trails.Add(default(Trail));
            return m_Trails.Count - 1;
        }

        // ---------------------------------------------------------------- 生命周期

        private void Awake()
        {
            m_Verts   = new Vector3[MaxQuads * 4];
            m_Uvs     = new Vector2[MaxQuads * 4];
            m_Cols    = new Color[MaxQuads * 4];
            m_Indices = new int[MaxQuads * 6];

            for (int q = 0; q < MaxQuads; q++)
            {
                int v = q * 4, t = q * 6;
                m_Indices[t + 0] = v + 0;
                m_Indices[t + 1] = v + 1;
                m_Indices[t + 2] = v + 2;
                m_Indices[t + 3] = v + 0;
                m_Indices[t + 4] = v + 2;
                m_Indices[t + 5] = v + 3;
            }

            m_Mesh = new Mesh { name = "MonsterTrails" };
            m_Mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m_Mesh.MarkDynamic();

            m_Filter = GetComponent<MeshFilter>();
            if (m_Filter == null) m_Filter = gameObject.AddComponent<MeshFilter>();
            m_Filter.sharedMesh = m_Mesh;

            m_Renderer = GetComponent<MeshRenderer>();
            if (m_Renderer == null) m_Renderer = gameObject.AddComponent<MeshRenderer>();

            // 和声波光带一样：材质必须来自资产，否则打包时 shader 会被剔除
            Material src = material != null ? material : EchoRuntimeMaterials.SonarTrail;
            if (src == null)
            {
                Debug.LogError("[EchoPuzzle] 怪物光带拿不到材质（Resources/EchoRuntime/EchoSonarTrail）。");
                enabled = false;
                return;
            }

            m_Mat = new Material(src) { name = "MonsterTrailMat", hideFlags = HideFlags.DontSave };
            m_OwnsMat = true;
            m_Mat.SetFloat("_Intensity", glowIntensity);

            m_Renderer.sharedMaterial             = m_Mat;
            m_Renderer.shadowCastingMode          = UnityEngine.Rendering.ShadowCastingMode.Off;
            m_Renderer.receiveShadows             = false;
            m_Renderer.lightProbeUsage            = UnityEngine.Rendering.LightProbeUsage.Off;
            m_Renderer.reflectionProbeUsage       = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            m_Renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;

            // 手动给包围盒，否则会被视锥剔除掉
            m_Renderer.localBounds = new Bounds(Vector3.zero, Vector3.one * 600f);
            m_Renderer.allowOcclusionWhenDynamic = false;

            m_Ready = true;
        }

        private void OnDestroy()
        {
            if (m_OwnsMat && m_Mat != null) Destroy(m_Mat);
            if (m_Mesh != null) Destroy(m_Mesh);
        }

        /// <summary>
        /// 沿折线路径铺四边形。每一小段都用**自己的切向**构造 up/side，
        /// 所以整条光带是连续的弧，而不是一段段生硬拼接的直线。
        /// 返回本次新增的四边形数。
        /// </summary>
        private int BuildPathTrail(Trail tr, Vector3 camPos, Vector3 camUp,
                                   float bodyAlpha, float frozen, float headFade,
                                   int quadsSoFar)
        {
            List<Vector3> path = tr.path;
            int segs = path.Count - 1;

            float halfW = Mathf.Max(0.002f, tr.width) * 0.5f;

            // 沿路径的累计长度，用来算"离波前多远"
            float total = 0f;
            for (int k = 0; k < segs; k++) total += Vector3.Distance(path[k], path[k + 1]);
            if (total <= 1e-4f) return 0;

            float headAtten = frozen > 0f ? Mathf.Exp(-frozen * headFade) : 1f;

            int made = 0;
            float run = 0f;

            for (int k = 0; k < segs; k++)
            {
                if (quadsSoFar + made >= MaxQuads) break;

                Vector3 a = path[k];
                Vector3 b = path[k + 1];

                Vector3 segDir = b - a;
                float   segLen = segDir.magnitude;
                if (segLen < 1e-5f) continue;
                segDir /= segLen;

                run += segLen;

                // 这一小段自己的切向基
                Vector3 toCam = camPos - a;
                if (toCam.sqrMagnitude < 1e-6f) toCam = camUp;
                toCam.Normalize();

                Vector3 up = camUp - segDir * Vector3.Dot(camUp, segDir);
                if (up.sqrMagnitude < 1e-4f) up = Vector3.up - segDir * Vector3.Dot(Vector3.up, segDir);
                if (up.sqrMagnitude < 1e-4f) up = Vector3.right;
                up.Normalize();

                // 沿路径的亮度：越靠近波前越亮
                float fromHead0 = total - (run - segLen);
                float fromHead1 = total - run;

                float d0 = Mathf.Clamp01(fromHead0 / Mathf.Max(0.01f, total));
                float d1 = Mathf.Clamp01(fromHead1 / Mathf.Max(0.01f, total));

                float alpha0 = bodyAlpha * Mathf.Lerp(0.15f, 1f, d0) * headAtten;
                float alpha1 = bodyAlpha * Mathf.Lerp(0.15f, 1f, d1) * headAtten;

                Color c0 = new Color(tr.color.r, tr.color.g, tr.color.b, Mathf.Clamp01(alpha0));
                Color c1 = new Color(tr.color.r, tr.color.g, tr.color.b, Mathf.Clamp01(alpha1));

                int v = (quadsSoFar + made) * 4;

                m_Verts[v + 0] = a + up * halfW;
                m_Verts[v + 1] = a - up * halfW;
                m_Verts[v + 2] = b - up * halfW;
                m_Verts[v + 3] = b + up * halfW;

                m_Uvs[v + 0] = new Vector2(0f, 0f);
                m_Uvs[v + 1] = new Vector2(0f, 1f);
                m_Uvs[v + 2] = new Vector2(1f, 1f);
                m_Uvs[v + 3] = new Vector2(1f, 0f);

                m_Cols[v + 0] = c0;
                m_Cols[v + 1] = c0;
                m_Cols[v + 2] = c1;
                m_Cols[v + 3] = c1;

                made++;
            }

            return made;
        }

        private void OnValidate()
        {
            if (m_Mat != null) m_Mat.SetFloat("_Intensity", glowIntensity);
        }

        // ---------------------------------------------------------------- 登记

        /// <summary>登记一条正在推进的怪物光带。返回它的**稳定 ID**，之后用 <see cref="StopHead"/> 停住它。</summary>
        public int AddTrail(Vector3 origin, Vector3 dir, float width, Color color, float initAlpha)
        {
            if (!m_Ready) return -1;

            int slot = RentSlot();
            if (slot < 0) return -1;

            Trail t = new Trail
            {
                id        = m_NextId++,
                alive     = true,
                origin    = origin,
                dir       = dir.normalized,
                length    = 0f,
                spawnTime = Time.timeSinceLevelLoad,
                headAge   = -1f,
                color     = color,
                width     = width,
                initAlpha = initAlpha,
            };

            m_Trails[slot] = t;
            return t.id;
        }

        /// <summary>把某条光带的波前长度推进到 value（米）。参数是 <see cref="AddTrail"/> 返回的 ID。</summary>
        public void SetLength(int id, float value)
        {
            int i = FindIndexById(id);
            if (i < 0) return;

            Trail t = m_Trails[i];
            t.length = value;
            m_Trails[i] = t;
        }

        // ---------------------------------------------------------------- 折线光带
        //
        // 直线光带（origin + dir + length）没法画弧。石子走的是抛物线，
        // 所以要能给它一条**自己的折线路径**，渲染时沿路径铺四边形。

        /// <summary>登记一条**折线**光带（用于石子那种抛物线轨迹）。返回稳定 ID。</summary>
        public int AddPathTrail(Vector3 firstPoint, float width, Color color, float initAlpha)
        {
            if (!m_Ready) return -1;

            int slot = RentSlot();
            if (slot < 0) return -1;

            var path = new List<Vector3>(64) { firstPoint };

            Trail t = new Trail
            {
                id        = m_NextId++,
                alive     = true,
                origin    = firstPoint,
                dir       = Vector3.forward,
                length    = 0f,
                spawnTime = Time.timeSinceLevelLoad,
                headAge   = -1f,
                color     = color,
                width     = width,
                initAlpha = initAlpha,
                path      = path,
            };

            m_Trails[slot] = t;
            return t.id;
        }

        /// <summary>
        /// 把折线光带的**末端**推进到 point（参数是光带 ID）。
        /// 如果和上一个点太近就不加，避免顶点爆炸。
        /// </summary>
        public void PushPathPoint(int id, Vector3 point, float minStep = 0.12f)
        {
            int i = FindIndexById(id);
            if (i < 0) return;

            Trail t = m_Trails[i];
            if (t.path == null) return;
            if (t.path.Count >= MaxPathPoints) return;

            Vector3 last = t.path[t.path.Count - 1];
            if ((point - last).sqrMagnitude < minStep * minStep) return;

            t.path.Add(point);
            m_Trails[i] = t;
        }

        /// <summary>折线光带：把末尾点固定为 point 并停止生长（参数是光带 ID）。</summary>
        public void StopPath(int id, Vector3 finalPoint)
        {
            int i = FindIndexById(id);
            if (i < 0) return;

            Trail t = m_Trails[i];
            if (t.headAge >= 0f) return;

            if (t.path != null)
            {
                Vector3 last = t.path[t.path.Count - 1];
                if ((finalPoint - last).sqrMagnitude > 1e-6f) t.path.Add(finalPoint);
            }

            t.headAge = Time.timeSinceLevelLoad;
            m_Trails[i] = t;
        }

        /// <summary>波前撞墙 / 到达极限：钉住长度，开始残留淡出（参数是光带 ID）。</summary>
        public void StopHead(int id, float finalLength)
        {
            int i = FindIndexById(id);
            if (i < 0) return;

            Trail t = m_Trails[i];
            if (t.headAge >= 0f) return;      // 已经停过了

            t.length  = finalLength;
            t.headAge = Time.timeSinceLevelLoad;
            m_Trails[i] = t;
        }

        public void ClearTrails()
        {
            if (m_Mesh != null) m_Mesh.Clear(false);

            m_Trails.Clear();
            m_FreeSlots.Clear();
            m_NextId = 1;
        }

        // ---------------------------------------------------------------- 渲染

        private void LateUpdate()
        {
            if (!m_Ready) return;
            if (m_Trails.Count == 0)
            {
                m_Mesh.Clear(false);
                return;
            }

            float now = Time.timeSinceLevelLoad;

            Camera cam = Camera.main;
            Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;
            Vector3 camUp  = cam != null ? cam.transform.up      : Vector3.up;

            float bodyFade = Mathf.Max(0.01f, bodyFadeSpeed);
            float headFade = Mathf.Max(0.01f, headFadeSpeed);
            float spacing  = Mathf.Max(0.05f, trailSpacing);

            int quads = 0;

            for (int i = m_Trails.Count - 1; i >= 0; i--)
            {
                Trail tr = m_Trails[i];

                // ★ 已淘汰的槽：直接跳过（槽位等下一次 AddTrail 复用）
                if (!tr.alive) continue;

                float age = now - tr.spawnTime;

                // 波前停止后过了多久（还在推进时为 0）
                float frozen = tr.headAge < 0f ? 0f : Mathf.Max(0f, now - tr.headAge);

                // 整条光带的不透明度由"本体衰减"决定
                float bodyAlpha = tr.initAlpha * Mathf.Exp(-age * bodyFade);

                // 太暗就淘汰
                if (bodyAlpha <= visibleThreshold)
                {
                    // ★★ 这里绝对**不能**再把末尾元素搬过来 ★★
                    //
                    //   原写法：
                    //       m_Trails[i] = m_Trails[m_Trails.Count - 1];
                    //       m_Trails.RemoveAt(m_Trails.Count - 1);
                    //   每淘汰一条，后面所有光带的槽位下标都会前移，
                    //   外部（PuzzleMonster / StoneThrowSystem）保存的下标就全部错位：
                    //     · SetLength 打到别人身上 -> 那条突然变长 / 变短
                    //     · 自己的光带没人推进 -> 停在半路（看起来"很短"）
                    //
                    //   现在改成：只把槽标记成 dead，槽位回收进自由栈，
                    //   下一次 AddTrail 复用；渲染时跳过 dead 槽。
                    tr.alive    = false;
                    tr.path     = null;         // 释放路径列表
                    m_Trails[i] = tr;
                    m_FreeSlots.Push(i);
                    continue;
                }

                // 已经铺出去的部分
                float tailLen = tr.length;
                if (tr.headAge < 0f)
                {
                    // 还在推进：波前位置 = 声速 × 年龄（由 PuzzleMonster 每帧 SetLength，
                    // 这里不再自己推进，保持"撞墙就停"的语义）
                }

                if (tailLen <= 1e-3f) continue;
                if (quads >= MaxQuads) continue;

                // ★ 折线光带（石子那种抛物线轨迹）：沿 path 逐段铺四边形。
                //   每一小段用自己的切向算 up/side，所以整条是**连贯的弧**，
                //   不是若干条首尾相接的直段。
                if (tr.path != null && tr.path.Count >= 2)
                {
                    quads += BuildPathTrail(tr, camPos, camUp, bodyAlpha, frozen,
                                            headFade, quads);
                    continue;
                }

                Vector3 flat    = tr.origin;
                Vector3 flatEnd = tr.origin + tr.dir * tailLen;

                // 面向相机 45° 的四边形基（和声波光带一致）
                Vector3 toCam = camPos - flat;
                if (toCam.sqrMagnitude < 1e-6f) toCam = camUp;
                toCam.Normalize();

                Vector3 up = camUp - tr.dir * Vector3.Dot(camUp, tr.dir);
                if (up.sqrMagnitude < 1e-4f) up = Vector3.up - tr.dir * Vector3.Dot(Vector3.up, tr.dir);
                if (up.sqrMagnitude < 1e-4f) up = Vector3.right;
                up.Normalize();

                Vector3 side = toCam - tr.dir * Vector3.Dot(toCam, tr.dir);
                if (side.sqrMagnitude < 1e-4f) side = Vector3.Cross(tr.dir, up);
                side.Normalize();

                float halfW = Mathf.Max(0.002f, tr.width) * 0.5f;

                int steps = Mathf.Clamp(Mathf.CeilToInt(tailLen / spacing), 1, 400);
                float stepLen = tailLen / steps;

                for (int s = 0; s < steps && quads < MaxQuads; s++)
                {
                    float t0 = s * stepLen;
                    float t1 = t0 + stepLen;

                    Vector3 p0 = tr.origin + tr.dir * t0;
                    Vector3 p1 = tr.origin + tr.dir * t1;

                    // 沿光带的年龄：越靠近波前越"新"
                    float a0 = tr.length - t0;      // 距离波前多远
                    float a1 = tr.length - t1;
                    float age0 = a0 / Mathf.Max(0.01f, 1f);
                    float age1 = a1 / Mathf.Max(0.01f, 1f);

                    // 波前处额外按 headFade 再降一次 ——
                    // 这就是"波前衰减快于本体"的实现：
                    // 越靠前（distToHead 越小）衰减越强，但只在**波前停止后**才起作用，
                    // 推进期间波前本身不应变暗，否则看不到"打出去"的感觉。
                    float h0 = frozen > 0f ? Mathf.Exp(-frozen * headFade) : 1f;
                    float h1 = frozen > 0f ? Mathf.Exp(-frozen * headFade) : 1f;

                    // 本体沿长度的衰减：尾巴比波前暗（用 bodyFade 摊到整条上）
                    float d0 = Mathf.Clamp01(a0 / Mathf.Max(0.01f, tailLen));
                    float d1 = Mathf.Clamp01(a1 / Mathf.Max(0.01f, tailLen));

                    float alpha0 = bodyAlpha * Mathf.Lerp(0.15f, 1f, d0) * h0;
                    float alpha1 = bodyAlpha * Mathf.Lerp(0.15f, 1f, d1) * h1;

                    Color c0 = new Color(tr.color.r, tr.color.g, tr.color.b, Mathf.Clamp01(alpha0));
                    Color c1 = new Color(tr.color.r, tr.color.g, tr.color.b, Mathf.Clamp01(alpha1));

                    int v = quads * 4;
                    m_Verts[v + 0] = p0 + up * halfW;
                    m_Verts[v + 1] = p0 - up * halfW;
                    m_Verts[v + 2] = p1 - up * halfW;
                    m_Verts[v + 3] = p1 + up * halfW;

                    m_Uvs[v + 0] = new Vector2(0f, 0f);
                    m_Uvs[v + 1] = new Vector2(0f, 1f);
                    m_Uvs[v + 2] = new Vector2(1f, 1f);
                    m_Uvs[v + 3] = new Vector2(1f, 0f);

                    m_Cols[v + 0] = c0;
                    m_Cols[v + 1] = c0;
                    m_Cols[v + 2] = c1;
                    m_Cols[v + 3] = c1;

                    quads++;
                }
            }

            m_Mesh.Clear(false);
            if (quads == 0) return;

            int vcount = quads * 4;
            m_Mesh.SetVertices(m_Verts, 0, vcount);
            m_Mesh.SetUVs(0, m_Uvs, 0, vcount, UnityEngine.Rendering.MeshUpdateFlags.Default);
            m_Mesh.SetColors(m_Cols, 0, vcount);
            m_Mesh.SetIndices(m_Indices, 0, quads * 6, MeshTopology.Triangles, 0, true);
        }
    }
}
