using UnityEngine;
using UnityEngine.SceneManagement;

namespace EchoPuzzle.Dungeon
{
    /// <summary>
    /// 【地牢模式】武器系统的自动装配入口。
    ///
    /// ── 为什么要有它 ────────────────────────────────────────
    ///   文档要求"不要往场景里加东西，一切由代码在运行时生成"，
    ///   而 DungeonMode 场景里目前只有 Player / SonarSystem 两个 prefab 实例
    ///   和一堆由 DungeonMapGenerator 临时生成的物件。
    ///   所以武器、蓝量、蓝条 HUD 也走同一套：进地牢场景时自动补上。
    ///
    /// ── 行为 ────────────────────────────────────────────────
    ///   · 只在地牢场景（<see cref="GameUI.IsDungeonMode"/>）里生效
    ///   · 场景里已经有 DungeonWeapon 就什么都不做（不会重复创建）
    ///   · 每个场景加载后检查一次，所以"回首页再进地牢"也会重新装配
    ///
    /// ── 和探秘模式的关系 ────────────────────────────────────
    ///   ★ 探秘模式下这个入口整段不执行，不会多出任何物体。
    /// </summary>
    public static class DungeonWeaponBootstrap
    {
        private const string RootName = "EchoPuzzle_DungeonWeapon";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;

            Ensure();
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => Ensure();

        private static void Ensure()
        {
            // 只在地牢模式装配
            if (!GameUI.IsDungeonMode) return;

            // 已经有了就别再造一份
            if (Object.FindObjectOfType<DungeonWeapon>() != null) return;

            var root = new GameObject(RootName);

            // 顺序无所谓：三个组件之间只通过 Instance / 事件互相找，
            // 不依赖 Awake 的先后（DungeonMana 在 Awake 里设 Instance，
            //  DungeonWeapon 每帧再去取，所以谁先谁后都能跑）。
            root.AddComponent<DungeonMana>();
            root.AddComponent<DungeonWeapon>();
            root.AddComponent<DungeonManaHUD>();

            // 金币（第十周任务 2）：击杀获得，用来刷新选卡 / 商店消费
            root.AddComponent<DungeonCurrency>();
            root.AddComponent<DungeonGoldHUD>();

            // 强化系统（buff）：状态 + 每层通关的三选一界面 + 左上角的已获得列表
            root.AddComponent<DungeonBuffState>();
            root.AddComponent<DungeonBuffSelectUI>();
            root.AddComponent<DungeonBuffHUD>();

            // 插件装备界面（第十周任务 5）：按 R 打开，两个武器模板按 E 切换
            root.AddComponent<DungeonLoadoutUI>();

            // 右下角的技能冷却（闪避 / 声波）
            root.AddComponent<DungeonCooldownHUD>();

            // 子弹命中时的"溅血光带"（不会被怪物捕捉）
            root.AddComponent<BloodSplashSonar>();

            // ★★ 两个池化渲染器（第十周：避免反复 new / Destroy）★★
            //   · BulletBlastRenderer   —— 爆炸圆形（一次性 + 光环常驻）
            //   · HandBladeLineRenderer —— 手刀线段
            root.AddComponent<BulletBlastRenderer>();
            root.AddComponent<HandBladeLineRenderer>();


        }
    }
}