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
    /// ★ 由 EchoRuntimeBootstrap 在 DungeonMode 场景自动创建，无需手动摆进场景。
    /// ★ 切层（TriggerVolume.OnNextStage）时清掉残留怪物并中断进行中的怪物潮。
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-80)]
    public sealed class MonsterWaveManager : MonoBehaviour
    {
        // ---------------------------------------------------------------- 配置

        [Header("波数（随机区间，含端点）")]
        [Min(1)] public int minWaves = 2;
        [Min(1)] public int maxWaves = 4;

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

        // ---------------------------------------------------------------- 运行时

        private GameObject m_Prefab;
        private Transform  m_Root;
        private readonly List<PuzzleMonster> m_Wave = new List<PuzzleMonster>(16);
        private Coroutine m_WaveRoutine;
        private float     m_CellSize = 1f;

        private static int s_Counter;

        // ---------------------------------------------------------------- 生命周期

        private void Awake()
        {
            LoadPrefab();

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
        /// 编辑器里直接从 Assets/EchoPuzzle/Prefabs 取；
        /// 打包前请把它复制一份到 Resources/EchoRuntime/DungeonMonster。
        /// </summary>
        private void LoadPrefab()
        {
            m_Prefab = Resources.Load<GameObject>("EchoRuntime/DungeonMonster");

#if UNITY_EDITOR
            if (m_Prefab == null)
                m_Prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
                    "Assets/EchoPuzzle/Prefabs/PuzzleMonster.prefab");
#endif
            if (m_Prefab == null)
                Debug.LogWarning(
                    "[怪物潮] 找不到怪物 prefab" +
                    "（Resources/EchoRuntime/DungeonMonster 或 Assets/EchoPuzzle/Prefabs/PuzzleMonster.prefab），" +
                    "怪物潮不会刷怪。");
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

            m_CellSize    = Mathf.Max(0.1f, zone.CellSize);
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

        private IEnumerator WaveRoutine(MonsterZone.MonsterZoneArgs zone)
        {
            int waves = Mathf.Max(1, Random.Range(minWaves, maxWaves + 1));
            Debug.Log($"[怪物潮] 开始：共 {waves} 波，区域 {zone.Cells.Count} 格。");

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

                Debug.Log($"[怪物潮] 第 {w + 1}/{waves} 波已全灭。");

                if (w < waves - 1 && waveInterval > 0f)
                    yield return new WaitForSeconds(waveInterval);
            }

            Debug.Log("[怪物潮] 全部波次结束，解除封锁。");

            zone.Finish();
            m_WaveRoutine = null;
        }

        private void SpawnWave(List<Vector2Int> cells, int waveIndex, int totalWaves)
        {
            if (m_Prefab == null || cells == null || cells.Count == 0) return;

            int count = Random.Range(minPerWave, maxPerWave + 1);
            // 后期波次稍微加压（难度随波次缓涨），但不越过上限太多
            count = Mathf.Min(count + waveIndex / 2, maxPerWave + 2);

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

            Debug.Log($"[怪物潮] 第 {waveIndex + 1}/{totalWaves} 波：刷出 {spawned} 只。");
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

            // 血量复位（并推一次事件给血条）
            monster.DungeonResetHealth();

            m_Wave.Add(monster);
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
