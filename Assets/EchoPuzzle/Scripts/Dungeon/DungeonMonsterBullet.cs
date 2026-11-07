using System.Collections.Generic;
using UnityEngine;

namespace EchoPuzzle.Dungeon
{
    /// <summary>
    /// 【地牢模式】怪物子弹。
    ///
    /// 外观与行为（需求原文）：
    ///   · 一个**完全透明**的圆球，平时看不见；
    ///   · 当**声波光带的波前**来到子弹下方（水平范围内）时，0.1 秒内淡入，
    ///     完全淡入后呈纯红 RGB(255,0,0)、不透明度 1，随后 0.5 秒内淡出；
    ///   · 碰撞到墙壁或玩家后迅速放大、同时淡出消失（约 0.5 秒的小型爆炸），
    ///     命中玩家时造成伤害。
    ///
    /// ★ 只服务于地牢模式：只由 <see cref="DungeonEnemy"/> 发射，
    ///   探秘模式（PuzzleMode）不会出现任何实例。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DungeonMonsterBullet : MonoBehaviour
    {
        // ---------------------------------------------------------------- 飞行

        [Header("飞行")]
        [Tooltip("飞行速度（米/秒）")]
        [Min(0.5f)] public float speed = 9f;

        [Tooltip("最长飞行时间（秒），超时就地爆炸")]
        [Min(0.5f)] public float maxLifetime = 6f;

        // ---------------------------------------------------------------- 淡入淡出

        [Header("声波淡入淡出")]
        [Tooltip("波前水平距离小于该值（米）判定“光带来了”")]
        [Min(0.1f)] public float sonarDetectRadius = 1.5f;

        [Tooltip("淡入时长（秒）")]
        [Min(0.01f)] public float fadeInSeconds = 0.1f;

        [Tooltip("淡出时长（秒）")]
        [Min(0.01f)] public float fadeOutSeconds = 0.5f;

        [Tooltip("淡入后的颜色（默认纯红）")]
        public Color bulletColor = new Color(1f, 0f, 0f, 1f);

        // ---------------------------------------------------------------- 爆炸

        [Header("爆炸")]
        [Tooltip("爆炸持续时长（秒）：放大 + 淡出")]
        [Min(0.05f)] public float explodeSeconds = 0.5f;

        [Tooltip("爆炸结束时直径放大到几倍")]
        [Min(1f)] public float explodeScaleMultiplier = 2.5f;

        // ---------------------------------------------------------------- 碰撞与伤害

        [Header("碰撞与伤害")]
        [Tooltip("参与碰撞的层（默认排除 Ignore Raycast / Water / UI）")]
        public LayerMask hitMask = ~((1 << 2) | (1 << 4) | (1 << 5));

        [Tooltip("命中玩家造成的伤害")]
        [Min(1)] public int damage = 1;

        // ---------------------------------------------------------------- 运行时

        private Vector3 m_Dir = Vector3.forward;
        private float   m_Lifetime;
        private bool    m_Exploding;
        private float   m_ExplodeTimer;
        private float   m_BaseDiameter = 0.5f;

        private Material          m_Mat;
        private SonarTrailRenderer m_Trails;
        private readonly List<Vector2> m_Wavefronts = new List<Vector2>(16);
        private PlayerHealth      m_PlayerHealth;

        /// <summary>可见性状态机：平时完全透明 -> 光带来了淡入 -> 淡出 -> 回到透明。</summary>
        private enum FadeState { Hidden, FadingIn, FadingOut }
        private FadeState m_Fade = FadeState.Hidden;
        private float     m_FadeTimer;

        /// <summary>子弹的默认半径（米）。</summary>
        private const float DefaultRadius = 0.25f;

        // ---------------------------------------------------------------- 生成

        /// <summary>
        /// 生成一颗子弹。
        /// </summary>
        /// <param name="pos">生成位置（世界坐标）。</param>
        /// <param name="dir">飞行方向（内部会归一化）。</param>
        /// <param name="owner">发射者（用于拷贝参数，可为 null）。</param>
        public static DungeonMonsterBullet Spawn(Vector3 pos, Vector3 dir, DungeonEnemy owner)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "[怪物子弹]";
            go.transform.position = pos;
            go.transform.localScale = Vector3.one * (DefaultRadius * 2f);

            // 子弹不做实体碰撞：自身碰撞体设为触发器，
            // 命中检测统一用射线（也避免挡住视线检测和别的子弹）。
            Collider col = go.GetComponent<Collider>();
            if (col != null) col.isTrigger = true;

            DungeonMonsterBullet bullet = go.AddComponent<DungeonMonsterBullet>();
            bullet.m_Dir = dir.sqrMagnitude > 1e-8f ? dir.normalized : Vector3.forward;

            if (owner != null)
            {
                bullet.speed                = owner.bulletSpeed;
                bullet.damage               = Mathf.Max(1, owner.bulletDamage);
                bullet.sonarDetectRadius    = owner.bulletSonarDetectRadius;
                bullet.bulletColor          = owner.bulletColor;
            }

            bullet.SetupVisual();
            bullet.EnsureHide();

            return bullet;
        }

        /// <summary>建立透明材质（参考项目里 AimOverlay 的运行时材质做法）。</summary>
        private void SetupVisual()
        {
            Renderer r = GetComponent<Renderer>();
            if (r == null) return;

            // 复用项目惯例：EchoPuzzle/AimOverlay shader + _Color 的 alpha 控透明度。
            // 但子弹是真实 3D 物体，要被墙挡住，所以 ZTest 用 LEqual、队列用 Transparent。
            m_Mat = EchoRuntimeMaterials.AimOverlay(bulletColor);

            if (m_Mat.HasProperty("_Color"))
                m_Mat.SetColor("_Color", WithAlpha(bulletColor, 0f));
            if (m_Mat.HasProperty("_BaseColor"))
                m_Mat.SetColor("_BaseColor", WithAlpha(bulletColor, 0f));

            m_Mat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.LessEqual);
            m_Mat.SetInt("_ZWrite", 0);
            m_Mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

            r.sharedMaterial = m_Mat;
        }

        /// <summary>★ 项目规矩：所有物体都要挂 Hide（0 = 正常存在于场景）。</summary>
        private void EnsureHide()
        {
            if (GetComponent<Hide>() == null)
            {
                Hide h = gameObject.AddComponent<Hide>();
                h.hiding = 0;
            }
        }

        private static Color WithAlpha(Color c, float a)
        {
            c.a = Mathf.Clamp01(a);
            return c;
        }

        // ---------------------------------------------------------------- 生命周期

        private void Start()
        {
            m_BaseDiameter = transform.localScale.x;
            SetAlpha(0f);
        }

        private void OnDestroy()
        {
            if (m_Mat != null) Destroy(m_Mat);
        }

        private void Update()
        {
            if (m_Exploding)
            {
                ExplodeTick();
                return;
            }

            FlyTick();
            FadeTick();
        }

        // ---------------------------------------------------------------- 飞行与碰撞

        private void FlyTick()
        {
            float dt   = Time.deltaTime;
            float dist = speed * dt;

            // 射线前进检测：撞到墙 / 玩家 / 其它实体 -> 爆炸
            if (dist > 0f &&
                Physics.Raycast(transform.position, m_Dir, out RaycastHit hit, dist,
                                hitMask, QueryTriggerInteraction.Ignore))
            {
                transform.position = hit.point;

                bool hitPlayer = hit.collider.GetComponentInParent<PlayerController>() != null;
                Explode(hitPlayer);
                return;
            }

            transform.position += m_Dir * dist;

            m_Lifetime += dt;
            if (m_Lifetime >= maxLifetime)
                Explode(false);
        }

        // ---------------------------------------------------------------- 声波淡入淡出

        private void FadeTick()
        {
            float dt = Time.deltaTime;

            switch (m_Fade)
            {
                case FadeState.Hidden:
                    if (SonarWavefrontNearby())
                    {
                        m_Fade      = FadeState.FadingIn;
                        m_FadeTimer = 0f;
                    }
                    break;

                case FadeState.FadingIn:
                    m_FadeTimer += dt;
                    SetAlpha(Mathf.Clamp01(m_FadeTimer / Mathf.Max(0.01f, fadeInSeconds)));
                    if (m_FadeTimer >= fadeInSeconds)
                    {
                        SetAlpha(1f);
                        m_Fade      = FadeState.FadingOut;
                        m_FadeTimer = 0f;
                    }
                    break;

                case FadeState.FadingOut:
                    m_FadeTimer += dt;
                    SetAlpha(1f - Mathf.Clamp01(m_FadeTimer / Mathf.Max(0.01f, fadeOutSeconds)));
                    if (m_FadeTimer >= fadeOutSeconds)
                    {
                        SetAlpha(0f);
                        m_Fade = FadeState.Hidden;
                    }
                    break;
            }
        }

        /// <summary>有没有声波光带的**波前**来到子弹下方（只看水平距离，忽略 y）。</summary>
        private bool SonarWavefrontNearby()
        {
            if (m_Trails == null)
            {
                SonarSystem sonar = FindObjectOfType<SonarSystem>();
                if (sonar == null) return false;

                m_Trails = sonar.GetComponentInChildren<SonarTrailRenderer>(true);
                if (m_Trails == null) return false;
            }

            m_Wavefronts.Clear();
            // sneakOnly = false：玩家脚步 / 石子 / 怪物光带都算；
            // useBox = false：圆形判定。接口内部只看 xz —— 正好是“光带在子弹下方”。
            m_Trails.CollectWavefrontsXZ(transform.position, sonarDetectRadius,
                                         m_Wavefronts, false, false);
            return m_Wavefronts.Count > 0;
        }

        private void SetAlpha(float a)
        {
            if (m_Mat == null) return;

            Color c = WithAlpha(bulletColor, a);
            if (m_Mat.HasProperty("_Color"))     m_Mat.SetColor("_Color", c);
            if (m_Mat.HasProperty("_BaseColor")) m_Mat.SetColor("_BaseColor", c);
        }

        // ---------------------------------------------------------------- 爆炸

        private void Explode(bool hitPlayer)
        {
            if (m_Exploding) return;
            m_Exploding    = true;
            m_ExplodeTimer = 0f;

            if (hitPlayer)
            {
                if (m_PlayerHealth == null) m_PlayerHealth = FindObjectOfType<PlayerHealth>();
                if (m_PlayerHealth != null) m_PlayerHealth.TakeDamage(damage);
            }

            SetAlpha(1f);
        }

        /// <summary>爆炸：迅速放大 + 淡出（小型爆炸），结束后销毁。</summary>
        private void ExplodeTick()
        {
            m_ExplodeTimer += Time.deltaTime;

            float t = Mathf.Clamp01(m_ExplodeTimer / Mathf.Max(0.05f, explodeSeconds));

            float s = m_BaseDiameter * (1f + (explodeScaleMultiplier - 1f) * t);
            transform.localScale = Vector3.one * s;

            SetAlpha(1f - t);

            if (t >= 1f)
                Destroy(gameObject);
        }
    }
}
