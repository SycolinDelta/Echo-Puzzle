using System.Collections;
using UnityEngine;
using UnityEngine.AI;

namespace EchoPuzzle.Dungeon
{
    /// <summary>
    /// 【地牢模式】怪物行为（挂在 PuzzleMonster 实例上，由怪物潮刷怪时自动添加）。
    ///
    /// ══════════════════════════════════════════════════════════════
    /// 行为总览（严格按需求实现）
    ///
    ///   · DungeonPlayerFound == 0
    ///       怪物**完全静止**、不开火、不释放怪物光带。
    ///       一旦感知圆柱捕捉到玩家的光带（PuzzleMonster 里判定 m_LastSonarBirthTime），
    ///       本怪物自己的 DungeonPlayerFound 置 1 —— 其它怪物各自独立判定。
    ///
    ///   · DungeonPlayerFound == 1
    ///       持续释放怪物光带（由 PuzzleMonster.UpdateDungeonMode 负责）。
    ///       每次"视线检测"按结果二选一：
    ///         A. 视线通畅 -> 在怪物周围找"能看见玩家快照点"的射击位，
    ///                        走过去，停 2~4 秒，再刷新快照点、再检测；
    ///         B. 视线不通 -> 朝玩家快照点寻路靠近，
    ///                        走满 3~5 米或离快照点 <= 3 米就停，停 1 秒，
    ///                        再刷新快照点、再检测。
    ///       视线通畅时进入**开火状态**，朝玩家**实时**位置持续齐射；
    ///       直到下一次视线检测判为不通才退出开火。
    ///
    ///   · 快照点 (x0, y0, z0)
    ///       只在三个时机刷新：0->1 时、每次停顿结束时。
    ///       它**不会**随玩家移动实时更新；只有开火瞄准用的是实时坐标。
    ///
    ///   · DungeonPlayerFound 变回 0
    ///       所有动作立即停止（清移动目标、停止停顿、停止开火）。
    ///       目前没有主动置 0 的入口，但代码已支持。
    /// ══════════════════════════════════════════════════════════════
    ///
    /// ★ 本组件只在地牢模式创建（怪物潮刷怪时 AddComponent），
    ///   探秘模式没有它，所以下面的逻辑不会影响探秘模式。
    /// ★ 水平移动复用 PuzzleMonster 的 NavMesh 寻路 / 重力悬浮管道
    ///   （DungeonSetMoveTarget / DungeonClearMoveTarget / DungeonHasMoveTarget）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DungeonEnemy : MonoBehaviour
    {
        // ---------------------------------------------------------------- 开火

        // ---------------------------------------------------------------- 刷出停顿

        [Header("刷出停顿")]
        [Tooltip("★ 怪物刷出后先停顿多久才开始正常行动（秒）。需求：1.5 秒\n\n" +
                 "★ 这段时间里怪物**持续无敌**（玩家子弹打不掉血），\n" +
                 "  也不会移动 / 开火 / 发光带。")]
        [Min(0f)] public float spawnPause = 1.5f;

        [Header("开火")]
        [Tooltip("两次齐射之间的基础间隔（秒）。\n\n" +
                 "★ 需求：减少 1 秒 —— 原来是 4，现在 3。")]
        [Min(0.2f)] public float fireInterval = 2f;

        [Tooltip("★ 每次开火前的「蓄力」停顿（秒）。需求：1 秒\n\n" +
                 "★ 这 1 秒里该怪物**不开火**（攒着等蓄力结束才打），\n" +
                 "  并且头顶右上角会显示一个**纯红色的小感叹号**。")]
        [Min(0f)] public float fireWindup = 1f;



        [Tooltip("每次齐射发射几颗子弹")]
        [Range(1, 12)] public int bulletsPerVolley = 5;

        [Tooltip("子弹飞行速度（米/秒）")]
        [Min(1f)] public float bulletSpeed = 10f;

        [Tooltip("命中玩家造成的伤害")]
        [Min(1)] public int bulletDamage = 3;

        [Tooltip("子弹“光带经过时淡入”的检测半径（米），传给子弹")]
        [Min(0.1f)] public float bulletSonarDetectRadius = 1.5f;

        [Tooltip("子弹淡入后的颜色（默认纯红）")]
        public Color bulletColor = new Color(1f, 0f, 0f, 1f);

        [Tooltip("齐射的水平散布角度（度）。0 = 全部瞄同一点")]
        [Range(0f, 60f)] public float bulletSpreadDegrees = 12f;

        [Tooltip("同一波子弹之间的发射间隔（秒）。0 = 同时发射")]
        [Min(0f)] public float volleyStagger = 0.15f;

        // ---------------------------------------------------------------- 移动

        [Header("移动（地牢模式）")]
        [Tooltip("视线通畅时，以怪物自己为圆心搜索“射击位”的半径（米）")]
        [Min(0.5f)] public float wanderRadius = 9f;

        [Tooltip("射击位到玩家快照点的最小**水平**距离（米）")]
        [Min(0f)] public float minPlayerDistance = 3f;

        [Tooltip("射击位到怪物当前位置的最小**水平**距离（米）。\n" +
                 "找不到满足点时会自动放宽这一条")]
        [Min(0f)] public float minSelfDistance = 0f;

        [Tooltip("到达射击位后的停顿区间（秒，min~max）。每只怪物各自独立随机")]
        public Vector2 sightPause = new Vector2(1f, 4f);

        [Tooltip("视线不通、朝玩家靠近、停下后的停顿区间（秒，min~max）。每只怪物各自独立随机")]
        public Vector2 approachPause = new Vector2(0.5f, 1f);

        [Tooltip("视线不通时，每次靠近随机移动的距离区间（米，min~max）")]
        public Vector2 approachDistance = new Vector2(7f, 9f);

        [Tooltip("视线不通时，靠近到离玩家快照点多近就停（水平距离，米）")]
        [Min(0f)] public float approachStopDistance = 3f;

        [Tooltip("“到达射击位”的判定半径（水平距离，米）")]
        [Min(0.05f)] public float arriveRadius = 0.35f;

        [Tooltip("单次移动的最长持续时间（秒）。超时强制结束，避免被墙卡住后死循环")]
        [Min(1f)] public float moveTimeout = 8f;

        // ---------------------------------------------------------------- 捕获玩家

        /// <summary>
        /// 【地牢模式】是否已捕捉到玩家位置。
        ///
        /// ★ 只在**地牢模式**变动：
        ///     · 默认 0（静止、不开火、不发光带）
        ///     · 感知到玩家光带后变 1（只影响本怪物自己）
        ///     · 目前没有主动改回 0 的途径，但一旦变 0，
        ///       下面所有动作会立即停止
        /// </summary>
        [System.NonSerialized] public int DungeonPlayerFound;

        // ---------------------------------------------------------------- 视线

        [Header("视线")]
        [Tooltip("参与视线检测的层（被这些东西挡住就算看不到）")]
        public LayerMask losMask = ~((1 << 2) | (1 << 4) | (1 << 5));

        [Tooltip("瞄准玩家的高度：玩家脚底向上多少米（胸口）")]
        [Min(0f)] public float playerAimHeight = 1.2f;

        // ---------------------------------------------------------------- 运行时

        /// <summary>缓存的 PuzzleMonster（刷怪时和它在同一个物体上）。</summary>
        private PuzzleMonster m_Monster;

        /// <summary>当前是否处于开火状态。只在“视线检测”时切换。</summary>
        private bool m_Firing;

        /// <summary>正在移动（等到达射击位 / 等走够靠近距离）。</summary>
        private bool m_Moving;

        /// <summary>本次移动的最终目标点（自己记一份，防止被新光带覆盖）。</summary>
        private Vector3 m_MoveGoal;

        /// <summary>本次移动是不是“去射击位”（true）而不是“朝玩家靠近”（false）。</summary>
        private bool m_MoveIsSightSpot;

        /// <summary>本次移动的起点（用于统计“已经走了几米”）。</summary>
        private Vector3 m_MoveStartPos;

        /// <summary>本次靠近要走的随机距离（在 approachDistance 区间内取）。</summary>
        private float m_ApproachGoal;

        /// <summary>本次移动开始的时间（超时保险用）。</summary>
        private float m_MoveStartTime;

        /// <summary>正在停顿。</summary>
        private bool m_Pausing;

        /// <summary>停顿剩余时间。</summary>
        private float m_PauseTimer;

        /// <summary>
        /// 玩家坐标快照 (x0, y0, z0)。
        ///
        /// ★ 只在三个时机刷新：0->1 时、每次停顿结束时。
        ///   它**不随玩家移动实时更新**，视线检测 / 选点都以它为准。
        /// ★ 开火瞄准**不使用**它，用的是实时玩家坐标。
        /// </summary>
        private Vector3 m_Snapshot;

        /// <summary>玩家实时坐标（已捕捉玩家后每帧刷新，供开火瞄准使用）。</summary>
        private Vector3 m_PlayerPos;

        /// <summary>玩家实时坐标（世界空间）。</summary>
        public Vector3 PlayerPos => m_PlayerPos;

        /// <summary>当前是否处于开火状态。</summary>
        public bool IsFiring => m_Firing;


        /// <summary>刷出停顿还剩多少秒。> 0 表示还在刷出无敌期。</summary>
        private float m_SpawnPauseLeft;

        /// <summary>
        /// 刷出停顿是否还没结束（= 无敌中）。
        ///
        /// ★ 由 PuzzleMonster.DungeonTakeDamage 读取：
        ///   为 true 时忽略一切伤害。
        /// </summary>
        public bool IsSpawnInvulnerable => m_SpawnPauseLeft > 0f;

        /// <summary>刷出停顿剩余时间（秒），给调试 / HUD 用。</summary>
        public float SpawnPauseLeft => Mathf.Max(0f, m_SpawnPauseLeft);
        // ---------------------------------------------------------------- 生命周期

        private void Awake()
        {
            m_Monster = GetComponent<PuzzleMonster>();
        }

        private void OnEnable()
        {
            // 声明接管移动：PuzzleMonster.MoveTowardTarget 内部据此跳过“直追玩家”分支。
            // 探秘模式没有本组件，该开关永远为 false。
            if (m_Monster == null) m_Monster = GetComponent<PuzzleMonster>();
            if (m_Monster != null) m_Monster.DungeonMoveControlled = true;

            // ★★ 刷出停顿：1.5 秒内无敌 + 完全静止 ★★
            //
            //   需求："地牢模式的怪物在刷出时，会先停顿1.5秒再开始正常的行动。
            //         这段时间里怪物持续无敌。"
            //
            //   ★ 只在**地牢模式**生效。探秘模式没有 DungeonEnemy 组件，
            //     这条逻辑根本不会跑到。
            m_SpawnPauseLeft = Mathf.Max(0f, spawnPause);

            if (m_SpawnPauseLeft > 0f)
            {
                // 期间不做任何动作
                m_Moving  = false;
                m_Pausing = false;
                m_Firing  = false;

                if (m_Monster != null) m_Monster.DungeonClearMoveTarget();
            }
        }

        private void OnDisable()
        {
            if (m_Monster != null) m_Monster.DungeonMoveControlled = false;
        }

        // ---------------------------------------------------- 由 PuzzleMonster.UpdateDungeonMode 驱动

        /// <summary>
        /// 【地牢模式】每帧行为入口（由 PuzzleMonster.UpdateDungeonMode 调用）。
        ///
        /// 状态机只有四个状态：
        ///   静止（found == 0） / 停顿中 / 移动中 / 需要做一次视线检测
        /// </summary>
        public void PatrolTick(float now)
        {
            if (m_Monster == null) m_Monster = GetComponent<PuzzleMonster>();
            if (m_Monster == null) return;

            // ★★ 刷出停顿：还没到时间就完全静止 + 无敌 ★★
            //
            //   需求："地牢模式的怪物在刷出时，会先停顿1.5秒再开始正常的行动。
            //         这段时间里怪物持续无敌。"
            //
            //   ★ 用 Time.deltaTime 递减（不依赖传入的 now 起点），
            //     这样即使怪物在暂停中被激活 / 恢复也能正常走完。
            if (m_SpawnPauseLeft > 0f)
            {
                m_SpawnPauseLeft -= Time.deltaTime;

                StopEverything();
                return;
            }

            // ---------------------------------------------------------------
            // found == 0：完全静止，什么都不做
            // ---------------------------------------------------------------
            if (DungeonPlayerFound == 0)
            {
                // ★ DungeonPlayerFound 现在由**外部**置 1：
                //     · 玩家开火时，所有怪物立即置 1（见 MonsterWaveManager / DungeonWeapon）
                //     · 玩家脚步光带扫到某只怪时，那只怪置 1（见 PuzzleMonster）
                //   所以这里只要看到它变成 1，就进入正常流程。
                if (DungeonPlayerFound == 0)
                {
                    // 还没发现玩家：清掉任何残留目标，保持静止
                    StopEverything();
                    return;
                }

                UpdatePlayerPos();
                RefreshSnapshot();
                m_Firing = false;

                // ★ 首次发现玩家先“愣”一个随机的小间隔（0 ~ 0.4 秒），再做第一次视线检测。
                //
                //   为什么需要这一步：
                //     一波怪物往往是同一帧刷出来的，如果它们的感知圆柱又同时被同一片光带扫到，
                //     那它们会同一帧发现玩家、同一帧迈步、同一帧开火 —— 看起来像“整体抽搐”。
                //     给每只怪各自一个随机小延迟，行动节拍立刻就错开了。
                //   （每只怪独立调 Random.Range，各自持有自己的 m_PauseTimer）
                BeginPause(Random.Range(0f, 0.4f));
                return;
            }

            // ---------------------------------------------------------------
            // found == 1：每帧刷新玩家实时坐标（开火要用）
            // ---------------------------------------------------------------
            UpdatePlayerPos();

            // ---------------------------------------------------------------
            // 停顿中：计时结束 -> 刷新快照点 -> 视线检测 -> A / B
            // ---------------------------------------------------------------
            if (m_Pausing)
            {
                // ★ 停顿期间必须真的“停住”：
                //   PuzzleMonster.CaptureSonarSource 每 0.1s 会把新光带的产生点
                //   写进 m_StopPoint，如果不清掉，怪物会一边“停顿”一边被光带拖着走。
                if (m_Monster.DungeonHasMoveTarget)
                    m_Monster.DungeonClearMoveTarget();

                m_PauseTimer -= Time.deltaTime;

                if (m_PauseTimer > 0f) return;

                m_Pausing = false;

                RefreshSnapshot();


                EvaluateSightAndAct(now);
                return;
            }

            // ---------------------------------------------------------------
            // 移动中：等本次移动结束
            // ---------------------------------------------------------------
            if (m_Moving)
            {
                // ★ CaptureSonarSource 每 0.1s 会把 m_StopPoint 改写成新光带的产生点。
                //   这里把“本次移动的既定目标”写回去，保证怪物走的是自己选的点
                //   （射击位 / 玩家快照点），而不是被脚步光带牵着跑。
                if (m_Monster.DungeonHasMoveTarget)
                {
                    Vector3 cur  = m_Monster.DungeonMoveTarget;
                    Vector2 a    = new Vector2(cur.x, cur.z);
                    Vector2 b    = new Vector2(m_MoveGoal.x, m_MoveGoal.z);

                    if (Vector2.Distance(a, b) > 0.02f)
                        m_Monster.DungeonSetMoveTarget(m_MoveGoal);
                }

                if (!IsMoveFinished(now)) return;

                FinishMoveAndPause();
                return;
            }

            // ---------------------------------------------------------------
            // 兜底（理论上走不到）：直接做一次视线检测
            // ---------------------------------------------------------------
            RefreshSnapshot();
            EvaluateSightAndAct(now);
        }

        /// <summary>刷新玩家实时坐标（已捕捉玩家后每帧取最新位置）。</summary>
        private void UpdatePlayerPos()
        {
            if (m_Monster == null) return;

            if (m_Monster.Player != null)
                m_PlayerPos = m_Monster.Player.transform.position;
            else
                m_PlayerPos = m_Monster.DungeonLastPlayerPos;
        }

        /// <summary>
        /// 刷新玩家坐标快照 (x0, y0, z0)。
        ///
        /// ★ 只在“0->1 那一刻”和“每次停顿结束”调用，
        ///   移动过程中、开火过程中都**不调用** —— 它就是这一轮的判定基准。
        /// </summary>
        private void RefreshSnapshot()
        {
            if (m_Monster == null) return;

            if (m_Monster.Player != null)
                m_Snapshot = m_Monster.Player.transform.position;
            else
                m_Snapshot = m_Monster.DungeonLastPlayerPos;
        }

        /// <summary>
        /// 做一次视线检测（对快照点），并按结果进入 A（找射击位）或 B（朝快照点靠近）。
        ///
        /// ★ 开火状态**只在这里切换**：
        ///     通畅 -> m_Firing = true，直到下一次检测判为不通才退出
        /// </summary>
        private void EvaluateSightAndAct(float now)
        {
            m_Firing = CheckLineOfSight();


            if (m_Firing) BeginSightSpotMove(now);
            else          BeginApproach(now);
        }

        /// <summary>
        /// 停止一切动作（DungeonPlayerFound 为 0 时调用）。
        ///
        /// ★ 清移动目标、清停顿、退开火状态。
        ///   下一帧 PuzzleMonster 的发光条件也会跟着变成“不发光”。
        /// </summary>
        private void StopEverything()
        {
            if (m_Monster != null && m_Monster.DungeonHasMoveTarget)
                m_Monster.DungeonClearMoveTarget();

            m_Moving           = false;
            m_MoveIsSightSpot  = false;
            m_Pausing          = false;
            m_PauseTimer       = 0f;
            m_Firing           = false;
        }

        // ---------------------------------------------------------------- A：视线通畅 -> 找射击位

        /// <summary>
        /// 视线通畅：在怪物自己周围找“能看见玩家快照点”的射击位并走过去。
        ///
        /// 选点条件（需求原文）：
        ///   · 以怪物自己为圆心，半径 wanderRadius（默认 5 米）
        ///   · 候选点到快照点 (x0, y0, z0) 的视线通畅
        ///   · 候选点与玩家的**水平距离** >= minPlayerDistance（默认 3 米）
        ///   · 候选点与怪物当前位置的**水平距离** >= minSelfDistance（默认 2 米）
        ///   · 找不到时**取消**最后一条（与自身距离）重新找
        ///   · 仍然找不到 -> 退回 B（朝快照点靠近），避免怪物原地僵住
        /// </summary>
        private void BeginSightSpotMove(float now)
        {
            Vector3 self = transform.position;

            if (!TryPickSightSpot(self, true, out Vector3 spot))
            {
                // ★ 放宽“与怪物当前位置距离 >= 2”的限制，再找一次
                if (!TryPickSightSpot(self, false, out spot))
                {

                    BeginApproach(now);
                    return;
                }
            }

            m_Monster.DungeonSetMoveTarget(spot);

            m_MoveGoal        = spot;
            m_Moving          = true;
            m_MoveIsSightSpot = true;
            m_MoveStartPos    = self;
            m_MoveStartTime   = now;

        }

        /// <summary>
        /// 在怪物周围随机挑一个“能看见快照点”的 NavMesh 点。
        ///
        /// <paramref name="requireSelfMin2"/> 为 true 时要求候选点离怪物 >= minSelfDistance；
        /// 为 false 时只保留“离玩家 >= minPlayerDistance + 视线通畅”。
        /// </summary>
        private bool TryPickSightSpot(Vector3 self, bool requireSelfMin2, out Vector3 spot)
        {
            spot = default;

            float radius = Mathf.Max(0.5f, wanderRadius);
            float minR   = requireSelfMin2 ? Mathf.Clamp(minSelfDistance, 0f, radius) : 0.3f;

            Vector2 flatSelf     = new Vector2(self.x, self.z);
            Vector2 flatSnapshot = new Vector2(m_Snapshot.x, m_Snapshot.z);

            // 瞄准点：快照点 + 胸口高度（和开火瞄准同一个高度口径）
            Vector3 aim = m_Snapshot + Vector3.up * playerAimHeight;

            const int tries = 16;

            for (int i = 0; i < tries; i++)
            {
                float ang = Random.Range(0f, Mathf.PI * 2f);
                float r   = Random.Range(minR, radius);

                Vector3 want = self + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * r;

                if (!NavMesh.SamplePosition(want, out NavMeshHit hit, 2f, NavMesh.AllAreas))
                    continue;

                Vector2 flatHit = new Vector2(hit.position.x, hit.position.z);

                // 与玩家快照点的水平距离 >= 3（只看 x/z）
                if (Vector2.Distance(flatHit, flatSnapshot) < minPlayerDistance) continue;

                // 与怪物当前位置的水平距离 >= 2（只用第一轮要求）
                if (requireSelfMin2 && Vector2.Distance(flatHit, flatSelf) < minSelfDistance) continue;

                // 候选点“眼睛”高度 = NavMesh 地面 + 怪物悬浮高度
                Vector3 eye = hit.position + Vector3.up * m_Monster.floatHeight;

                if (!HasClearShot(eye, aim)) continue;

                spot = hit.position;
                return true;
            }

            return false;
        }

        // ---------------------------------------------------------------- B：视线不通 -> 朝快照点靠近

        /// <summary>
        /// 视线不通：朝玩家快照点寻路靠近。
        ///
        /// 停止条件（满足其一）：
        ///   · 已经水平走了本次随机的 approachDistance 米（默认 3~9）
        ///   · 离快照点的水平距离 <= approachStopDistance（默认 3 米）
        ///
        /// 停下后停顿 approachPause（默认 1~1.6 秒，每只怪物各自随机）
        /// -> 刷新快照点 -> 再检测。
        /// </summary>
        private void BeginApproach(float now)
        {
            Vector3 self = transform.position;

            Vector2 flatSelf     = new Vector2(self.x, self.z);
            Vector2 flatSnapshot = new Vector2(m_Snapshot.x, m_Snapshot.z);

            // ★ 已经足够近了：不再往前挤，直接停一会儿等下一次检测
            if (Vector2.Distance(flatSelf, flatSnapshot) <= approachStopDistance)
            {
                BeginPause(RandomFrom(approachPause));
                return;
            }

            m_Monster.DungeonSetMoveTarget(m_Snapshot);

            m_MoveGoal        = m_Snapshot;
            m_Moving          = true;
            m_MoveIsSightSpot = false;
            m_MoveStartPos    = self;
            m_MoveStartTime   = now;

            float lo = Mathf.Max(0f, Mathf.Min(approachDistance.x, approachDistance.y));
            float hi = Mathf.Max(lo, Mathf.Max(approachDistance.x, approachDistance.y));
            m_ApproachGoal = Random.Range(lo, hi);
        }

        // ---------------------------------------------------------------- 移动结束 / 停顿

        /// <summary>本次移动是否已经结束。</summary>
        private bool IsMoveFinished(float now)
        {
            // ★ 目标被底层清掉（比如 PuzzleMonster 的“卡住 3 次放弃目标”）
            //   —— 也要当作本次移动结束，否则会永远停在“移动中”状态
            if (!m_Monster.DungeonHasMoveTarget) return true;

            // 超时保险：被物理墙顶住、NavMesh 规划不到等情况兜底
            if (moveTimeout > 0f && now - m_MoveStartTime >= moveTimeout)
                return true;

            Vector3 self = transform.position;
            Vector2 flatSelf = new Vector2(self.x, self.z);

            if (m_MoveIsSightSpot)
            {
                // A：到射击位的水平距离 <= arriveRadius 就算到达
                //    ★ 用自己记的 m_MoveGoal，而不是 m_Monster.DungeonMoveTarget
                //      （后者可能刚被光带的产生点覆盖掉）
                Vector2 flatTarget = new Vector2(m_MoveGoal.x, m_MoveGoal.z);

                return Vector2.Distance(flatSelf, flatTarget) <= arriveRadius;
            }

            // B：走够本次随机的 approachDistance 米，或离快照点 <= approachStopDistance
            Vector2 flatStart    = new Vector2(m_MoveStartPos.x, m_MoveStartPos.z);
            Vector2 flatSnapshot = new Vector2(m_Snapshot.x, m_Snapshot.z);

            float walked = Vector2.Distance(flatStart, flatSelf);
            if (walked >= m_ApproachGoal) return true;

            return Vector2.Distance(flatSelf, flatSnapshot) <= approachStopDistance;
        }

        /// <summary>本次移动结束：清目标，进入对应时长的停顿。</summary>
        private void FinishMoveAndPause()
        {
            if (m_Monster.DungeonHasMoveTarget)
                m_Monster.DungeonClearMoveTarget();

            m_Moving = false;

            float pause;
            if (m_MoveIsSightSpot)
            {
                // 去射击位：用 sightPause 区间
                pause = RandomFrom(sightPause);
            }
            else
            {
                // 朝玩家靠近：用 approachPause 区间
                pause = RandomFrom(approachPause);
            }

            BeginPause(pause);
        }

        /// <summary>
        /// 从 Vector2 区间里取一个随机值（秒）。
        /// 两个分量顺序写反也没关系，这里自动纠正。
        ///
        /// ★ 每只怪物都持有自己的 m_PauseTimer，
        ///   各自独立调 Random.Range —— 所以同一批刷出来的怪物
        ///   每次停顿的时长都不一样，行动时间自然错开。
        /// </summary>
        private static float RandomFrom(Vector2 range)
        {
            float lo = Mathf.Min(range.x, range.y);
            float hi = Mathf.Max(range.x, range.y);
            return Random.Range(lo, hi);
        }

        /// <summary>进入停顿。</summary>
        private void BeginPause(float duration)
        {
            m_Moving     = false;
            m_Pausing    = true;
            m_PauseTimer = Mathf.Max(0f, duration);
        }

        // ---------------------------------------------------------------- 开火

        /// <summary>
        /// 开火：处于开火状态时，每隔一段时间朝玩家**实时**位置齐射。
        /// 由 PuzzleMonster.UpdateDungeonMode 每帧调用。
        ///
        /// ★ 开火与移动完全无关：
        ///     开火状态由“视线检测”决定，移动状态由 A / B 决定，
        ///     两者可以同时进行（边走边打）。
        /// </summary>
        public void CombatTick(float now)
        {
            if (m_Monster == null) return;
            if (DungeonPlayerFound != 1) return;   // 没发现玩家 -> 不开火
            if (!m_Firing) return;                 // 视线不通 -> 不开火

            // ★★ 开火前的蓄力（前摇）★★
            //
            //   需求："在每次开火前会停顿1s"
            //
            //   流程：
            //     冷却到点 -> 进入蓄力 m_WindupLeft = 1s
            //     蓄力期间（IsFireWindup == true）：
            //         · 不开火
            //         · 头顶右上角显示**纯红色小感叹号**（MonsterHealthBar 画的）
            //     蓄力结束 -> FireVolley()，重新开始冷却
            if (m_WindupLeft > 0f)
            {
                m_WindupLeft -= Time.deltaTime;

                if (m_WindupLeft <= 0f)
                {
                    m_WindupLeft = 0f;

                    // ★ 蓄力结束 -> 真正开火
                    UpdatePlayerPos();

                    m_NextFireTime = now + fireInterval * Random.Range(0.5f, 1.5f);

                    FireVolley();
                }

                return;
            }

            if (now < m_NextFireTime) return;      // 冷却中

            // ★ 冷却到点 -> 开始蓄力（这一段不立刻开火）
            if (fireWindup > 0f)
            {
                m_WindupLeft = fireWindup;
                return;
            }

            // 蓄力时长配成 0 -> 直接开火
            UpdatePlayerPos();

            m_NextFireTime = now + fireInterval * Random.Range(0.5f, 1.5f);

            FireVolley();
        }

        /// <summary>开火蓄力剩余时间（秒）。</summary>
        private float m_WindupLeft;

        /// <summary>
        /// 是否正处于「开火前蓄力」。
        ///
        /// ★ MonsterHealthBar 读它来在怪物右上角画红色感叹号。
        /// </summary>
        public bool IsFireWindup => m_WindupLeft > 0f;



        /// <summary>下一次允许开火的时间。</summary>
        private float m_NextFireTime;

        // ---------------------------------------------------------------- 开火实现

        private void FireVolley()
        {
            Vector3 aim = GetAimPoint();
            Vector3 eye = transform.position;
            Vector3 toAim = aim - eye;
            if (toAim.sqrMagnitude < 0.01f) return;

            // 枪口放在身体外一点，避免子弹一出生就打到自己
            Vector3 muzzle = eye + toAim.normalized * 0.8f;
            Vector3 baseDir = (aim - muzzle).normalized;

            for (int i = 0; i < bulletsPerVolley; i++)
            {
                // 水平散布：把整波子弹均匀摊开在 ±spread 里
                float t = bulletsPerVolley > 1
                        ? ((float)i / (bulletsPerVolley - 1) - 0.5f) * 2f
                        : 0f;
                Vector3 dir = Quaternion.Euler(0f, bulletSpreadDegrees * t, 0f) * baseDir;
                dir += new Vector3(0f, Random.Range(-0.05f, 0.05f), 0f);

                if (volleyStagger > 0f)
                    StartCoroutine(SpawnBulletAfter(i * volleyStagger, muzzle, dir));
                else
                    DungeonMonsterBullet.Spawn(muzzle, dir, this);
            }
        }

        private IEnumerator SpawnBulletAfter(float delay, Vector3 muzzle, Vector3 dir)
        {
            if (delay > 0f) yield return new WaitForSeconds(delay);
            DungeonMonsterBullet.Spawn(muzzle, dir, this);
        }

        /// <summary>开火瞄准点：玩家**实时**胸口位置（不是快照点）。</summary>
        private Vector3 GetAimPoint()
        {
            if (m_Monster == null) return Vector3.zero;

            // ★ 直接取玩家当前实时位置
            //   （PatrolTick / CombatTick 每帧都会刷新，移动中开火也不会打偏）
            if (m_Monster.Player != null)
                return m_Monster.Player.transform.position + Vector3.up * playerAimHeight;

            // 退回最近一次捕捉到的玩家坐标
            if (DungeonPlayerFound == 1)
                return m_Monster.DungeonLastPlayerPos + Vector3.up * playerAimHeight;

            return Vector3.zero;
        }

        // ---------------------------------------------------------------- 视线

        /// <summary>现在能不能直接看见玩家**快照点**。</summary>
        private bool CheckLineOfSight()
        {
            Vector3 to = m_Snapshot + Vector3.up * playerAimHeight;
            return HasClearShot(transform.position, to);
        }

        /// <summary>from 到 to 的视线上有没有遮挡（忽略自己和玩家自身的碰撞体）。</summary>
        private bool HasClearShot(Vector3 from, Vector3 to)
        {
            Vector3 diff = to - from;
            float dist = diff.magnitude;
            if (dist < 0.05f) return true;

            Vector3 dir = diff / dist;

            RaycastHit[] hits = Physics.RaycastAll(from, dir, dist, losMask,
                                                   QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits.Length; i++)
            {
                Collider c = hits[i].collider;
                if (c == null) continue;
                if (c.transform.root == transform.root) continue;                  // 自己
                if (c.GetComponentInParent<PlayerController>() != null) continue;  // 玩家

                return false;   // 被墙 / 其它怪物挡住
            }

            return true;
        }
    }
}