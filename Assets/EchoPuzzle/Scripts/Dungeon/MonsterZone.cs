using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace EchoPuzzle.Dungeon
{
    /// <summary>
    /// 【地牢模式】怪物区。
    ///
    /// ══════════════════════════════════════════════════════════════
    /// 玩法
    ///   · 地图矩阵里数字 3 的格子就是「怪物区」（怪物房的地板）
    ///   · 玩家踏入怪物区 -> 与之**相连的整片**怪物区一起被封锁
    ///   · 同时在玩家脚下触发「怪物潮」事件
    ///   · 怪物潮结束后 -> 解除封锁，玩家可以离开
    ///   · **再次进入不会刷新怪物**（同一层里每个区域只触发一次）
    ///
    /// ══════════════════════════════════════════════════════════════
    /// ★★ 交接说明（怪物潮的具体内容由别人实现）★★
    ///
    ///   这个脚本**不生成任何怪物**，只负责：
    ///     1. 检测玩家进入怪物区
    ///     2. 计算"相连的一整片"并封锁
    ///     3. 广播 <see cref="OnMonsterWave"/> 事件
    ///     4. 等事件结束后解除封锁
    ///
    ///   要接上怪物潮，只需订阅：
    ///
    ///       MonsterZone.OnMonsterWave += zone => StartCoroutine(MyWave(zone));
    ///
    ///   其中 <c>zone</c> 是 <see cref="MonsterZoneArgs"/>，带这些信息：
    ///     · <c>Cells</c>      这片怪物区包含哪些格子（可以在里面刷怪）
    ///     · <c>Center</c>     这片区域的世界坐标中心
    ///     · <c>IsBossZone</c> 这片区域是不是 boss 房（字母 "B" 的块）
    ///     · <c>BossCell</c>   boss 固定落点（矩阵坐标，B 块里模板第 18 行 18 列）
    ///     · <c>Finish</c>()   怪物潮打完后**必须**调一次，否则玩家永远被锁在里面
    ///
    ///   典型写法：
    ///
    ///       IEnumerator MyWave(MonsterZoneArgs zone)
    ///       {
    ///           foreach (var cell in zone.Cells) SpawnEnemy(cell);
    ///           while (AnyEnemyAlive()) yield return null;
    ///           zone.Finish();       // ← 一定要调
    ///       }
    /// ══════════════════════════════════════════════════════════════
    /// </summary>
    [DefaultExecutionOrder(-90)]
    [AddComponentMenu("Echo Puzzle/Dungeon/Monster Zone")]
    public sealed class MonsterZone : MonoBehaviour
    {
        public static MonsterZone Instance { get; private set; }

        // ------------------------------------------------------------------ 怪物潮事件

        /// <summary>
        /// 玩家踏入一片新的怪物区时触发。
        ///
        /// ★ 订阅者**必须**在怪物潮结束时调用 <see cref="MonsterZoneArgs.Finish"/>，
        ///   否则封锁永远不会解除。
        ///
        /// ★ 如果**没有**任何订阅者，会当作"怪物潮瞬间结束"，
        ///   立即解除封锁（方便先跑通流程）。
        /// </summary>
        public static event System.Action<MonsterZoneArgs> OnMonsterWave;

        /// <summary>怪物潮的参数。</summary>
        public sealed class MonsterZoneArgs
        {
            /// <summary>这片怪物区包含的格子（矩阵坐标）。</summary>
            public readonly List<Vector2Int> Cells;

            /// <summary>这片区域的世界坐标中心。</summary>
            public readonly Vector3 Center;

            /// <summary>矩阵一格对应的世界尺寸。</summary>
            public readonly float CellSize;

            /// <summary>
            /// 这片区域是不是 boss 房（字母 "B" 的块）。
            ///
            /// ★ true  = 怪物潮只刷**一只** boss，位置固定为 <see cref="BossCell"/>；
            /// ★ false = 普通怪物房，按常规的"多波多只"刷怪。
            /// </summary>
            public readonly bool IsBossZone;

            /// <summary>
            /// boss 固定落点（矩阵坐标）= B 块里模板第 18 行 18 列。
            /// 只有 <see cref="IsBossZone"/> 为 true 时才有意义。
            /// </summary>
            public readonly Vector2Int BossCell;

            private readonly System.Action m_OnFinish;
            private bool m_Finished;

            public MonsterZoneArgs(List<Vector2Int> cells, Vector3 center,
                                   float cellSize, System.Action onFinish,
                                   bool isBossZone = false,
                                   Vector2Int bossCell = default(Vector2Int))
            {
                Cells    = cells;
                Center   = center;
                CellSize = cellSize;
                IsBossZone = isBossZone;
                BossCell   = bossCell;
                m_OnFinish = onFinish;
            }

            /// <summary>
            /// 怪物潮结束：解除封锁。
            ///
            /// ★ 重复调用无副作用。
            /// </summary>
            public void Finish()
            {
                if (m_Finished) return;
                m_Finished = true;

                m_OnFinish?.Invoke();
            }
        }

        // ------------------------------------------------------------------ 配置

        [Header("封锁外观")]
        [Tooltip("封锁时在区域边界生成的立方体材质。留空则用默认材质")]
        public Material barrierMaterial;

        [Tooltip("封锁用的立方体边长（米）")]
        [Min(0.1f)] public float barrierCellSize = 1f;

        [Header("解除封锁时")]
        [Tooltip("★ 触发怪物房、**封锁**时，是否播一次 Trigger 分类的随机音效。\n\n" +
                 "★ 音效来自 Assets/Resources/AudioPack/Trigger/ 下的所有音频，\n" +
                 "  每次随机挑一个（走 AudioSystem.Cue.Trigger）。")]
        public bool playLockSound = true;

        [Tooltip("★ 怪物潮结束、解除封锁时，是否播一次 Trigger 分类的随机音效。\n\n" +
                 "★ 音效来自 Assets/Resources/AudioPack/Trigger/ 下的所有音频，\n" +
                 "  每次随机挑一个（走 AudioSystem.Cue.Trigger）。")]
        public bool playUnlockSound = true;

        [Tooltip("★ 怪物潮结束、解除封锁时，是否在被视为 1 的出口门格子处各释放一次光带。")]
        public bool emitUnlockSonar = true;

        [Tooltip("★ 解除封锁时，在开放门口释放的光带参数。\n\n" +
                 "★ 这套参数可以独立调，不影响玩家自己的脚步光带。")]
        public SonarEmitSettings unlockSonar = new SonarEmitSettings
        {
            // ★ 这套参数可以独立调，不影响玩家自己的脚步光带
            rayCount90      = 16,
            rayCount30      = 0,
            rayCount60      = 0,
            diffuseCount15  = 0,
            diffuseCount45  = 0,
            diffuseCount75  = 0,
            hemisphereCount = 0,

            range           = 90f,
            speed           = 9f,
            trailSpacing    = 0.3f,
            maxBounces      = 4,
            bounceFalloff   = 0.65f,
            bounceLifeLoss  = 0.35f,

            lifetime        = 10f,
            tailLinger      = 20f,
            intensity       = 1f,
            holdAlpha       = true,

            color           = new Color(255f / 255f, 185f / 255f, 0f, 1f),
            width           = 0.1f,
        };

        [Tooltip("★ 解封光带的**世界 Y 高度**（米）。需求：0.6\n\n" +
                 "★ 这是绝对世界 Y 坐标，不是相对格子的偏移。")]
        public float unlockSonarHeight = 0.8f;

        [Header("调试")]
        [Tooltip("没有订阅者时，等多久自动结束怪物潮（秒）。0 = 立刻结束")]
        [Min(0f)] public float autoFinishDelay = 3f;

        // ------------------------------------------------------------------ 运行时

        /// <summary>这一层所有怪物区的格子 -> 是否已经触发过。</summary>
        private readonly HashSet<Vector2Int> m_AllCells = new HashSet<Vector2Int>();

        /// <summary>已经触发过的怪物区（用格子集合里的任一格作代表）。</summary>
        private readonly HashSet<Vector2Int> m_Consumed = new HashSet<Vector2Int>();

        /// <summary>当前正在封锁中的区域格子。</summary>
        private readonly HashSet<Vector2Int> m_Locked = new HashSet<Vector2Int>();

        /// <summary>
        /// boss 房（字母 "B"）里的怪物区格子。
        ///
        /// ★ 区域里只要有一格在这里，这次怪物潮就走 boss 流程（只刷一只 boss）。
        /// ★ 空集合 = 这张图没有 boss 房。
        /// </summary>
        private readonly HashSet<Vector2Int> m_BossCells = new HashSet<Vector2Int>();

        /// <summary>boss 固定落点（矩阵坐标）；(-1,-1) = 这张图没有 boss 房。</summary>
        private Vector2Int m_BossAnchorCell = new Vector2Int(-1, -1);

        /// <summary>封锁用的方块。</summary>
        private readonly List<GameObject> m_Barriers = new List<GameObject>();

        /// <summary>正在等待的怪物潮数量。</summary>
        private int m_PendingWaves;

        private float m_CellSize = 1f;
        private Transform m_BarrierRoot;
        private Texture2D m_Black;

        private PlayerController m_Player;

        // ------------------------------------------------------------------ 生命周期

        private void Awake()
        {
            Instance = this;

            var rootGo = new GameObject("[生成] 怪物区封锁");
            rootGo.transform.SetParent(transform, false);
            m_BarrierRoot = rootGo.transform;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (m_Black != null) Destroy(m_Black);
        }

        private void Update()
        {
            if (m_Player == null) m_Player = FindObjectOfType<PlayerController>();
            if (m_Player == null) return;

            // 正在封锁 / 正在打怪物潮 -> 不检测新的区域
            if (m_PendingWaves > 0) return;

            Vector2Int cell = WorldToCell(m_Player.FootPosition);

            if (!m_AllCells.Contains(cell)) return;       // 不在怪物区
            if (m_Consumed.Contains(cell)) return;        // 这片已经打过了

            EnterZone(cell);
        }

        // ------------------------------------------------------------------ 注册（由地图生成器调用）

        /// <summary>
        /// 地图生成后调用：把这层的怪物区格子交给本组件。
        ///
        /// ★ 每次建图都会重置"已触发"记录 —— 新的一层怪物区要重新打。
        /// </summary>
        /// <param name="cells">矩阵里所有数字 3 的格子。</param>
        /// <param name="cellSize">矩阵一格的世界尺寸。</param>
        /// <param name="bossCells">
        /// boss 房（字母 "B"）里的 3 格子。传 null / 空 = 这张图没有 boss 房。
        /// </param>
        /// <param name="bossAnchorCell">
        /// boss 固定落点（矩阵坐标，B 块里模板第 18 行 18 列）。
        /// 传 (-1,-1) = 没有 boss 房。
        /// </param>
        public void RegisterCells(List<Vector2Int> cells, float cellSize,
                                  List<Vector2Int> bossCells = null,
                                  Vector2Int bossAnchorCell = default(Vector2Int))
        {
            m_CellSize = Mathf.Max(0.1f, cellSize);

            m_AllCells.Clear();
            m_Consumed.Clear();

            ClearBarriers();
            m_Locked.Clear();
            m_PendingWaves = 0;

            m_BossCells.Clear();
            m_BossAnchorCell = new Vector2Int(-1, -1);

            if (bossCells != null)
            {
                for (int i = 0; i < bossCells.Count; i++)
                    m_BossCells.Add(bossCells[i]);

                if (m_BossCells.Count > 0) m_BossAnchorCell = bossAnchorCell;
            }

            if (cells == null) return;

            for (int i = 0; i < cells.Count; i++)
                m_AllCells.Add(cells[i]);

        }

        /// <summary>
        /// 【重开一局】清掉所有"已经打过"的进度。
        ///
        /// ★ 只清进度，不动格子布局（m_AllCells）——
        ///   布局是地图生成时注册的，重开时地图会重新生成（RegisterCells 也会再跑一次）。
        ///
        /// ★ 同时解锁所有被锁的区域、清掉屏障，免得上一局锁着的门带到新一局。
        /// </summary>
        public void ResetProgress()
        {
            m_Consumed.Clear();
            m_Locked.Clear();
            m_PendingWaves = 0;

            ClearBarriers();
        }

        // ------------------------------------------------------------------ 进入

        /// <summary>玩家踏入了某个怪物区格子。</summary>
        private void EnterZone(Vector2Int startCell)
        {
            // ---- 1) 找出相连的一整片 ----
            List<Vector2Int> region = FloodFill(startCell);

            if (region.Count == 0) return;

            // ---- 2) 标记为"已触发" ----
            for (int i = 0; i < region.Count; i++)
                m_Consumed.Add(region[i]);

            // ---- 3) 封锁 ----
            m_Locked.Clear();
            for (int i = 0; i < region.Count; i++)
                m_Locked.Add(region[i]);

            BuildBarriers(region);

            // ★★ 封锁墙建好后立刻重烘一次 NavMesh ★★
            //
            //   原因：NavMesh 是“烘焙快照”，运行中新生成的封锁墙不会自动进入它。
            //   不重烘的话，NavMesh 里仍然是一整片连续可走地面 ——
            //   怪物会把“射击位 / 巡逻点”选到封锁区外面，然后被物理墙顶住，
            //   表现为贴墙抖动、卡死。
            //
            //   NavMeshRuntimeBaker 的规则：非地板（y 不是 0/8、不是斜坡）的
            //   一切有碰撞体网格都按 NotWalkable 处理，
            //   所以这里刚生成的 0.2 厚封锁墙会被正确“挖”成障碍。
            NavMeshRuntimeBaker.Rebuild();

            // ---- 4) 算中心（给怪物潮刷怪用）----
            Vector3 center = Vector3.zero;
            for (int i = 0; i < region.Count; i++)
                center += new Vector3(region[i].x, 0f, region[i].y) * m_CellSize;
            center /= Mathf.Max(1, region.Count);
            center.y = 1f;


            // ---- 5) 广播怪物潮事件 ----
            //
            //   ★ boss 房（字母 "B"）：这片区域里有任何一格属于 B 块，
            //     这次怪物潮就走 boss 流程，并把"模板第 18 行 18 列"的落点带上。
            bool isBossZone = false;

            if (m_BossCells.Count > 0)
            {
                for (int i = 0; i < region.Count; i++)
                {
                    if (m_BossCells.Contains(region[i])) { isBossZone = true; break; }
                }
            }

            TriggerWave(region, center, isBossZone, m_BossAnchorCell);
        }

        /// <summary>触发怪物潮。</summary>
        private void TriggerWave(List<Vector2Int> region, Vector3 center,
                                 bool isBossZone, Vector2Int bossCell)
        {
            m_PendingWaves++;

            // ★★ 封锁瞬间的音效 ★★
            //
            //   需求："在触发怪物房封锁时和解除封锁时一样都会播放一个 Trigger
            //         文件夹的随机音效。"
            if (playLockSound)
                AudioSystem.Play(AudioSystem.Cue.Trigger);

            var args = new MonsterZoneArgs(region, center, m_CellSize, OnWaveFinished,
                                           isBossZone, bossCell);

            if (OnMonsterWave != null)
            {
                // ★ 有订阅者：交给它处理，它打完后会调 args.Finish()
                OnMonsterWave.Invoke(args);
            }
            else
            {
                // ★ 没有订阅者（还没接怪物潮）：按 autoFinishDelay 自动结束，
                //   这样流程能先跑通。
                StartCoroutine(AutoFinish(args));
            }
        }

        private IEnumerator AutoFinish(MonsterZoneArgs args)
        {
            if (autoFinishDelay > 0f)
                yield return new WaitForSeconds(autoFinishDelay);

            args.Finish();
        }

        /// <summary>怪物潮结束：解除封锁。</summary>
        private void OnWaveFinished()
        {
            m_PendingWaves = Mathf.Max(0, m_PendingWaves - 1);

            // 还有别的怪物潮在跑 -> 先不解除
            if (m_PendingWaves > 0) return;

            // ★★ 先拍下此刻锁定的区域（清空 m_Locked 之后就拿不到了）★★
            var unlockedRegion = new List<Vector2Int>(m_Locked);

            m_Locked.Clear();
            ClearBarriers();

            // ★★ 解除封锁的表现：音效 + 出口门光带 ★★
            //
            //   需求：
            //     1) 播放 AudioPack/Trigger 里的随机音效
            //     2) 该房间**所有被视为 1 的 4 单元格**各释放一次光带
            if (playUnlockSound)
                AudioSystem.Play(AudioSystem.Cue.Trigger);

            // ★★ 封锁解除后也要重烘一次 ★★
            //
            //   墙是 Destroy（延迟到帧末才真正销毁）的，
            //   如果立刻重烘，封锁墙还在场景里，NavMesh 会继续把它们当障碍，
            //   “洞”会保留下来 —— 怪物以后会绕开一片已经不存在的墙。
            //   所以等一帧，确认墙已经销毁，再重建 NavMesh。
            //
            // ★★ 光带也放到**等一帧之后**再发 ★★
            //
            //   原因：封锁墙高 6 米（y = 0..6），而解封光带在 y = 0.6~0.8。
            //   Destroy 是延迟到帧末的，如果在 ClearBarriers() 之后**当帧**
            //   就发光线，声波一出生就会被仍在地图上的封锁墙吞掉 ——
            //   在门口根本看不到任何光带。
            if (emitUnlockSonar)
                StartCoroutine(EmitUnlockSonarAfterBarriersGone(unlockedRegion));

            StartCoroutine(RebuildNavMeshNextFrame());

        }

        /// <summary>
        /// 解除封锁时，在所有**带标记的出口门格子**（矩阵值 = 5）处各释放一次光带。
        ///
        /// ★ 需求："等到怪物潮结束后，带有这个标记的格子全部会在 y=0.8 处发射声波。"
        ///
        ///   就是遍历一遍标记格子，每格发一个声波。不分走廊、不分段。
        /// </summary>
        private void EmitUnlockSonar(List<Vector2Int> region)
        {
            SonarSystem sonar = FindObjectOfType<SonarSystem>();
            if (sonar == null) return;

            // ★ 所有带标记的门格子（全图）
            List<Vector2Int> gateCells = DungeonMapGenerator.FindOpenGateCells();


            // 区域集合（用来判断某扇门属于哪个房间）
            var inRegion = region != null ? new HashSet<Vector2Int>(region) : new HashSet<Vector2Int>();

            float s = Mathf.Max(0.1f, m_CellSize);
            float y = Mathf.Max(0f, unlockSonarHeight);

            // 方位角起点：朝北（环境效果，不依赖玩家朝向）
            Vector3 facing = Vector3.forward;

            // ★★ 先把门格子分组，只保留"整条走廊里至少有一格挨着本房间"的那些 ★★
            //
            //   ⚠ 不能逐格用 IsAdjacentToRegion 过滤 —— 那样只有**紧挨怪物区
            //     的那一格**能通过，走廊朝外延伸的其余格子全被丢掉。
            //     （实测：一侧 6 格的走廊只发出 5 个、甚至更少）
            //
            //   ✔ 正确做法：门走廊是一个整体，
            //     只要这条走廊里**任意一格**挨着本房间的怪物区，整条走廊都发光带。
            var gateSet = new HashSet<Vector2Int>(gateCells);
            var visited = new HashSet<Vector2Int>();
            var toEmit = new List<Vector2Int>(gateCells.Count);

            for (int i = 0; i < gateCells.Count; i++)
            {
                Vector2Int seed = gateCells[i];
                if (visited.Contains(seed)) continue;

                // ---- 取出一条完整走廊（4-邻接连通块）----
                var comp = new List<Vector2Int>();
                var stack = new Stack<Vector2Int>();
                stack.Push(seed);
                visited.Add(seed);

                while (stack.Count > 0)
                {
                    Vector2Int c = stack.Pop();
                    comp.Add(c);

                    Vector2Int[] nb =
                    {
                        c + Vector2Int.right, c + Vector2Int.left,
                        c + new Vector2Int(0, 1), c + new Vector2Int(0, -1),
                    };

                    for (int k = 0; k < 4; k++)
                    {
                        if (!gateSet.Contains(nb[k])) continue;
                        if (!visited.Add(nb[k])) continue;
                        stack.Push(nb[k]);
                    }
                }

                // ---- 这条走廊挨着本房间吗 ----
                bool belong = false;
                for (int k = 0; k < comp.Count && !belong; k++)
                    belong = IsAdjacentToRegion(comp[k], inRegion);

                if (!belong) continue;

                // ---- 整条走廊全部加入发射列表 ----
                toEmit.AddRange(comp);
            }

            for (int i = 0; i < toEmit.Count; i++)
            {
                Vector2Int c = toEmit[i];

                Vector3 world = new Vector3(c.x * s, y, c.y * s);

                sonar.Emit(world, unlockSonar.ToEmitParams(facing));
            }
        }

        /// <summary>
        /// 这个格子的四邻里，有没有属于给定区域的格子。
        /// </summary>
        private static bool IsAdjacentToRegion(Vector2Int c, HashSet<Vector2Int> region)
        {
            if (region == null || region.Count == 0) return false;

            if (region.Contains(c)) return true;
            if (region.Contains(c + Vector2Int.right)) return true;
            if (region.Contains(c + Vector2Int.left))  return true;
            if (region.Contains(c + new Vector2Int(0,  1))) return true;
            if (region.Contains(c + new Vector2Int(0, -1))) return true;

            return false;
        }

        /// <summary>
        /// 等一帧（让 ClearBarriers 的 Destroy 真正生效）后，
        /// 再在门口释放解封光带。
        ///
        /// ★ 封锁墙 y 范围 0..6，而光带在 y≈0.8 ——
        ///   不等到墙消失就发，声波会被墙直接挡掉。
        /// </summary>
        private IEnumerator EmitUnlockSonarAfterBarriersGone(List<Vector2Int> region)
        {
            // 两帧更保险：Destroy 在帧末处理，
            // 再等一帧确保物理场景也已经刷新
            yield return null;
            yield return null;

            EmitUnlockSonar(region);
        }

        /// <summary>
        /// 等一帧再重烘 NavMesh（给 Destroy 留出真正销毁的时机）。
        /// </summary>
        private IEnumerator RebuildNavMeshNextFrame()
        {
            yield return null;
            NavMeshRuntimeBaker.Rebuild();
        }

        // ------------------------------------------------------------------ 洪水填充

        /// <summary>
        /// 从 <paramref name="start"/> 出发，找出所有**相连**的怪物区格子
        /// （上下左右四邻接）。
        /// </summary>
        private List<Vector2Int> FloodFill(Vector2Int start)
        {
            var result  = new List<Vector2Int>(64);
            var visited = new HashSet<Vector2Int>();

            var stack = new Stack<Vector2Int>();
            stack.Push(start);
            visited.Add(start);

            while (stack.Count > 0)
            {
                Vector2Int c = stack.Pop();
                result.Add(c);

                TryPush(c + Vector2Int.right);
                TryPush(c + Vector2Int.left);
                TryPush(c + new Vector2Int(0, 1));
                TryPush(c + new Vector2Int(0, -1));
            }

            return result;

            void TryPush(Vector2Int n)
            {
                if (visited.Contains(n)) return;
                if (!m_AllCells.Contains(n)) return;

                visited.Add(n);
                stack.Push(n);
            }
        }

        // ------------------------------------------------------------------ 封锁几何

        /// <summary>
        /// 沿这片区域的**边界**生成一圈方块，把玩家关在里面。
        ///
        /// 做法：对区域里每个格子，检查四邻接 —— 邻居不在这片区域里
        /// 就在那一侧立一块（从这里可以走出去，需要挡住）。
        /// </summary>
        private void BuildBarriers(List<Vector2Int> region)
        {
            var inRegion = new HashSet<Vector2Int>(region);

            for (int i = 0; i < region.Count; i++)
            {
                Vector2Int c = region[i];

                if (!inRegion.Contains(c + Vector2Int.right)) SpawnWall(c, Vector3.right);
                if (!inRegion.Contains(c + Vector2Int.left))  SpawnWall(c, Vector3.left);
                if (!inRegion.Contains(c + new Vector2Int(0,  1))) SpawnWall(c, Vector3.forward);
                if (!inRegion.Contains(c + new Vector2Int(0, -1))) SpawnWall(c, Vector3.back);
            }
        }

        /// <summary>在格子 c 的某一侧立一块墙。</summary>
        private void SpawnWall(Vector2Int c, Vector3 dir)
        {
            float s = m_CellSize;

            // 格子的世界中心
            Vector3 center = new Vector3(c.x * s, 0f, c.y * s);

            // 半格偏移 -> 贴到格子边缘
            Vector3 pos = center + dir * (s * 0.5f);

            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);

            go.name = $"[封锁] ({c.x},{c.y}) {dir}";
            go.transform.SetParent(m_BarrierRoot, false);

            // 薄墙：沿墙方向厚 0.2，高 6
            Vector3 scale;
            if (Mathf.Abs(dir.x) > 0.5f) scale = new Vector3  (0.2f, 6f, s);
            else                          scale = new Vector3  (s, 6f, 0.2f);

            go.transform.position   = pos + Vector3.up * 3f;
            go.transform.localScale = scale;

            // 墙要有碰撞体才会真的挡住玩家（Primitive Cube 自带 BoxCollider）

            // ★ 材质留空 -> 用完全透明的 EchoPuzzle/Invisible
            //   （封锁墙在游戏里应该看不见，玩家只能从"走不过去"察觉）
            var r = go.GetComponent<Renderer>();
            if (r != null)
                r.sharedMaterial = barrierMaterial != null ? barrierMaterial : InvisibleMat();

            m_Barriers.Add(go);
        }

        /// <summary>封锁墙的默认材质（完全透明）。</summary>
        private static Material s_InvisibleMat;

        private static Material InvisibleMat()
        {
            if (s_InvisibleMat != null) return s_InvisibleMat;

            s_InvisibleMat = Resources.Load<Material>("EchoRuntime/EchoInvisible");

#if UNITY_EDITOR
            if (s_InvisibleMat == null)
                s_InvisibleMat = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(
                    "Assets/EchoPuzzle/Materials/EchoInvisible.mat");
#endif

            if (s_InvisibleMat == null)
            {
                Shader sh = Shader.Find("EchoPuzzle/Invisible");
                if (sh != null)
                {
                    s_InvisibleMat = new Material(sh)
                    {
                        name      = "MonsterZoneInvisibleRuntime",
                        hideFlags = HideFlags.DontSave,
                    };
                }
            }

            return s_InvisibleMat;
        }

        private void ClearBarriers()
        {
            for (int i = 0; i < m_Barriers.Count; i++)
                if (m_Barriers[i] != null) Destroy(m_Barriers[i]);

            m_Barriers.Clear();
        }

        // ------------------------------------------------------------------ 工具

        /// <summary>世界坐标 -> 矩阵格子。</summary>
        private Vector2Int WorldToCell(Vector3 world)
        {
            float s = Mathf.Max(0.1f, m_CellSize);

            return new Vector2Int(
                Mathf.RoundToInt(world.x / s),
                Mathf.RoundToInt(world.z / s));
        }
    }
}