using UnityEngine;

namespace EchoPuzzle.Dungeon
{
    /// <summary>插件颜色（第十一周任务）。</summary>
    public enum PluginColor
    {
        White  = 0,   // 白：基础数值
        Green  = 1,   // 绿：强化数值
        Blue   = 2,   // 蓝：极端改装（只能拿一次）
        Purple = 3,   // 紫：机制插件（带 ×1.5 蓝耗）
        Orange = 4,   // 橙：机制插件（无额外蓝耗）
        Red    = 5,   // 红：debuff（换数值）
    }

    /// <summary>武器模板等级（两个模板同步升级）。</summary>
    public enum TemplateTier
    {
        White  = 0,
        Green  = 1,
        Blue   = 2,
        Purple = 3,
        Orange = 4,
    }

    /// <summary>
    /// 【地牢模式】六色插槽的数量规则（第十一周任务）。
    ///
    /// ── 任务书原文换算成公式 ────────────────────────────────
    ///   白槽 = 3 + (层-1)/2（上限 7）− 已装绿插件数 − 紫橙插件数
    ///   绿槽 = 3 + (层-1)/2（上限 7）− 已装白插件数 − 紫橙插件数
    ///   蓝槽 = 模板 ≥ 紫 ? 1 : 0                      （上限 1）
    ///   紫槽 = 1 + (模板 ≥ 绿 ? 1 : 0)                （上限 2）
    ///   橙槽 = (模板≥蓝) + (模板≥紫) + (模板≥橙)      （上限 3）
    ///   红槽 = 白×1 + 绿×2 + 蓝×3 + 紫×1 + 橙×3
    ///
    /// ★ 白绿是"共享 7 格"：装绿插件会挤掉白槽，反之亦然。
    /// ★ 紫橙插件每装一个，白槽和绿槽**各**少 1 格。
    /// ★ 红槽不需要玩家操心数量，它完全由其它插件"喂养"出来。
    ///
    ///   全部算完后统一夹到 ≥ 0（挤爆了就显示 0 格，不会出现负数）。
    /// </summary>
    public static class SlotRules
    {
        public const int BaseWhiteGreenSlots = 3;
        public const int FloorsPerExtraSlot  = 2;
        public const int MaxWhiteGreenSlots  = 7;
        public const int MaxBlueSlots        = 1;
        public const int BasePurpleSlots     = 1;
        public const int MaxPurpleSlots      = 2;
        public const int MaxOrangeSlots      = 3;

        /// <summary>颜色种类数。</summary>
        public const int ColorCount = 6;

        /// <summary>
        /// 白/绿的基础格数：3 起，每推进 2 层 +1，最多 7。
        /// （还没扣"互相挤占"和"紫橙挤占"）
        /// </summary>
        public static int WhiteGreenBase(int stage)
            => Mathf.Clamp(BaseWhiteGreenSlots + Mathf.Max(0, stage - 1) / FloorsPerExtraSlot,
                           BaseWhiteGreenSlots, MaxWhiteGreenSlots);

        /// <summary>
        /// 算某一套已装插件下的六色槽位数量。
        /// </summary>
        /// <param name="tier">武器模板等级</param>
        /// <param name="stage">当前层号（1 起）</param>
        /// <param name="equipped">
        /// 已装插件数，下标是 <see cref="PluginColor"/> 的整数值，
        /// 比如 equipped[(int)PluginColor.Green] = 装了几个绿插件
        /// </param>
        /// <param name="caps">输出：各颜色可用槽位数，下标同 <paramref name="equipped"/></param>
        public static void ComputeCaps(TemplateTier tier, int stage, int[] equipped, int[] caps)
        {
            if (caps == null || caps.Length < ColorCount) return;

            int w  = CountOf(equipped, PluginColor.White);
            int g  = CountOf(equipped, PluginColor.Green);
            int b  = CountOf(equipped, PluginColor.Blue);
            int p  = CountOf(equipped, PluginColor.Purple);
            int o  = CountOf(equipped, PluginColor.Orange);
            int r  = CountOf(equipped, PluginColor.Red);

            int baseWG = WhiteGreenBase(stage);

            // 紫橙插件每装一个，白绿各占 1 格
            int heavy = p + o;

            int capWhite  = baseWG - g - heavy;
            int capGreen  = baseWG - w - heavy;

            int capBlue   = tier >= TemplateTier.Purple ? MaxBlueSlots : 0;

            int capPurple = BasePurpleSlots + (tier >= TemplateTier.Green ? 1 : 0);

            int capOrange = 0;
            if (tier >= TemplateTier.Blue)   capOrange += 1;
            if (tier >= TemplateTier.Purple) capOrange += 1;
            if (tier >= TemplateTier.Orange) capOrange += 1;

            // 红槽由其它颜色喂养
            int capRed = w * 1 + g * 2 + b * 3 + p * 1 + o * 3;

            caps[(int)PluginColor.White]  = Mathf.Clamp(capWhite,  0, MaxWhiteGreenSlots);
            caps[(int)PluginColor.Green]  = Mathf.Clamp(capGreen,  0, MaxWhiteGreenSlots);
            caps[(int)PluginColor.Blue]   = Mathf.Clamp(capBlue,   0, MaxBlueSlots);
            caps[(int)PluginColor.Purple] = Mathf.Clamp(capPurple, 0, MaxPurpleSlots);
            caps[(int)PluginColor.Orange] = Mathf.Clamp(capOrange, 0, MaxOrangeSlots);
            caps[(int)PluginColor.Red]    = Mathf.Max(0, capRed);

            // r 只用于完整性检查（红插件不会挤占别的颜色）
            _ = r;
        }

        /// <summary>六色合计槽位数。</summary>
        public static int TotalSlots(int[] caps)
        {
            if (caps == null) return 0;

            int n = 0;
            for (int i = 0; i < ColorCount && i < caps.Length; i++) n += Mathf.Max(0, caps[i]);
            return n;
        }

        private static int CountOf(int[] arr, PluginColor c)
            => (arr != null && (int)c < arr.Length) ? Mathf.Max(0, arr[(int)c]) : 0;

        // ---------------------------------------------------------------- 显示用

        public static string NameOf(PluginColor c)
        {
            switch (c)
            {
                case PluginColor.White:  return "白";
                case PluginColor.Green:  return "绿";
                case PluginColor.Blue:   return "蓝";
                case PluginColor.Purple: return "紫";
                case PluginColor.Orange: return "橙";
                case PluginColor.Red:    return "红";
                default:                 return "?";
            }
        }

        public static string NameOf(TemplateTier t)
        {
            switch (t)
            {
                case TemplateTier.White:  return "白";
                case TemplateTier.Green:  return "绿";
                case TemplateTier.Blue:   return "蓝";
                case TemplateTier.Purple: return "紫";
                case TemplateTier.Orange: return "橙";
                default:                  return "?";
            }
        }

        /// <summary>插件 / 槽位在界面上的颜色。</summary>
        public static Color UiColor(PluginColor c)
        {
            switch (c)
            {
                case PluginColor.White:  return new Color(0.92f, 0.92f, 0.92f);
                case PluginColor.Green:  return new Color(0.42f, 0.86f, 0.45f);
                case PluginColor.Blue:   return new Color(0.36f, 0.66f, 1f);
                case PluginColor.Purple: return new Color(0.72f, 0.48f, 1f);
                case PluginColor.Orange: return new Color(1f, 0.66f, 0.28f);
                case PluginColor.Red:    return new Color(1f, 0.36f, 0.36f);
                default:                 return Color.white;
            }
        }

        /// <summary>模板等级在界面上的颜色（沿用插件配色）。</summary>
        public static Color UiColor(TemplateTier t) => UiColor((PluginColor)(int)t);
    }
}