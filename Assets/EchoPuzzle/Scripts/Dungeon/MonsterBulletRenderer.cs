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

        [Tooltip("球的经线数")]
        [Range(6, 32)] public int segments = 14;

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

            /// <summary>生命期是否已经结束（回收用）。</summary>
            public bool dead;
        }

        // ---------------------------------------------------------------- 运行时

        private readonly List<Body> m_Bodies = new List<Body>(64);

        private Mesh         m_Mesh;
        private MeshFilter   m_Filter;
        private MeshRenderer m_Renderer;

        private Vector3[] m_UnitSphere;
        private int[]     m_SphereTris;

        private Vector3[] m_Vertices;
        private Color[]   m_Colors;
        private int[]     m_Triangles;

        /// <summary>撞到这些层 -> 爆炸（墙 / 玩家 / 怪物）。</summary>
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

            if (dist > 0f &&
                Physics.Raycast(b.pos, b.dir, out RaycastHit hit, dist,
                                hitMask, QueryTriggerInteraction.Ignore))
            {
                // ★★ 判断撞到的是谁 ★★
                //
                //   需求 1："玩家在闪避的无敌帧期间不会被怪物子弹碰撞到"
                //         -> 撞到"无敌中的玩家"直接**穿过去**，不爆炸
                //   需求 2："怪物始终不会被怪物子弹碰撞到"
                //         -> 撞到怪物一律**穿过去**，不爆炸
                //
                //   ★ 穿过去的做法：把 b.pos 往前推过这个碰撞体，
                //     然后继续本帧的剩余位移。用连续检测避免卡在体内。
                if (ShouldPassThrough(hit.collider))
                {
                    b.pos = hit.point + b.dir * PierceStep;

                    b.lifetime += dt;
                    if (b.lifetime >= b.maxLifetime) Explode(b, false);
                    return;
                }

                b.pos = hit.point;

                bool hitPlayer = hit.collider.GetComponentInParent<PlayerController>() != null;
                Explode(b, hitPlayer);
                return;
            }

            b.pos += b.dir * dist;

            b.lifetime += dt;
            if (b.lifetime >= b.maxLifetime) Explode(b, false);
        }

        /// <summary>穿透用的前推距离（米）。比任何碰撞体略大一点就够。</summary>
        private const float PierceStep = 0.35f;

        /// <summary>
        /// 这颗子弹撞到 <paramref name="col"/> 时，是否应该**直接穿过去**。
        ///
        /// ★ 会穿过去的两种情况：
        ///     1. 撞到的是玩家，但玩家此刻处于**无敌帧**（闪避中 / 受击后无敌）
        ///     2. 撞到的是怪物（怪物永远不吃怪物子弹）
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
            int lon = Mathf.Clamp(segments, 6, 32);
            int lat = Mathf.Clamp(rings, 4, 24);

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
            if (mat != null) m_Renderer.sharedMaterial = mat;
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
                float s = r * Mathf.Max(0.01f, b.scale);

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
