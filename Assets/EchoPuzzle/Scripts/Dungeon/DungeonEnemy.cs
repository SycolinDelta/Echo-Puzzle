using System.Collections;
using UnityEngine;
using UnityEngine.AI;

namespace EchoPuzzle.Dungeon
{
    /// <summary>
    /// 【地牢模式】怪物行为（挂在 PuzzleMonster 实例上，由怪物潮刷怪时自动添加）。
    ///
    /// 探秘模式里怪物是"收到声波 -> 捕获玩家坐标 -> 冲向玩家"；
    /// 地牢模式里这套行为被**替换**为：
    ///   · 检测到玩家（捕捉到玩家声波）后做**随机运动**；
    ///   · **每隔一段时间**向玩家发射 <see cref="bulletsPerVolley"/> 颗子弹；
    ///   · 无法直接命中玩家（视线被墙挡住）时，用 NavMesh **寻路**到
    ///     一个能直接命中的位置再开火。
    ///
    /// ★ 只在地牢模式创建/使用，不影响探秘模式。
    /// ★ 本组件由 <see cref="PuzzleMonster.UpdateDungeonMode"/> 每帧驱动
    ///   （PatrolTick + CombatTick），水平移动复用 PuzzleMonster 的
    ///   NavMesh 寻路 / 重力悬浮管道（DungeonSetMoveTarget / DungeonClearMoveTarget）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DungeonEnemy : MonoBehaviour
    {
        // ---------------------------------------------------------------- 开火

        [Header("开火")]
        [Tooltip("两次齐射之间的基础间隔（秒）")]
        [Min(0.2f)] public float fireInterval = 2.5f;

        [Tooltip("每次齐射发射几颗子弹")]
        [Range(1, 12)] public int bulletsPerVolley = 3;

        [Tooltip("子弹飞行速度（米/秒）")]
        [Min(1f)] public float bulletSpeed = 9f;

        [Tooltip("命中玩家造成的伤害")]
        [Min(1)] public int bulletDamage = 1;

        [Tooltip("子弹“光带经过时淡入”的检测半径（米），传给子弹")]
        [Min(0.1f)] public float bulletSonarDetectRadius = 1.5f;

        [Tooltip("子弹淡入后的颜色（默认纯红）")]
        public Color bulletColor = new Color(1f, 0f, 0f, 1f);

        [Tooltip("齐射的水平散布角度（度）。0 = 全部瞄同一点")]
        [Range(0f, 60f)] public float bulletSpreadDegrees = 12f;

        [Tooltip("同一波子弹之间的发射间隔（秒）。0 = 同时发射")]
        [Min(0f)] public float volleyStagger = 0.15f;

        // ---------------------------------------------------------------- 移动

        [Header("移动")]
        [Tooltip("随机踱步时目标点离自己多远（米）")]
        [Min(1f)] public float wanderRadius = 5f;

        [Tooltip("随机踱步之间的停留区间（秒，min~max）")]
        public Vector2 wanderPauseRange = new Vector2(0.8f, 2.0f);

        [Tooltip("看不到玩家时，多久重新找一次“能命中玩家的位置”（秒）")]
        [Min(0.1f)] public float repositionInterval = 0.8f;

        [Tooltip("找射击位时，在玩家周围多远的环上采样（米，min~max）")]
        public Vector2 shootingSpotRange = new Vector2(2.5f, 7f);

        // ---------------------------------------------------------------- 视线

        [Header("视线")]
        [Tooltip("参与视线检测的层（被这些东西挡住就算看不到）")]
        public LayerMask losMask = ~((1 << 2) | (1 << 4) | (1 << 5));

        [Tooltip("瞄准玩家的高度：玩家脚底向上多少米（胸口）")]
        [Min(0f)] public float playerAimHeight = 1.2f;

        // ---------------------------------------------------------------- 运行时

        private PuzzleMonster m_Monster;
        private bool  m_HasLOS;
        private float m_NextFireTime;
        private float m_NextRepositionTime;
        private float m_NextWanderTime;

        /// <summary>当前是否直接看得到玩家（可开火）。</summary>
        public bool HasLineOfSight => m_HasLOS;

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
        }

        private void OnDisable()
        {
            if (m_Monster != null) m_Monster.DungeonMoveControlled = false;
        }

        // ---------------------------------------------------- 由 PuzzleMonster.UpdateDungeonMode 驱动

        /// <summary>
        /// 巡逻：随机运动 / 寻路到可命中位置。
        /// 由 PuzzleMonster.UpdateDungeonMode 每帧调用（在 MoveTowardTarget 之前）。
        /// </summary>
        public void PatrolTick(float now)
        {
            if (m_Monster == null) m_Monster = GetComponent<PuzzleMonster>();
            if (m_Monster == null) return;

            m_HasLOS = CheckLineOfSight();

            // ---- 能直接命中玩家：站住，准备开火 ----
            if (m_HasLOS)
            {
                m_Monster.DungeonClearMoveTarget();
                return;
            }

            // ---- 看不到：周期性重新选目标 ----
            //   优先找“能命中玩家的位置”（寻路过去）；
            //   找不到射击位 / 还没发现玩家时，随机踱步。
            if (now >= m_NextRepositionTime)
            {
                m_NextRepositionTime = now + repositionInterval;

                Vector3 point;
                if (m_Monster.DungeonHasPlayerPos && TryFindShootingSpot(out point))
                {
                    m_Monster.DungeonSetMoveTarget(point);
                }
                else if (now >= m_NextWanderTime && TryPickWanderPoint(out point))
                {
                    m_NextWanderTime = now + Random.Range(wanderPauseRange.x, wanderPauseRange.y);
                    m_Monster.DungeonSetMoveTarget(point);
                }
            }
        }

        /// <summary>
        /// 开火：检测到玩家且视线通畅时，每隔一段时间发射数颗子弹。
        /// 由 PuzzleMonster.UpdateDungeonMode 每帧调用。
        /// </summary>
        public void CombatTick(float now)
        {
            if (m_Monster == null) return;
            if (!m_Monster.DungeonHasPlayerPos) return;   // 还没发现玩家
            if (!m_HasLOS) return;                        // 看不到就不开火
            if (now < m_NextFireTime) return;

            m_NextFireTime = now + fireInterval * Random.Range(0.9f, 1.15f);

            FireVolley();
        }

        // ---------------------------------------------------------------- 开火

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

        // ---------------------------------------------------------------- 视线与选点

        /// <summary>瞄准点：玩家胸口。拿不到玩家时返回 Vector3.zero（表示无效）。</summary>
        private Vector3 GetAimPoint()
        {
            if (m_Monster == null) return Vector3.zero;

            // 优先实时玩家位置
            if (m_Monster.Player != null)
                return m_Monster.Player.transform.position + Vector3.up * playerAimHeight;

            // 退回最近一次捕捉到的玩家坐标
            if (m_Monster.DungeonHasPlayerPos)
                return m_Monster.DungeonLastPlayerPos + Vector3.up * playerAimHeight;

            return Vector3.zero;
        }

        /// <summary>现在能不能直接命中玩家（视线不被墙 / 其它怪物挡住）。</summary>
        private bool CheckLineOfSight()
        {
            Vector3 to = GetAimPoint();
            if (to == Vector3.zero) return false;

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

        /// <summary>
        /// 在玩家周围找一眼能看到玩家的 NavMesh 点（怪物移动过去就能开火）。
        /// </summary>
        private bool TryFindShootingSpot(out Vector3 spot)
        {
            Vector3 aim = GetAimPoint();
            if (aim == Vector3.zero || m_Monster == null) { spot = default; return false; }

            Vector3 playerPos = m_Monster.Player != null
                ? m_Monster.Player.transform.position
                : m_Monster.DungeonLastPlayerPos;

            const int tries = 10;

            for (int i = 0; i < tries; i++)
            {
                float r = Random.Range(shootingSpotRange.x, shootingSpotRange.y);
                Vector2 ring = Random.insideUnitCircle.normalized * r;
                Vector3 want = playerPos + new Vector3(ring.x, 0f, ring.y);

                if (!NavMesh.SamplePosition(want, out NavMeshHit hit, 2f, NavMesh.AllAreas))
                    continue;

                // 视线检查点与怪物球心同高（怪物悬浮在离地 2m 处）
                Vector3 eye = hit.position + Vector3.up * 2f;
                if (HasClearShot(eye, aim))
                {
                    spot = hit.position;
                    return true;
                }
            }

            spot = default;
            return false;
        }

        /// <summary>在自己周围随机挑一个可走的 NavMesh 点（随机运动）。</summary>
        private bool TryPickWanderPoint(out Vector3 spot)
        {
            for (int i = 0; i < 6; i++)
            {
                Vector2 rnd = Random.insideUnitCircle * wanderRadius;
                Vector3 want = transform.position + new Vector3(rnd.x, 0f, rnd.y);

                if (NavMesh.SamplePosition(want, out NavMeshHit hit, 2f, NavMesh.AllAreas))
                {
                    spot = hit.position;
                    return true;
                }
            }

            spot = default;
            return false;
        }
    }
}
