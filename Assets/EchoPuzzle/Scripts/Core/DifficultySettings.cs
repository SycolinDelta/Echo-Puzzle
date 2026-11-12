using UnityEngine;

namespace EchoPuzzle
{
    /// <summary>
    /// 「降低难度」的三个开关。
    ///
    /// ── 为什么是静态类 ──────────────────────────────────────
    ///   这几个开关会被 MarkerSystem / GameUI 等多处读取，
    ///   而且要在场景切换（Home ↔ 关卡）之间保留 —— 所以放静态字段。
    ///
    /// ── 默认值 ──────────────────────────────────────────────
    ///   · 提高标记上限：**关**
    ///   · 显示指南针：  **关**
    ///   · 显示脚印：    **开**  ← 需求：潜行模式和地牢模式都要能看到脚印
    ///
    /// ── 各开关的效果 ────────────────────────────────────────
    ///   · <b>提高标记上限</b>：关闭时数字键 1~3 能放标记，开启时 1~9 都能放
    ///   · <b>显示指南针</b>：关闭时 HUD 不画指南针
    ///   · <b>显示脚印</b>：关闭时 HUD 不画脚印
    ///
    /// ★ "降低难度" = 把开关**打开**。默认关闭，所以默认是标准难度。
    /// </summary>
    public static class DifficultySettings
    {
        /// <summary>提高标记上限：开 -> 数字键 1~9；关 -> 数字键 1~3。</summary>
        public static bool RaiseMarkerLimit = false;

        /// <summary>显示指南针。</summary>
        public static bool ShowCompass = false;

        /// <summary>
        /// 显示脚印。
        ///
        /// ★★ 默认**开启** ★★
        ///   需求原文："我希望在潜行模式和地牢模式都默认开启'显示脚印'而关闭其他两项"
        /// </summary>
        public static bool ShowFootprints = true;

        // ── 标记槽位编号约定 ───────────────────────────────────
        //   槽位 0..9 一共 10 个，对应数字键：
        //     槽位 0 -> 数字键 0
        //     槽位 1 -> 数字键 1
        //     ...
        //     槽位 9 -> 数字键 9
        //
        //   ★ 默认（提高标记上限 = 关）只有槽位 1~3 可用；
        //     开启后槽位 0~9 全部可用。

        /// <summary>默认开放的槽位：1、2、3（槽位 0 不开放）。</summary>
        public const int NormalMinSlot = 1;
        public const int NormalMaxSlot = 3;

        /// <summary>开启「提高标记上限」后开放的槽位范围：0 ~ 9。</summary>
        public const int RaisedMinSlot = 0;
        public const int RaisedMaxSlot = 9;

        /// <summary>关闭时能用几个标记槽（1~3，共 3 个）。</summary>
        public static int NormalMarkerSlots => NormalMaxSlot - NormalMinSlot + 1;

        /// <summary>开启时能用几个标记槽（0~9，共 10 个）。</summary>
        public static int RaisedMarkerSlots => RaisedMaxSlot - RaisedMinSlot + 1;

        /// <summary>当前允许使用的标记槽数量。</summary>
        public static int MarkerSlotCount =>
            RaiseMarkerLimit ? RaisedMarkerSlots : NormalMarkerSlots;

        /// <summary>槽位 <paramref name="slot"/> 在当前难度下能不能放标记。</summary>
        public static bool IsSlotAllowed(int slot)
        {
            return RaiseMarkerLimit
                 ? slot >= RaisedMinSlot && slot <= RaisedMaxSlot
                 : slot >= NormalMinSlot && slot <= NormalMaxSlot;
        }

        /// <summary>把三个开关恢复成默认（全部关闭）。</summary>
        public static void ResetAll()
        {
            RaiseMarkerLimit = false;
            ShowCompass      = false;
            ShowFootprints   = false;
        }
    }
}
