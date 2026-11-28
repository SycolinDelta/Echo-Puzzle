using System.Collections.Generic;
using UnityEngine;

namespace EchoPuzzle.Dungeon
{
    /// <summary>
    /// 【地牢模式】主动技能「血条蹦迪」。
    ///
    /// 被动：
    ///   · 全程伤害 buff：满血 ×0.512，每少 1 血乘 (1.25^7)^(1/x)，x = 生命上限-1，结果保留三位小数。
    ///     （生命上限 8 时：满血 ×0.512、5 血 ×1.000、1 血 ×2.441）
    ///   · 玩家每次命中敌人有 1s 内置 CD 的骰子：按当前血量分档概率扣 1 血（不吃护盾）/ 回 1 血。
    ///     扣血触发时，本次攻击对范围内随机一只敌人追加其最大生命 50% 的伤害（不吃任何增伤）。
    /// 主动（左 Alt）：
    ///   · 每击杀一只怪 +1 充能，上限 15；5~9 → 回 1 血，10~14 → 回 2 血，15 → 回满。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DungeonHealthDanceSkill : MonoBehaviour
    {
        private const float FullHealthMul = 0.512f;
        private const float PerHpFactor   = 1.25f;
        private const int   StagePower    = 7;
        private const float BonusDamageRatio = 0.5f;
        private const float RollCooldown   = 1f;
        private const int   MaxCharges     = 15;

        public static DungeonHealthDanceSkill Instance { get; private set; }

        private PlayerHealth m_Health;
        private int   m_Charges;
        private float m_NextRollTime;
        private bool  m_ApplyingBonus;

        public int Charges => m_Charges;

        public static bool IsSelected => DungeonPreparation.IsHealthDance;

        /// <summary>准备界面确定选了本技能后，确保玩家身上挂着这个组件。</summary>
        public static void EnsureInstance()
        {
            if (Instance != null) return;

            PlayerHealth h = FindObjectOfType<PlayerHealth>();
            if (h != null) h.gameObject.AddComponent<DungeonHealthDanceSkill>();
        }

        private void Awake()
        {
            Instance = this;
            m_Health = GetComponent<PlayerHealth>();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>重开一局：充能和骰子 CD 归零。</summary>
        public void ResetRun()
        {
            m_Charges      = 0;
            m_NextRollTime = 0f;
            m_ApplyingBonus = false;
        }

        /// <summary>
        /// 当前伤害倍率：0.512 × ((1.25^7)^(1/x))^(生命上限-当前生命)，保留三位。
        /// </summary>
        public float DamageMultiplier
        {
            get
            {
                if (!IsSelected) return 1f;

                PlayerHealth h = GetHealth();
                if (h == null) return 1f;

                int maxHp = Mathf.Max(1, h.maxHealth);
                int x     = Mathf.Max(1, maxHp - 1);
                int lost  = Mathf.Clamp(maxHp - h.Current, 0, maxHp);

                float perHpLoss = Mathf.Pow(Mathf.Pow(PerHpFactor, StagePower), 1f / x);
                float v = FullHealthMul * Mathf.Pow(perHpLoss, lost);

                return Mathf.Round(v * 1000f) / 1000f;
            }
        }

        /// <summary>
        /// 一次玩家攻击命中后的骰子（整次攻击只调一次：子弹单测 / 爆炸范围 / 手刀扇形）。
        /// </summary>
        public void OnPlayerAttack(IList<PuzzleMonster> targets)
        {
            if (!IsSelected || m_ApplyingBonus) return;
            if (targets == null || targets.Count == 0) return;

            // 刷出无敌期间打上去并不造成伤害，不能掷骰子
            bool anyDamage = false;
            for (int i = 0; i < targets.Count; i++)
            {
                if (targets[i] != null && targets[i].DungeonIsDamageable) { anyDamage = true; break; }
            }
            if (!anyDamage) return;

            float now = Time.timeSinceLevelLoad;
            if (now < m_NextRollTime) return;

            m_NextRollTime = now + RollCooldown;

            PlayerHealth h = GetHealth();
            if (h == null || h.Current <= 0) return;

            int hp = h.Current;
            float roll = Random.value;

            bool loseHp = false;
            bool healHp = false;

            // 血量分档（高血档覆盖低血档，从低往高依次判断）
            if (hp <= 1)
            {
                // 只剩 1 血：只可能回血
                healHp = roll < 0.4f;
            }
            else if (hp <= 3)
            {
                if (roll < 0.4f)      loseHp = true;
                else if (roll < 0.8f) healHp = true;
            }
            else if (hp <= 5)
            {
                if (roll < 0.6f)      loseHp = true;
                else if (roll < 0.8f) healHp = true;
            }
            else
            {
                loseHp = roll < 0.6f;
            }

            if (healHp) h.Heal(1);

            if (loseHp)
            {
                // 扣血直接走 SetHealth，绕过护盾
                h.SetHealth(hp - 1);

                // 扣血触发：本次攻击范围内随机一只目标吃最大生命的 50% 追加伤害
                PuzzleMonster bonusTarget = PickAliveTarget(targets);
                if (bonusTarget != null)
                {
                    m_ApplyingBonus = true;
                    bonusTarget.DungeonTakeDamage(bonusTarget.DungeonMaxHealth * BonusDamageRatio);
                    m_ApplyingBonus = false;
                }
            }
        }

        /// <summary>怪物死亡时 +1 充能（上限 15）。</summary>
        public void OnMonsterKilled()
        {
            if (!IsSelected) return;

            m_Charges = Mathf.Min(MaxCharges, m_Charges + 1);
        }

        /// <summary>左 Alt：消耗全部充能回血。5-9 → 1，10-14 → 2，15 → 回满。</summary>
        public void TryActivate()
        {
            if (!IsSelected || m_Charges < 5) return;

            int used = m_Charges;
            m_Charges = 0;

            PlayerHealth h = GetHealth();
            if (h == null) return;

            if (used >= MaxCharges)
                h.SetHealth(h.maxHealth);
            else
                h.Heal(used >= 10 ? 2 : 1);
        }

        private PlayerHealth GetHealth()
        {
            if (m_Health == null) m_Health = FindObjectOfType<PlayerHealth>();
            return m_Health;
        }

        private static PuzzleMonster PickAliveTarget(IList<PuzzleMonster> targets)
        {
            List<PuzzleMonster> alive = new List<PuzzleMonster>();

            for (int i = 0; i < targets.Count; i++)
            {
                PuzzleMonster m = targets[i];
                if (m != null && m.DungeonIsDamageable) alive.Add(m);
            }

            if (alive.Count == 0) return null;
            return alive[Random.Range(0, alive.Count)];
        }
    }
}
