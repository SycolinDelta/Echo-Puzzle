namespace EchoPuzzle.Dungeon
{
    /// <summary>战前准备里二选一的主动技能（都用左 Alt 释放）。</summary>
    public enum DungeonActiveSkill
    {
        Dodge,       // 闪避（原有）
        HealthDance  // 血条蹦迪（本次新增）
    }

    /// <summary>
    /// 【地牢模式】战前准备的配置（跨场景保留）。
    ///
    /// 生命 / 护盾交换：每 -3 生命换 +1 护盾（或反过来），护盾最多 ±2。
    /// 所以 生命 = 8 - 3×护盾变化，护盾 = 5 + 护盾变化。
    /// </summary>
    public static class DungeonPreparation
    {
        public const int BaseHealth  = 8;
        public const int BaseShield  = 5;
        public const int MaxShieldChange = 2;

        public static int HealthCap { get; private set; } = BaseHealth;
        public static int ShieldCap { get; private set; } = BaseShield;

        public static DungeonActiveSkill ActiveSkill { get; private set; } = DungeonActiveSkill.Dodge;

        public static bool IsHealthDance => ActiveSkill == DungeonActiveSkill.HealthDance;

        /// <summary>准备界面点「开始」时调用：写入上限和技能。</summary>
        public static void Configure(int shieldDelta, DungeonActiveSkill skill)
        {
            int d = UnityEngine.Mathf.Clamp(shieldDelta, -MaxShieldChange, MaxShieldChange);

            HealthCap   = BaseHealth - 3 * d;
            ShieldCap   = BaseShield + d;
            ActiveSkill = skill;

            DungeonHealthDanceSkill.EnsureInstance();
            DungeonHealthDanceSkill.Instance?.ResetRun();
        }

        public static void ResetDefaults()
        {
            HealthCap   = BaseHealth;
            ShieldCap   = BaseShield;
            ActiveSkill = DungeonActiveSkill.Dodge;
        }
    }
}
