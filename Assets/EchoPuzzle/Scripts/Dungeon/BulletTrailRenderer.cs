using System.Collections.Generic;
using UnityEngine;

namespace EchoPuzzle.Dungeon
{
    /// <summary>
    /// 【地牢模式】所有**玩家子弹**——**逻辑 + 光带渲染**——都住在这个全局单例里。
    ///
    /// ══════════════════════════════════════════════════════════════
    /// 为什么
    ///   以前每发子弹都会：
    ///     · new GameObject("DungeonBullet") + DungeonBullet 组件
    ///     · new GameObject("BulletTrailMesh") + MeshFilter + MeshRenderer
    ///     · new Mesh() + 4 个大数组（约 107 KB）
    ///   射速 4 发/秒 -> 每秒约 430 KB 托管堆分配 + 每发一个独立 draw call。
    ///
    ///   现在：
    ///     · 每发子弹只是一个 <see cref="Shot"/>（纯数据，无 GameObject）
    ///     · 全部光带拼进**1 个 Mesh**
    ///     · 全局 0 个子弹物体、1 个 draw call
    ///
    /// ══════════════════════════════════════════════════════════════
    /// 光带外观
    ///   · 每个发光点生成后**立即**开始淡出
    ///   · pointFadeTime（默认 0.5 秒）后完全消失
    ///   · 波前停住后，已经生成的段仍留在场上自然淡完
    ///
    /// ★ 只在地牢模式创建。
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Echo Puzzle/Dungeon/Bullet Trail Renderer")]
    public sealed class BulletTrailRenderer : MonoBehaviour
    {
        public static BulletTrailRenderer Instance { get; private set; }

        // ---------------------------------------------------------------- 渲染配置

        [Header("外观")]
        [Tooltip("相邻采样点之间的间距（米）。越小越顺滑、点越多")]
        [Min(0.02f)] public float pointSpacing = 0.12f;

        [Tooltip("每个点从生成到完全淡出的时长（秒）")]
        [Min(0.05f)] public float pointFadeTime = 0.1f;

        [Header("性能")]
        [Tooltip("所有子弹加起来最多多少段。超出的不再画")]
        [Range(64, 8192)] public int maxSegments = 2048;

        [Tooltip("最多同时存在多少发子弹")]
        [Range(8, 512)] public int maxShots = 128;

        // ---------------------------------------------------------------- 子弹数据

        /// <summary>一个发光点。</summary>
        public struct Point
        {
            public Vector3 pos;
            public float   birthTime;
        }

        /// <summary>一发玩家子弹的**全部数据**（没有 GameObject）。</summary>
        public sealed class Shot
        {
            public Vector3 origin;
            public Vector3 dir;

            public float speed = 35f;
            public float range = 40f;
            public float width = 0.1f;

            public float travel;              // 波前已经飞了多远
            public bool  stopped;             // 命中 / 到射程，波前停住

            // ★★ 伤害 / 回蓝都是 float，保留三位小数 ★★
            public float damage = 4f;
            public float manaRestore = 12f;
            public float hurtRadius = 2f;
            public float hurtHeight = 4f;

            /// <summary>
            /// 范围伤害半径（米）。0 = 没有范围伤害（第十周任务 4）。
            /// 命中怪物时，以命中点为圆心、这个半径内的**其它**怪物也会吃伤害。
            /// </summary>
            public float aoeRadius;

            /// <summary>撞墙后还能反弹几次（第十周任务 6）。</summary>
            public int bouncesLeft;

            /// <summary>还能穿过几只怪（第十周任务 6）。</summary>
            public int pierceLeft;

            /// <summary>已经命中过的怪（穿透时避免同一只被打两次）。跨段共享。</summary>
            public HashSet<int> hitMonsters;

            public Color color = new Color(0.35f, 0.62f, 1f, 1f);

            public readonly List<Point> points = new List<Point>(128);

            /// <summary>下一个该生成的采样点序号（绝对距离 / pointSpacing）。</summary>
            public int nextIndex;
        }

        // ---------------------------------------------------------------- 运行时

        private readonly List<Shot> m_Shots = new List<Shot>(32);

        private Mesh         m_Mesh;
        private MeshFilter   m_Filter;
        private MeshRenderer m_Renderer;

        private Vector3[] m_Vertices;
        private Color[]   m_Colors;
        private Vector2[] m_Uvs;
        private int[]     m_Triangles;

        /// <summary>和声波一致的"墙"层掩码。</summary>
        private static readonly LayerMask WallMask = ~((1 << 2) | (1 << 4) | (1 << 5));

        private Camera m_Cam;

        // ---------------------------------------------------------------- 生命周期

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }

            Instance = this;

            // ★ 新实例接管：旧的怪物缓存一律作废
            ClearStaticCaches();

            BuildMesh();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;

            // ★★ 静态缓存必须一起清 ★★
            //
            //   s_Monsters 是 static，跨场景不会自动重置。
            //   回主页再进地牢时，它里面全是**上一局已经销毁的怪物**，
            //   而 s_MonstersNextRefresh 又记着旧时间戳 ——
            //   新场景的 timeSinceLevelLoad 更小，刷新条件永远不成立，
            //   于是子弹一直拿旧的（null）列表做判定 -> **打不到任何怪**。
            ClearStaticCaches();

            if (m_Mesh != null) Destroy(m_Mesh);
            if (m_Filter != null) Destroy(m_Filter.gameObject);
        }

        // ---------------------------------------------------------------- 发射

        /// <summary>
        /// 发射一发玩家子弹。★ 不创建任何 GameObject。
        /// </summary>
        public Shot Fire(Vector3 origin, Vector3 dir,
                         float speed, float range, float width,
                         float damage, float manaRestore,
                         float hurtRadius, float hurtHeight,
                         Color color, float aoeRadius = 0f,
                         int bounces = 0, int pierce = 0)
        {
            if (m_Shots.Count >= Mathf.Clamp(maxShots, 8, 512))
                m_Shots.RemoveAt(0);        // 上限满了：顶掉最老的

            var s = new Shot
            {
                origin      = origin,
                dir         = dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector3.forward,
                speed       = Mathf.Max(0.1f, speed),
                range       = Mathf.Max(0.1f, range),
                width       = Mathf.Max(0.01f, width),
                damage      = Mathf.Max(0.001f, damage),
                manaRestore = Mathf.Max(0f, manaRestore),
                hurtRadius  = Mathf.Max(0.01f, hurtRadius),
                hurtHeight  = Mathf.Max(0.01f, hurtHeight),
                aoeRadius   = Mathf.Max(0f, aoeRadius),
                bouncesLeft = Mathf.Max(0, bounces),
                pierceLeft  = Mathf.Max(0, pierce),
                color       = color,
            };

            m_Shots.Add(s);

            // 出膛瞬间先打一个点，否则第一帧是空的
            AddPointsUpTo(s, 0f);

            return s;
        }

        // ---------------------------------------------------------------- 每帧

        private void Update()
        {
            float dt  = Time.deltaTime;
            float now = Time.timeSinceLevelLoad;

            for (int i = m_Shots.Count - 1; i >= 0; i--)
            {
                Shot s = m_Shots[i];

                // ---- 1) 波前推进 ----
                if (!s.stopped) Step(s, dt);

                // ---- 2) 剔除已淡完的老点 ----
                PruneFaded(s, now);

                // ---- 3) 全部点都淡完了 -> 回收 ----
                //
                //   ★ 波前停住之后，已经生成的段仍然留在场上自然淡完
                if (s.stopped && AllFaded(s, now))
                    m_Shots.RemoveAt(i);
            }
        }

        /// <summary>推进一步波前，顺带做碰撞。</summary>
        private void Step(Shot s, float dt)
        {
            Vector3 a = s.origin + s.dir * s.travel;

            float next = Mathf.Min(s.travel + s.speed * dt, s.range);
            Vector3 b = s.origin + s.dir * next;

            float segLen = (b - a).magnitude;

            // ---- 1) 先量墙 ----
            bool  wallHit  = false;
            float wallDist = float.MaxValue;
            RaycastHit wallInfo = default;

            if (segLen > 1e-5f &&
                Physics.SphereCast(a, s.width * 0.5f, (b - a) / segLen,
                                   out wallInfo, segLen,
                                   WallMask, QueryTriggerInteraction.Ignore))
            {
                wallHit  = true;
                wallDist = Mathf.Max(0f, wallInfo.distance);
            }

            // ---- 2) 再看怪物 ----
            if (TryHitMonster(s, a, b, out float hitT))
            {
                float monDist = segLen * hitT;

                if (!wallHit || monDist <= wallDist)
                {
                    StopAt(s, s.travel + monDist);
                    return;
                }
            }

            // ---- 3) 撞墙 ----
            if (wallHit)
            {
                // ★★ 怪物技能墙：玩家子弹打在墙上会扣墙的血（墙有生命值）★★
                if (wallInfo.collider != null)
                {
                    MonsterSkillWall skillWall = wallInfo.collider.GetComponent<MonsterSkillWall>();
                    if (skillWall != null) skillWall.AbsorbPlayerBullet(s.damage);
                }
                Vector3 dirUnit   = (b - a) / Mathf.Max(1e-5f, segLen);
                Vector3 wallPoint = a + dirUnit * wallDist;

                // ★★ 爆炸弹头：打墙要炸，但**不要**溅血光带 ★★
                //
                //   需求："当选了爆炸弹头的buff时，子弹命中墙壁时会触发溅血光带，
                //         我不希望这样。我希望有爆炸弹头的子弹命中墙壁和敌人时
                //         会产生一个巨大的淡出的圆形，这个圆形和爆炸范围一样大。"
                //
                //   ★ 这块原来会调 BloodSplashSonar.Emit —— 那是"血"，撞墙不该有。
                //     现在改为只铺一个爆炸圆形（半径 = aoeRadius），
                //     伤害结算照旧走 ApplyAoeDamage。
                if (s.aoeRadius > 0.01f)
                {
                    BulletBlastRenderer.Spawn(wallPoint, s.aoeRadius, s.color);
                    ApplyAoeDamage(null, wallPoint, s);
                }

                // ★ 跳弹（第十周任务 6）：还有反弹次数就从撞点朝反射方向再飞一段
                if (s.bouncesLeft > 0)
                {
                    s.bouncesLeft--;

                    Vector3 reflected = Vector3.Reflect(dirUnit, wallInfo.normal);

                    // 当前这一段到墙为止（保留它的光带），然后开一段新的
                    StopAt(s, s.travel + wallDist);
                    SpawnBounce(s, wallPoint + reflected * 0.02f, reflected);
                    return;
                }

                StopAt(s, s.travel + wallDist);
                return;
            }

            s.travel = next;

            AddPointsUpTo(s, s.travel);

            // 打空到射程尽头
            if (s.travel >= s.range - 1e-4f)
            {
                StopAt(s, s.range);
            }
        }

        private void StopAt(Shot s, float distance)
        {
            s.stopped = true;
            s.travel  = Mathf.Clamp(distance, 0f, s.range);

            AddPointsUpTo(s, s.travel);
        }

        /// <summary>
        /// 跳弹：从撞点朝反射方向再开"一段新光带"。
        ///
        /// ★ 为什么不直接改原来那条的 dir：
        ///   光带的采样点是按 origin + dir × 距离 算的（直线假设），
        ///   中途拐弯会把前面已经画出来的点全部算错。
        ///   拆成两段各自走直线，视觉上正好是一个折角，逻辑也最简单。
        /// </summary>
        private void SpawnBounce(Shot from, Vector3 origin, Vector3 dir)
        {
            var s = new Shot
            {
                origin      = origin,
                dir         = dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector3.forward,
                speed       = from.speed,
                range       = from.range,
                width       = from.width,
                damage      = from.damage,
                manaRestore = from.manaRestore,
                hurtRadius  = from.hurtRadius,
                hurtHeight  = from.hurtHeight,
                aoeRadius   = from.aoeRadius,
                color       = from.color,

                // 弹跳次数已经在外层扣过；穿透次数要继承。
                bouncesLeft = from.bouncesLeft,
                pierceLeft  = from.pierceLeft,

                // ★★ 弹跳后清空"已命中集合" ★★
                //
                //   需求："我希望有buff跳弹和穿甲弹的子弹在打中怪物后，穿透过去，
                //         从墙壁反弹回来时，如果再次打中怪物，则可以再造成一次伤害。"
                //
                //   ★ 以前这里是 hitMonsters = from.hitMonsters（继承），
                //     意思是「整发子弹的生命周期里，同一只怪只吃一次伤害」，
                //     所以反弹回来再打中就无效了。
                //   ✔ 现在改成不继承 —— 新的一段光带从空集合开始，
                //     反弹后再次命中同一只怪会再造成一次伤害。
                //
                //   ★ 段内仍然去重：一次穿透飞行途中穿过同一只怪只吃一次，
                //     不会因为还留在怪物体积内而每帧重复扣血。
                hitMonsters = null,
            };

            m_Shots.Add(s);
            AddPointsUpTo(s, 0f);
        }

        // ---------------------------------------------------------------- 采样点

        /// <summary>把采样点补齐到 headDistance（米）—— 每 pointSpacing 米一个。</summary>
        private void AddPointsUpTo(Shot s, float headDistance)
        {
            float spacing = Mathf.Max(0.02f, pointSpacing);
            float now     = Time.timeSinceLevelLoad;

            while (s.nextIndex * spacing <= headDistance)
            {
                if (s.points.Count >= maxSegments) break;

                s.points.Add(new Point
                {
                    pos       = s.origin + s.dir * (s.nextIndex * spacing),
                    birthTime = now,
                });

                s.nextIndex++;
            }
        }

        /// <summary>剔除已淡完的老点（保留一个边界点保证连接不断）。</summary>
        private void PruneFaded(Shot s, float now)
        {
            if (s.points.Count == 0) return;

            float fade = Mathf.Max(0.05f, pointFadeTime);

            int remove = 0;

            while (remove < s.points.Count - 1)
            {
                if (now - s.points[remove].birthTime > fade * 1.5f) remove++;
                else break;
            }

            if (remove > 0) s.points.RemoveRange(0, remove);
        }

        /// <summary>这条光带的所有点是否都淡完了。</summary>
        private bool AllFaded(Shot s, float now)
        {
            if (s.points.Count == 0) return true;

            return now - s.points[s.points.Count - 1].birthTime >= pointFadeTime;
        }

        // ---------------------------------------------------------------- 怪物判定

        // 怪物列表每帧全场景扫太贵，0.5 秒刷新一次就够
        private static PuzzleMonster[] s_Monsters;
        private static float           s_MonstersNextRefresh;

        private bool TryHitMonster(Shot s, Vector3 a, Vector3 b, out float hitT)
        {
            hitT = 0f;

            RefreshMonsters();
            if (s_Monsters == null) return false;

            float best = float.MaxValue;
            PuzzleMonster bestMon = null;

            for (int i = 0; i < s_Monsters.Length; i++)
            {
                PuzzleMonster mon = s_Monsters[i];
                if (mon == null || !mon.isActiveAndEnabled) continue;
                if (mon.DungeonIsDead) continue;

                // ★ 穿透时，同一段飞行里同一只怪只吃一次伤害。
                //
                //   注意这个集合是每段独立的：
                //   子弹撞墙反弹时会生成新的一段（hitMonsters = null），
                //   所以反弹回来再打中同一只怪，会再造成一次伤害。
                if (s.hitMonsters != null && s.hitMonsters.Contains(mon.GetInstanceID())) continue;

                if (!SegmentHitsCylinder(a, b, mon.transform.position,
                                         s.hurtRadius, s.hurtHeight * 0.5f, out float t))
                    continue;

                if (t < best) { best = t; bestMon = mon; }
            }

            if (bestMon == null) return false;

            hitT = best;

            bool wasDead = bestMon.DungeonIsDead;
            bestMon.DungeonTakeDamage(s.damage);

            // ★★ 溅血光带：在命中点炸一次小型光带（不会被怪物捕捉）★★
            //
            //   命中点 = 线段 a->b 上 t = best 处
            Vector3 hitPoint = Vector3.Lerp(a, b, Mathf.Clamp01(best));

            // ★ 需求："有爆炸弹头的子弹在直接命中敌人时**同样**要触发溅血光带。"
            //   所以这里无条件放（不管有没有爆炸弹头）。
            BloodSplashSonar.Emit(hitPoint, s.dir);

            // ★ 范围伤害（第十周任务 4）：命中点周围的其它怪物也吃伤害
            //
            //   ★ 有爆炸弹头时额外铺一个和爆炸范围一样大的圆形（纯视觉）。
            if (s.aoeRadius > 0.01f)
            {
                BulletBlastRenderer.Spawn(hitPoint, s.aoeRadius, s.color);
                ApplyAoeDamage(bestMon, hitPoint, s);
            }

            // 这一发把它打死了 -> 回蓝
            if (!wasDead && bestMon.DungeonIsDead && s.manaRestore > 0)
            {
                if (DungeonMana.Instance != null) DungeonMana.Instance.Restore(s.manaRestore);
            }

            // ★ 穿甲弹（第十周任务 6）：还有穿透次数就记下这只、继续飞
            if (s.pierceLeft > 0)
            {
                s.pierceLeft--;

                if (s.hitMonsters == null) s.hitMonsters = new HashSet<int>();
                s.hitMonsters.Add(bestMon.GetInstanceID());

                return false;      // false = 不停止，波前继续往前
            }

            return true;
        }

        /// <summary>
        /// 清掉所有静态缓存。
        /// ★ 时间戳用 float.NegativeInfinity 而不是 0 ——
        ///   新场景的 timeSinceLevelLoad 可能从 0 开始，
        ///   用 0 的话 "now < 0" 不成立，能刷新；
        ///   但用 -inf 更保险，保证第一次一定刷新。
        /// </summary>
        private static void ClearStaticCaches()
        {
            s_Monsters            = null;
            s_MonstersNextRefresh = float.NegativeInfinity;
        }

        /// <summary>
        /// 范围伤害：以命中点为圆心，把半径内的**其它**怪物也打一遍（第十周任务 4）。
        ///
        /// ★ 主目标已经在外面按全额伤害处理过了，这里排除它，避免一枪打两次。
        /// ★ 溅射伤害取主伤害的 60% —— 全额的话"爆炸弹头"会直接取代单体输出。
        /// </summary>
        private static void ApplyAoeDamage(PuzzleMonster primary, Vector3 center, Shot s)
        {
            RefreshMonsters();
            if (s_Monsters == null) return;

            float r2  = s.aoeRadius * s.aoeRadius;
            float dmg = s.damage * 0.8f;

            for (int i = 0; i < s_Monsters.Length; i++)
            {
                PuzzleMonster mon = s_Monsters[i];
                if (mon == null || mon == primary) continue;
                if (!mon.isActiveAndEnabled || mon.DungeonIsDead) continue;

                Vector3 d = mon.transform.position - center;
                d.y = 0f;                       // 只按水平距离算，和光带一样是平面判定

                if (d.sqrMagnitude > r2) continue;

                mon.DungeonTakeDamage(dmg);

                // ★★ 被爆炸波及的怪物身上也冒一次溅血 ★★
                //
                //   需求："玩家的爆炸弹头没有直接命中却让怪物处于爆炸范围时，
                //         会在怪物身上随机位置触发一次溅血光带。"
                //
                //   ★ 这里 primary（直接命中的那只）已经被 exclude 掉了，
                //     所以走到这里的都是"被波及"的，补一次溅血正好。
                //   ★ 位置用怪物身上的随机点，而不是爆炸中心 ——
                //     看起来才像是"这只怪被炸到了"。
                BloodSplashSonar.Emit(RandomPointOnMonster(mon), s.dir);
            }
        }

        /// <summary>
        /// 在怪物身上取一个随机位置（给溅血光带用）。
        ///
        /// ★ 在怪物碰撞体的包围盒里随机取一点，再把它拉到水平方向，
        ///   这样血花会出现在怪物身体的不同部位，而不是永远同一个点。
        /// </summary>
        public static Vector3 RandomPointOnMonster(PuzzleMonster mon)
        {
            if (mon == null) return Vector3.zero;

            Vector3 c = mon.transform.position;

            Collider col = mon.GetComponentInChildren<Collider>();
            if (col == null) return c;

            Bounds b = col.bounds;

            float rx = Random.Range(-0.5f, 0.5f) * b.extents.x;
            float rz = Random.Range(-0.5f, 0.5f) * b.extents.z;
            float ry = Random.Range(-0.35f, 0.35f) * b.extents.y;

            return new Vector3(b.center.x + rx, b.center.y + ry, b.center.z + rz);
        }

        private static void RefreshMonsters()
        {
            float now = Time.timeSinceLevelLoad;
            if (s_Monsters != null && now < s_MonstersNextRefresh) return;

            s_MonstersNextRefresh = now + 0.5f;
            s_Monsters = FindObjectsOfType<PuzzleMonster>(true);
        }

        /// <summary>线段与竖直圆柱的解析求交。</summary>
        private static bool SegmentHitsCylinder(Vector3 a, Vector3 b, Vector3 center,
                                                float radius, float halfHeight, out float t)
        {
            t = 0f;

            Vector3 p = a - center;
            Vector3 d = b - a;

            float tLo = 0f;
            float tHi = 1f;

            // ---- 水平方向 ----
            float A = d.x * d.x + d.z * d.z;
            float B = 2f * (p.x * d.x + p.z * d.z);
            float C = p.x * p.x + p.z * p.z - radius * radius;

            if (A < 1e-8f)
            {
                if (C > 0f) return false;
            }
            else
            {
                float disc = B * B - 4f * A * C;
                if (disc < 0f) return false;

                float sq = Mathf.Sqrt(disc);
                float t0 = (-B - sq) / (2f * A);
                float t1 = (-B + sq) / (2f * A);

                tLo = Mathf.Max(tLo, Mathf.Min(t0, t1));
                tHi = Mathf.Min(tHi, Mathf.Max(t0, t1));

                if (tLo > tHi) return false;
            }

            // ---- 竖直方向 ----
            if (Mathf.Abs(d.y) < 1e-8f)
            {
                if (Mathf.Abs(p.y) > halfHeight) return false;
            }
            else
            {
                float y0 = (-halfHeight - p.y) / d.y;
                float y1 = ( halfHeight - p.y) / d.y;

                if (y0 > y1) { float tmp = y0; y0 = y1; y1 = tmp; }

                tLo = Mathf.Max(tLo, y0);
                tHi = Mathf.Min(tHi, y1);

                if (tLo > tHi) return false;
            }

            t = Mathf.Clamp01(tLo);
            return true;
        }

        // ---------------------------------------------------------------- 网格

        private void BuildMesh()
        {
            int cap = Mathf.Clamp(maxSegments, 64, 8192);

            m_Vertices  = new Vector3[cap * 4];
            m_Colors    = new Color[cap * 4];
            m_Uvs       = new Vector2[cap * 4];
            m_Triangles = new int[cap * 6];

            m_Mesh = new Mesh { name = "BulletTrails" };
            m_Mesh.MarkDynamic();

            var go = new GameObject("BulletTrailRendererMesh");

            go.hideFlags = HideFlags.DontSave;
            go.transform.SetParent(null, false);
            go.transform.position   = Vector3.zero;
            go.transform.rotation   = Quaternion.identity;
            go.transform.localScale = Vector3.one;

            m_Filter = go.AddComponent<MeshFilter>();
            m_Filter.sharedMesh = m_Mesh;

            m_Renderer = go.AddComponent<MeshRenderer>();
            m_Renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            m_Renderer.receiveShadows    = false;

            Material mat = EchoRuntimeMaterials.AimOverlay(Color.white);
            if (mat != null) m_Renderer.sharedMaterial = mat;
        }

        private void LateUpdate()
        {
            if (m_Mesh == null || m_Renderer == null) return;

            float now = Time.timeSinceLevelLoad;

            if (m_Cam == null) m_Cam = Camera.main;

            Vector3 camPos = m_Cam != null ? m_Cam.transform.position : Vector3.zero;

            float fade = Mathf.Max(0.05f, pointFadeTime);

            int written = 0;
            int cap     = m_Vertices.Length / 4;

            for (int si = 0; si < m_Shots.Count && written < cap; si++)
            {
                Shot s = m_Shots[si];
                if (s.points.Count < 2) continue;

                float halfW = s.width * 0.5f;

                // 每条光带各自算宽度方向
                Vector3 toCam = camPos - s.points[0].pos;
                toCam.y = 0f;
                if (toCam.sqrMagnitude < 1e-6f) toCam = Vector3.back;
                toCam.Normalize();

                Vector3 widthDir = Vector3.Cross(Vector3.up, toCam).normalized;

                for (int i = 0; i + 1 < s.points.Count && written < cap; i++)
                {
                    Point p0 = s.points[i];
                    Point p1 = s.points[i + 1];

                    float a0 = 1f - (now - p0.birthTime) / fade;
                    float a1 = 1f - (now - p1.birthTime) / fade;

                    if (a0 <= 0f) continue;
                    if (a1 < 0f) a1 = 0f;

                    int v = written * 4;

                    m_Vertices[v + 0] = p0.pos - widthDir * halfW;
                    m_Vertices[v + 1] = p0.pos + widthDir * halfW;
                    m_Vertices[v + 2] = p1.pos + widthDir * halfW;
                    m_Vertices[v + 3] = p1.pos - widthDir * halfW;

                    m_Uvs[v + 0] = new Vector2(0f, 0f);
                    m_Uvs[v + 1] = new Vector2(1f, 0f);
                    m_Uvs[v + 2] = new Vector2(1f, 1f);
                    m_Uvs[v + 3] = new Vector2(0f, 1f);

                    Color c0 = s.color; c0.a *= Mathf.Clamp01(a0);
                    Color c1 = s.color; c1.a *= Mathf.Clamp01(a1);

                    m_Colors[v + 0] = c0;
                    m_Colors[v + 1] = c0;
                    m_Colors[v + 2] = c1;
                    m_Colors[v + 3] = c1;

                    int t = written * 6;

                    m_Triangles[t + 0] = v + 0; m_Triangles[t + 1] = v + 2; m_Triangles[t + 2] = v + 1;
                    m_Triangles[t + 3] = v + 0; m_Triangles[t + 4] = v + 3; m_Triangles[t + 5] = v + 2;

                    written++;
                }
            }

            m_Mesh.Clear();

            if (written <= 0)
            {
                m_Renderer.enabled = false;
                return;
            }

            m_Renderer.enabled = true;

            m_Mesh.SetVertices(m_Vertices, 0, written * 4);
            m_Mesh.SetUVs(0, m_Uvs, 0, written * 4);
            m_Mesh.SetColors(m_Colors, 0, written * 4);
            m_Mesh.SetTriangles(m_Triangles, 0, written * 6, 0);
            m_Mesh.RecalculateBounds();
        }
    }
}
