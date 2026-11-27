using System.Collections.Generic;
using UnityEngine;

namespace EchoPuzzle.Dungeon
{
    /// <summary>
    /// 【地牢模式】所有怪物子弹——**逻辑 + 渲染**——都住在这个全局单例里。
    ///
    /// ══════════════════════════════════════════════════════════════
    /// 为什么
    ///   以前每发怪物子弹 = `GameObject.CreatePrimitive(Sphere)`
    ///   + MeshRenderer + SphereCollider + DungeonMonsterBullet 组件。
    ///   齐射 3 颗 × 多只怪同时开火，物体数瞬间堆起来。
    ///
    ///   现在：
    ///     · 每发子弹只是一个 <see cref="Body"/>（纯数据，无 GameObject）
    ///     · 全部塞进**1 个 Mesh**，每帧统一推进 + 重画
    ///     · 全局 0 个子弹物体
    ///
    /// ══════════════════════════════════════════════════════════════
    /// 外观
    ///   · 平时完全透明
    ///   · 声波光带波前靠近 -> 淡入 fadeInSeconds
    ///   · 完全显示 holdSeconds
    ///   · 淡出 fadeOutSeconds
    ///   · 撞墙 / 命中 / 超时 -> 放大 + 淡出（爆炸）
    ///
    /// ★ 只在地牢模式使用。
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Echo Puzzle/Dungeon/Monster Bullet Renderer")]
    public sealed class MonsterBulletRenderer : MonoBehaviour
    {
        public static MonsterBulletRenderer Instance { get; private set; }

        // ---------------------------------------------------------------- 渲染配置

        [Header("外观")]
        [Tooltip("每发子弹的球半径（米）")]
        [Min(0.01f)] public float radius = 0.25f;

        [Tooltip("球的经线数。越大越圆\n\n" +
                 "★ 需求：球看起来要圆 —— 原来是 14，多边形感很明显，提到 32")]
        [Range(6, 64)] public int segments = 32;

        [Tooltip("球的纬线数")]
        [Range(4, 24)] public int rings = 10;

        [Header("性能")]
        [Tooltip("最多同时存在多少发子弹")]
        [Range(16, 512)] public int maxBullets = 128;

        // ---------------------------------------------------------------- 子弹数据

        /// <summary>淡入淡出的状态机。</summary>
        public enum Fade { Hidden, FadingIn, Holding, FadingOut }

        /// <summary>一发怪物子弹的**全部数据**（没有 GameObject）。</summary>
        public sealed class Body
        {
            public Vector3 pos;
            public Vector3 dir;

            public float speed = 9f;
            public int   damage = 1;
            public float lifetime;
            public float maxLifetime = 6f;

            public float alpha;
            public float scale = 1f;

            /// <summary>
            /// 这一发子弹自己的**球半径**（米）。0 = 用渲染器上的全局 radius。
            ///
            /// ★ 同时用于两件事：
            ///   · 视觉：画球时的半径
            ///   · 碰撞：SphereCast 的球半径
            ///   这样"看起来多大就撞多大"。
            /// </summary>
            public float selfRadius;

            public Color color = new Color(1f, 0f, 0f, 1f);

            // 淡入淡出参数（由发射者传入）
            public float sonarDetectRadius = 1.5f;
            public float fadeInSeconds     = 0.1f;
            public float holdSeconds       = 1f;
            public float fadeOutSeconds    = 0.5f;

            // 爆炸
            public float explodeSeconds          = 0.5f;
            public float explodeScaleMultiplier  = 2.5f;

            public Fade  fade = Fade.Hidden;
            public float fadeTimer;

            public bool  exploding;
            public float explodeTimer;

            // ★★ 爆炸弹（怪物主动技能）：爆炸时对范围内玩家造成溅射伤害 ★★
            public bool  explosive;
            public float blastRadius = 2.5f;
            public int   blastDamage = 2;

            /// <summary>
            /// 撞墙剩余反弹次数（子弹圈技能用）。0 = 撞到就爆炸，和原来一样。
            /// 反弹只发生在撞到非玩家的世界几何体上；玩家仍然正常命中结算。
            /// </summary>
            public int bouncesLeft;

            /// <summary>生命期是否已经结束（回收用）。</summary>
            public bool dead;
        }

        // ---------------------------------------------------------------- 运行时

        private readonly List<Body> m_Bodies = new List<Body>(64);

        /// <summary>SphereCastNonAlloc 的复用缓冲（避免每帧给每颗子弹分配数组）。</summary>
        private readonly RaycastHit[] m_HitBuffer = new RaycastHit[16];

        private Mesh         m_Mesh;
        private MeshFilter   m_Filter;
        private MeshRenderer m_Renderer;

        private Vector3[] m_UnitSphere;
        private int[]     m_SphereTris;

        private Vector3[] m_Vertices;
        private Color[]   m_Colors;
        private int[]     m_Triangles;

        /// <summary>SphereCast 会检测这些层；命中后的处理见 FlyTick / ShouldPassThrough
        /// （怪物、无敌玩家、己方技能墙会穿过；子弹圈子弹撞墙会反弹）。</summary>
        public LayerMask hitMask = ~((1 << 2) | (1 << 4) | (1 << 5));

        private SonarTrailRenderer m_Trails;
        private readonly List<Vector2> m_Wavefronts = new List<Vector2>(16);

        private PlayerHealth m_PlayerHealth;

        // ---------------------------------------------------------------- 生命周期

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }

            Instance = this;

            BuildSphereTemplate();
            BuildMesh();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;

            if (m_Mesh != null) Destroy(m_Mesh);
            if (m_Filter != null) Destroy(m_Filter.gameObject);
        }

        // ---------------------------------------------------------------- 发射

        /// <summary>
        /// 发射一发怪物子弹。★ 不创建任何 GameObject。
        /// </summary>
        public Body Fire(Vector3 pos, Vector3 dir, DungeonEnemy owner)
        {
            if (m_Bodies.Count >= Mathf.Clamp(maxBullets, 16, 512))
            {
                // 上限满了：复用最老的那个
                m_Bodies.RemoveAt(0);
            }

            var b = new Body
            {
                pos = pos,
                dir = dir.sqrMagnitude > 1e-8f ? dir.normalized : Vector3.forward,
            };

            if (owner != null)
            {
                b.speed               = owner.bulletSpeed;
                b.damage              = Mathf.Max(1, owner.bulletDamage);
                b.sonarDetectRadius   = owner.bulletSonarDetectRadius;
                b.color               = owner.bulletColor;
            }

            // ★ 出生时保持 Fade.Hidden（Body 的默认值），**不**做任何提前淡入。
            //
            //   怪物子弹的既定规则：
            //     · 只有声波波前（SonarWavefrontNearby）靠近时才亮
            //     · 亮了之后 1 秒保持、0.5 秒淡出，重新回到 Hidden
            //   之前在这里写过一行"出膛就 FadingIn"，结果子弹在怪物身边
            //   一直亮着、不再重新隐藏 —— 已经删掉，恢复原规则。

            m_Bodies.Add(b);
            return b;
        }

        // ---------------------------------------------------------------- 每帧

        private void Update()
        {
            float dt = Time.deltaTime;

            for (int i = m_Bodies.Count - 1; i >= 0; i--)
            {
                Body b = m_Bodies[i];

                if (b.exploding)
                {
                    ExplodeTick(b, dt);
                }
                else
                {
                    FlyTick(b, dt);

                    if (!b.exploding) FadeTick(b, dt);
                }

                if (b.dead) m_Bodies.RemoveAt(i);
            }
        }

        // ---------------------------------------------------------------- 飞行与碰撞

        private void FlyTick(Body b, float dt)
        {
            float dist = b.speed * dt;

            // ★★ 用 SphereCast 而不是 Raycast ★★
            //
            //   需求："把怪物爆炸弹改用 SphereCast，碰撞半径 1m"
            //
            //   ★ Raycast 是一根无限细的射线，贴着墙边飞过都不会命中；
            //     而子弹在画面上是一个有体积的球，二者不一致。
            //   ★ SphereCast 用球去扫，命中判定和视觉体积对得上。
            //
            //   ★ 半径取自 Body.selfRadius（0 时回落到渲染器的全局 radius）。
            float castR = b.selfRadius > 0.001f
                        ? b.selfRadius
                        : Mathf.Max(0.01f, radius);

            if (dist > 0f)
            {
                // ★★ 一次性扫出这一整段路程上的**所有**碰撞体，再把该忽略的整个丢掉 ★★
                //
                //   需求："怪物子弹不会被怪物碰撞" —— 不是"撞到之后强行穿过去"，
                //   而是**根本没有这次碰撞**。
                //
                //   ★ 为什么不用 Physics.SphereCast：
                //     它只给最近的**一个**命中。如果那一个是怪物，就只能原地丢弃这次
                //     结果（或者像以前那样把子弹瞬移过去），于是出现两个副作用：
                //       · 子弹在怪物身上"卡帧"，一帧只推进一点点；
                //       · 瞬移量按碰撞体 bounds 算（boss 放大后接近 3 米），
                //         画面上就是一下子跳过一大段。
                //   ★ SphereCastNonAlloc 一次拿到多个候选，挑出**第一个真正有效的**
                //     （墙 / 玩家），怪物、无敌帧的玩家、己方技能墙直接不参与比较。
                //     子弹该走多远走多远，速度是匀速的。
                int count = Physics.SphereCastNonAlloc(b.pos, castR, b.dir, m_HitBuffer, dist,
                                                      hitMask, QueryTriggerInteraction.Ignore);

                RaycastHit hit     = default;
                bool       found   = false;
                float      nearest = float.MaxValue;

                for (int i = 0; i < count; i++)
                {
                    RaycastHit h = m_HitBuffer[i];
                    if (h.collider == null) continue;

                    // 怪物 / 无敌玩家 / 己方技能墙：对子弹来说等于不存在
                    if (ShouldPassThrough(h.collider)) continue;

                    float d = h.distance > 0f ? h.distance : 0f;
                    if (d >= nearest) continue;

                    nearest = d;
                    hit     = h;
                    found   = true;
                }

                if (found)
                {
                    bool hitPlayer = hit.collider.GetComponentInParent<PlayerController>() != null;

                    // ★★ 子弹圈：撞墙反弹（最多 bouncesLeft 次）★★
                    //   玩家不受反弹影响：打到玩家永远按普通命中结算。
                    if (!hitPlayer && b.bouncesLeft > 0)
                    {
                        Vector3 n = hit.normal;
                        n.y = 0f;                       // 只按墙面法线的水平分量反射
                        if (n.sqrMagnitude > 1e-6f)
                        {
                            n.Normalize();
                            b.dir = Vector3.Reflect(b.dir, n);
                            b.dir.y = 0f;
                            if (b.dir.sqrMagnitude > 1e-6f) b.dir.Normalize();

                            // 从墙面往外挪出球体半径，避免下一帧又立刻命中同一面墙
                            b.pos = hit.point + n * (castR + 0.02f);
                            b.bouncesLeft--;

                            b.lifetime += dt;
                            if (b.lifetime >= b.maxLifetime) Explode(b, false);
                            return;
                        }
                    }

                    b.pos = hit.point;
                    Explode(b, hitPlayer);
                    return;
                }
            }

            b.pos += b.dir * dist;

            b.lifetime += dt;
            if (b.lifetime >= b.maxLifetime) Explode(b, false);
        }

        /// <summary>
        /// 在玩家身上取一个随机位置（给溅血光带用）。
        /// </summary>
        private static Vector3 RandomPointOnPlayer()
        {
            if (m_PlayerHealthRef == null) return Vector3.zero;

            Transform t = m_PlayerHealthRef.transform;

            CharacterController cc = t.GetComponent<CharacterController>();
            if (cc != null)
            {
                Vector3 c = t.position + cc.center;

                return new Vector3(
                    c.x + Random.Range(-0.5f, 0.5f) * cc.radius,
                    c.y + Random.Range(-0.35f, 0.35f) * cc.height * 0.5f,
                    c.z + Random.Range(-0.5f, 0.5f) * cc.radius);
            }

            return t.position + Vector3.up * Random.Range(0.4f, 1.4f);
        }

        /// <summary>最近一次找到的 PlayerHealth（给 RandomPointOnPlayer 用）。</summary>
        private static PlayerHealth m_PlayerHealthRef;

        /// <summary>
        /// 这颗子弹撞到 <paramref name="col"/> 时，是否应该**直接忽略**。
        ///
        /// ★ 会被忽略的三种情况：
        ///     1. 撞到的是玩家，但玩家此刻处于**无敌帧**（闪避中 / 受击后无敌）
        ///     2. 撞到的是怪物（怪物永远不吃怪物子弹，子弹也永远撞不到它）
        ///     3. 怪物技能墙（己方子弹不能把自己施法者的墙打爆）
        /// </summary>
        private bool ShouldPassThrough(Collider col)
        {
            if (col == null) return false;

            // ---- 情况 1：无敌中的玩家 ----
            if (col.GetComponentInParent<PlayerController>() != null)
            {
                if (m_PlayerHealth == null) m_PlayerHealth = FindObjectOfType<PlayerHealth>();

                if (m_PlayerHealth != null && m_PlayerHealth.IsInvincible)
                    return true;

                return false;
            }

            // ---- 情况 2：怪物 ----
            //
            //   怪物身上挂的是 PuzzleMonster（探秘 / 地牢共用同一套组件）
            //   和 DungeonEnemy（地牢的 AI 外壳）。
            //   两者任一存在就当作"怪物"，子弹穿过去。
            if (col.GetComponentInParent<PuzzleMonster>() != null) return true;
            if (col.GetComponentInParent<DungeonEnemy>() != null)  return true;

            // ---- 情况 3：怪物技能墙 ----
            //   造墙技能是怪物放的：己方子弹直接穿过，不然施法者会被自己的墙挡住
            if (col.GetComponent<MonsterSkillWall>() != null) return true;

            return false;
        }

        // ---------------------------------------------------------------- 淡入淡出

        private void FadeTick(Body b, float dt)
        {
            switch (b.fade)
            {
                case Fade.Hidden:
                    if (SonarWavefrontNearby(b.pos, b.sonarDetectRadius))
                    {
                        b.fade      = Fade.FadingIn;
                        b.fadeTimer = 0f;
                    }
                    break;

                case Fade.FadingIn:
                    b.fadeTimer += dt;
                    b.alpha = Mathf.Clamp01(b.fadeTimer / Mathf.Max(0.01f, b.fadeInSeconds));

                    if (b.fadeTimer >= b.fadeInSeconds)
                    {
                        b.alpha     = 1f;
                        b.fade      = Fade.Holding;      // ★ 淡入后先"完全显示"
                        b.fadeTimer = 0f;
                    }
                    break;

                case Fade.Holding:
                    b.alpha = 1f;
                    b.fadeTimer += dt;

                    if (b.fadeTimer >= Mathf.Max(0f, b.holdSeconds))
                    {
                        b.fade      = Fade.FadingOut;
                        b.fadeTimer = 0f;
                    }
                    break;

                case Fade.FadingOut:
                    b.fadeTimer += dt;
                    b.alpha = 1f - Mathf.Clamp01(b.fadeTimer / Mathf.Max(0.01f, b.fadeOutSeconds));

                    if (b.fadeTimer >= b.fadeOutSeconds)
                    {
                        b.alpha = 0f;
                        b.fade  = Fade.Hidden;
                    }
                    break;
            }
        }

        /// <summary>有没有声波光带波前来到子弹下方（只看水平距离）。</summary>
        private bool SonarWavefrontNearby(Vector3 pos, float detectRadius)
        {
            if (m_Trails == null)
            {
                SonarSystem sonar = FindObjectOfType<SonarSystem>();
                if (sonar == null) return false;

                m_Trails = sonar.GetComponentInChildren<SonarTrailRenderer>(true);
                if (m_Trails == null) return false;
            }

            m_Wavefronts.Clear();
            m_Trails.CollectWavefrontsXZ(pos, detectRadius, m_Wavefronts, false, false);

            return m_Wavefronts.Count > 0;
        }

        // ---------------------------------------------------------------- 爆炸

        private void Explode(Body b, bool hitPlayer)
        {
            if (b.exploding) return;

            b.exploding    = true;
            b.explodeTimer = 0f;
            b.alpha        = 1f;

            if (hitPlayer)
            {
                if (m_PlayerHealth == null) m_PlayerHealth = FindObjectOfType<PlayerHealth>();
                if (m_PlayerHealth != null) m_PlayerHealth.TakeDamage(b.damage);

                // ★★ 溅血光带：玩家被击中时在命中点炸一次（不会被怪物捕捉）★★
                BloodSplashSonar.Emit(b.pos, b.dir);
            }
            else if (b.explosive)
            {
                // ★★ 爆炸弹：撞墙/超时爆炸时对范围内玩家造成溅射伤害 ★★
                //   （直击玩家的那发已经在上面算过更高伤，不重复算溅射）
                if (m_PlayerHealth == null) m_PlayerHealth = FindObjectOfType<PlayerHealth>();
                m_PlayerHealthRef = m_PlayerHealth;

                if (m_PlayerHealth != null &&
                    HorizontalDist(m_PlayerHealth.transform.position, b.pos) <= b.blastRadius)
                {
                    m_PlayerHealth.TakeDamage(b.blastDamage);

                    // ★★ 被爆炸波及的玩家身上也冒一次溅血 ★★
                    //
                    //   需求："怪物的爆炸弹没有直接命中却让玩家处于爆炸范围时，
                    //         也会在玩家身上随机位置触发一次溅血光带。"
                    //
                    //   ★ 以前这里是在**爆炸中心** b.pos 处冒血，
                    //     可那时玩家并不在爆炸中心（只是被波及），
                    //     血花出现在空地上一看就不对。
                    //   ★ 现在改成玩家身上的随机点。
                    BloodSplashSonar.Emit(RandomPointOnPlayer(), b.dir);
                }
            }
        }

        /// <summary>水平距离（只看 x/z）。</summary>
        private static float HorizontalDist(Vector3 a, Vector3 b)
        {
            return new Vector2(a.x - b.x, a.z - b.z).magnitude;
        }

        // ---------------------------------------------------------------- 手刀清弹

        /// <summary>
        /// 清除一个水平扇形（或圆形）区域内的怪物子弹 —— 玩家手刀用。
        ///
        /// 被清除的子弹保留爆炸视觉（放大 + 淡出），但**不**对玩家造成伤害
        /// （需求："清除区域内的子弹，但是仍然会产生爆炸、地面伤害等效果"）。
        /// </summary>
        /// <param name="center">扇形圆心（玩家位置）</param>
        /// <param name="radius">半径（米，只看水平距离）</param>
        /// <param name="halfAngleDeg">扇形半角（度）；>= 180 视为整圆</param>
        /// <param name="forward">扇形朝向（水平方向）</param>
        /// <returns>清掉多少发</returns>
        public int ClearArea(Vector3 center, float radius, float halfAngleDeg, Vector3 forward)
        {
            int cleared = 0;

            for (int i = 0; i < m_Bodies.Count; i++)
            {
                Body b = m_Bodies[i];
                if (b.dead || b.exploding) continue;

                Vector2 d = new Vector2(b.pos.x - center.x, b.pos.z - center.z);
                if (d.magnitude > radius) continue;

                if (halfAngleDeg < 179.9f)
                {
                    Vector2 f  = new Vector2(forward.x, forward.z).normalized;
                    Vector2 dn = d.normalized;

                    float ang = Mathf.Acos(Mathf.Clamp(Vector2.Dot(f, dn), -1f, 1f)) * Mathf.Rad2Deg;
                    if (ang > halfAngleDeg) continue;
                }

                Explode(b, false);      // 视觉爆炸，不掉血
                cleared++;
            }

            return cleared;
        }

        private void ExplodeTick(Body b, float dt)
        {
            b.explodeTimer += dt;

            float t = Mathf.Clamp01(b.explodeTimer / Mathf.Max(0.05f, b.explodeSeconds));

            b.scale = 1f + (Mathf.Max(1f, b.explodeScaleMultiplier) - 1f) * t;
            b.alpha = 1f - t;

            if (t >= 1f) b.dead = true;
        }

        // ---------------------------------------------------------------- 球网格

        private void BuildSphereTemplate()
        {
            // ★ 上限一并放宽（原来是 32/24），否则 Inspector 里调大也没用
            int lon = Mathf.Clamp(segments, 6, 64);
            int lat = Mathf.Clamp(rings, 4, 48);

            m_UnitSphere = new Vector3[(lat + 1) * (lon + 1)];

            int index = 0;

            for (int y = 0; y <= lat; y++)
            {
                float phi = (float)y / lat * Mathf.PI;
                float sy  = Mathf.Cos(phi);
                float sr  = Mathf.Sin(phi);

                for (int x = 0; x <= lon; x++)
                {
                    float theta = (float)x / lon * Mathf.PI * 2f;

                    m_UnitSphere[index++] = new Vector3(
                        sr * Mathf.Cos(theta),
                        sy,
                        sr * Mathf.Sin(theta));
                }
            }

            var tris = new int[lat * lon * 6];
            int t = 0;

            for (int y = 0; y < lat; y++)
            {
                for (int x = 0; x < lon; x++)
                {
                    int a = y * (lon + 1) + x;
                    int bb = a + 1;
                    int c = a + (lon + 1);
                    int d = c + 1;

                    tris[t++] = a;  tris[t++] = c;  tris[t++] = bb;
                    tris[t++] = bb; tris[t++] = c;  tris[t++] = d;
                }
            }

            m_SphereTris = tris;
        }

        private void BuildMesh()
        {
            int cap = Mathf.Clamp(maxBullets, 16, 512);

            int vPer = m_UnitSphere.Length;
            int tPer = m_SphereTris.Length;

            m_Vertices  = new Vector3[cap * vPer];
            m_Colors    = new Color[cap * vPer];
            m_Triangles = new int[cap * tPer];

            m_Mesh = new Mesh { name = "MonsterBullets" };
            m_Mesh.MarkDynamic();

            var go = new GameObject("MonsterBulletRendererMesh");

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
            if (mat != null)
            {
                // ★ 显式压到 Overlay 队列，并且关深度测试开关里最要命的那一项。
                //   AimOverlay 的 shader 本身就把 ZTest 硬编码成 Always；
                //   但万一 shader 没找到、回落到 URP/Unlit，那些属性就得靠这里兜住 ——
                //   需求："子弹在视觉上不能被怪物的实体黑球挡住"。
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Overlay;
                mat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                mat.SetInt("_ZWrite", 0);
                m_Renderer.sharedMaterial = mat;
            }
        }

        private void LateUpdate()
        {
            if (m_Mesh == null || m_Renderer == null) return;

            int vPer = m_UnitSphere.Length;

            int cap = Mathf.Min(m_Bodies.Count, m_Vertices.Length / vPer);

            int vCount = 0;
            int tCount = 0;

            float r = Mathf.Max(0.01f, radius);

            for (int i = 0; i < cap; i++)
            {
                Body b = m_Bodies[i];

                if (b.alpha <= 0.001f) continue;

                int vBase = vCount;

                // ★ 子弹自己的半径优先（selfRadius > 0），否则用全局 radius
                float br = b.selfRadius > 0.001f ? b.selfRadius : r;

                float s = br * Mathf.Max(0.01f, b.scale);

                Color c = b.color;
                c.a = b.alpha;

                for (int v = 0; v < vPer; v++)
                {
                    m_Vertices[vBase + v] = m_UnitSphere[v] * s + b.pos;
                    m_Colors[vBase + v]   = c;
                }

                vCount += vPer;

                for (int t = 0; t < m_SphereTris.Length; t++)
                    m_Triangles[tCount + t] = m_SphereTris[t] + vBase;

                tCount += m_SphereTris.Length;
            }

            m_Mesh.Clear();

            if (vCount <= 0)
            {
                m_Renderer.enabled = false;
                return;
            }

            m_Renderer.enabled = true;

            m_Mesh.SetVertices(m_Vertices, 0, vCount);
            m_Mesh.SetColors(m_Colors, 0, vCount);
            m_Mesh.SetTriangles(m_Triangles, 0, tCount, 0);
            m_Mesh.RecalculateBounds();
        }
    }
}
