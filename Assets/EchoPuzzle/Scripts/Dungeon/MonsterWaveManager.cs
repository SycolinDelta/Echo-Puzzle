using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace EchoPuzzle.Dungeon
{
    /// <summary>
    /// 【地牢模式】怪物潮管理器。
    ///
    /// 订阅 <see cref="MonsterZone.OnMonsterWave"/>（玩家进入怪物房时由
    /// MonsterZone 广播），然后在怪物房里按波刷新怪物：
    ///   · 总共随机 <see cref="minWaves"/>~<see cref="maxWaves"/> 波；
    ///   · 每波随机 <see cref="minPerWave"/>~<see cref="maxPerWave"/> 只，
    ///     刷在怪物房格子里（离玩家太近的格子跳过，但保证至少刷出一只）；
    ///   · 每波全灭后才刷下一波；
    ///   · 全部打完调 <see cref="MonsterZoneArgs.Finish"/> 解除封锁，
    ///     玩家再去终点触发下一层。
    ///
    /// ★ boss 房（字母 "B"）走另一条流程：只刷**一只** BossMandala，
    ///   位置固定在模板第 18 行 18 列（房间正中），打死即解除封锁。
    ///
    /// ★ 由 EchoRuntimeBootstrap 在 DungeonMode 场景自动创建，无需手动摆进场景。
    /// ★ 切层（TriggerVolume.OnNextStage）时清掉残留怪物并中断进行中的怪物潮。
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-80)]
    public sealed class MonsterWaveManager : MonoBehaviour
    {
        // ---------------------------------------------------------------- 配置

        [Header("波数（随机区间，含端点）")]
        [Tooltip("最少波数")]
        [Min(1)] public int minWaves = 2;
        [Tooltip("最多波数（第 6 层起的上限）")]
        [Min(1)] public int maxWaves = 4;

        [Header("按层数收窄波数")]
        [Tooltip("前期（1~midStageFromStage-1 层）固定这么多波。\n\n" +
                 "★ 需求：第 1~2 层怪物潮总波数固定为 2 波。")]
        [Min(1)] public int earlyStageWaves = 2;

        [Tooltip("中期（midStageFromStage ~ fullWavesFromStage-1 层）的波数上限。\n\n" +
                 "★ 需求：第 3~5 层为 2~3 波。")]
        [Min(1)] public int midStageMaxWaves = 3;

        [Tooltip("中期从第几层开始（含）")]
        [Min(1)] public int midStageFromStage = 3;

        [Tooltip("从第几层开始用 minWaves~maxWaves 的完整区间。\n\n" +
                 "★ 需求：从第 6 层开始才为 2~4 波。")]
        [Min(1)] public int fullWavesFromStage = 6;

        [Header("每波数量（随机区间，含端点）")]
        [Min(1)] public int minPerWave = 2;
        [Min(1)] public int maxPerWave = 5;

        [Header("刷怪规则")]
        [Tooltip("刷怪点与玩家的最小水平距离（米）")]
        [Min(0f)] public float minPlayerDistance = 4f;

        [Tooltip("两波之间的间隔（秒）")]
        [Min(0f)] public float waveInterval = 1.5f;

        [Tooltip("怪物出生时的悬浮高度（米），与怪物 prefab 的 Float Height 一致")]
        [Min(0.5f)] public float monsterSpawnHeight = 2f;

        [Tooltip("boss 的固定悬浮高度（球心世界 y）。boss 站桩，不跑悬停逻辑，刷出即停在这个高度")]
        [Min(0.5f)] public float bossSpawnHeight = 4f;

        // ---------------------------------------------------------------- 运行时

        private GameObject m_Prefab;
        private GameObject m_BossPrefab;
        private Transform  m_Root;
        private readonly List<PuzzleMonster> m_Wave = new List<PuzzleMonster>(16);
        private Coroutine m_WaveRoutine;
        private float     m_CellSize = 1f;

        private static int s_Counter;

        // ---------------------------------------------------------------- 生命周期

        private void Awake()
        {
            LoadPrefab();
            LoadBossPrefab();

            var rootGo = new GameObject("[生成] 怪物潮");
            rootGo.transform.SetParent(transform, false);
            m_Root = rootGo.transform;
        }

        private void OnEnable()
        {
            MonsterZone.OnMonsterWave += HandleWave;
            TriggerVolume.OnNextStage += HandleNextStage;
        }

        private void OnDisable()
        {
            MonsterZone.OnMonsterWave -= HandleWave;
            TriggerVolume.OnNextStage -= HandleNextStage;
        }

        // ---------------------------------------------------------------- prefab

        /// <summary>
        /// 怪物 prefab。
        ///
        /// ★ 顺序：
        ///   1. Resources 里的索引资产 DungeonPrefabIndex（打包 / 编辑器都有效），
        ///      它持有的是 Assets/EchoPuzzle/Prefabs/PuzzleMonster.prefab 的引用；
        ///   2. 历史路径 Resources/EchoRuntime/DungeonMonster（如果哪天有人放了一份）；
        ///   3. 编辑器直接按路径取 Prefabs/PuzzleMonster.prefab（索引坏了时的兜底）。
        /// </summary>
        private void LoadPrefab()
        {
            var index = DungeonPrefabIndex.Load();
            if (index != null) m_Prefab = index.puzzleMonster;

            if (m_Prefab == null)
                m_Prefab = Resources.Load<GameObject>("EchoRuntime/DungeonMonster");

#if UNITY_EDITOR
            if (m_Prefab == null)
                m_Prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
                    "Assets/EchoPuzzle/Prefabs/PuzzleMonster.prefab");
#endif
            if (m_Prefab == null)
                Debug.LogWarning(
                    "[怪物潮] 找不到怪物 prefab" +
                    "（Resources/EchoRuntime/DungeonPrefabIndex 或 Assets/EchoPuzzle/Prefabs/PuzzleMonster.prefab），" +
                    "怪物潮不会刷怪。");
        }

        /// <summary>
        /// boss prefab（BossMandala）。
        ///
        /// ★ 和普通怪一样：优先吃 Resources 里的 DungeonPrefabIndex，
        ///   索引里引用的是 Assets/EchoPuzzle/Prefabs/BossMandala.prefab，
        ///   所以改那一份，打包后 boss 房里生成的就是改完的样子。
        /// </summary>
        private void LoadBossPrefab()
        {
            var index = DungeonPrefabIndex.Load();
            if (index != null) m_BossPrefab = index.bossMandala;

            if (m_BossPrefab == null)
                m_BossPrefab = Resources.Load<GameObject>("EchoRuntime/BossMandala");

#if UNITY_EDITOR
            if (m_BossPrefab == null)
                m_BossPrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
                    "Assets/EchoPuzzle/Prefabs/BossMandala.prefab");
#endif
            if (m_BossPrefab == null)
                Debug.LogWarning(
                    "[怪物潮] 找不到 boss prefab" +
                    "（Resources/EchoRuntime/DungeonPrefabIndex 或 Assets/EchoPuzzle/Prefabs/BossMandala.prefab），" +
                    "boss 挑战不会刷 boss。");
        }

        // ---------------------------------------------------------------- 事件

        private void HandleWave(MonsterZone.MonsterZoneArgs zone)
        {
            if (m_WaveRoutine != null)
            {
                // 上一波还在跑（理论上被封锁时不会再触发新的），保险起见先清掉。
                StopCoroutine(m_WaveRoutine);
                ClearMonsters();
            }

            m_CellSize = Mathf.Max(0.1f, zone.CellSize);

            // ★★ boss 房（字母 "B"）：只刷一只 boss，不再是多波多只 ★★
            if (zone.IsBossZone)
            {
                m_WaveRoutine = StartCoroutine(BossRoutine(zone));
                return;
            }

            m_WaveRoutine = StartCoroutine(WaveRoutine(zone));
        }

        /// <summary>切层：中断进行中的怪物潮并清掉残留怪物。</summary>
        private void HandleNextStage()
        {
            if (m_WaveRoutine != null)
            {
                StopCoroutine(m_WaveRoutine);
                m_WaveRoutine = null;
            }

            ClearMonsters();
        }

        // ---------------------------------------------------------------- 波次

        /// <summary>
        /// 按当前层数算这次怪物潮的总波数。
        ///
        /// ★ 需求："在第 1~2 层，怪物潮总波数固定为 2 波；
        ///         第 3~5 层时，总波数 2~3 波；从第 6 层开始才为 2~4 波。"
        ///
        ///   · 层 &lt; midStageFromStage  -> 固定 earlyStageWaves（默认 2）
        ///   · 层 &lt; fullWavesFromStage -> minWaves ~ min(midStageMaxWaves, maxWaves)
        ///   · 否则                      -> minWaves ~ maxWaves
        /// </summary>
        private int ResolveWaveCount()
        {
            int stage = DungeonBuffState.StageIndex;

            int lo = Mathf.Max(1, minWaves);

            // ---- 前期：固定波数 ----
            if (stage < Mathf.Max(1, midStageFromStage))
                return Mathf.Max(1, earlyStageWaves);

            // ---- 中期：上限收窄 ----
            int hi;

            if (stage < Mathf.Max(1, fullWavesFromStage))
                hi = Mathf.Min(Mathf.Max(1, midStageMaxWaves), Mathf.Max(1, maxWaves));
            else
                hi = Mathf.Max(1, maxWaves);

            if (hi < lo) hi = lo;

            // ★ Random.Range(int,int) 的区间是 [min, max)，所以要 +1
            return Random.Range(lo, hi + 1);
        }

        private IEnumerator WaveRoutine(MonsterZone.MonsterZoneArgs zone)
        {
            // ★★ 总波数按当前层数决定 ★★
            //
            //   需求："在第 1~2 层，怪物潮总波数固定为 2 波；
            //         第 3~5 层时，总波数 2~3 波；从第 6 层开始才为 2~4 波。"
            int waves = ResolveWaveCount();

            for (int w = 0; w < waves; w++)
            {
                SpawnWave(zone.Cells, w, waves);

                // ---- 等待本波全灭 ----
                while (m_Wave.Count > 0)
                {
                    yield return null;

                    for (int i = m_Wave.Count - 1; i >= 0; i--)
                    {
                        PuzzleMonster m = m_Wave[i];
                        if (m == null || m.DungeonIsDead || m.IsHidden())
                            m_Wave.RemoveAt(i);
                    }
                }


                if (w < waves - 1 && waveInterval > 0f)
                    yield return new WaitForSeconds(waveInterval);
            }


            zone.Finish();
            m_WaveRoutine = null;
        }

        /// <summary>
        /// 【boss 房】怪物潮：**只刷一只** boss，位置固定为模板第 18 行 18 列
        /// （也就是 B 块的正中央），打死它就解除封锁。
        ///
        /// ★ 没有波次概念 —— 不读 minWaves / maxWaves / minPerWave / maxPerWave。
        /// </summary>
        private IEnumerator BossRoutine(MonsterZone.MonsterZoneArgs zone)
        {
            Vector3 pos = new Vector3(zone.BossCell.x * m_CellSize,
                                      bossSpawnHeight,
                                      zone.BossCell.y * m_CellSize);

            PuzzleMonster boss = SpawnBoss(pos);

            // ---- 等这只 boss 死亡 ----
            while (boss != null && !boss.DungeonIsDead && !boss.IsHidden())
                yield return null;

            m_Wave.Clear();

            zone.Finish();
            m_WaveRoutine = null;
        }

        /// <summary>
        /// 生成 boss（BossMandala）。
        ///
        /// ★ 外观 / 全部参数都来自 prefab 本身（它就是 PuzzleMonster 的完整副本，
        ///   地牢模式下的 dungeon* 光带参数一并照抄），这里只补齐地牢运行期组件。
        /// ★ 暂不投怪物技能骰子 —— 需求："暂时不用写它的移动和开火规则"。
        /// </summary>
        private PuzzleMonster SpawnBoss(Vector3 pos)
        {
            if (m_BossPrefab == null) return null;

            s_Counter++;

            var go = Instantiate(m_BossPrefab, pos, Quaternion.identity, m_Root);
            go.name = "[怪物潮] BossMandala";

            PuzzleMonster monster = go.GetComponent<PuzzleMonster>();
            if (monster == null) monster = go.AddComponent<PuzzleMonster>();

            // ★ 项目规矩：所有物体挂 Hide（0 = 正常存在于场景）
            Hide hide = go.GetComponent<Hide>();
            if (hide == null) hide = go.AddComponent<Hide>();
            hide.SetHiding(0);

            // 地牢行为 + 头顶血条
            DungeonEnemy enemy = go.GetComponent<DungeonEnemy>();
            if (enemy == null) enemy = go.AddComponent<DungeonEnemy>();
            if (go.GetComponent<MonsterHealthBar>() == null)
                go.AddComponent<MonsterHealthBar>();

            // ★★ boss 技能 + 模板 ★★
            //   模板固定 Mandala（一套独立技能表，目前内容和输出型一致），
            //   并且**不投被动骰子** —— 需求："让 BossMandala 不会获得被动"。
            //   没有这个组件的话左上角连模板图标都画不出来（血条靠它读 Template）。
            DungeonMonsterSkills skills = go.GetComponent<DungeonMonsterSkills>();
            if (skills == null)
                skills = go.AddComponent<DungeonMonsterSkills>();

            skills.RollOnSpawn(enemy, monster, DungeonBuffState.StageIndex,
                               DungeonMonsterSkills.MonsterTemplate.Mandala, false);

            // ★ BossMandala：能开火、不能移动 ★
            //   两个开关一起置位：DungeonEnemy 那边跳过"找射击位 / 靠近"的移动状态机
            //   （但仍每帧算视线），PuzzleMonster 那边直接不执行 MoveTowardTarget，
            //   连声波诱饵（CaptureSonarSource 写进来的 m_StopPoint）都不理会。
            enemy.holdPosition       = true;
            monster.dungeonHoldPosition = true;

            // ★ 受击圆柱加高到 8 米 ★
            //   boss 球心在 4 米，玩家子弹的弹道在 1.15 米，
            //   子弹自带的 4 米圆柱（0~4）够不到球体上沿，所以给 boss 单独 8 米的判定高度。
            monster.dungeonHurtHeight = 8f;

            // ★ 受击半径 2 米 ★
            //   普通怪的圆柱半径是 1 米，boss 球半径 1.5 米，所以单独覆盖成 2 米。
            monster.dungeonHurtRadius = 2f;

            // 血量复位（并推一次事件给血条）
            monster.DungeonResetHealth();

            m_Wave.Add(monster);
            return monster;
        }

        private void SpawnWave(List<Vector2Int> cells, int waveIndex, int totalWaves)
        {
            if (m_Prefab == null || cells == null || cells.Count == 0) return;

            // ★ 每波都是 minPerWave~maxPerWave，**不再随波次 +1**。
            //   需求："去掉随波次+1的机制，每波都是刷2~5只。"
            int count = Random.Range(minPerWave, maxPerWave + 1);

            var shuffled = new List<Vector2Int>(cells);
            Shuffle(shuffled);

            Vector3 playerPos = FindPlayerPos();
            int spawned = 0;

            foreach (Vector2Int cell in shuffled)
            {
                if (spawned >= count) break;

                Vector3 pos = new Vector3(cell.x * m_CellSize, monsterSpawnHeight, cell.y * m_CellSize);

                // 离玩家太近的格子跳过 —— 但保证至少刷出第一只（别空波）
                bool nearPlayer = playerPos != Vector3.zero &&
                    new Vector2(pos.x - playerPos.x, pos.z - playerPos.z).sqrMagnitude <
                    minPlayerDistance * minPlayerDistance;

                if (nearPlayer && spawned >= 1) continue;

                SpawnMonster(pos);
                spawned++;
            }

        }

        private void SpawnMonster(Vector3 pos)
        {
            s_Counter++;

            var go = Instantiate(m_Prefab, pos, Quaternion.identity, m_Root);
            go.name = $"[怪物潮] PuzzleMonster #{s_Counter}";

            PuzzleMonster monster = go.GetComponent<PuzzleMonster>();
            if (monster == null) monster = go.AddComponent<PuzzleMonster>();

            // ★ 项目规矩：所有物体挂 Hide（0 = 正常存在于场景）
            Hide hide = go.GetComponent<Hide>();
            if (hide == null) hide = go.AddComponent<Hide>();
            hide.SetHiding(0);

            // 地牢行为 + 头顶血条
            if (go.GetComponent<DungeonEnemy>() == null)
                go.AddComponent<DungeonEnemy>();
            if (go.GetComponent<MonsterHealthBar>() == null)
                go.AddComponent<MonsterHealthBar>();

            // ★★ 怪物技能系统 ★★
            //   模板按**登场权重**随机（输出 3 / 近战 3 / 控场 2 / 支援 2），
            //   并且第 1 层屏蔽控场 / 支援 —— 这两条规则写在
            //   DungeonMonsterSkills.PickTemplate() 里，这里**不要**再自己掷一个
            //   Uniform 的模板传进去，否则权重和层数屏蔽全被绕开。
            //   主动技能池按模板 + 层数确定，被动按模板优先级 + 原有概率获取。
            //   ★ 必须在 DungeonResetHealth 之前投骰子 ——
            //     「生命值 ×2」被动要改 dungeonMaxHealth，复位时才生效。
            DungeonMonsterSkills skills = go.GetComponent<DungeonMonsterSkills>();
            if (skills == null)
                skills = go.AddComponent<DungeonMonsterSkills>();

            skills.RollOnSpawn(go.GetComponent<DungeonEnemy>(), monster,
                               DungeonBuffState.StageIndex);

            // 血量复位（并推一次事件给血条）
            monster.DungeonResetHealth();

            m_Wave.Add(monster);
        }

        /// <summary>
        /// 强制中断当前怪物潮并清掉所有残留怪物。
        ///
        /// ★ 给「重新开始」用：那一局的波次进度作废，新一局重新开始。
        /// </summary>
        public void ForceClear()
        {
            if (m_WaveRoutine != null)
            {
                StopCoroutine(m_WaveRoutine);
                m_WaveRoutine = null;
            }

            ClearMonsters();
        }

        private void ClearMonsters()
        {
            m_Wave.Clear();

            if (m_Root == null) return;
            for (int i = m_Root.childCount - 1; i >= 0; i--)
                Destroy(m_Root.GetChild(i).gameObject);
        }

        // ---------------------------------------------------------------- 工具

        private static void Shuffle(List<Vector2Int> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        private Vector3 FindPlayerPos()
        {
            PlayerController p = FindObjectOfType<PlayerController>();
            return p != null ? p.transform.position : Vector3.zero;
        }
    }
}
