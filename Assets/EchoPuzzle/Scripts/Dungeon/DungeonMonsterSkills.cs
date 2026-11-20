using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace EchoPuzzle.Dungeon
{
    /// <summary>
    /// 【地牢模式】怪物技能系统（MonsterWaveManager 刷怪时自动添加）。
    /// 主动技能：按「模板 + 层数」确定技能池（不再随机抽技能），每次攻击从池里随机触发一个；
    ///           第 11 层起每次攻击同时触发两个不同的技能。
    /// 被动技能：刷出时按模板优先级、以原有概率链式获取，最多 4 个。
    /// ★ 只在地牢模式创建，探秘模式没有本组件。
    /// ★ 技能名对照：扫射=Volley、爆炸弹=ExplosiveShot、造墙=Wall、火焰地板=GroundField、
    ///   震波=Nova、群体回血=HealAllies、突进=Dash、子弹圈=BulletRing；
    ///   双倍生命=DoubleHealth、额外伤害=ExtraDamage、快速开火=FastFire、伤害光环=DamageAura。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DungeonMonsterSkills : MonoBehaviour
    {
        public enum SkillId
        {
            Volley,        // 扫射：一大串子弹
            ExplosiveShot, // 爆炸弹
            Wall,          // 造墙
            GroundField,   // 火焰地板
            Nova,          // 震波
            HealAllies,    // 群体回血
            Dash,          // 突进
            BulletRing     // 子弹圈：360° 慢速弹，撞墙反弹
        }

        public enum PassiveId { DoubleHealth, ExtraDamage, FastFire, DamageAura }

        /// <summary>怪物模板：输出型 / 近战型 / 控场型 / 支援型。</summary>
        public enum MonsterTemplate { Damage, Melee, Control, Support }

        // ---------------------------------------------------------------- 参数

        [Header("主动技能参数")]
        [Tooltip("爆炸弹：直伤 = 普通子弹伤害 + 该值")]
        public int explosiveBonusDamage = 1;

        [Tooltip("爆炸弹：爆炸半径（米）")]
        [Min(0.5f)] public float explosiveBlastRadius = 4f;

        [Tooltip("爆炸弹：爆炸溅射伤害")]
        public int explosiveBlastDamage = 2;

        [Tooltip("★ 爆炸弹的**子弹半径**（米）。同时决定视觉大小和 SphereCast 碰撞半径。\n\n" +
                 "需求：1")]
        [Min(0.01f)] public float explosiveBulletRadius = 1f;

        [Tooltip("爆炸弹的爆炸视觉效果半径（米）。\n" +
                 "★ 内部换算成缩放倍率 = 这个值 ÷ 子弹半径；默认值与当前 Inspector 数值一致。")]
        [Min(0.1f)] public float explosiveVisualRadius = 4f;

        [Tooltip("造墙：墙离怪物多远（米）")]
        [Min(1f)] public float wallDistance = 2.4f;

        [Tooltip("伤害地面：铺在怪物到玩家距离的这个比例处")]
        [Range(0.2f, 1f)] public float groundFieldAt = 0.55f;

        [Tooltip("伤害地面：最大铺设距离（米）")]
        [Min(1f)] public float groundFieldMaxDistance = 4.5f;

        [Tooltip("Nova：伤害半径（米）")]
        [Min(1f)] public float novaRadius = 8f;

        [Tooltip("Nova：对范围内玩家造成的伤害")]
        public int novaDamage = 2;

        [Tooltip("群体回血：影响半径（米）")]
        [Min(1f)] public float healRadius = 8f;

        [Tooltip("群体回血：每只怪回复的**基础**血量（第 1 层）")]
        [Min(1f)] public float healAmount = 12f;

        [Tooltip("★ 群体回血每进入新一层的倍率。需求：1.25\n\n" +
                 "★ 第 1 层 ×1.0，第 2 层 ×1.25，第 3 层 ×1.5625 …")]
        [Min(1f)] public float healPerStage = 1.25f;

        [Tooltip("突进：玩家超过该距离（米）就不突进")]
        [Min(2f)] public float dashMaxDistance = 12f;

        [Tooltip("突进：碰到玩家造成的伤害")]
        public int dashDamage = 3;

        [Tooltip("突进：速度倍率（乘怪物基础移速）")]
        [Min(1f)] public float dashSpeedMultiplier = 4f;

        [Tooltip("突进：最长持续时间（秒）")]
        [Min(0.1f)] public float dashMaxDuration = 1f;

        [Header("子弹圈参数")]
        [Tooltip("子弹圈：一次 360° 发射多少发")]
        [Range(4, 32)] public int bulletRingCount = 12;

        [Tooltip("子弹圈：飞行速度 = 怪物基础弹速 × 该倍率（慢速）")]
        [Min(0.05f)] public float bulletRingSpeedMultiplier = 0.25f;

        [Tooltip("子弹圈：撞墙最多反弹几次")]
        [Range(0, 5)] public int bulletRingBounces = 2;

        [Header("被动技能参数")]
        [Tooltip("基础获得概率（1/8）。每 3 层 +1/8，封顶 1/2")]
        [Range(0f, 1f)] public float passiveBaseChance = 0.125f;

        [Tooltip("概率封顶值")]
        [Range(0f, 1f)] public float passiveMaxChance = 0.5f;

        [Tooltip("DamageAura：影响半径（米）")]
        [Min(1f)] public float auraRadius = 3.5f;

        [Tooltip("DamageAura：每跳伤害")]
        public int auraDamage = 1;

        [Tooltip("DamageAura：每跳间隔（秒）")]
        [Min(0.2f)] public float auraInterval = 1f;

        [Tooltip("★ 光环可视化的颜色。需求：纯红")]
        public Color auraVisualColor = new Color(1f, 0f, 0f, 1f);

        [Tooltip("★ 光环可视化的不透明度。\n\n" +
                 "★ 注意：这个值**直接**就是材质的不透明度。\n" +
                 "  0 = 完全不画。")]
        [Range(0f, 1f)] public float auraVisualAlpha = 0.1f;

        /// <summary>
        /// 含层数缩放后的群体回血量。
        ///
        /// ★ 需求："怪物有个群体回血的技能，这个回血的数值应当在每进入下一层时乘 1.25。"
        ///
        ///   ★ 和怪物血量用同一个层数（DungeonBuffState.StageIndex），
        ///     所以"越往后血越厚、回得也越多"，比例保持一致。
        /// </summary>
        public float EffectiveHealAmount
        {
            get
            {
                int stage = Dungeon.DungeonBuffState.StageIndex;

                float scale = 1f;
                for (int i = 1; i < stage; i++) scale *= healPerStage;

                return Mathf.Max(1f, Mathf.Round(healAmount * scale * 1000f) / 1000f);
            }
        }

        // ---------------------------------------------------------------- 运行时

        public readonly List<SkillId> Actives = new List<SkillId>(3);
        public readonly List<PassiveId> Passives = new List<PassiveId>(4);

        /// <summary>本怪物的模板（输出 / 近战 / 控场 / 支援），由刷怪时随机指定。</summary>
        public MonsterTemplate Template { get; private set; }

        private DungeonEnemy  m_Enemy;
        private PuzzleMonster m_Monster;
        private PlayerHealth  m_PlayerHealth;
        private NavMeshAgent  m_Agent;
        private float         m_NextAuraTick;
        private bool          m_Rolled;

        /// <summary>四种模板的被动获取优先级：随机判定成功时，取当前还没拿到的第一个。</summary>
        private static readonly PassiveId[][] s_PassivePriority =
        {
            // 输出型：额外伤害、快速开火、伤害光环、双倍生命
            new[] { PassiveId.ExtraDamage, PassiveId.FastFire, PassiveId.DamageAura, PassiveId.DoubleHealth },
            // 近战型：伤害光环、双倍生命、额外伤害、快速开火
            new[] { PassiveId.DamageAura, PassiveId.DoubleHealth, PassiveId.ExtraDamage, PassiveId.FastFire },
            // 控场型：快速开火、双倍生命、伤害光环、额外伤害
            new[] { PassiveId.FastFire, PassiveId.DoubleHealth, PassiveId.DamageAura, PassiveId.ExtraDamage },
            // 支援型：双倍生命、快速开火、伤害光环、额外伤害
            new[] { PassiveId.DoubleHealth, PassiveId.FastFire, PassiveId.DamageAura, PassiveId.ExtraDamage },
        };

        // ---------------------------------------------------------------- 刷怪骰子

        /// <summary>
        /// 兼容旧调用：不指定模板时随机选一种。
        /// ★ 必须在 DungeonResetHealth 之前调用（让 ×2 血量生效）。
        /// </summary>
        public void RollOnSpawn(DungeonEnemy enemy, PuzzleMonster monster, int stageIndex)
        {
            RollOnSpawn(enemy, monster, stageIndex,
                (MonsterTemplate)Random.Range(0, System.Enum.GetValues(typeof(MonsterTemplate)).Length));
        }

        /// <summary>
        /// 刷出时确定模板 / 主动技能池 / 被动骰子。
        /// ★ 必须在 DungeonResetHealth 之前调用（让 ×2 血量生效）。
        /// </summary>
        public void RollOnSpawn(DungeonEnemy enemy, PuzzleMonster monster, int stageIndex,
                                MonsterTemplate template)
        {
            m_Enemy   = enemy;
            m_Monster = monster;
            m_Rolled  = true;
            Template  = template;

            Actives.Clear();
            Actives.AddRange(BuildActivePool(template, stageIndex));

            Passives.Clear();

            // ★ 概率公式沿用现有代码，一行未改：每 3 层提升一次，封顶 1/2。
            float chance = Mathf.Min(passiveMaxChance,
                passiveBaseChance * (1 + Mathf.Max(0, stageIndex - 1) / 3));

            while (Passives.Count < 4 && Random.value < chance)
            {
                PassiveId p = PickUnusedPassive();
                if ((int)p < 0) break;

                Passives.Add(p);
                ApplyPassive(p);
            }

            if (Passives.Count > 0)
                Debug.Log($"[怪物技能] {name} {Template} 被动 x{Passives.Count}: {string.Join(", ", Passives)}（层 {stageIndex}，概率 {chance:0.###}）");
        }

        /// <summary>
        /// 按模板和层数返回固定主动技能池（每次攻击从池里随机选，而不是刷怪时随机抽技能）。
        /// 分层：1-2 / 3-5 / 6-10 / 11+。
        /// </summary>
        private static SkillId[] BuildActivePool(MonsterTemplate template, int stageIndex)
        {
            if (stageIndex <= 2)
            {
                switch (template)
                {
                    case MonsterTemplate.Damage:  return new[] { SkillId.Volley };
                    case MonsterTemplate.Melee:   return new[] { SkillId.Nova };
                    case MonsterTemplate.Control: return new[] { SkillId.Wall };
                    default:                      return new[] { SkillId.HealAllies };
                }
            }

            if (stageIndex <= 5)
            {
                switch (template)
                {
                    case MonsterTemplate.Damage:  return new[] { SkillId.Volley };
                    case MonsterTemplate.Melee:   return new[] { SkillId.Nova, SkillId.Dash };
                    case MonsterTemplate.Control: return new[] { SkillId.Wall, SkillId.GroundField };
                    default:                      return new[] { SkillId.HealAllies };
                }
            }

            if (stageIndex <= 10)
            {
                switch (template)
                {
                    case MonsterTemplate.Damage:
                        return new[] { SkillId.Volley, SkillId.ExplosiveShot };
                    case MonsterTemplate.Melee:
                        return new[] { SkillId.Nova, SkillId.Dash };
                    case MonsterTemplate.Control:
                        return new[] { SkillId.Wall, SkillId.GroundField, SkillId.BulletRing };
                    default:
                        return new[] { SkillId.HealAllies, SkillId.Wall, SkillId.Volley };
                }
            }

            // 11 层及以后（每次攻击双技能，在 TriggerRandomSkill 里处理）
            switch (template)
            {
                case MonsterTemplate.Damage:
                    return new[] { SkillId.Volley, SkillId.ExplosiveShot, SkillId.BulletRing };
                case MonsterTemplate.Melee:
                    return new[] { SkillId.Nova, SkillId.Dash, SkillId.Volley };
                case MonsterTemplate.Control:
                    return new[] { SkillId.Wall, SkillId.GroundField, SkillId.BulletRing };
                default:
                    return new[] { SkillId.HealAllies, SkillId.Wall, SkillId.Volley };
            }
        }

        /// <summary>按当前模板的优先级取第一个还没获得的被动。</summary>
        private PassiveId PickUnusedPassive()
        {
            PassiveId[] priority = s_PassivePriority[(int)Template];

            for (int i = 0; i < priority.Length; i++)
                if (!Passives.Contains(priority[i]))
                    return priority[i];

            return (PassiveId)(-1);
        }

        private void ApplyPassive(PassiveId p)
        {
            switch (p)
            {
                case PassiveId.DoubleHealth:
                    if (m_Monster != null) m_Monster.dungeonMaxHealth *= 2f;
                    break;

                case PassiveId.ExtraDamage:
                    if (m_Enemy != null) m_Enemy.bulletDamage += 1;
                    break;

                case PassiveId.FastFire:
                    if (m_Enemy != null)
                    {
                        m_Enemy.fireInterval *= 0.5f;
                        m_Enemy.fireWindup   *= 0.5f;
                    }
                    break;
            }
        }

        // ---------------------------------------------------------------- 触发

        /// <summary>
        /// 开火：从本模板的技能池里随机触发技能（蓄力结束时由 DungeonEnemy 调用）。
        /// ★ 第 11 层起每次攻击同时释放两个不同的技能。
        /// </summary>
        public void TriggerRandomSkill()
        {
            if (!m_Rolled || Actives.Count == 0)
            {
                if (m_Enemy != null) m_Enemy.FireVolley();
                return;
            }

            int first = Random.Range(0, Actives.Count);
            FireSkill(Actives[first]);

            if (Dungeon.DungeonBuffState.StageIndex >= 11 && Actives.Count >= 2)
            {
                int second = Random.Range(0, Actives.Count - 1);
                if (second >= first) second++;   // 跳过第一个，保证两个技能不同
                FireSkill(Actives[second]);
            }
        }

        private void FireSkill(SkillId id)
        {
            switch (id)
            {
                case SkillId.Volley:        DoVolley();        break;
                case SkillId.ExplosiveShot: DoExplosiveShot(); break;
                case SkillId.Wall:          DoWall();          break;
                case SkillId.GroundField:   DoGroundField();   break;
                case SkillId.Nova:          DoNova();          break;
                case SkillId.HealAllies:    DoHealAllies();    break;
                case SkillId.Dash:          DoDash();          break;
                case SkillId.BulletRing:    DoBulletRing();    break;
            }
        }

        // ---------------------------------------------------------------- 主动技能

        private void DoVolley()
        {
            if (m_Enemy != null) m_Enemy.FireVolley();
        }

        private void DoExplosiveShot()
        {
            Vector3 muzzle = GetMuzzle(out bool ok);
            if (!ok) { DoVolley(); return; }

            Vector3 dir = (GetAimPoint() - muzzle).normalized;
            if (dir.sqrMagnitude < 1e-6f) { DoVolley(); return; }
            dir.Normalize();

            var r = MonsterBulletRenderer.Instance;
            if (r == null)
            {
                var go = new GameObject("MonsterBulletRenderer");
                r = go.AddComponent<MonsterBulletRenderer>();
            }

            MonsterBulletRenderer.Body b = r.Fire(muzzle, dir, m_Enemy);
            if (b == null) return;

            // 淡入淡出 / 外观参数与 DungeonMonsterBullet.Spawn 同款
            b.sonarDetectRadius = m_Enemy.bulletSonarDetectRadius;
            b.fadeInSeconds     = DungeonMonsterBullet.fadeInSeconds;
            b.holdSeconds       = DungeonMonsterBullet.holdSeconds;
            b.fadeOutSeconds    = DungeonMonsterBullet.fadeOutSeconds;
            b.explodeSeconds    = DungeonMonsterBullet.explodeSeconds;
            b.maxLifetime       = DungeonMonsterBullet.maxLifetime;

            // ★★ 视觉大小 / 碰撞半径：1 米 ★★
            //
            //   需求："碰撞半径1m，怪物子弹的视觉大小单独改成半径1m"
            //
            //   ★ selfRadius 同时用于渲染（球多大）和 SphereCast（撞多远），
            //     所以"看起来多大就撞多大"，不会再出现
            //     "贴着墙飞过去却没炸" 或 "还没碰到就炸了"。
            //   ★ 这是**爆炸弹专属**的，普通子弹仍用渲染器的全局 radius。
            b.selfRadius = explosiveBulletRadius;

            // ★★ 爆炸视觉效果半径：取 explosiveVisualRadius（当前 4 米）★★
            //
            //   视觉半径 = selfRadius × explodeScaleMultiplier
            //   所以倍率 = 目标半径 ÷ 子弹半径（当前 4 / 1 = 4）。
            //   ★ 用反推而不是写死倍率，以后改子弹半径也不会错位。
            b.explodeScaleMultiplier = Mathf.Max(1f,
                explosiveVisualRadius / Mathf.Max(0.01f, explosiveBulletRadius));

            // 爆炸弹专属：更快、更高伤、橙色、范围爆炸
            b.speed       = Mathf.Max(1f, m_Enemy.bulletSpeed * 1.25f);
            b.damage      = m_Enemy.bulletDamage + Mathf.Max(0, explosiveBonusDamage);
            b.color       = new Color(1f, 0.55f, 0.1f, 1f);
            b.explosive   = true;
            b.blastRadius = explosiveBlastRadius;
            b.blastDamage = Mathf.Max(1, explosiveBlastDamage);
        }

        private void DoWall()
        {
            Vector3 dir = GetAimDir(out bool ok);
            if (!ok) return;

            Vector3 self = transform.position;
            MonsterSkillWall.Spawn(self, dir, wallDistance,
                                   HorizontalDist(self, PlayerPos()));
        }

        private void DoGroundField()
        {
            Vector3 dir = GetAimDir(out bool ok);
            if (!ok) return;

            Vector3 self = transform.position;
            float dist   = HorizontalDist(self, PlayerPos());

            float d = Mathf.Clamp(dist * groundFieldAt, 1.5f, groundFieldMaxDistance);

            MonsterDamageField.Spawn(self + dir * d);
        }

        private void DoNova()
        {
            Vector3 self = transform.position;

            PlayerHealth ph = GetPlayerHealth();
            if (ph != null && HorizontalDist(self, ph.transform.position) <= novaRadius)
            {
                ph.TakeDamage(novaDamage);
                BloodSplashSonar.Emit(ph.transform.position, Vector3.up);
            }

            // ★ RingRoutine 现在是普通方法（内部创建一个独立特效物体），
            //   不再走 StartCoroutine —— 那样怪物一死 SetActive(false)
            //   就会把协程掐断，特效卡在半途。
            RingRoutine(self, new Color(1f, 0.3f, 0.2f, 1f), novaRadius, 0.4f);
        }

        private void DoHealAllies()
        {
            PuzzleMonster[] all = FindObjectsOfType<PuzzleMonster>(true);

            int healed = 0;

            for (int i = 0; i < all.Length; i++)
            {
                PuzzleMonster m = all[i];
                if (m == null || m.DungeonIsDead) continue;
                if (m.DungeonHealth >= m.DungeonMaxHealth) continue;
                if (HorizontalDist(transform.position, m.transform.position) > healRadius) continue;

                m.DungeonHeal(EffectiveHealAmount);
                healed++;
            }

            // ★ 同上：普通方法调用，不用 StartCoroutine
            RingRoutine(transform.position, new Color(0.3f, 1f, 0.4f, 1f),
                        healRadius, 0.5f);

            if (healed > 0)
                Debug.Log($"[怪物技能] {name} 群体回血：治疗 {healed} 只，每只 {EffectiveHealAmount} 点");
        }

        private void DoDash()
        {
            float dist = HorizontalDist(transform.position, PlayerPos());

            if (dist > dashMaxDistance || dist < 0.5f) { DoVolley(); return; }

            StartCoroutine(DashRoutine());
        }

        /// <summary>
        /// 子弹圈：以怪物为中心 360° 均匀发射一圈慢速子弹。
        /// 每发子弹的撞墙反弹次数由 MonsterBulletRenderer.Body.bouncesLeft 控制。
        /// </summary>
        private void DoBulletRing()
        {
            if (m_Enemy == null) return;

            MonsterBulletRenderer r = MonsterBulletRenderer.Instance;
            if (r == null)
            {
                var go = new GameObject("MonsterBulletRenderer");
                r = go.AddComponent<MonsterBulletRenderer>();
            }

            int count = Mathf.Max(4, bulletRingCount);
            float speed = Mathf.Max(0.5f,
                m_Enemy.bulletSpeed * Mathf.Max(0.05f, bulletRingSpeedMultiplier));

            // 抬高一点，避免子弹一生成就碰到地面
            Vector3 origin = transform.position + Vector3.up * 0.8f;

            for (int i = 0; i < count; i++)
            {
                float a = i * Mathf.PI * 2f / count;
                Vector3 dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));

                MonsterBulletRenderer.Body b = r.Fire(origin, dir, m_Enemy);
                if (b == null) continue;

                b.speed        = speed;
                b.color        = new Color(0.55f, 0.35f, 1f, 1f);
                b.bouncesLeft  = Mathf.Max(0, bulletRingBounces);
            }
        }

        private IEnumerator DashRoutine()
        {
            if (m_Enemy == null) yield break;

            m_Enemy.IsSkillDashing = true;

            float baseSpeed = m_Monster != null ? m_Monster.moveSpeed : 2.2f;

            // 复用 PuzzleMonster 的 NavMesh -> CharacterController 移动管道，
            // 临时把 agent 速度拉满，寻路绕墙自动成立
            if (m_Agent == null) m_Agent = GetComponent<NavMeshAgent>();
            if (m_Agent != null) m_Agent.speed = Mathf.Max(6f, baseSpeed * dashSpeedMultiplier);

            float endAt = Time.timeSinceLevelLoad + dashMaxDuration;
            PlayerHealth ph = GetPlayerHealth();
            bool hitPlayer = false;

            while (Time.timeSinceLevelLoad < endAt)
            {
                // 每帧把目标定成玩家当前位置（追踪式突进）
                // ★ DungeonSetMoveTarget / ClearMoveTarget 定义在 PuzzleMonster 上
                m_Monster.DungeonSetMoveTarget(PlayerPos());

                if (!hitPlayer && ph != null &&
                    HorizontalDist(transform.position, ph.transform.position) <= 1.6f)
                {
                    hitPlayer = true;
                    ph.TakeDamage(dashDamage);
                    BloodSplashSonar.Emit(ph.transform.position, Vector3.up);
                    break;
                }

                yield return null;
            }

            if (m_Agent != null) m_Agent.speed = baseSpeed;
            if (m_Monster != null) m_Monster.DungeonClearMoveTarget();
            m_Enemy.IsSkillDashing = false;
        }

        // ---------------------------------------------------------------- 被动：伤害光环

        private void Update()
        {
            if (!Passives.Contains(PassiveId.DamageAura)) return;

            // ★★ 怪物已死 / 被隐藏 -> 把光环还回去 ★★
            //
            //   需求："在怪物被击杀后这个光圈虽然无法造成伤害却仍然停留在场地上。"
            //
            //   ★ 原因：地牢模式怪物死亡走的是 Hide.SetHiding(1) -> SetActive(false)，
            //     **不会**触发 OnDestroy，所以 OnDestroy 里那句 ReleaseDisc 永远跑不到。
            //   ★ 而 Update 一旦因为 SetActive(false) 停了，也没人再更新它。
            //     现在在 Update 里先做一次"该不该存在"的检查 ——
            //     但 SetActive(false) 之后 Update 根本不会跑，所以还得在
            //     OnDisable 里也还一次（见下面）。
            if (m_Monster != null && (m_Monster.DungeonIsDead || !gameObject.activeInHierarchy))
            {
                ReleaseAuraVisual();
                return;
            }

            // ★ 需求："有这个光环的怪物的周围会出现一圈相同范围的纯红色圆形，
            //         并且其不透明度为 0.1。"
            UpdateAuraVisual();

            float now = Time.timeSinceLevelLoad;
            if (now < m_NextAuraTick) return;

            m_NextAuraTick = now + auraInterval;

            PlayerHealth ph = GetPlayerHealth();
            if (ph == null) return;

            if (HorizontalDist(transform.position, ph.transform.position) <= auraRadius)
                ph.TakeDamage(auraDamage);
        }

        // ---------------------------------------------------------------- 视觉：光环常驻圆

        /// <summary>光环可视化用的那个圆盘（从 BulletBlastRenderer 池里借的）。</summary>
        private object m_AuraVisual;
        private bool   m_AuraRented;

        /// <summary>
        /// 维护脚下一圈"纯红、不透明度 0.1"的圆形，半径 = auraRadius。
        ///
        /// ★ 和爆炸圆共用 BulletBlastRenderer 的网格/材质池，
        ///   只是它**不淡出**、位置每帧跟随怪物。
        /// </summary>
        private void UpdateAuraVisual()
        {
            if (BulletBlastRenderer.Instance == null) return;

            // 第一次：从池里借一个常驻圆盘（不新建网格/材质）
            if (m_AuraVisual == null && !m_AuraRented)
            {
                m_AuraVisual = BulletBlastRenderer.RentPersistent(
                    auraRadius, auraVisualColor, auraVisualAlpha);

                // 让它每帧跟着怪物走
                BulletBlastRenderer.SetFollow(m_AuraVisual, transform);

                m_AuraRented = true;
            }
        }

        /// <summary>
        /// 把光环圆还回池里（幂等）。
        ///
        /// ★ 三条路径都会调：
        ///   · 怪物死亡（DungeonIsDead -> Update）
        ///   · 怪物被隐藏（Hide.SetHiding(1) -> OnDisable）
        ///   · 怪物被销毁（OnDestroy）
        /// </summary>
        private void ReleaseAuraVisual()
        {
            if (!m_AuraRented) return;

            BulletBlastRenderer.ReleaseDisc(m_AuraVisual);

            m_AuraVisual = null;
            m_AuraRented = false;
        }

        /// <summary>
        /// ★ 关键：地牢怪物死亡是 SetActive(false)，**不触发 OnDestroy**，
        ///   所以必须在这里把光环还回去，否则它会一直留在场地中央。
        /// </summary>
        private void OnDisable()
        {
            ReleaseAuraVisual();
        }

        private void OnDestroy()
        {
            ReleaseAuraVisual();
        }

        // ---------------------------------------------------------------- 视觉：扩散环

        // ---------------------------------------------------------------- 视觉：扩散环
        //
        // ★★ 为什么改成"独立物体" ★★
        //
        //   需求："有很多在场上留下图形的怪物技能，在怪物被击杀后这个图形会卡在
        //         中间的某个阶段，并留在原地，无法正常消失……
        //         我希望即使怪物被击杀，它们也能正常播放完动画并消失。"
        //
        //   ★ 原因：RingRoutine 原来是**本组件上的协程**。
        //     怪物一死走 Hide.SetHiding(1) -> SetActive(false)，
        //     Unity 会**立即中断**该物体上所有协程 —— 循环没跑完，
        //     后面那句 Destroy(go) 永远执行不到，光环就停在半途。
        //
        //   ✔ 现在改成：建一个**独立的**扩散环物体（自带 MonoBehaviour 管自己的动画），
        //     它和怪物没有父子/同体关系，怪物死了它照样把动画播完再自毁。
        //   ✔ 顺带把内置 Cylinder（只有 20 条边）换成自建高细分圆盘。

        private void RingRoutine(Vector3 center, Color color, float radius, float life)
        {
            MonsterSkillRingFx.Spawn(center, color, radius, life);
        }

        // ---------------------------------------------------------------- 工具

        private Vector3 PlayerPos()
        {
            if (m_Enemy != null) return m_Enemy.PlayerPos;

            if (m_Monster != null && m_Monster.Player != null)
                return m_Monster.Player.transform.position;

            return m_Monster != null ? m_Monster.DungeonLastPlayerPos : transform.position;
        }

        private Vector3 GetAimPoint()
        {
            if (m_Enemy != null)
            {
                Vector3 p = m_Enemy.GetAimPoint();
                if (p != Vector3.zero) return p;
            }

            return PlayerPos() + Vector3.up * 1.2f;
        }

        private Vector3 GetAimDir(out bool ok)
        {
            Vector3 self = transform.position;
            Vector3 to   = PlayerPos() - self;
            to.y = 0f;

            ok = to.sqrMagnitude > 1e-6f;
            return ok ? to.normalized : Vector3.forward;
        }

        private Vector3 GetMuzzle(out bool ok)
        {
            Vector3 dir = GetAimDir(out ok);
            if (!ok) return transform.position;

            return transform.position + dir * 0.8f;
        }

        private static float HorizontalDist(Vector3 a, Vector3 b)
        {
            return new Vector2(a.x - b.x, a.z - b.z).magnitude;
        }

        private PlayerHealth GetPlayerHealth()
        {
            if (m_PlayerHealth == null)
                m_PlayerHealth = FindObjectOfType<PlayerHealth>();

            return m_PlayerHealth;
        }
    }
}