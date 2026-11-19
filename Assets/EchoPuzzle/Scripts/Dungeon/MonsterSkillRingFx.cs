using UnityEngine;

namespace EchoPuzzle.Dungeon
{
    /// <summary>
    /// 【地牢模式】怪物技能的"扩散环"特效 —— **独立**物体，自己管自己的动画。
    ///
    /// ── 为什么不用协程 ──────────────────────────────────
    ///   需求："有很多在场上留下图形的怪物技能，在怪物被击杀后这个图形会卡在
    ///         中间的某个阶段，并留在原地，无法正常消失……
    ///         我希望即使怪物被击杀，它们也能正常播放完动画并消失。"
    ///
    ///   ★ 旧实现是 DungeonMonsterSkills 上的协程。
    ///     怪物一死走 Hide.SetHiding(1) -> SetActive(false)，
    ///     Unity 会**立即中断**该物体上的所有协程 ——
    ///     循环停在中途，后面那句 Destroy(go) 永远跑不到，
    ///     光环就冻结在某个大小/透明度上留在场地里。
    ///
    ///   ✔ 现在它是一个**独立的** GameObject + 自己的 Update()，
    ///     和怪物没有父子/同体关系。怪物死了它照常把动画播完，然后自毁。
    ///
    /// ── 几何 ────────────────────────────────────────────
    ///   ★ 内置 PrimitiveType.Cylinder 只有 20 条边（直径 5 米时每段弦长 0.8 米，
    ///     近看是个明显的二十边形）。这里改成自建的高细分圆盘。
    /// </summary>
    [AddComponentMenu("Echo Puzzle/Dungeon/Monster Skill Ring Fx")]
    public sealed class MonsterSkillRingFx : MonoBehaviour
    {
        [Header("外观")]
        [Tooltip("圆盘细分数。越大越圆（内置 Cylinder 只有 20）")]
        [Range(12, 128)] public int segments = 64;

        [Tooltip("最大不透明度")]
        [Range(0f, 1f)] public float maxAlpha = 0.85f;

        [Header("动画")]
        [Tooltip("起始直径占目标直径的比例")]
        [Range(0.01f, 1f)] public float startScaleRatio = 0.3f;

        // ---------------------------------------------------------------- 运行时

        private float m_T0;
        private float m_Life;
        private float m_TargetRadius;
        private Color m_Color;
        private Material m_Mat;
        private Mesh m_Mesh;

        // ---------------------------------------------------------------- 生成

        /// <summary>
        /// 在 <paramref name="center"/> 播一个从 0.6 倍扩到 1 倍、同时淡出的圆环。
        /// </summary>
        /// <param name="radius">最终半径（米）</param>
        /// <param name="life">整套动画时长（秒）</param>
        public static MonsterSkillRingFx Spawn(Vector3 center, Color color, float radius, float life)
        {
            // 贴地
            float groundY = 0.06f;
            Vector3 probe = center + Vector3.up * 6f;

            if (Physics.Raycast(probe, Vector3.down, out RaycastHit hit, 12f,
                                ~((1 << 2) | (1 << 4) | (1 << 5)),
                                QueryTriggerInteraction.Ignore))
            {
                groundY = hit.point.y + 0.05f;
            }

            var go = new GameObject("[怪物技能] 技能光环");

            // ★ 纯视觉，不进世界快照
            go.hideFlags = HideFlags.DontSave;

            go.transform.position = new Vector3(center.x, groundY, center.z);
            go.transform.SetParent(null, true);      // ★ 不挂在怪物下面

            var fx = go.AddComponent<MonsterSkillRingFx>();
            fx.Init(color, radius, life);

            return fx;
        }

        private void Init(Color color, float radius, float life)
        {
            m_Color        = color;
            m_Color.a      = 1f;
            m_TargetRadius = Mathf.Max(0.05f, radius);
            m_Life         = Mathf.Max(0.05f, life);
            m_T0           = Time.timeSinceLevelLoad;

            BuildDisc();

            Hide hide = GetComponent<Hide>();
            if (hide == null) hide = gameObject.AddComponent<Hide>();
            hide.SetHiding(0);

            Apply(0f);
        }

        // ---------------------------------------------------------------- 网格

        private void BuildDisc()
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

            m_Mesh = new Mesh { name = "MonsterSkillRingDisc" };
            m_Mesh.SetVertices(new System.Collections.Generic.List<Vector3>(verts));
            m_Mesh.SetTriangles(tris, 0);
            m_Mesh.RecalculateBounds();

            var mf = gameObject.AddComponent<MeshFilter>();
            mf.sharedMesh = m_Mesh;

            var mr = gameObject.AddComponent<MeshRenderer>();

            m_Mat = EchoRuntimeMaterials.AimOverlay(m_Color);
            if (m_Mat != null)
            {
                m_Mat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.LessEqual);
                m_Mat.SetInt("_ZWrite", 0);
                m_Mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

                mr.sharedMaterial = m_Mat;
            }

            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows    = false;
        }

        // ---------------------------------------------------------------- 动画

        private void Update()
        {
            float t = Mathf.Clamp01((Time.timeSinceLevelLoad - m_T0) / m_Life);

            Apply(t);

            if (t >= 1f) Destroy(gameObject);
        }

        private void Apply(float t)
        {
            // ★★ 缩放 = 半径 × 扩比，**不能**再乘 2 ★★
            //
            //   本组件的网格顶点是**单位半径**（cos/sin 没乘任何系数），
            //   所以 localScale = 半径 时，画出来半径正好是目标值。
            //
            //   ★ 之前这里写的是 m_TargetRadius * 2f —— 那是从旧实现
            //     "内置 Cylinder" 搬过来的：Cylinder 的直径是 1、半径 0.5，
            //     所以要乘 2 才等于直径。换成自建单位半径网格后这个 2 就多余了，
            //     结果所有扩散环都画成了设定值的 2 倍大。
            float ratio = Mathf.Lerp(startScaleRatio, 1f, t);
            float d     = m_TargetRadius * ratio;

            transform.localScale = new Vector3(d, 1f, d);

            if (m_Mat == null) return;

            Color c = m_Color;
            c.a = Mathf.Clamp01(1f - t) * maxAlpha;

            m_Mat.SetColor("_Color", c);
            if (m_Mat.HasProperty("_BaseColor"))
                m_Mat.SetColor("_BaseColor", c);
        }

        private void OnDestroy()
        {
            if (m_Mat  != null) Destroy(m_Mat);
            if (m_Mesh != null) Destroy(m_Mesh);
        }
    }
}
