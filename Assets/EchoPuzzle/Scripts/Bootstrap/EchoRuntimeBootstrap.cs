using UnityEngine;

namespace EchoPuzzle
{
    /// <summary>
    /// 「打开工程 -> 直接按 Play」的入口。
    ///
    /// 用 <see cref="RuntimeInitializeOnLoadMethodAttribute"/> 在场景加载后自动跑一次：
    ///   1. 如果当前场景里已经有完整的可玩配置（存在 SonarSystem），认为场景是手工搭好的，
    ///      什么都不做。
    ///   2. 否则自动创建一个挂 <see cref="EchoBootstrap"/> 的物体，由它去生成
    ///      关卡 / 玩家 / 相机 / 后处理 / 声波系统 / 首页 UI。
    ///
    /// 所以不需要新建空物体、也不需要点菜单「在场景中生成关卡」。
    /// 想关掉这个行为，把 <see cref="AutoStart"/> 改成 false 即可。
    /// </summary>
    public static class EchoRuntimeBootstrap
    {
        /// <summary>
        /// 总开关。默认 **false** —— 关卡由手工在 Scene 里摆好，不再自动生成。
        /// 改成 true 会退回"自动生成整关"的旧行为。
        /// </summary>
        public static bool AutoStart = false;

        /// <summary>
        /// 只补 UI / 判定这些"每关都需要、但手工摆很烦"的东西。
        /// 关卡几何、玩家、相机都不在这里创建 —— 它们由拖进场景的 Prefab 提供。
        /// </summary>
        public static bool AutoCreateUI = true;

        /// <summary>自动生成的物体名，方便在层级里认出来。</summary>
        public const string RootName = "EchoPuzzle_AutoBootstrap";

        /// <summary>
        /// 早期旧版测试场景里会残留一些手搓的方块关卡（含旧版"圆环墙壁"）。
        /// 它们和新关卡在几何上对不上，叠在一起会互相穿模，
        /// 所以自动启动时把这些遗留物件停用掉。
        /// </summary>
        public static bool DisableLegacyGeometry = true;

        /// <summary>已知的旧版手工物件名。</summary>
        private static readonly string[] LegacyNames = { "Maze", "GameObject" };

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoBootstrap()
        {
            // 退出时丢掉临时的开局快照（它不写盘，没必要留到进程结束）
            Application.quitting -= OnAppQuitting;
            Application.quitting += OnAppQuitting;

            // ★★ 关键：RuntimeInitializeOnLoadMethod 只在**游戏启动时**跑一次，
            //   LoadScene 换场景时不会再执行。
            //
            //   而我们的架构是"Home -> PuzzleMode / DungeonMode"多场景，
            //   每个关卡场景都需要自己的 GameUI。所以挂上 sceneLoaded，
            //   让每个场景加载后都补一次系统。
            //
            //   OnSceneLoaded 里也有幂等判断（FindObjectOfType == null 才建），
            //   所以重复调用不会建出两份。
            UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded;
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;

            // 启动时的第一个场景
            BootstrapScene();
        }

        /// <summary>每次场景加载完成后调用。</summary>
        private static void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene,
                                          UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            BootstrapScene();
        }

        /// <summary>给当前场景补齐系统（幂等）。</summary>
        private static void BootstrapScene()
        {
            if (AutoStart)
            {
                BuildWholeLevel();
                return;
            }

            if (AutoCreateUI) EnsureUISystems();

            if (DisableLegacyGeometry) DisableLegacyObjects();
        }

        /// <summary>退出时调用：丢掉开局快照（临时数据）。</summary>
        private static void OnAppQuitting()
        {
            WorldSnapshot.DropStartSnapshot();
        }

        /// <summary>旧行为：整关自动生成（现在默认关闭）。</summary>
        private static void BuildWholeLevel()
        {
            if (Object.FindObjectOfType<SonarSystem>() != null) return;
            if (GameObject.Find(RootName) != null) return;
            if (Object.FindObjectOfType<EchoBootstrap>() != null) return;

            if (DisableLegacyGeometry) DisableLegacyObjects();

            var go = new GameObject(RootName);
            go.AddComponent<EchoBootstrap>();

            Debug.Log("[EchoPuzzle] 已自动生成关卡并进入首页。");
        }

        /// <summary>
        /// 补上 UI / 判定这些系统。
        /// 已经手工放进场景的（或 Prefab 里带的）不会再重复创建。
        /// </summary>
        private static void EnsureUISystems()
        {
            bool made = false;

            if (Object.FindObjectOfType<GameUI>() == null)
            {
                new GameObject("GameUI").AddComponent<GameUI>();
                made = true;
            }

            // 判定系统只在场景里确实有玩家时才建，免得空场景里每帧白跑
            if (Object.FindObjectOfType<ZoneTrigger>() == null &&
                Object.FindObjectOfType<PlayerController>() != null)
            {
                new GameObject("ZoneTrigger").AddComponent<ZoneTrigger>();
                made = true;
            }

            // 标记系统：按数字键 1~5 在脚下放光点。玩家 Prefab 里已经带了就不重复建
            if (Object.FindObjectOfType<MarkerSystem>() == null)
            {
                new GameObject("MarkerSystem").AddComponent<MarkerSystem>();
                made = true;
            }

            // 脚印系统：每次释放声波在脚下打一个鞋印
            if (Object.FindObjectOfType<FootprintSystem>() == null)
            {
                new GameObject("FootprintSystem").AddComponent<FootprintSystem>();
                made = true;
            }

            // 音效系统：全局单例，靠 Resources 加载 AudioPack 下的音频
            if (Object.FindObjectOfType<AudioSystem>() == null)
            {
                new GameObject("AudioSystem").AddComponent<AudioSystem>();
                made = true;
            }

            // 电池系统：左上角三个圆点，控制光刃能否使用
            if (Object.FindObjectOfType<BatterySystem>() == null)
            {
                new GameObject("BatterySystem").AddComponent<BatterySystem>();
                made = true;
            }

            // 石头系统：左上角（电池下方）三个方块，控制投石次数
            if (Object.FindObjectOfType<StoneSystem>() == null)
            {
                new GameObject("StoneSystem").AddComponent<StoneSystem>();
                made = true;
            }

            // ★★ 地牢模式专用：左上角爱心生命值 HUD ★★
            //   只在 DungeonMode 场景里建（DungeonHUD 内部也会判断模式）。
            if (GameUI.IsDungeonMode &&
                Object.FindObjectOfType<EchoPuzzle.Dungeon.DungeonHUD>() == null)
            {
                new GameObject("DungeonHUD")
                    .AddComponent<EchoPuzzle.Dungeon.DungeonHUD>();
                made = true;
            }

            // ★★ 地牢模式专用：怪物区（数字 3 的格子）★★
            //   必须在地图生成器之前创建 —— 生成器建图时会调用它的 RegisterCells。
            if (GameUI.IsDungeonMode &&
                Object.FindObjectOfType<EchoPuzzle.Dungeon.MonsterZone>() == null)
            {
                new GameObject("MonsterZone")
                    .AddComponent<EchoPuzzle.Dungeon.MonsterZone>();
                made = true;
            }

            // ★★ 地牢模式专用：地图生成器 ★★
            //   读取 DungeonMapGenerator.MapMatrix 建图。
            if (GameUI.IsDungeonMode &&
                Object.FindObjectOfType<EchoPuzzle.Dungeon.DungeonMapGenerator>() == null)
            {
                new GameObject("DungeonMapGenerator")
                    .AddComponent<EchoPuzzle.Dungeon.DungeonMapGenerator>();
                made = true;
            }

            // ★★ 地牢模式专用：怪物潮管理器 ★★
            //   订阅 MonsterZone.OnMonsterWave：玩家进怪物房后按波刷怪，
            //   全灭后解除封锁。必须在 MonsterZone 之后创建（订阅其事件）。
            if (GameUI.IsDungeonMode &&
                Object.FindObjectOfType<EchoPuzzle.Dungeon.MonsterWaveManager>() == null)
            {
                new GameObject("MonsterWaveManager")
                    .AddComponent<EchoPuzzle.Dungeon.MonsterWaveManager>();
                made = true;
            }

            if (made)
                Debug.Log("[EchoPuzzle] 已自动补上 GameUI / ZoneTrigger / MarkerSystem / " +
                          "FootprintSystem / AudioSystem / BatterySystem / StoneSystem。\n" +
                          "（关卡几何与玩家由你手工摆放在场景里）");
        }

        private static void DisableLegacyObjects()
        {
            foreach (string name in LegacyNames)
            {
                // 只找场景根节点下的，避免误伤我们自己生成的东西
                GameObject found = GameObject.Find(name);
                if (found == null) continue;

                // GameObject.Find 本身只找激活对象，这里再确认一次它确实在根层
                if (found.transform.parent != null) continue;

                found.SetActive(false);
                Debug.Log($"[EchoPuzzle] 已停用场景里遗留的旧测试物件「{name}」。" +
                          "（EchoRuntimeBootstrap.DisableLegacyGeometry 可关掉这个行为）");
            }
        }
    }
}
