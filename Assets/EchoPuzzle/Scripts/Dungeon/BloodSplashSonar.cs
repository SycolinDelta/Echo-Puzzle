using UnityEngine;

namespace EchoPuzzle.Dungeon
{
    /// <summary>
    /// 【地牢模式】"溅血光带"—— 子弹命中时在命中点炸一次小型光带。
    ///
    /// ══════════════════════════════════════════════════════════════
    /// 触发时机
    ///   · 玩家子弹命中**怪物** -> 在怪物身上那一点
    ///   · 怪物子弹命中**玩家** -> 在玩家身上那一点
    ///
    /// 特性
    ///   · **不会被怪物捕捉**（走 EmitParams.ignoreMonster）
    ///   · 纯红色、不透明度固定为 1
    ///   · 其余数值参考玩家在地牢模式的**潜行脚步光带**
    ///     （PlayerController.dungeonSneakEcho）
    ///
    /// 调参数
    ///   下面 <see cref="paramsTemplate"/> 里的每一项都可以直接在 Inspector 改，
    ///   也可以改代码里的默认值。
    ///
    /// ★ 只在**地牢模式**生效，探秘模式完全不受影响。
    /// ══════════════════════════════════════════════════════════════
    /// </summary>
    [AddComponentMenu("Echo Puzzle/Dungeon/Blood Splash Sonar")]
    public sealed class BloodSplashSonar : MonoBehaviour
    {
        public static BloodSplashSonar Instance { get; private set; }

        [Header("开关")]
        [Tooltip("关掉就完全不发溅血光带。")]
        public bool enabled = true;

        [Header("外观 = 潜行脚步光带，但不透明度 1 / 纯红")]
        [Tooltip("★ 溅血光带的参数。\n\n" +
                 "★ 默认值 = PlayerController.dungeonSneakEcho：\n" +
                 "   rayCount90=18, range=2, speed=4, trailSpacing=0.25,\n" +
                 "   maxBounces=2, bounceFalloff=0.5, lifetime=0.5,\n" +
                 "   tailLinger=20, bounceLifeLoss=0.15\n\n" +
                 "★ 只改了两项：\n" +
                 "   intensity = 1（不透明度固定 1）\n" +
                 "   color = 纯红 (1,0,0)")]
        public SonarEmitSettings splashSonar = new SonarEmitSettings
        {
            // ---- 以下是 dungeonSneakEcho 的数值 ----
            rayCount90      = 24,
            rayCount30      = 0,
            rayCount60      = 0,
            diffuseCount15  = 0,
            diffuseCount45  = 0,
            diffuseCount75  = 0,
            hemisphereCount = 0,

            range           = 2f,
            speed           = 12f,
            trailSpacing    = 0.25f,
            maxBounces      = 2,
            bounceFalloff   = 0.5f,
            bounceLifeLoss  = 0.15f,

            lifetime        = 0.05f,
            tailLinger      = 1f,

            // ---- 这两项按要求固定 ----
            intensity       = 1f,                                   // 不透明度 = 1
            holdAlpha       = true,
            color           = new Color(1f, 0f, 0f, 1f),            // 纯红

            width           = 0f,
        };

        [Header("释放位置")]
        [Tooltip("命中点沿「受击者朝攻击者」方向往外推多少米。\n\n" +
                 "0 = 就在命中点。稍微推一点可以避免声源卡在碰撞体内部。")]
        [Min(0f)] public float surfaceOffset = 0.05f;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>
        /// 在某个位置释放一次溅血光带。
        /// </summary>
        /// <param name="position">命中点（世界坐标）</param>
        /// <param name="incomingDir">
        /// 子弹的飞行方向（世界坐标，不必归一化）。
        /// 用来决定"沿哪个方向往外推 + 方位角起点"。
        /// 传 zero 时会用世界 +Z。
        /// </param>
        public static void Emit(Vector3 position, Vector3 incomingDir)
        {
            if (Instance == null) return;
            if (!Instance.enabled) return;
            if (!GameUI.IsDungeonMode) return;

            SonarSystem sonar = FindObjectOfType<SonarSystem>();
            if (sonar == null) return;

            Vector3 dir = incomingDir;
            dir.y = 0f;

            if (dir.sqrMagnitude < 1e-6f) dir = Vector3.forward;
            dir.Normalize();

            // 命中点稍微往"子弹来的一侧"推一点，避免声源埋在敌人身体里
            Vector3 pos = position + dir * Mathf.Max(0f, Instance.surfaceOffset);

            SonarEmitSettings st = Instance.splashSonar;

            EmitParams p = st.ToEmitParams(dir);

            // ★★ 不会被怪物捕捉 ★★
            //
            //   ignoreMonster = true -> SonarTrailRenderer 的怪物感知会跳过这条光带
            //   （见 SonarTrailRenderer 里 `if (tr.isSneak || tr.ignoreMonster) continue;`）
            p.ignoreMonster = true;

            // 保险：同时打上 isSneak，双层保证怪物一定看不到
            p.isSneak = true;

            // 不透明度固定 1
            p.intensity = 1f;

            // 纯红
            p.color = new Color(1f, 0f, 0f, 1f);

            sonar.Emit(pos, p);
        }
    }
}
