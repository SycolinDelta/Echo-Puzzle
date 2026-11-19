namespace EchoPuzzle.Dungeon
{
    /// <summary>
    /// 【地牢模式】当前是否有"模态界面"开着（插件装配 / 商店 / 三选一）。
    ///
    /// ★ 为什么需要这个：
    ///   所有地牢 HUD 都是 IMGUI（OnGUI）画的，而 MonoBehaviour 之间的 OnGUI
    ///   调用顺序不固定，GUI.depth 在跨脚本时也不可靠 —— 结果就是装配界面打开时，
    ///   左上角的"伤害 / 射速 / 蓝耗 / 蓝量"会盖在面板上挡住背包。
    ///
    ///   最稳的办法不是抢绘制层级，而是**模态界面打开时让 HUD 自己让路**：
    ///   各 HUD 的 OnGUI 开头统一问一句 AnyOpen，是就 return。
    /// </summary>
    public static class DungeonModalUi
    {
        /// <summary>是否有模态界面正开着。</summary>
        public static bool AnyOpen
        {
            get
            {
                var loadout = DungeonLoadoutUI.Instance;
                if (loadout != null && loadout.IsOpen) return true;

                var shop = DungeonShopUI.Instance;
                if (shop != null && shop.IsOpen) return true;

                var picker = DungeonBuffSelectUI.Instance;
                if (picker != null && picker.IsOpen) return true;

                return false;
            }
        }
    }
}