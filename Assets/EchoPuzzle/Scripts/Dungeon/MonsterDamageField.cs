using UnityEngine;

namespace EchoPuzzle.Dungeon
{
    /// <summary>
    /// 【地牢模式】怪物主动技能产生的「持续伤害地面」。
    ///
    ///   · 一片贴地的半透明圆形区域（橙红色脉冲发光）；
    ///   · 玩家站在里面时按 tick 间隔持续扣血（走 PlayerHealth.TakeDamage，
    ///     自动尊重无敌帧）；
    ///   · 持续 duration 秒后淡出销毁。
    ///
    /// ★ 由 DungeonMonsterSkills 在施放「毒圈」技能时创建。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MonsterDamageField : MonoBehaviour
    {
        [Header("区域参数")]
        [Tooltip("区域半径（米）")]
        [Min(0.5f)] public float radius = 2.2f;

        [Tooltip("持续时长（秒）")]
        [Min(0.5f)] public float duration = 5f;

        [Tooltip("每次扣血的间隔（秒）")]
        [Min(0.1f)] public float tickInterval = 0.5f;

        [Tooltip("每次扣多少血")]
        [Min(1)] public int tickDamage = 1;

        public Color fieldColor = new Color(1f, 0.45f, 0.1f, 1f);

        [Tooltip("圆盘的细分数。越大越圆（内置 Cylinder 只有 20，这里默认 64）")]
        [Range(12, 128)] public int discSegments = 64;

        // ---------------------------------------------------------------- 运行时

        private float m_DieAt;
        private float m_NextTick;
        private Material m_Mat;
        private PlayerHealth m_PlayerHealth;
        private Vector3 m_Center;
        private float m_BaseAlpha = 0.45f;

        // ---------------------------------------------------------------- 生成

        /// <summary>在 center（世界坐标）铺一片伤害地面。</summary>
        public static MonsterDamageField Spawn(Vector3 center)
        {
            // 贴地：从上往下找地面
            Vector3 probe = center + Vector3.up * 6f;
            float groundY = 0.05f;

            if (Physics.Raycast(probe, Vector3.down, out RaycastHit hit, 12f,
                                ~((1 << 2) | (1 << 4) | (1 << 5)),
                                QueryTriggerInteraction.Ignore))
            {
                groundY = hit.point.y + 0.04f;
            }

            // ★★ 不再用内置 Cylinder ★★
            //
            //   需求："还有一个能铺设一个圆形地面区域造成地板伤害的怪物技能，
            //         这个似乎也不够圆"
            //
            //   ★ Unity 的 PrimitiveType.Cylinder 只有 20 条边，
            //     直径 5 米时每段弦长约 0.8 米 —— 近看是个明显的二十边形。
            //   ★ 现在自己生成一个高细分的贴地圆盘（默认 64 段）。

            var go = new GameObject("[怪物技能] 伤害地面");

            var f = go.AddComponent<MonsterDamageField>();
            f.m_Center = new Vector3(center.x, groundY, center.z);

            go.transform.position = f.m_Center;

            // 自建圆盘网格（半径 = f.radius，贴在本地 y = 0 平面）
            f.BuildDiscMesh();

            // ★ 项目规矩：所有物体挂 Hide（0 = 正常存在于场景）
            Hide hide = go.GetComponent<Hide>();
            if (hide == null) hide = go.AddComponent<Hide>();
            hide.SetHiding(0);

            f.SetupVisual();
            return f;
        }

        /// <summary>
        /// 生成贴地圆盘网格：半径 = radius，位于物体本地 y = 0 平面。
        /// </summary>
        private void BuildDiscMesh()
        {
            int n = Mathf.Clamp(discSegments, 12, 128);

            var verts = new Vector3[n + 1];
            var tris  = new int[n * 3];

            verts[0] = Vector3.zero;                     // 圆心

            for (int i = 0; i < n; i++)
            {
                float ang = i * Mathf.PI * 2f / n;
                verts[i + 1] = new Vector3(Mathf.Cos(ang) * radius, 0f,
                                           Mathf.Sin(ang) * radius);
            }

            for (int i = 0; i < n; i++)
            {
                tris[i * 3 + 0] = 0;
                tris[i * 3 + 1] = i + 1;
                tris[i * 3 + 2] = (i + 1) % n + 1;
            }

            var mesh = new Mesh { name = "MonsterDamageDisc" };
            mesh.SetVertices(new System.Collections.Generic.List<Vector3>(verts));
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();

            var mf = GetComponent<MeshFilter>();
            if (mf == null) mf = gameObject.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;

            var mr = GetComponent<MeshRenderer>();
            if (mr == null) mr = gameObject.AddComponent<MeshRenderer>();

            // 伤害判定走代码（水平距离），碰撞体不需要
            var col = GetComponent<Collider>();
            if (col != null) Destroy(col);

            m_DiscMesh = mesh;
        }

        /// <summary>自建圆盘网格（OnDestroy 里一并销毁）。</summary>
        private Mesh m_DiscMesh;

        private void SetupVisual()
        {
            m_DieAt    = Time.timeSinceLevelLoad + duration;
            m_NextTick = Time.timeSinceLevelLoad + 0.35f;   // 铺下后很快开始第一跳

            Renderer r = GetComponent<Renderer>();
            if (r != null)
            {
                m_Mat = EchoRuntimeMaterials.AimOverlay(fieldColor);
                m_Mat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.LessEqual);
                m_Mat.SetInt("_ZWrite", 0);
                m_Mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                r.sharedMaterial = m_Mat;
            }
        }

        private void OnDestroy()
        {
            if (m_Mat != null) Destroy(m_Mat);
            if (m_DiscMesh != null) Destroy(m_DiscMesh);
        }

        private void Update()
        {
            float now = Time.timeSinceLevelLoad;

            // ---- 持续伤害 ----
            if (now >= m_NextTick && now < m_DieAt)
            {
                m_NextTick = now + Mathf.Max(0.1f, tickInterval);
                TickDamage();
            }

            // ---- 视觉：脉冲 + 尾部淡出 ----
            float remain = m_DieAt - now;

            float pulse = 0.75f + 0.25f * Mathf.Sin(now * 6f);
            float alpha = m_BaseAlpha * pulse;

            if (remain < 0.6f)
                alpha *= Mathf.Clamp01(remain / 0.6f);

            SetAlpha(alpha);

            if (remain <= 0f)
                Destroy(gameObject);
        }

        private void TickDamage()
        {
            if (m_PlayerHealth == null)
            {
                m_PlayerHealth = FindObjectOfType<PlayerHealth>();
                if (m_PlayerHealth == null) return;
            }

            Vector3 p = m_PlayerHealth.transform.position;
            Vector2 a = new Vector2(p.x, p.z);
            Vector2 b = new Vector2(m_Center.x, m_Center.z);

            if (Vector2.Distance(a, b) <= radius)
                m_PlayerHealth.TakeDamage(tickDamage);
        }

        private void SetAlpha(float a)
        {
            if (m_Mat == null) return;

            Color c = fieldColor;
            c.a = Mathf.Clamp01(a);
            m_Mat.SetColor("_Color", c);
            if (m_Mat.HasProperty("_BaseColor"))
                m_Mat.SetColor("_BaseColor", c);
        }
    }
}
