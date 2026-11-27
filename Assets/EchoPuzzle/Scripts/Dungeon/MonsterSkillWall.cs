using UnityEngine;

namespace EchoPuzzle.Dungeon
{
    /// <summary>
    /// 【地牢模式】怪物主动技能产生的「有生命值的墙」。
    ///
    /// ── 它挡谁、不挡谁 ────────────────────────────────────
    ///   ✔ 挡**玩家子弹**（撞上会扣墙的血，然后被挡下）
    ///   ✔ 挡**玩家移动**（对玩家是实体墙）
    ///   ✘ 不挡**怪物本体**（怪物可以自由穿过自己的墙）
    ///   ✘ 不挡**怪物子弹**
    ///   ✘ 不挡**其它怪物技能**
    ///
    /// ── 怎么做到的 ────────────────────────────────────────
    ///   墙本身有 BoxCollider（玩家子弹走 Physics.SphereCast，需要它是实体碰撞体）。
    ///   创建之后立刻对所有**怪物**调用 Physics.IgnoreCollision，
    ///   让"怪物碰撞体 ↔ 墙碰撞体"这一对互相穿透。
    ///
    ///   ★ 怪物子弹不走物理碰撞（MonsterBulletRenderer 自己 Raycast + 白名单跳过），
    ///     所以不需要额外处理 —— ShouldPassThrough 里已经放行 MonsterSkillWall。
    ///
    ///   ★ 新刷出来的怪物也要补一次 IgnoreCollision，
    ///     所以墙每帧检查一下有没有"还没登记"的怪物。
    ///
    /// ★ 由 DungeonMonsterSkills 在施放「造墙」技能时创建。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MonsterSkillWall : MonoBehaviour
    {
        [Header("墙体参数")]
        [Tooltip("墙的总血量（玩家子弹伤害会直接扣它）")]
        [Min(1f)] public float maxHealth = 24f;

        [Tooltip("★ 墙耐久度每进入新一层的倍率。需求：1.25\n\n" +
                 "★ 第 1 层 ×1.0，第 2 层 ×1.25，第 3 层 ×1.5625 …")]
        [Min(1f)] public float healthPerStage = 1.25f;

        [Tooltip("墙的存在时长（秒），超时自动消失")]
        [Min(1f)] public float lifetime = 10f;

        [Tooltip("墙体基础不透明度")]
        [Range(0f, 1f)] public float baseAlpha = 0.7f;

        public Color wallColor = new Color(0.65f, 0.35f, 1f, 1f);

        // ---------------------------------------------------------------- 运行时

        private float m_Health;
        private float m_DieAt;
        private Material m_Mat;
        private Vector3 m_BaseScale;
        private bool m_Dying;
        private float m_DieTimer;

        /// <summary>已经和本墙互相穿透的怪物碰撞体（避免每帧重复调用）。</summary>
        private readonly System.Collections.Generic.HashSet<Collider> m_IgnoredMonsters
            = new System.Collections.Generic.HashSet<Collider>();

        /// <summary>本墙自己的碰撞体。</summary>
        private Collider m_SelfCollider;

        /// <summary>多久重新扫一次"有没有新刷出来的怪物"（秒）。</summary>
        private const float MonsterRescanInterval = 0.5f;
        private float m_NextRescan;

        /// <summary>
        /// 含层数缩放后的墙耐久度。
        ///
        /// ★ 需求："怪物墙的耐久度也会在每次进入下一层时 ×1.25。"
        /// </summary>
        public float EffectiveMaxHealth
        {
            get
            {
                int stage = DungeonBuffState.StageIndex;

                float scale = 1f;
                for (int i = 1; i < stage; i++) scale *= healthPerStage;

                return Mathf.Max(1f, Mathf.Round(maxHealth * scale * 1000f) / 1000f);
            }
        }

        /// <summary>血量比例（0~1），给透明度用。</summary>
        public float Health01
        {
            get
            {
                float mx = EffectiveMaxHealth;
                return mx > 0f ? Mathf.Clamp01(m_Health / mx) : 0f;
            }
        }

        // ---------------------------------------------------------------- 生成

        /// <summary>
        /// 在怪物与玩家之间竖一面墙。
        /// </summary>
        /// <param name="from">怪物位置</param>
        /// <param name="dir">怪物朝玩家的水平方向</param>
        /// <param name="distance">墙放在离怪物多远的位置</param>
        /// <param name="distToPlayer">怪物到玩家的水平距离</param>
        public static MonsterSkillWall Spawn(Vector3 from, Vector3 dir,
                                             float distance, float distToPlayer)
        {
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-6f) dir = Vector3.forward;
            dir.Normalize();

            // ★ 别把墙立在玩家身上：夹在怪物与玩家之间靠中间的位置
            float d = Mathf.Clamp(distance, 1.2f, Mathf.Max(1.2f, distToPlayer - 0.8f));

            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "[怪物技能] 挡弹墙";

            var w = go.AddComponent<MonsterSkillWall>();
            w.m_BaseScale = new Vector3(5f, 2.6f, 1f);
            go.transform.localScale = w.m_BaseScale;

            // 贴地：墙中心离地半高（墙底落在 y=0 的地面上）
            Vector3 pos = from + dir * d;
            pos.y = w.m_BaseScale.y * 0.5f;
            go.transform.position = pos;

            go.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);

            // ★ 项目规矩：所有物体挂 Hide（0 = 正常存在于场景）
            Hide hide = go.GetComponent<Hide>();
            if (hide == null) hide = go.AddComponent<Hide>();
            hide.SetHiding(0);

            w.SetupVisual();

            // ★★ 让怪物从一开始就能穿过这面墙 ★★
            w.TrackSelfCollider();
            w.IgnoreAllMonsters();
            w.m_NextRescan = Time.timeSinceLevelLoad + MonsterRescanInterval;
            return w;
        }

        /// <summary>缓存自己的碰撞体。</summary>
        private void TrackSelfCollider()
        {
            if (m_SelfCollider == null) m_SelfCollider = GetComponent<Collider>();
        }

        /// <summary>
        /// 让场上**所有**怪物碰撞体与本墙互相穿透。
        ///
        /// ★ 只对怪物做 —— 玩家不在忽略名单里，所以玩家仍然被这面墙挡住。
        /// ★ 幂等：已经登记过的 Collider 直接跳过。
        /// </summary>
        private void IgnoreAllMonsters()
        {
            TrackSelfCollider();
            if (m_SelfCollider == null) return;

            // 怪物的碰撞体来自 PuzzleMonster / DungeonEnemy 这一套组件
            var monsters = FindObjectsOfType<PuzzleMonster>(true);

            for (int i = 0; i < monsters.Length; i++)
            {
                PuzzleMonster mon = monsters[i];
                if (mon == null) continue;
                if (mon.DungeonIsDead) continue;

                Collider[] cols = mon.GetComponentsInChildren<Collider>(true);

                for (int k = 0; k < cols.Length; k++)
                {
                    Collider c = cols[k];
                    if (c == null || c == m_SelfCollider) continue;
                    if (!m_IgnoredMonsters.Add(c)) continue;   // 已经处理过

                    Physics.IgnoreCollision(m_SelfCollider, c, true);
                }
            }
        }

        private void SetupVisual()
        {
            // ★★ 耐久度按层数缩放 ★★
            //
            //   需求："我希望怪物墙的耐久度也会在每次进入下一层时 *1.25。"
            //
            //   ★ 层号来源和怪物血量一致（DungeonBuffState.StageIndex），
            //     第 1 层 ×1.0，第 2 层 ×1.25，第 3 层 ×1.5625 …
            m_Health = EffectiveMaxHealth;
            m_DieAt  = Time.timeSinceLevelLoad + lifetime;

            Renderer r = GetComponent<Renderer>();
            if (r != null)
            {
                // 真实 3D 物体：ZTest LEqual + Transparent 队列（和怪物子弹同一套做法）
                m_Mat = EchoRuntimeMaterials.AimOverlay(wallColor);
                m_Mat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.LessEqual);
                m_Mat.SetInt("_ZWrite", 0);
                m_Mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                r.sharedMaterial = m_Mat;
            }

            UpdateVisual();
        }

        private void OnDestroy()
        {
            if (m_Mat != null) Destroy(m_Mat);
        }

        private void Update()
        {
            if (m_Dying)
            {
                m_DieTimer += Time.deltaTime;

                float t = Mathf.Clamp01(m_DieTimer / 0.3f);

                transform.localScale = m_BaseScale * (1f - t * 0.85f);
                SetAlpha(baseAlpha * (1f - t));

                if (t >= 1f) Destroy(gameObject);
                return;
            }

            UpdateVisual();

            // ★ 定期补登记：怪物是新刷出来的 / 之前没在场上，
            //   都要让它们也能穿过这面墙。
            if (Time.timeSinceLevelLoad >= m_NextRescan)
            {
                m_NextRescan = Time.timeSinceLevelLoad + MonsterRescanInterval;
                IgnoreAllMonsters();
            }

            // 超时 -> 开始消散
            if (Time.timeSinceLevelLoad >= m_DieAt)
                BeginDying();
        }

        private void UpdateVisual()
        {
            // 血越多越实，血越少越透明（顺便给玩家反馈“快打穿了”）
            SetAlpha(Mathf.Lerp(0.05f, baseAlpha, Health01));
        }

        private void SetAlpha(float a)
        {
            if (m_Mat == null) return;

            Color c = wallColor;
            c.a = Mathf.Clamp01(a);
            m_Mat.SetColor("_Color", c);
            if (m_Mat.HasProperty("_BaseColor"))
                m_Mat.SetColor("_BaseColor", c);
        }

        // ---------------------------------------------------------------- 承伤

        /// <summary>
        /// 玩家子弹打在墙上（由 BulletTrailRenderer 在撞墙判定时调用）。
        /// </summary>
        public void AbsorbPlayerBullet(float damage)
        {
            if (m_Dying) return;

            m_Health -= Mathf.Max(0f, damage);

            if (m_Health <= 0f)
                BeginDying();
        }

        private void BeginDying()
        {
            if (m_Dying) return;
            m_Dying    = true;
            m_DieTimer = 0f;

            // 死掉的墙不再挡子弹 / 不再挡人
            Collider col = GetComponent<Collider>();
            if (col != null) Destroy(col);
        }
    }
}