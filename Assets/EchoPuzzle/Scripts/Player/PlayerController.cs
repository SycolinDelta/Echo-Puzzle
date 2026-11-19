using UnityEngine;

namespace EchoPuzzle
{
    /// <summary>玩家做出的一类可被检测的操作。<see cref="None"/> 表示"还没有过操作"。</summary>
    public enum PlayerAction
    {
        None   = 0,
        Space  = 1,   // 按下空格（喊叫）
        Look   = 2,   // 转动视角
        Zoom   = 3,   // 滚轮缩放
        Move   = 4,   // 移动角色
        Marker = 5,   // 按 E 放标记
    }

    public enum MoveMode { Sneak, Walk, Run }

    /// <summary>
    /// M1：第三人称角色控制 + 相机环绕 + 移动发声。
    ///
    /// 四种发声方式（走 / 冲 / 潜 / 空格踏步）的全部参数都在 Inspector 上：
    /// 颜色、初始不透明度、衰减速度、射程、点密度…… 改完直接生效，不用改代码。
    ///
    /// 关于不透明度：衰减速度（fadeSpeed）**天然是全局统一的**，
    /// 因为它是声波的物理属性。所以「这串点能亮多久」完全由初始不透明度决定：
    ///     存活时长 ≈ -ln(0.02) / fadeSpeed × (初始不透明度 / 1.0)
    /// 想让某种步伐看得更久，调它的 initialAlpha，而不是调 fadeSpeed。
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public sealed class PlayerController : MonoBehaviour
    {
        [Header("引用")]
        public Camera      playerCamera;
        public SonarSystem echo;

        [Tooltip("脚印系统。留空则自动创建（每次释放声波打印一个鞋印）")]
        public FootprintSystem footprints;

        [Header("自动补齐（手工摆 Player 时用）")]
        [Tooltip("场景里没有相机时，自动创建一个（清屏纯黑 + Bloom）")]
        public bool autoCreateCamera = true;

        [Tooltip("场景里没有声波系统时，自动创建一个")]
        public bool autoCreateSonar = true;

        private GameObject m_AutoCamera;
        private GameObject m_AutoSonar;

        // ------------------------------------------------------------------ 移动速度

        [Header("移动速度（米/秒）")]
        [Tooltip("潜行速度")]
        public float sneakSpeed = 1.35f;
        [Tooltip("正常走路速度")]
        public float walkSpeed  = 3.4f;
        [Tooltip("冲刺速度")]
        public float runSpeed   = 6.2f;
        [Tooltip("加速度，越大起步越干脆")]
        public float accel      = 16f;

        /// <summary>
        /// 转身的角加速度（Slerp 系数 / 秒）。
        ///
        /// ★ 走路、冲刺、潜行、闪避**全部用这一个值** ——
        ///   需求："给闪避期间加入加速度，包括角速度的加速度和移动速率的加速度，
        ///         这两个加速度和走路时的加速度一样。"
        ///
        ///   原来这个 12f 是写死在 Slerp 调用里的，现在提上来方便统一调整。
        /// </summary>
        public float walkTurnLerp = 12f;

        [Header("击杀后潜行加速")]
        [Tooltip("成功移除一只怪物后，潜行速度立刻变成 N 倍")]
        [Min(1f)] public float sneakBoostMultiplier = 4f;

        [Tooltip("加速后回落到正常速度所需时间（秒）")]
        [Min(0.01f)] public float sneakBoostFadeDuration = 3f;

        /// <summary>潜行加速的剩余时间（秒）。</summary>
        private float m_SneakBoostTimer;

        /// <summary>
        /// 通知：玩家刚刚成功移除了一只怪物。
        /// 潜行速度立刻 ×sneakBoostMultiplier，然后在 sneakBoostFadeDuration 内线性回落。
        /// </summary>
        public void NotifyMonsterRemoved()
        {
            m_SneakBoostTimer = Mathf.Max(0.01f, sneakBoostFadeDuration);
        }

        // ------------------------------------------------------------------ 脚步节奏

        [Header("脚步节奏（走过多少米踏一步）")]
        [Tooltip("潜行一步的位移距离")]
        public float sneakStride = 1.0f;
        [Tooltip("走路一步的位移距离。调小 = 声波更连贯，但生成更频繁")]
        public float walkStride  = 1.9f;
        [Tooltip("冲刺一步的位移距离")]
        public float runStride   = 1.5f;
        [Tooltip("两次发声的最小间隔（秒），防止慢帧时爆炸")]
        public float minEmitInterval = 0.16f;

        [Tooltip("【起步第一步】玩家从**静止**开始移动时，只走过多远就立刻发出第一声（米）。\n\n" +
                 "让起步就有反馈，不必等走满一整个 stride。走 / 跑 / 潜行共用这个值。\n" +
                 "第一声之后恢复按各自 stride 的常规节奏。")]
        [Min(0.05f)] public float firstStepDistance = 0.5f;

        // ------------------------------------------------------------------ 四种声波

        // 四种声波**速度完全一样**（都跟随 SonarTrailRenderer.referenceSpeed）。
        // 它们的区别在：生成频率（stride）、三个仰角层的射线数量（rayCount90/30/60）、
        // 初始不透明度（initialAlpha）、波前存在时长（lifetimeCap）、
        // 尾巴残留时长（tailLinger）、每次反射扣的时长（bounceLifeLoss）。
        //
        // 关于那几个时间：
        //   lifetimeCap    —— 波前推进多久后**停止推进**，形状钉住
        //   tailLinger     —— 钉住之后光带还能**残留多久**才消失
        //   bounceLifeLoss —— 每反射一次，从剩余波前时长里再扣掉这么多秒
        //   三者独立。tailLinger = 0 就是旧行为（波前一停，光带跟着立刻没）。
        //
        // 射线数量是性能大头：一次发声 = 三层之和，每条再弹 maxBounces 次，
        // 总射线检测次数 ≈ 三层之和 × 2^maxBounces，别随手调大。

        [Header("① 走路声波")]
        public EchoPreset walkEcho = new EchoPreset
        {
            enabled = true, color = Color.white, initialAlpha = 0.4f,
            rayCount90 = 12, rayCount30 = 0, rayCount60 = 6, range = 50f, speed = 8f, trailSpacing = 0.30f,
            maxBounces = 3, bounceFalloff = 0.65f,
            lifetimeCap = 4f, tailLinger = 20f, bounceLifeLoss = 0.35f,
            diffuseCount15 = 0, diffuseCount45 = 0, diffuseCount75 = 0,
            hemisphereCount = 18, hemisphereDiffuse = true,
        };

        [Header("② 冲刺声波")]
        public EchoPreset runEcho = new EchoPreset
        {
            enabled = true, color = Color.white, initialAlpha = 0.6f,
            rayCount90 = 12, rayCount30 = 0, rayCount60 = 6, range = 50f, speed = 8f, trailSpacing = 0.30f,
            maxBounces = 3, bounceFalloff = 0.65f,
            lifetimeCap = 4f, tailLinger = 20f, bounceLifeLoss = 0.35f,
            diffuseCount15 = 0, diffuseCount45 = 0, diffuseCount75 = 0,
            hemisphereCount = 18, hemisphereDiffuse = true,
        };

        [Header("③ 潜行声波")]
        public EchoPreset sneakEcho = new EchoPreset
        {
            enabled = true, color = Color.white, initialAlpha = 0.22f,
            rayCount90 = 12, rayCount30 = 0, rayCount60 = 12, range = 2f, speed = 6f, trailSpacing = 0.25f,
            maxBounces = 2, bounceFalloff = 0.5f,
            lifetimeCap = 0.5f, tailLinger = 20f, bounceLifeLoss = 0.15f,
            diffuseCount15 = 0, diffuseCount45 = 0, diffuseCount75 = 0,
            hemisphereCount = 0, hemisphereDiffuse = false,
        };

        [Header("④ 空格踏步声波")]
        public KeyCode    manualPingKey = KeyCode.Space;
        public EchoPreset shoutEcho = new EchoPreset
        {
            enabled = true, color = Color.white, initialAlpha = 1.0f,
            rayCount90 = 40, rayCount30 = 0, rayCount60 = 20, range = 80f, speed = 8f, trailSpacing = 0.30f,
            maxBounces = 4, bounceFalloff = 0.65f,
            lifetimeCap = 10f, tailLinger = 20f, bounceLifeLoss = 0.35f,
            diffuseCount15 = 0, diffuseCount45 = 0, diffuseCount75 = 0,
            hemisphereCount = 140, hemisphereDiffuse = true,
        };

        // ------------------------------------------------------------------ 地牢模式的声波覆盖

        // ★★ 地牢模式（DungeonMode）专用的一套声波参数 ★★
        //
        //   探秘模式用上面那四个（walkEcho / runEcho / sneakEcho / shoutEcho），
        //   地牢模式**完全独立**用下面这几个，互不影响。
        //
        //   ★ 勾上 useDungeonEchoes 才会生效。
        //   ★ 不勾时，地牢模式**和探秘模式一样**用上面那四个。
        //
        //   所以：
        //     · 想让两个模式声波一致 -> 不勾
        //     · 想单独调地牢模式的声波数量 -> 勾上，然后改下面这四个

        [Header("⑤ 地牢模式声波（独立）")]
        [Tooltip("勾上 = 地牢模式使用下面这套独立参数。\n\n" +
                 "★ 不勾 = 地牢模式和探秘模式用同一套（上面那四个）。\n" +
                 "★ 探秘模式**永远**用上面那四个，跟这个开关无关。")]
        public bool useDungeonEchoes = true;

        [Tooltip("地牢 · 走路声波")]
        public EchoPreset dungeonWalkEcho = new EchoPreset
        {
            enabled = true, color = Color.white, initialAlpha = 0.4f,
            rayCount90 = 24, rayCount30 = 0, rayCount60 = 0, range = 10f, speed = 9f, trailSpacing = 0.30f,
            maxBounces = 3, bounceFalloff = 0.65f,
            lifetimeCap = 0.7f, tailLinger = 20f, bounceLifeLoss = 0.35f,
            diffuseCount15 = 0, diffuseCount45 = 0, diffuseCount75 = 0,
            hemisphereCount = 0, hemisphereDiffuse = false,
        };

        [Tooltip("地牢 · 冲刺声波")]
        public EchoPreset dungeonRunEcho = new EchoPreset
        {
            enabled = true, color = Color.white, initialAlpha = 0.6f,
            rayCount90 = 24, rayCount30 = 0, rayCount60 = 0, range = 10f, speed = 9f, trailSpacing = 0.30f,
            maxBounces = 3, bounceFalloff = 0.65f,
            lifetimeCap = 0.7f, tailLinger = 20f, bounceLifeLoss = 0.35f,
            diffuseCount15 = 0, diffuseCount45 = 0, diffuseCount75 = 0,
            hemisphereCount = 0, hemisphereDiffuse = false,
        };

        [Tooltip("地牢 · 潜行声波")]
        public EchoPreset dungeonSneakEcho = new EchoPreset
        {
            enabled = true, color = Color.white, initialAlpha = 0.22f,
            rayCount90 = 18, rayCount30 = 0, rayCount60 = 0, range = 2f, speed = 4f, trailSpacing = 0.25f,
            maxBounces = 2, bounceFalloff = 0.5f,
            lifetimeCap = 0.5f, tailLinger = 20f, bounceLifeLoss = 0.15f,
            diffuseCount15 = 0, diffuseCount45 = 0, diffuseCount75 = 0,
            hemisphereCount = 0, hemisphereDiffuse = false,
        };

        [Tooltip("地牢 · 空格踏步声波")]
        public EchoPreset dungeonShoutEcho = new EchoPreset
        {
            enabled = true, color = Color.white, initialAlpha = 1.0f,
            rayCount90 = 150, rayCount30 = 0, rayCount60 = 0, range = 20f, speed = 9f, trailSpacing = 0.30f,
            maxBounces = 4, bounceFalloff = 0.65f,
            lifetimeCap = 3f, tailLinger = 20f, bounceLifeLoss = 0.35f,
            diffuseCount15 = 0, diffuseCount45 = 0, diffuseCount75 = 0,
            hemisphereCount = 0, hemisphereDiffuse = false,
        };

        /// <summary>
        /// 是否正在使用地牢模式的那套声波参数。
        ///
        /// ★★ 必须**同时**满足两个条件 ★★
        ///     1. 勾上 useDungeonEchoes
        ///     2. 当前在**地牢场景**（DungeonMode）
        ///
        /// ── 为什么要有第 2 条 ────────────────────────────────
        ///   需求："探秘模式里走路、冲刺、潜行、按空格时释放的光带数量
        ///         应该与地牢模式完全分开。"
        ///
        ///   之前只看 useDungeonEchoes。但这个开关是 **Player.prefab 上的
        ///   序列化字段**，地牢模式要用它就必须在 prefab 里勾上 ——
        ///   而同一个 prefab 也被探秘模式使用，于是探秘模式跟着用上了
        ///   dungeonWalkEcho / dungeonShoutEcho。
        ///
        ///   结果就是：在 Inspector 里改 walkEcho / runEcho / sneakEcho /
        ///   shoutEcho（探秘模式那四个）**完全没有反应**，因为根本没人读它们。
        ///
        ///   加上场景判断后：
        ///     · 探秘模式 -> 永远用 walkEcho / runEcho / sneakEcho / shoutEcho
        ///     · 地牢模式 + 勾选 -> 用 dungeonXxxEcho
        ///     · 地牢模式 + 未勾选 -> 退回探秘那一套（有意为之的开关语义）
        /// </summary>
        private bool UseDungeonEchoSet => useDungeonEchoes && GameUI.IsDungeonMode;

        // ------------------------------------------------------------------ 相机

        [Header("相机")]
        public float lookSensitivity = 2.6f;
        [Tooltip("俯视角度上限（负值 = 向上看）")]
        public float pitchMin = -42f;
        [Tooltip("仰视角度上限（正值 = 向下看）")]
        public float pitchMax = 89.9f;

        [Tooltip("每次开始 / 重玩时，相机距离重置回这个值（玩家用滚轮缩放过的距离会被还原）")]
        public float defaultCameraDistance = 9.0f;

        [Tooltip("当前相机距离（米），会被滚轮修改")]
        public float cameraDistance = 9.0f;
        [Tooltip("滚轮能拉近到多近")]
        public float zoomMin = 4f;
        [Tooltip("滚轮能拉到多远")]
        public float zoomMax = 14f;
        [Tooltip("滚轮灵敏度：每一格改变多少米")]
        public float zoomStep = 1f;

        // ------------------------------------------------------------------ 地牢模式：正交俯视相机
        //
        // ★★ 只影响 DungeonMode，探秘模式（PuzzleMode）仍然是原来的透视自由视角。★★
        //
        //   地牢模式的需求：
        //     · 正交相机（Orthographic）
        //     · 始终垂直于地面（俯仰锁定 90°，正上方往下看）
        //     · 滚轮仍然能缩放
        //     · **水平角锁死**，但数值可以在下面这个字段里调

        [Header("地牢模式 · 正交俯视相机")]
        [Tooltip("是否在地牢模式使用正交俯视相机。\n\n" +
                 "关闭 = 地牢模式和探秘模式一样用透视自由视角。")]
        public bool dungeonOrthographicCamera = true;

        [Tooltip("★★★ 地牢模式相机的水平角（度）★★★\n\n" +
                 "这是**固定不变**的朝向，鼠标左右移动不会再转镜头。\n\n" +
                 "   0   = 镜头朝 +Z 方向看（北）\n" +
                 "   90  = 镜头朝 +X 方向看（东）\n" +
                 "   180 = 镜头朝 -Z 方向看（南）\n" +
                 "   270 = 镜头朝 -X 方向看（西）\n\n" +
                 "★ 只是绕竖直轴的旋转，俯仰永远是正下方，不影响\"垂直俯拍\"。")]
        [Range(0f, 360f)] public float dungeonCameraYaw = 45f;

        [Tooltip("地牢模式正交相机的透视体高度（= 可视范围，米）。\n\n" +
                 "★ 正交相机的\"缩放\"就是改这个值：越小看得越近、越大看得越广。\n" +
                 "  实际值由滚轮在 dungeonZoomMin ~ dungeonZoomMax 之间插值得到。")]
        [Min(1f)] public float dungeonOrthoSize = 9f;

        [Tooltip("地牢模式滚轮能拉近到多少（正交体高度，米）。越小越近")]
        [Min(1f)] public float dungeonZoomMin = 8f;

        [Tooltip("地牢模式滚轮能拉到多远（正交体高度，米）。越大越广")]
        [Min(1f)] public float dungeonZoomMax = 14f;

        [Tooltip("地牢模式相机离地高度（米）。正交相机的机位高度只影响裁剪面，" +
                 "不影响画面大小 —— 画面大小由上面那几个 Ortho Size 决定。\n\n" +
                 "填一个比地图高得多的值即可（默认 60，足够盖住整个地牢）。")]
        [Min(1f)] public float dungeonCameraHeight = 60f;

        public float cameraHeight   = 1.55f;
        public float cameraLag      = 14f;

        [Header("拉远时抬高相机")]
        [Tooltip("拉到最远时，相机额外抬高多少米。\n" +
                 "0 = 关闭这个效果。\n\n" +
                 "为什么需要：房间较高时，平视会让顶部超出画面、而地板以下空一大片。\n" +
                 "拉远时把相机抬高，画面中心会更接近房间中段。\n\n" +
                 "注意：这是字段初始值，只对**新建**的组件生效。\n" +
                 "场景里已有的实例请在检查器里改（Unity 会把实例值序列化进场景）。")]
        public float maxZoomExtraHeight = 3f;

        [Tooltip("滚轮距离到这个值时开始抬高（通常在默认距离附近）。\n" +
                 "比它近的距离完全不抬高 —— 近距离看角色时视角保持低位。")]
        public float zoomHeightStart = 8f;

        [Tooltip("抬高的曲线。1 = 线性；>1 = 越远抬得越猛（前段几乎不抬）；<1 = 一开始就抬得快")]
        [Range(0.2f, 4f)] public float zoomHeightCurve = 1.6f;

        [Tooltip("进入 Play 时锁定鼠标。关掉的话需要按住右键才能转视角")]
        public bool lockCursorOnStart = true;

        [Tooltip("相机防穿墙。正式玩法必须保持关闭：相机能穿过墙体，玩家才没法靠相机位置反推墙在哪。")]
        public bool cameraCollision = false;
        [Tooltip("仅在 cameraCollision 打开时生效")]
        public LayerMask cameraCollisionMask = ~((1 << 2) | (1 << 4) | (1 << 5));

        // ------------------------------------------------------------------ 运行状态

        public float CurrentNoiseLoudness { get; private set; }
        public MoveMode Mode { get; private set; } = MoveMode.Walk;
        public bool IsMoving { get; private set; }

        // ------------------------------------------------------------------ 操作检测
        // 供 HintZone 之类的组件读取：玩家最后做的是哪类操作、什么时候做的。
        // 只在"操作开始的那一帧"更新，持续按住不会每帧刷新 ——
        // 这样消费方可以用"时间戳是否变新"来判断又发生了一次操作。

        /// <summary>玩家最近一次操作的类型。</summary>
        public PlayerAction LastAction { get; private set; } = PlayerAction.None;

        /// <summary>最近一次操作发生的时间（Time.timeSinceLevelLoad）。</summary>
        public float LastActionTime { get; private set; } = -999f;

        private bool m_WasMoving;
        private bool m_WasLooking;

        private void NoteAction(PlayerAction action)
        {
            LastAction     = action;
            LastActionTime = Time.timeSinceLevelLoad;
        }

        private CharacterController m_Controller;
        private Transform m_CamPivot;

        private float m_Yaw;
        private float m_Pitch = 12f;
        private float m_VerticalVelocity;
        private Vector3 m_HorizontalVelocity;

        private float m_StrideAccum;
        private float m_LastEmitTime;

        /// <summary>
        /// 是否已经迈出过"起步第一步"。
        /// 静止时置回 false，于是下一次起步会先用 firstStepDistance 触发一声。
        /// </summary>
        private bool m_HasTakenFirstStep;

        private void Awake()
        {
            m_Controller = GetComponent<CharacterController>();

            // 这几个是"手工把 Player 拖进场景时也必须存在"的配套系统。
            // 以前由 EchoBootstrap 统一创建，现在关卡是手搭的，所以在这里补齐。
            EnsureCamera();
            EnsureSonar();
            EnsureMarkerSystem();
            EnsureFootprintSystem();

            // ★★ 地牢模式：没有石子 / 匕首，这两个系统整个不建 ★★
            //
            //   StoneThrowSystem 的 Awake 会建出 AimRing / LandingRing 两个圆环，
            //   DaggerSystem 会建出 DaggerAimRing —— 它们用 AimOverlay 材质
            //   （ZTest Always + Overlay 队列），一旦被启用就会穿透一切，
            //   在地图上显示成一个漂浮的圆环。
            //   地牢模式的武器是枪，这两套都不该存在。
            if (!GameUI.IsDungeonMode)
            {
                EnsureStoneThrowSystem();
                EnsureDaggerSystem();
            }

            m_Yaw = transform.eulerAngles.y;

            if (m_CamPivot == null)
            {
                var pivot = new GameObject("EchoCameraPivot");
                m_CamPivot = pivot.transform;
                m_CamPivot.SetParent(null);
            }
            // ★ 地牢模式：开局就切到正交俯视，避免第一帧闪一下透视画面
            if (IsDungeonTopDown)
            {
                ApplyDungeonCameraMode(true);

                cameraDistance = Mathf.Clamp(dungeonOrthoSize,
                                             Mathf.Max(1f, dungeonZoomMin),
                                             Mathf.Max(1.1f, dungeonZoomMax));

                m_CamPivot.position = transform.position + Vector3.up * Mathf.Max(1f, dungeonCameraHeight);
                m_CamPivot.rotation = Quaternion.Euler(90f, dungeonCameraYaw, 0f);

                if (playerCamera != null)
                {
                    playerCamera.orthographicSize = cameraDistance;

                    Transform dct = playerCamera.transform;
                    dct.position = m_CamPivot.position;
                    dct.rotation = m_CamPivot.rotation;
                }

                return;
            }

            m_CamPivot.position = transform.position + Vector3.up * CurrentCameraHeight();
            m_CamPivot.rotation = Quaternion.Euler(m_Pitch, m_Yaw, 0f);

            // 开局就把相机摆到默认俯仰 / 缩放距离，避免第一帧闪一下
            if (playerCamera != null)
            {
                Transform ct = playerCamera.transform;
                ct.position = m_CamPivot.position - m_CamPivot.forward * cameraDistance;
                ct.rotation = m_CamPivot.rotation;
            }

            // 光标由 GameUI 统一管理（首页 / 暂停要解锁）。这里不主动锁定。
        }

        /// <summary>场景里没有相机就建一个（纯黑清屏 + Bloom）。</summary>
        private void EnsureCamera()
        {
            playerCamera = Camera.main;
            if (playerCamera != null) return;
            if (!autoCreateCamera) return;

            m_AutoCamera = new GameObject("EchoCamera");

            var cam = m_AutoCamera.AddComponent<Camera>();
            cam.clearFlags      = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.fieldOfView     = 68f;
            cam.nearClipPlane   = 0.05f;
            cam.farClipPlane    = 400f;
            m_AutoCamera.tag    = "MainCamera";

            var data = m_AutoCamera.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            data.renderPostProcessing = true;
            data.renderShadows        = true;
            data.antialiasing         = UnityEngine.Rendering.Universal.AntialiasingMode.FastApproximateAntialiasing;

            // Bloom 是声波发光的关键
            var profile = ScriptableObject.CreateInstance<UnityEngine.Rendering.VolumeProfile>();
            profile.name      = "EchoVolumeProfile";
            profile.hideFlags = HideFlags.DontSave;

            var bloom = profile.Add<UnityEngine.Rendering.Universal.Bloom>(true);
            bloom.intensity.overrideState = true;
            bloom.intensity.value         = 1.1f;
            bloom.threshold.overrideState = true;
            bloom.threshold.value         = 0.85f;
            bloom.scatter.overrideState   = true;
            bloom.scatter.value           = 0.72f;

            var vignette = profile.Add<UnityEngine.Rendering.Universal.Vignette>(true);
            vignette.intensity.overrideState  = true;
            vignette.intensity.value          = 0.30f;
            vignette.smoothness.overrideState = true;
            vignette.smoothness.value         = 0.6f;

            var volumeGo = new GameObject("EchoVolume");
            volumeGo.transform.SetParent(m_AutoCamera.transform, false);

            var volume = volumeGo.AddComponent<UnityEngine.Rendering.Volume>();
            volume.isGlobal      = true;
            volume.priority      = 10f;
            volume.sharedProfile = profile;

            playerCamera = cam;

            // ★ 相机必须带一个 AudioListener，否则整个场景一点声音都听不见，
            //   而且每个 AudioSource 播放时都会刷一条
            //   "There are no audio listeners in the scene" 警告。
            EnsureAudioListener(m_AutoCamera);

        }

        /// <summary>
        /// 保证场上恰有一个 AudioListener。
        ///
        /// 场景里完全没有相机时（本项目的常见情况），这里会补在自动创建的相机上。
        /// 已经有的话不动它 —— 多个 AudioListener 反而会让 Unity 报警告。
        /// </summary>
        private static void EnsureAudioListener(GameObject host)
        {
            if (Object.FindObjectOfType<AudioListener>() != null) return;

            host.AddComponent<AudioListener>();
        }

        /// <summary>场景里没有声波系统就建一个。</summary>
        private void EnsureSonar()
        {
            echo = FindObjectOfType<SonarSystem>();
            if (echo != null) return;
            if (!autoCreateSonar) return;

            m_AutoSonar = new GameObject("SonarSystem");

            var rendererGo = new GameObject("SonarTrailRenderer");
            rendererGo.transform.SetParent(m_AutoSonar.transform, false);
            var trailRenderer = rendererGo.AddComponent<SonarTrailRenderer>();

            echo = m_AutoSonar.AddComponent<SonarSystem>();
            echo.trailRenderer = trailRenderer;

        }

        /// <summary>场景里没有标记系统就建一个（按数字键 1~5 放标记）。</summary>
        private void EnsureMarkerSystem()
        {
            if (FindObjectOfType<MarkerSystem>() != null) return;

            var go = new GameObject("MarkerSystem");
            var ms = go.AddComponent<MarkerSystem>();
            ms.player = this;
        }

        /// <summary>场景里没有脚印系统就建一个。</summary>
        private void EnsureFootprintSystem()
        {
            footprints = FindObjectOfType<FootprintSystem>();
            if (footprints != null) return;

            var go = new GameObject("FootprintSystem");
            footprints = go.AddComponent<FootprintSystem>();
            footprints.player = this;
        }

        /// <summary>投掷石子系统。场景里没有就建一个。</summary>
        public StoneThrowSystem stones;

        private void EnsureStoneThrowSystem()
        {
            // ★ 连**未激活**的也一起找 —— 否则某人把它 SetActive(false) 之后，
            //   这里会找不到、再建一个新的（旧的圆环还在，又多了新的）。
            stones = FindObjectOfType<StoneThrowSystem>(true);
            if (stones != null) return;

            var go = new GameObject("StoneThrowSystem");
            stones = go.AddComponent<StoneThrowSystem>();
            stones.player = this;
        }

        /// <summary>匕首刺出技能。场景里没有就建一个。</summary>
        public DaggerSystem dagger;

        private void EnsureDaggerSystem()
        {
            // ★ 同上：连未激活的也一起找，避免重复创建
            dagger = FindObjectOfType<DaggerSystem>(true);
            if (dagger != null) return;

            var go = new GameObject("DaggerSystem");
            dagger = go.AddComponent<DaggerSystem>();
            dagger.player = this;
        }

        /// <summary>把玩家与相机瞬移回指定位置，并清掉动量。重玩时用。</summary>
        public void Teleport(Vector3 position, float yaw, float pitch)
        {
            if (m_Controller != null) m_Controller.enabled = false;

            transform.position = position;
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);

            m_Yaw   = yaw;
            m_Pitch = pitch;
            m_VerticalVelocity   = 0f;
            m_HorizontalVelocity = Vector3.zero;

            // 瞬移后等同于"从静止重新起步"：累计器和第一步标志都要清
            m_StrideAccum        = 0f;
            m_HasTakenFirstStep  = false;

            // ★★ 地牢模式：正交俯视相机，直接摆到"玩家正上方 + 固定水平角" ★★
            //
            //   和下面的透视逻辑分开写，探秘模式完全不受影响。
            if (IsDungeonTopDown)
            {
                ApplyDungeonCameraMode(true);

                // 正交尺寸也回到默认
                cameraDistance = Mathf.Clamp(dungeonOrthoSize,
                                             Mathf.Max(1f, dungeonZoomMin),
                                             Mathf.Max(1.1f, dungeonZoomMax));

                if (m_CamPivot != null)
                {
                    m_CamPivot.position = position + Vector3.up * Mathf.Max(1f, dungeonCameraHeight);
                    m_CamPivot.rotation = Quaternion.Euler(90f, dungeonCameraYaw, 0f);
                }

                // ★ 下一帧 LateUpdate 不要再 lerp，直接吸附到玩家
                m_CameraSnapNextFrame = true;

                if (playerCamera != null)
                {
                    playerCamera.orthographicSize = cameraDistance;

                    Transform dct = playerCamera.transform;
                    dct.position = m_CamPivot != null
                        ? m_CamPivot.position
                        : position + Vector3.up * Mathf.Max(1f, dungeonCameraHeight);
                    dct.rotation = m_CamPivot != null ? m_CamPivot.rotation
                                                      : Quaternion.Euler(90f, dungeonCameraYaw, 0f);
                }

                if (m_Controller != null) m_Controller.enabled = true;
                return;
            }

            // 不在正交模式时确保相机是透视的（从地牢切回探秘 / 关掉开关时用）
            ApplyDungeonCameraMode(false);

            // 每次开局把相机距离还原成默认值，玩家滚轮缩放过的距离不跨局残留
            cameraDistance = defaultCameraDistance;

            if (m_CamPivot != null)
            {
                m_CamPivot.position = position + Vector3.up * CurrentCameraHeight();
                m_CamPivot.rotation = Quaternion.Euler(pitch, yaw, 0f);
            }

            if (playerCamera != null)
            {
                Transform ct = playerCamera.transform;
                ct.position = m_CamPivot != null
                    ? m_CamPivot.position - m_CamPivot.forward * cameraDistance
                    : position;
                ct.rotation = m_CamPivot != null ? m_CamPivot.rotation : Quaternion.identity;
            }

            if (m_Controller != null) m_Controller.enabled = true;
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            // 只在"镜头还需要接受输入"时重新锁光标（进行中 / 胜负界面淡出期）。
            // 首页 / 暂停 / 淡出结束后的胜负界面都保持解锁 —— 那些界面要用鼠标点按钮。
            // ★ 任何瞄准期间都保持解锁（投石 / 匕首都要用鼠标位置做极坐标），
            //   否则切窗口回来时这里会把瞄准用的光标重新锁死。
            // 只有投石需要解锁光标；匕首用虚拟光标，光标保持锁定。
            bool aiming = stones != null && stones.IsAiming;

            // ★ 地牢模式的强化三选一界面要用真实指针点卡片，
            //   切窗口回来时不能把它锁回去（否则点哪都没反应）。
            bool buffUiOpen = Dungeon.DungeonBuffSelectUI.Instance != null
                           && Dungeon.DungeonBuffSelectUI.Instance.IsOpen;

            if (hasFocus && lockCursorOnStart && GameUI.CameraInputActive && !aiming && !buffUiOpen)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible   = false;
            }
        }

        /// <summary>脚下的地面点 —— 脚步的声源就放在这里。</summary>
        public Vector3 FootPosition
        {
            get
            {
                Vector3 p = transform.position;
                p.y += m_Controller.skinWidth + 0.01f;
                return p;
            }
        }

        // ------------------------------------------------------------------ 主循环

        private void Update()
        {
            // 镜头是否接受输入（转视角 / 滚轮）。
            // 首页 / 暂停时为 false —— 否则会跟 UI 按钮抢鼠标。
            // 但**胜负界面淡出那几秒**是 true：那几秒里世界继续跑，还能环顾四周。
            if (!GameUI.CameraInputActive)
            {
                m_HorizontalVelocity = Vector3.zero;
                IsMoving = false;
                return;
            }

            HandleLook();
            HandleZoom();

            // 角色本身是否可操作。
            // 胜负界面淡出期间这里是 false —— 世界继续跑、光带继续动、镜头能动，
            // 但人不能再走、不能再发声、不能再放标记。
            if (!GameUI.PlayerControllable)
            {
                // 已经站住了就别再走，但也不要强行归零速度 ——
                // 让惯性自然停下，看起来更顺（角色会滑一小段然后停住）。
                m_HorizontalVelocity = Vector3.MoveTowards(m_HorizontalVelocity, Vector3.zero,
                                                           accel * 2f * Time.deltaTime);
                IsMoving = false;
                m_WasMoving = false;

                // 停下 -> 累计器和第一步标志都清，下次起步重新从 0.5m 开始
                m_StrideAccum       = 0f;
                m_HasTakenFirstStep = false;

                // 重力照常，免得站在半空时僵住
                if (m_Controller != null)
                {
                    if (m_Controller.isGrounded && m_VerticalVelocity < 0f) m_VerticalVelocity = -2f;
                    m_VerticalVelocity += Physics.gravity.y * Time.deltaTime;
                    m_Controller.Move(Vector3.up * m_VerticalVelocity * Time.deltaTime);
                }

                return;
            }

            // ★★ 充能推进（闪避结束后才开始充）★★
            TickDodgeCharge();

            // ★★ 闪避必须在 HandleMove **之前**处理 ★★
            //
            //   HandleMove 里要靠 IsDodging 决定"用走路速度×5"和"强制退出冲刺/潜行"。
            //   如果闪避放在它后面，按下 Alt 的那一帧 IsDodging 还是 false，
            //   于是当帧仍然按冲刺/潜行速度走，而且不会退出冲刺/潜行状态。
            HandleDungeonDodge();

            HandleMove();
            HandleStrideNoise();

            // ★ 空格：地牢模式有冷却（dungeonShoutCooldown），探秘模式无冷却
            bool shoutReady = !GameUI.IsDungeonMode
                           || Time.timeSinceLevelLoad >= m_NextShoutTime;

            if (Input.GetKeyDown(manualPingKey) && shoutReady)
            {
                NoteAction(PlayerAction.Space);

                if (GameUI.IsDungeonMode)
                    m_NextShoutTime = Time.timeSinceLevelLoad + Mathf.Max(0f, dungeonShoutCooldown);

                // ★ 空格（跺脚）是独立声源：**不受潜行影响**
                //   地牢模式用自己那一套 dungeonShoutEcho
                EchoPreset shoutPreset = UseDungeonEchoSet ? dungeonShoutEcho : shoutEcho;

                if (echo != null && shoutPreset.enabled)
                    Emit(shoutPreset, affectedBySneak: false);

                // 空格（跺脚）的音效。放在 Emit 外面：
                // Emit 里挂的是"脚步类"音效（按 Mode 分走/跑/潜行），
                // 而空格是单独一种动作，不该跟着脚步声走。
                AudioSystem.Play(AudioSystem.Cue.Stomp);
            }

            // 按 R：把相机拉回玩家正后方（朝向玩家正前方），
            // 俯仰角和缩放距离用下面的可调参数
            if (Input.GetKeyDown(cameraResetKey)) ResetCamera();
        }

        // ------------------------------------------------------------------ 地牢模式闪避

        /// <summary>
        /// 地牢模式的闪避：按下左 Alt -> 0.5 秒无敌 + 5 倍走路速度 + 半透明。
        ///
        /// ★ 探秘模式完全不执行。
        /// ★ 冷却从**按下那一刻**开始算（不是从闪避结束算）。
        /// </summary>
        // ══════════════════════════════════════════════════════════════════
        //                     地牢模式 · 闪避（充能制）
        // ══════════════════════════════════════════════════════════════════
        //
        //   ★★ 没有冷却，改成「充能」★★
        //
        //   ── 能量 ─────────────────────────────────────────────────────
        //     · 满充能 = 2 秒
        //     · 基础单位 = 0.1 秒（一切按 0.1s 的倍数走）
        //     · **只要能量 >= 0.5s 就能按左 Alt**
        //
        //   ── 一次闪避消耗多少 ─────────────────────────────────────────
        //     · 按下就立刻扣 0.5s（这是每次最少消耗）
        //     · 时间轴：0.2 加速 → 0.1 维持 17 → [继续消耗] → 0.2 减速
        //     · 到第 0.3s 若仍按住 Alt，则每多维持 0.1s 再扣 0.1s 能量，
        //       1:1 兑换，直到能量耗尽
        //     · 最多再消耗 1.5s -> 17 速一共 1.6s -> 全程 2.0s
        //
        //   ── 充能节奏（已有能量越少，充能越慢）────────────────────────
        //     已有能量        每次充能间隔
        //     1.5 ~ 2.0s      0.2s
        //     1.2 ~ 1.4s      0.3s
        //     0.9 ~ 1.1s      0.4s
        //     0.6 ~ 0.8s      0.5s
        //     0.3 ~ 0.5s      0.6s
        //     0.0 ~ 0.2s      0.7s
        //     · 每次充能 +0.1s
        //     · **闪避结束后才开始充能**
        //
        // ══════════════════════════════════════════════════════════════════

        private const float DodgeEnergyStep = 0.1f;    // 充能基础单位


        /// <summary>
        /// 地牢模式的闪避（充能制，支持长按）。
        /// </summary>
        private void HandleDungeonDodge()
        {
            if (!GameUI.IsDungeonMode) return;
            if (!GameUI.PlayerControllable) return;

            float now = Time.timeSinceLevelLoad;

            // ---- 1) 按下键：开始闪避（只要能量 >= 最小消耗）----
            if (Input.GetKeyDown(dungeonDodgeKey) && !m_DodgeCharging && !IsDodging)
            {
                if (!CanStartDodge()) return;      // 能量不足，按了也没用

                // ★ 按下立刻扣掉最小消耗 0.5s
                m_DodgeHoldExtend  = 0f;
                m_DodgeHoldCurrent = dodgeHoldTime;
                m_DodgeCharging    = true;

                // 三段式曲线的时间原点 + 加速段起点速度
                m_DodgeStartTime  = now;
                m_DodgeStartSpeed = m_HorizontalVelocity.magnitude;

                // ★★ 只扣「基础消耗 0.5s」★★
                //
                //   需求："单击左alt会立即消耗0.5s充能，这也是每次使用时
                //         至少会消耗的能量。"
                //         "在0.3s后继续消耗能量来1:1维持17速，
                //          最多再消耗1.5s后耗尽"
                //
                //   ★ 0.2 加速和 0.1 固定维持是"基础 0.5s"覆盖的，
                //     不再额外扣 —— 否则满能量只能撑 1.9s。
                SpendDodgeEnergy(MinDodgeEnergy);

                StartDodgeSegment(now);

                // 加速度的方向：有输入就朝输入方向，没输入就朝玩家正面
                m_DodgeDirection = CurrentMoveDirection();

                ApplyDodgeAlpha(true);

                NoteAction(PlayerAction.Move);

                // 一旦开始闪避，充能暂停
                m_ChargeAccum = 0f;
            }

            // ---- 2) 按住键：从第 0.3s 起继续消耗能量维持 17 速 ----
            //
            //   需求："如果在 0.3s 后仍然按住左 alt，则会继续消耗充能，
            //         每维持 0.1s 消耗 0.1s 能量，直至能量耗尽。"
            if (m_DodgeCharging)
            {
                float t = now - m_DodgeStartTime;

                // 延长段起点 = 加速 + 固定维持
                float extendStart = Mathf.Max(0f, dodgeAccelTime) + Mathf.Max(0f, dodgeHoldTime);

                if (t > extendStart && Input.GetKey(dungeonDodgeKey))
                {
                    float maxHold = Mathf.Max(dodgeHoldTime, dodgeMaxHoldTime);
                    float step    = Mathf.Max(0.01f, DodgeEnergyStep);

                    // 理论上想延长多少（按 0.1s 取整，只增不减）
                    float wantExtend = Mathf.Floor((t - extendStart) / step) * step;
                    wantExtend = Mathf.Clamp(wantExtend, 0f, Mathf.Max(0f, maxHold - dodgeHoldTime));

                    if (wantExtend > m_DodgeHoldExtend)
                    {
                        // ★ 能量够不够撑到 wantExtend？
                        //
                        //   基础 0.5s 已经在按下时扣掉了，
                        //   这里只需要再付「新延长出来的那部分」。
                        float need = wantExtend - m_DodgeHoldExtend;
                        float avail = Mathf.Max(0f, m_DodgeEnergy);

                        if (avail < need)
                        {
                            // 能量只够撑一小段 -> 按 0.1s 单位向下取整
                            need      = Mathf.Floor(avail / step) * step;
                            wantExtend = m_DodgeHoldExtend + need;
                        }

                        if (need > 0f)
                        {
                            SpendDodgeEnergy(need);

                            m_DodgeHoldExtend  = wantExtend;
                            m_DodgeHoldCurrent = dodgeHoldTime + wantExtend;

                            StartDodgeSegment(now);
                        }
                    }
                }
            }

            // ---- 3) 闪避结束：恢复正常材质 ----
            //
            // ★ 判断依据是"有没有正在替换中"（m_DodgeRenderers.Count），
            //   不是早就废弃的 m_SavedOutlineAlpha —— 那个字段现在没人赋值了，
            //   用它当判据会导致材质永远恢复不回来。
            if (!IsDodging)
            {
                if (m_DodgeCharging)
                {
                    m_DodgeCharging = false;

                    // 闪避结束 -> 充能累加器清零，从这一刻开始重新充能
                    m_ChargeAccum = 0f;
                }

                ApplyDodgeAlpha(false);
            }
        }

        /// <summary>
        /// 按当前的维持时长，重算闪避的结束时刻 + 重新颁发无敌帧。
        ///
        /// ★ 每次延长维持段都会重新调用一次 ——
        ///   m_DodgeUntil 和无敌时间都跟着往后推。
        /// </summary>
        private void StartDodgeSegment(float now)
        {
            float a = Mathf.Max(0f, dodgeAccelTime);
            float h = Mathf.Max(0f, m_DodgeHoldCurrent);
            float d = Mathf.Max(0f, dodgeDecelTime);

            float total = Mathf.Max(0.01f, a + h + d);

            m_DodgeUntil = m_DodgeStartTime + total;

            // ★ 无敌帧：覆盖**整段**闪避（加速 + 维持 + 减速）
            //
            //   PlayerHealth 没有 Instance 单例，用 FindObjectOfType 找
            //   （和 DungeonMonsterBullet 里同一个做法）
            if (m_CachedHealth == null) m_CachedHealth = FindObjectOfType<Dungeon.PlayerHealth>();

            if (m_CachedHealth != null)
            {
                // GrantInvincibility 是"只延长不缩短"，
                // 所以每次延长都要把绝对时刻重新算一遍
                float remain = m_DodgeUntil - now;

                if (remain > 0f) m_CachedHealth.GrantInvincibility(remain);
            }
        }

        // ------------------------------------------------------------------ 充能

        /// <summary>
        /// 能量是否够按一次闪避（至少 0.5s）。
        /// </summary>
        public bool CanStartDodge()
            => m_DodgeEnergy >= MinDodgeEnergy - 1e-4f;

        /// <summary>扣能量（按 0.1s 精度）。</summary>
        private void SpendDodgeEnergy(float amount)
        {
            if (amount <= 0f) return;

            SetDodgeEnergy(m_DodgeEnergy - amount);
        }

        /// <summary>设置能量（夹到 0..2，并对齐到 0.1s 精度）。</summary>
        private void SetDodgeEnergy(float value)
        {
            float max   = Mathf.Max(DodgeEnergyStep, DodgeEnergyMax);
            float step  = Mathf.Max(0.01f, DodgeEnergyStep);

            // 对齐到 0.1s 的整数倍（用 Round 消掉累积误差）
            float rounded = Mathf.Round(value / step) * step;

            m_DodgeEnergy = Mathf.Clamp(rounded, 0f, max);
        }

        /// <summary>
        /// 按当前能量决定充能间隔（秒）。
        ///
        /// 需求：
        ///   1.5~2.0s -> 0.2s
        ///   1.2~1.4s -> 0.3s
        ///   0.9~1.1s -> 0.4s
        ///   0.6~0.8s -> 0.5s
        ///   0.3~0.5s -> 0.6s
        ///   0.0~0.2s -> 0.7s
        /// </summary>
        private float ChargeIntervalFor(float energy)
        {
            float e = Mathf.Max(0f, energy);

            if (e >= 1.5f - 1e-3f) return 0.3f;
            if (e >= 1.2f - 1e-3f) return 0.4f;
            if (e >= 0.9f - 1e-3f) return 0.5f;
            if (e >= 0.6f - 1e-3f) return 0.6f;
            if (e >= 0.3f - 1e-3f) return 0.7f;

            return 0.7f;
        }

        /// <summary>
        /// 充能推进（每帧调）。
        ///
        /// ★ 闪避进行中不充能（"在闪避结束后才开始充能"）。
        /// ★ 每满一个间隔就 +0.1s，间隔由**当前能量**决定。
        /// </summary>
        private void TickDodgeCharge()
        {
            if (!GameUI.IsDungeonMode) return;

            // 闪避中不充能
            if (m_DodgeCharging || IsDodging)
            {
                m_ChargeAccum = 0f;
                return;
            }

            if (m_DodgeEnergy >= DodgeEnergyMax - 1e-4f)
            {
                m_ChargeAccum = 0f;
                return;
            }

            float interval = ChargeIntervalFor(m_DodgeEnergy);

            m_ChargeAccum += Time.deltaTime;

            // 一帧内可能跨过多个间隔（帧率很低时），用 while 补齐
            while (m_ChargeAccum >= interval && m_DodgeEnergy < DodgeEnergyMax - 1e-4f)
            {
                m_ChargeAccum -= interval;

                SetDodgeEnergy(m_DodgeEnergy + DodgeEnergyStep);

                // 能量变了 -> 间隔可能也变了
                interval = ChargeIntervalFor(m_DodgeEnergy);

                if (interval <= 0f) break;
            }
        }


        /// <summary>
        /// 闪避期间的速度曲线（三段式平滑加减速）。
        ///
        /// ★ 需求原文："把闪避时的加速度改成平滑的加减速，不论原本的速度是多少，
        ///             按左alt后都会在0.2秒内将移速提高至17，维持0.1秒，
        ///             然后在0.2秒内将移速降低至当前该有的移速。"
        ///
        ///   时间轴：
        ///     [0, a)          加速：startSpeed -> peak     （a = dodgeAccelTime）
        ///     [a, a+h)        维持：peak                    （h = dodgeHoldTime）
        ///     [a+h, a+h+d]    减速：peak -> naturalSpeed    （d = dodgeDecelTime）
        ///
        ///   ★ naturalSpeed 是**每帧实时算的** ——
        ///     玩家在这 0.5 秒里按住 Shift，减速段就会降到冲刺速度。
        /// </summary>
        private float DodgeSpeedAt(float now, float peak, float naturalSpeed)
        {
            float a = Mathf.Max(0f, dodgeAccelTime);

            // ★★ 用**动态**的维持时长 ★★
            //   单击 = dodgeHoldTime（0.1s）
            //   长按 = dodgeHoldTime + 已延长量（最多到 dodgeMaxHoldTime）
            float h = Mathf.Max(0f, m_DodgeHoldCurrent > 0f ? m_DodgeHoldCurrent : dodgeHoldTime);

            float d = Mathf.Max(0f, dodgeDecelTime);

            float t = now - m_DodgeStartTime;

            // ---- 加速段 ----
            if (t < a)
            {
                if (a <= 1e-4f) return peak;

                // SmoothStep 让起步和到达都不突兀
                float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / a));
                return Mathf.Lerp(m_DodgeStartSpeed, peak, k);
            }

            // ---- 维持段 ----
            if (t < a + h) return peak;

            // ---- 减速段 ----
            if (t < a + h + d)
            {
                if (d <= 1e-4f) return naturalSpeed;

                float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((t - a - h) / d));
                return Mathf.Lerp(peak, naturalSpeed, k);
            }

            // 曲线走完（理论上 IsDodging 已经为 false）
            return naturalSpeed;
        }

        /// <summary>当前输入对应的世界移动方向（没有输入时用玩家正面）。</summary>
        private Vector3 CurrentMoveDirection()
        {
            float h = -Input.GetAxisRaw("Horizontal");
            float v = Input.GetAxisRaw("Vertical");

            Vector3 fwd;

            if (IsDungeonTopDown)
            {
                float yawRad = dungeonCameraYaw * Mathf.Deg2Rad;
                fwd = new Vector3(Mathf.Sin(yawRad), 0f, Mathf.Cos(yawRad));
            }
            else
            {
                fwd = m_CamPivot != null ? m_CamPivot.forward : transform.forward;
                fwd.y = 0f;
            }

            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
            fwd.Normalize();

            Vector3 right = Vector3.Cross(fwd, Vector3.up).normalized;

            Vector3 dir = fwd * v + right * h;

            if (dir.sqrMagnitude < 1e-6f)
                dir = transform.forward;

            dir.y = 0f;

            return dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector3.forward;
        }

        // ------------------------------------------------------------------ 闪避的半透明

        // ★★ 为什么不是"改 alpha"，而是"换材质" ★★
        //
        //   玩家的可见性由 EchoPuzzle/Outline 着色器决定，而那个 shader
        //   **没有 Blend 指令**（走的是不透明管线）—— 改 _OutlineColor.a
        //   在画面上毫无变化。
        //
        //   所以这里另外准备一个 EchoPuzzle/OutlineTransparent：
        //   外观和 Outline 完全一致，只是多了 _Alpha 属性 + 透明混合。
        //   闪避时把它换上去，结束后换回原材质。

        /// <summary>闪避用的半透明材质（懒创建，整个组件共用一个）。</summary>
        private Material m_DodgeMat;

        /// <summary>闪避期间被替换的渲染器 → 它们原本的材质。</summary>
        private readonly System.Collections.Generic.List<Renderer> m_DodgeRenderers
            = new System.Collections.Generic.List<Renderer>(8);
        private readonly System.Collections.Generic.List<Material> m_DodgeSavedMats
            = new System.Collections.Generic.List<Material>(8);

        /// <summary>
        /// 开 / 关闪避的半透明效果。
        ///
        /// ★ 需求原文："要将玩家物体和武器建模、瞄准圈一同替换。"
        ///   所以搜集范围是：
        ///     · 玩家自己的所有 Renderer
        ///     · DungeonWeapon 的瞄准圆环 / 武器模型 / 辅助线（它们挂在武器物体下）
        /// </summary>
        private void ApplyDodgeAlpha(bool on)
        {
            if (on) EnterDodgeVisual();
            else    ExitDodgeVisual();
        }

        private void EnterDodgeVisual()
        {
            if (m_DodgeRenderers.Count > 0) return;   // 已经在闪避视觉里了

            Material dodge = EnsureDodgeMaterial();
            if (dodge == null) return;

            // ---- 1) 玩家自己的渲染器 ----
            CollectDodgeRenderers(transform);

            // ---- 2) 武器 / 瞄准圈 / 辅助线 ----
            //
            //   DungeonWeapon 在场景里是个独立物体（DungeonWeaponBootstrap 建的），
            //   所以要单独找一次。
            var gun = Dungeon.DungeonWeapon.Instance;
            if (gun != null && gun != transform && !gun.transform.IsChildOf(transform))
                CollectDodgeRenderers(gun.transform);

            if (m_DodgeRenderers.Count == 0)
            {
                Debug.LogWarning("[地牢] 闪避：没找到可以替换的轮廓渲染器，" +
                                 "半透明效果不会生效。");
                return;
            }


            // ---- 3) 把 alpha 设成目标值 ----
            if (dodge.HasProperty("_Alpha"))
                dodge.SetFloat("_Alpha", Mathf.Clamp01(dodgeAlpha));

            // ---- 4) 换成半透明材质 ----
            for (int i = 0; i < m_DodgeRenderers.Count; i++)
            {
                Renderer r = m_DodgeRenderers[i];
                if (r == null) continue;

                m_DodgeSavedMats.Add(r.sharedMaterial);
                r.sharedMaterial = dodge;
            }
        }

        private void ExitDodgeVisual()
        {
            if (m_DodgeRenderers.Count == 0) return;

            for (int i = 0; i < m_DodgeRenderers.Count; i++)
            {
                Renderer r = m_DodgeRenderers[i];
                if (r == null) continue;

                if (i < m_DodgeSavedMats.Count)
                    r.sharedMaterial = m_DodgeSavedMats[i];
            }

            m_DodgeRenderers.Clear();
            m_DodgeSavedMats.Clear();
        }

        /// <summary>把一个物体及其所有子物体里有材质的 Renderer 收进来。</summary>
        private void CollectDodgeRenderers(Transform root)
        {
            Renderer[] rs = root.GetComponentsInChildren<Renderer>(true);

            for (int i = 0; i < rs.Length; i++)
            {
                Renderer r = rs[i];
                if (r == null) continue;
                if (r.sharedMaterial == null) continue;

                // ★ 只处理轮廓类材质 —— 声波光带、标记、脚印这些不该跟着变透明
                Material m = r.sharedMaterial;
                if (!IsOutlineLike(m)) continue;

                // ★★ 排除"特效对象" ★★
                //
                //   DungeonWeaponBootstrap 把 DungeonWeapon / BulletBlastRenderer /
                //   HandBladeLineRenderer 挂在**同一个 root** 上，
                //   而爆炸圆形、怪物光环圆、手刀线段都是这些渲染器的子物体，
                //   材质同样是 AimOverlay。不排除的话，玩家一按 Alt 闪避，
                //   场上所有爆炸圆和光环圆都会跟着变半透明
                //   —— 表现就是"光环在闪避期间消失了"。
                if (IsDodgeExcludedRenderer(r)) continue;

                if (!m_DodgeRenderers.Contains(r))
                    m_DodgeRenderers.Add(r);
            }
        }

        /// <summary>
        /// 这个渲染器是不是"纯特效"，不该跟着闪避一起变透明。
        ///
        /// ★ 目前排除三类：
        ///   · 爆炸圆形（BulletBlastRenderer 建出来的）
        ///   · 怪物伤害光环（也是 BulletBlastRenderer 建的）
        ///   · 手刀线段（HandBladeLineRenderer 建出来的）
        ///
        /// ★ 判定方式：看它的父级上有没有这两个渲染器组件。
        ///   （它们都是运行时 AddComponent 到 root 上的单例组件。）
        /// </summary>
        private static bool IsDodgeExcludedRenderer(Renderer r)
        {
            if (r == null) return false;

            if (r.GetComponentInParent<Dungeon.BulletBlastRenderer>() != null) return true;
            if (r.GetComponentInParent<Dungeon.HandBladeLineRenderer>() != null) return true;

            return false;
        }

        /// <summary>这个材质是不是"玩家轮廓 / 瞄准圈"这一类（要一起变透明的）。</summary>
        private static bool IsOutlineLike(Material m)
        {
            if (m == null || m.shader == null) return false;

            string n = m.shader.name;

            return n == "EchoPuzzle/Outline"
                || n == "EchoPuzzle/OutlineTransparent"
                || n == "EchoPuzzle/AimOverlay";
        }

        /// <summary>懒创建闪避用的半透明材质。</summary>
        private Material EnsureDodgeMaterial()
        {
            if (m_DodgeMat != null) return m_DodgeMat;

            // ★ 优先从 Resources 加载现成的材质资产 ——
            //   Shader.Find 只在"shader 被任何材质引用"时才可靠，
            //   打包后裸 shader 可能被剔除。
            Material asset = Resources.Load<Material>("EchoRuntime/EchoDodgeOutline");

            if (asset != null)
            {
                m_DodgeMat = new Material(asset)
                {
                    name      = "DodgeTransparentRuntime",
                    hideFlags = HideFlags.DontSave,
                };

                if (m_DodgeMat.HasProperty("_Alpha"))
                    m_DodgeMat.SetFloat("_Alpha", 1f);

                // ★ 闪避期间描边亮度降到 1.0（默认 2.0 会过曝成纯白）
                if (m_DodgeMat.HasProperty("_Glow"))
                    m_DodgeMat.SetFloat("_Glow", dodgeGlow);

                return m_DodgeMat;
            }

            // ---- 兜底：现场用 shader 建一个 ----
            Shader sh = Shader.Find("EchoPuzzle/OutlineTransparent");

            if (sh == null)
            {
                Debug.LogWarning("[地牢] 找不到闪避用的半透明材质\n" +
                                 "（Resources/EchoRuntime/EchoDodgeOutline 或 " +
                                 "Shader \"EchoPuzzle/OutlineTransparent\"），" +
                                 "闪避的半透明效果不会生效。");
                return null;
            }

            m_DodgeMat = new Material(sh)
            {
                name      = "DodgeTransparentRuntime",
                hideFlags = HideFlags.DontSave,
            };

            if (m_DodgeMat.HasProperty("_OutlineColor")) m_DodgeMat.SetColor("_OutlineColor", Color.white);
            if (m_DodgeMat.HasProperty("_BodyColor"))    m_DodgeMat.SetColor("_BodyColor", Color.black);
            if (m_DodgeMat.HasProperty("_Alpha"))        m_DodgeMat.SetFloat("_Alpha", 1f);

            // ★★ 闪避期间把描边亮度降到 1.0 ★★
            //
            //   EchoOutline 的 _Glow 默认是 2.0 —— 描边 RGB 会过曝成纯白，
            //   即使 alpha 降到 0.1，深色背景上还能看见一圈白边。
            //   降到 1.0 后就是"本色 × 1"，配合低 alpha 才能真正淡下去。
            if (m_DodgeMat.HasProperty("_Glow")) m_DodgeMat.SetFloat("_Glow", dodgeGlow);

            return m_DodgeMat;
        }

        /// <summary>闪避方向（按下那一刻定好，闪避期间不转向）。</summary>
        private Vector3 m_DodgeDirection = Vector3.forward;

        /// <summary>缓存的 PlayerHealth（地牢模式闪避加无敌用）。</summary>
        private Dungeon.PlayerHealth m_CachedHealth;

        /// <summary>上一帧是否在闪避（用来检测"闪避刚结束"那一帧）。</summary>
        private bool m_WasDodging;

        /// <summary>
        /// 下一帧 LateUpdate 把镜头**立即吸附**到玩家身上（不做平滑）。
        ///
        /// ★ 换地图 / 重生 / 重新开始时置 true ——
        ///   否则玩家瞬移后镜头会从旧位置"滑过去"。
        /// </summary>
        private bool m_CameraSnapNextFrame;

        // ------------------------------------------------------------------ 相机复位

        // ------------------------------------------------------------------ 地牢模式：闪避 + 技能冷却

        // ★★ 只在地牢模式生效，探秘模式完全不受影响。★★
        //
        //   闪避：按左 Alt，0.5 秒无敌 + 走路速度的 5 倍，期间玩家半透明。
        //   CD：  闪避 1 秒 / 空格发声 5 秒，剩余时间显示在右下角。

        [Header("地牢模式 · 闪避")]
        [Tooltip("闪避按键")]
        public KeyCode dungeonDodgeKey = KeyCode.LeftAlt;

        // ★★ 闪避的移速曲线（三段式，总长 = 加速 + 维持 + 减速）★★
        //
        //   需求原文："把闪避时的加速度改成平滑的加减速，不论原本的速度是多少，
        //             按左alt后都会在0.2秒内将移速提高至17，维持0.1秒，
        //             然后在0.2秒内将移速降低至当前该有的移速。"
        //
        //   时间轴（秒）：
        //     0.0 ──────── 0.2 ── 0.3 ──────── 0.5
        //     加速到峰值    维持    降到当前该有速度

        [Tooltip("★ 加速段时长（秒）：从当前速度平滑升到闪避峰值速度")]
        [Min(0f)] public float dodgeAccelTime = 0.2f;

        [Tooltip("★ 单击时的维持段时长（秒）：保持在峰值速度")]
        [Min(0f)] public float dodgeHoldTime = 0.1f;

        [Tooltip("★ 长按时的维持段**上限**（秒）。\n\n" +
                 "需求：最多让维持 17 速的时间增至 1.6 秒，\n" +
                 "     也就是整个无敌时间最多 2 秒（0.2 + 1.6 + 0.2）。")]
        [Min(0f)] public float dodgeMaxHoldTime = 1.6f;

        [Tooltip("★ 长按延长维持时间的**最小单位**（秒）。需求：0.1 秒")]
        [Min(0.01f)] public float dodgeHoldStep = 0.1f;

        [Tooltip("★ 减速段时长（秒）：从峰值速度平滑降到「当前该有的速度」\n\n" +
                 "★ 这个目标是**实时**算的 —— 玩家在闪避期间按住 Shift / Ctrl，\n" +
                 "  减速段就降到冲刺 / 潜行速度，否则降到走路速度。")]
        [Min(0f)] public float dodgeDecelTime = 0.2f;

        [Tooltip("闪避时的速度 = 走路速度 × 这个倍数")]
        [Min(1f)] public float dodgeSpeedMultiplier = 5f;

        [Tooltip("闪避期间玩家轮廓的不透明度（1 = 正常，0.5 = 半透明）")]
        [Range(0.05f, 1f)] public float dodgeAlpha = 0.1f;

        [Tooltip("闪避期间描边的亮度倍率。\n\n" +
                 "★ 玩家的 EchoOutline 默认 _Glow = 2.0，描边会过曝成纯白，\n" +
                 "  即使 alpha 很低也看得见一圈白边。闪避时降到 1.0 才能真正变淡。")]
        [Range(0f, 4f)] public float dodgeGlow = 0.2f;

        // ★★ 充能制：取代原来的冷却 ★★
        //
        //   需求："去掉cd，改成充能。充能的基础单位为0.1s，满充能时能量为2s。
        //         使用闪避会消耗充能，单击左alt会立即消耗0.5s充能，
        //         这也是每次使用技能时至少会消耗的能量。"
        //
        //   ★ 只要能量 >= 0.5s 就能按左 Alt。

        [Header("地牢模式 · 闪避充能")]
        [Tooltip("★ 每次使用闪避时**至少**要消耗的能量（秒）。\n\n" +
                 "需求：0.5 秒。能量 >= 这个值才能按左 Alt。")]
        [Min(0.1f)] public float minDodgeEnergy = 0.5f;

        [Tooltip("★ 满充能 = 多少秒。需求：2.0 秒")]
        [Min(0.1f)] public float maxDodgeEnergy = 2.0f;

        [Tooltip("★ 每次充能增加多少秒。需求：0.1 秒（也是充能的基础单位）")]
        [Min(0.01f)] public float dodgeChargePerTick = 0.1f;

        [Tooltip("★ 充能间隔表：能量越高充能越快。\n\n" +
                 "数组含义：从能量高点往下每 0.3s 一档，依次对应间隔。\n" +
                 "  能量 1.5~2.0 -> 0.2s\n" +
                 "  能量 1.2~1.4 -> 0.3s\n" +
                 "  能量 0.9~1.1 -> 0.4s\n" +
                 "  能量 0.6~0.8 -> 0.5s\n" +
                 "  能量 0.3~0.5 -> 0.6s\n" +
                 "  能量 0.0~0.2 -> 0.7s")]
        public float[] dodgeChargeIntervals = new float[]
        {
            0.2f,   // 能量 >= 1.5
            0.3f,   // 能量 >= 1.2
            0.4f,   // 能量 >= 0.9
            0.5f,   // 能量 >= 0.6
            0.6f,   // 能量 >= 0.3
            0.7f,   // 能量 <  0.3
        };

        /// <summary>每次闪避至少需要的能量（秒）。走 Inspector 上的 minDodgeEnergy。</summary>
        public float MinDodgeEnergy => Mathf.Max(0.1f, minDodgeEnergy);

        [Header("地牢模式 · 空格声波冷却")]
        [Tooltip("空格（跺脚发声）的冷却（秒）。\n\n" +
                 "★ 只在地牢模式生效；探秘模式的空格没有冷却。")]
        [Min(0f)] public float dungeonShoutCooldown = 3.5f;

        /// <summary>闪避是否正在进行。</summary>
        public bool IsDodging => Time.timeSinceLevelLoad < m_DodgeUntil;

        /// <summary>当前闪避能量（秒，0.1s 精度）。</summary>
        public float DodgeEnergy => m_DodgeEnergy;

        /// <summary>满充能（秒）。</summary>
        public float DodgeEnergyMax => Mathf.Max(0.1f, maxDodgeEnergy);

        /// <summary>能量占用比例 0~1（给 HUD 画充能条）。</summary>
        public float DodgeEnergy01 => Mathf.Clamp01(m_DodgeEnergy / DodgeEnergyMax);

        /// <summary>能否使用闪避（能量 >= 最小消耗）。</summary>
        public bool DodgeReady => CanStartDodge();

        /// <summary>当前充能间隔（秒）。</summary>
        public float DodgeChargeInterval => ChargeIntervalFor(m_DodgeEnergy);

        /// <summary>距离下一次充能还剩多少秒（给 HUD 用）。</summary>
        public float DodgeChargeCountdown =>
            Mathf.Max(0f, DodgeChargeInterval - m_ChargeAccum);

        /// <summary>空格声波剩余冷却（秒），0 = 可用。给 HUD 用。</summary>
        public float ShoutCooldownRemaining =>
            Mathf.Max(0f, m_NextShoutTime - Time.timeSinceLevelLoad);

        /// <summary>
        /// 【地牢模式】刷新技能状态（换地图 / 重新开始时调用）。
        ///
        /// ★ 闪避改成充能制后：直接把能量充满。
        /// ★ 空格声波的冷却照旧清掉。
        /// </summary>
        public void ResetDungeonCooldowns()
        {
            if (!GameUI.IsDungeonMode) return;

            float now = Time.timeSinceLevelLoad;

            m_NextShoutTime = now;        // 空格立即可用

            // 闪避：能量拉满
            m_DodgeEnergy = DodgeEnergyMax;   // 属性，走 Inspector 上的 maxDodgeEnergy
            m_ChargeAccum = 0f;

            // 正在闪避的话，直接结束（并恢复材质）
            if (IsDodging)
            {
                m_DodgeUntil       = now;
                m_DodgeCharging    = false;
                m_DodgeHoldExtend  = 0f;
                m_DodgeHoldCurrent = dodgeHoldTime;

                ExitDodgeVisual();
            }
        }
        /// <summary>空格冷却总量。</summary>
        public float ShoutCooldownTotal => Mathf.Max(0.01f, dungeonShoutCooldown);

        /// <summary>闪避结束时刻。</summary>
        private float m_DodgeUntil;

        /// <summary>本次闪避已经延长了多少秒（0 = 单击）。</summary>
        private float m_DodgeHoldExtend;

        /// <summary>本次闪避当前的维持段时长（dodgeHoldTime + 延长量）。</summary>
        private float m_DodgeHoldCurrent;

        /// <summary>
        /// 当前闪避是否还允许继续延长。
        ///
        /// ★ 从按下 Alt 起为 true，直到**无敌帧结束**（而不是松手）才变 false。
        /// </summary>
        private bool m_DodgeCharging;

        /// <summary>闪避开始时刻（三段式曲线的时间原点）。</summary>
        private float m_DodgeStartTime;

        /// <summary>按下闪避那一刻的水平速度大小 —— 加速段的起点。</summary>
        private float m_DodgeStartSpeed;

        /// <summary>
        /// 当前闪避能量（秒，0.1s 精度，0..maxDodgeEnergy）。
        ///
        /// ★ 用字段初始化器给满值：Awake 里有多个 return 分支，
        ///   写在那里可能被跳过。
        /// </summary>
        private float m_DodgeEnergy = 2f;

        /// <summary>充能累加器：攒够一个间隔就 +0.1s 能量。</summary>
        private float m_ChargeAccum;

        /// <summary>下次可以空格发声的时刻。</summary>
        private float m_NextShoutTime;

        /// <summary>闪避期间的轮廓不透明度备份（-1 = 当前没在改）。</summary>
        private float m_SavedOutlineAlpha = -1f;

        [Header("按 R 复位相机")]
        [Tooltip("触发相机复位的按键")]
        public KeyCode cameraResetKey = KeyCode.R;

        [Tooltip("复位后的俯仰角（度）。正数 = 从上往下看")]
        public float resetPitch = 12f;

        [Tooltip("复位后的缩放距离（米）")]
        public float resetDistance = 9f;

        [Tooltip("复位时相机是否瞬移过去。取消勾选则平滑过渡")]
        public bool resetCameraInstant = true;

        /// <summary>
        /// 把相机复位到玩家**正后方**（也就是朝向玩家正前方）。
        ///
        /// 做法：取玩家当前正前方的水平方向，把相机枢轴的 yaw / pitch 设成与它一致，
        /// 距离用 <see cref="resetDistance"/>。相机在 LastUpdate 里会被摆到
        /// 「枢轴位置 - 枢轴前方 × 距离」，所以正好落在玩家背后、和角色朝同一个方向。
        ///
        /// 玩家自身朝向不变，只是视角回到背后。
        /// </summary>
        public void ResetCamera()
        {
            // 玩家正前方（投影到水平面，避免前后倾斜影响 yaw）
            Vector3 fwd = transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
            fwd.Normalize();

            m_Yaw          = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
            m_Pitch        = Mathf.Clamp(resetPitch, pitchMin, pitchMax);
            cameraDistance = Mathf.Clamp(resetDistance, zoomMin, zoomMax);

            if (resetCameraInstant) SnapCameraToPivot();
        }

        /// <summary>立刻把相机摆到枢轴后面的正确位置（不做插值）。</summary>
        private void SnapCameraToPivot()
        {
            if (m_CamPivot == null) return;

            m_CamPivot.position = transform.position + Vector3.up * CurrentCameraHeight();
            m_CamPivot.rotation = Quaternion.Euler(m_Pitch, m_Yaw, 0f);

            if (playerCamera != null)
            {
                Transform ct = playerCamera.transform;
                ct.position = m_CamPivot.position - m_CamPivot.forward * cameraDistance;
                ct.rotation = m_CamPivot.rotation;
            }
        }

        // ------------------------------------------------------------------ 对外访问（存档用）

        /// <summary>当前水平朝向（度）。</summary>
        public float Yaw => m_Yaw;

        /// <summary>当前俯仰角（度）。</summary>
        public float Pitch => m_Pitch;

        /// <summary>当前缩放距离（米）。可读可写。</summary>
        public float Distance
        {
            get => cameraDistance;
            set => cameraDistance = Mathf.Clamp(value, zoomMin, zoomMax);
        }

        /// <summary>读档时用：直接把朝向 / 俯仰 / 距离设成指定值并立刻摆好相机。</summary>
        public void ResetCameraImmediate(float pitch, float yaw, float distance)
        {
            m_Pitch        = Mathf.Clamp(pitch, pitchMin, pitchMax);
            m_Yaw          = yaw;
            cameraDistance = Mathf.Clamp(distance, zoomMin, zoomMax);

            SnapCameraToPivot();
        }

        private void HandleLook()
        {
            // 光标没锁定时不转视角（由 GameUI 负责在进入游戏时锁定）
            if (Cursor.lockState != CursorLockMode.Locked)
            {
                m_WasLooking = false;
                return;
            }

            // ★★ 地牢模式正交俯视：**水平和俯仰都锁死** ★★
            //
            //   需求原文："水平角度锁死，我需要能在文件中调整水平角的数值"。
            //   朝向完全由 dungeonCameraYaw 决定（见 LateUpdateDungeonTopDown），
            //   这里连 m_Pitch / m_Yaw 都不再被鼠标改动。
            if (IsDungeonTopDown)
            {
                m_WasLooking = false;
                return;
            }

            // ★ 投掷瞄准时**不转视角**。
            //   瞄准是用鼠标拖动的：以按下右键的位置为原点算角度和力度，
            //   如果这里同时还在转镜头，鼠标一动视角就飘了，根本没法瞄。
            var thrower = StoneThrowSystem.Instance;
            if (thrower != null && thrower.IsAiming)
            {
                m_WasLooking = false;
                return;
            }

            // ★ 地牢模式枪械瞄准时同样不转视角 —— 鼠标改为绕玩家旋转武器。
            //   写法和上面两条完全一样：拿不到 Instance（探秘模式下永远是 null）
            //   就整段跳过，所以探秘模式的镜头行为没有改变。
            var gun = Dungeon.DungeonWeapon.Instance;
            if (gun != null && gun.IsAiming)
            {
                m_WasLooking = false;
                return;
            }

            // ★ 匕首瞄准时同样不转视角 —— 鼠标改为控制匕首的水平角。
            //
            //   例外：受惊死亡倒计时期间匕首被「冻结」（圆环 + 匕首还在淡出，
            //   但鼠标不再控制它们），这时要让鼠标**重新接管镜头**。
            var dagger = DaggerSystem.Instance;
            if (dagger != null && dagger.IsAiming && !dagger.IsAimFrozen)
            {
                m_WasLooking = false;
                return;
            }

            float dx = Input.GetAxisRaw("Mouse X");
            float dy = Input.GetAxisRaw("Mouse Y");

            m_Yaw   += dx * lookSensitivity;
            m_Pitch -= dy * lookSensitivity;
            m_Pitch  = Mathf.Clamp(m_Pitch, pitchMin, pitchMax);

            // 只在"从没转 -> 开始转"的那一帧记一次，持续转不会每帧刷新时间戳
            bool looking = Mathf.Abs(dx) > 0.01f || Mathf.Abs(dy) > 0.01f;
            if (looking && !m_WasLooking) NoteAction(PlayerAction.Look);
            m_WasLooking = looking;
        }

        private void HandleZoom()
        {
            float scroll = Input.GetAxisRaw("Mouse ScrollWheel");
            if (Mathf.Abs(scroll) < 0.0001f) return;

            // ★ 地牢模式：cameraDistance 表示"正交体高度"，量程也不同
            float lo = IsDungeonTopDown ? Mathf.Max(1f, dungeonZoomMin) : zoomMin;
            float hi = IsDungeonTopDown ? Mathf.Max(lo + 0.1f, dungeonZoomMax) : zoomMax;

            cameraDistance = Mathf.Clamp(cameraDistance - scroll * zoomStep * 10f, lo, hi);

            NoteAction(PlayerAction.Zoom);
        }

        private void HandleMove()
        {
            bool wantRun   = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            bool wantSneak = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)
                          || Input.GetKey(KeyCode.C);

            // ★★ 闪避期间：**暂时退出冲刺 / 潜行** ★★
            //
            //   需求原文："我希望在按住shift或ctrl时按左alt时，会暂时退出冲刺、
            //             潜行模式，等0.5秒后闪避结束时再重新生效。"
            //
            //   做法：闪避中把 Mode 强制成 Walk。
            //   玩家按住 Shift/Ctrl 的状态**没有丢**（wantRun / wantSneak 仍然算了），
            //   只是闪避这 0.5 秒不用它 —— 闪避结束那一帧自动恢复，
            //   因为这里的判断每帧都会重新跑。
            //
            //   ★ 注意：下面还要单独算一个 "naturalSpeed"（当前该有的速度），
            //     闪避减速段的目标就是它 —— 所以这里即使把 Mode 改成 Walk，
            //     也不会影响减速段落到冲刺 / 潜行速度。
            Mode = IsDodging
                 ? MoveMode.Walk
                 : (wantSneak ? MoveMode.Sneak : (wantRun ? MoveMode.Run : MoveMode.Walk));

            float sneakNow = sneakSpeed;

            // ★ 击杀怪物后的潜行加速：
            //   刚击杀时 = sneakSpeed × sneakBoostMultiplier（默认 4 倍），
            //   然后在 sneakBoostFadeDuration（默认 1 秒）内**线性**回落到 sneakSpeed。
            if (m_SneakBoostTimer > 0f)
            {
                m_SneakBoostTimer -= Time.deltaTime;

                float dur = Mathf.Max(0.01f, sneakBoostFadeDuration);
                float k   = Mathf.Clamp01(m_SneakBoostTimer / dur);   // 1 -> 0

                sneakNow = Mathf.Lerp(sneakSpeed, sneakSpeed * Mathf.Max(1f, sneakBoostMultiplier), k);
            }

            // ★★ 闪避的峰值速度：固定 = 走路速度 × dodgeSpeedMultiplier（默认 17）★★
            float dodgePeak = walkSpeed * Mathf.Max(1f, dodgeSpeedMultiplier);

            // ★★ "当前该有的速度" —— **不论是否在闪避**，都按玩家此刻的按键算 ★★
            //
            //   闪避减速段的目标就是它，所以玩家在闪避期间按住 Shift，
            //   减速段最后会落到冲刺速度。
            float naturalSpeed = wantSneak ? sneakNow
                               : wantRun   ? runSpeed
                               : walkSpeed;

            // ★★ 闪避的三段式移速曲线 ★★
            //
            //   加速 dodgeAccelTime -> 维持 dodgeHoldTime -> 减速 dodgeDecelTime
            //   加速段从**进闪避那一刻的速度**平滑升到 dodgePeak；
            //   减速段从 dodgePeak 平滑降到 naturalSpeed。
            float speed;

            if (IsDodging)
            {
                speed = DodgeSpeedAt(Time.timeSinceLevelLoad, dodgePeak, naturalSpeed);
            }
            else
            {
                speed = naturalSpeed;
            }

            // ★ 受惊死亡倒计时期间：**只能以很慢的速度移动**（默认 0.4 m/s）
            float cap = GameUI.FrightenedMoveSpeed;
            if (speed > cap) speed = cap;

            // ★ 闪避结束后不瞬移速度 —— 交给上面那句统一的 MoveTowards
            //   用走路同样的 accel 自然减速到当前模式的速度。
            m_WasDodging = IsDodging;

            float h = Input.GetAxisRaw("Horizontal");
            float v = Input.GetAxisRaw("Vertical");

            // 左右方向与直觉相反，所以把水平轴取反。前后（v）不动。
            h = -h;

            if (Mathf.Abs(h) > 0.01f || Mathf.Abs(v) > 0.01f)
            {
                // ★★ "前"方向的来源 ★★
                //
                //   · 探秘模式：用相机朝向（枢轴前向）—— 原来就是这样
                //   · 地牢正交俯视：枢轴前向是**正下方**（俯仰 90°），
                //     投影到水平面后长度为 0，没法用！
                //     这时改用 dungeonCameraYaw 算出的水平前向，
                //     也就是"屏幕上的上方"在地面里的方向，
                //     这样 W 永远是往画面上方走。
                Vector3 fwd;

                if (IsDungeonTopDown)
                {
                    float yawRad = dungeonCameraYaw * Mathf.Deg2Rad;
                    fwd = new Vector3(Mathf.Sin(yawRad), 0f, Mathf.Cos(yawRad));
                }
                else
                {
                    fwd = m_CamPivot != null ? m_CamPivot.forward : transform.forward;
                    fwd.y = 0f;
                }

                if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
                fwd.Normalize();

                Vector3 right = Vector3.Cross(fwd, Vector3.up).normalized;

                Vector3 world = (fwd * v + right * h).normalized * speed;

                // ★ 闪避期间 speed 本身已经是三段式平滑曲线，
                //   所以这里**直接赋值**，不再叠一层 MoveTowards
                //   （否则两套加速度会互相干扰）。
                if (IsDodging)
                    m_HorizontalVelocity = world;
                else
                    m_HorizontalVelocity = Vector3.MoveTowards(m_HorizontalVelocity, world, accel * Time.deltaTime);

                IsMoving = true;

                // 只在"从静止 -> 开始走"的那一帧记一次
                if (!m_WasMoving) NoteAction(PlayerAction.Move);
            }
            else if (IsDodging)
            {
                // ★ 闪避期间**即使没有按下方向键**也朝闪避方向冲
                //
                //   需求原文："玩家如果在按下左alt后的这0.5秒里没有按方向键，
                //             则默认朝玩家朝向移动。"
                //
                //   m_DodgeDirection 是在按下 Alt 那一刻定好的：
                //     · 当时有方向键 -> 朝输入方向
                //     · 当时没方向键 -> 朝玩家正面（CurrentMoveDirection 的兜底）
                //   而 speed 用的就是上面算好的闪避速度
                //   （= walkSpeed × dodgeSpeedMultiplier，默认 3.4 × 5 = 17 m/s）。
                // ★ 闪避期间即使没按方向键也朝闪避方向冲。
                //   speed 已经是三段式曲线，直接赋值即可。
                m_HorizontalVelocity = m_DodgeDirection * speed;
                IsMoving = true;

                if (!m_WasMoving) NoteAction(PlayerAction.Move);
            }
            else
            {
                m_HorizontalVelocity = Vector3.MoveTowards(m_HorizontalVelocity, Vector3.zero,
                                                           accel * 2f * Time.deltaTime);
                IsMoving = false;
            }

            m_WasMoving = IsMoving;

            // 重力
            if (m_Controller.isGrounded && m_VerticalVelocity < 0f) m_VerticalVelocity = -2f;
            m_VerticalVelocity += Physics.gravity.y * Time.deltaTime;

            m_Controller.Move((m_HorizontalVelocity + Vector3.up * m_VerticalVelocity) * Time.deltaTime);

            // 角色面朝移动方向
            //
            // ★ 转向也用 walkTurnLerp 这个系数 ——
            //   闪避时速度是 17 m/s，但转身的角加速度和走路时**完全一样**，
            //   不会因为跑得快就"瞬间掉头"。
            Vector3 flat = m_HorizontalVelocity;
            flat.y = 0f;
            if (flat.sqrMagnitude > 0.05f)
                transform.rotation = Quaternion.Slerp(transform.rotation,
                    Quaternion.LookRotation(flat.normalized, Vector3.up),
                    walkTurnLerp * Time.deltaTime);
        }

        private void HandleStrideNoise()
        {
            if (echo == null) return;

            EchoPreset preset = Mode == MoveMode.Sneak ? sneakEcho
                              : Mode == MoveMode.Run   ? runEcho
                              : walkEcho;

            if (!preset.enabled) return;

            float stride = Mode == MoveMode.Sneak ? sneakStride
                         : Mode == MoveMode.Run   ? runStride
                         : walkStride;

            bool isMoving = m_HorizontalVelocity.sqrMagnitude > MovingThreshold;

            // ---------------------------------------------------------------
            // ★ 玩家停下 -> 重置一切，下一次起步重新开始
            //
            //   这是之前最核心的 bug：旧代码在 moved <= 0 时直接 return，
            //   累计器 m_StrideAccum **永远不会被清零**。
            //   于是"走 3 米停下再走 1 米"这种情况，累计器还留着上次的余量，
            //   新的一步要靠旧余量凑够 stride 才响 —— 节奏完全不对。
            // ---------------------------------------------------------------
            if (!isMoving)
            {
                m_StrideAccum       = 0f;
                m_HasTakenFirstStep = false;
                return;
            }

            float moved = m_HorizontalVelocity.magnitude * Time.deltaTime;
            m_StrideAccum += moved;

            // ---------------------------------------------------------------
            // 阈值：
            //   · 还没迈出第一步 -> firstStepDistance（默认 0.5 米）
            //   · 之后           -> 各自的 stride（走 / 跑 / 潜行）
            // ---------------------------------------------------------------
            float threshold = !m_HasTakenFirstStep
                            ? Mathf.Max(0.05f, firstStepDistance)
                            : stride;

            if (m_StrideAccum < threshold) return;

            // 最小间隔防抖：还没到间隔就**不消耗**累计器，下一帧继续累
            // （旧代码在这里 return 之前已经把 accum 加上去了，但不会清零，
            //   所以这里不清零是对的 —— 只是别提前把 accum 归零）
            if (Time.timeSinceLevelLoad - m_LastEmitTime < minEmitInterval) return;

            // ---------------------------------------------------------------
            // 发一声。
            //
            // ★ 扣除而不是清零：
            //   如果这帧刚好跨过阈值，多走的那一点应该计入下一步。
            //   例如 firstStepDistance = 0.5、这帧到 0.53，那么剩下的 0.03
            //   属于下一步，不该被丢掉。这样长期看节奏是准确的。
            // ---------------------------------------------------------------
            m_StrideAccum  -= threshold;
            if (m_StrideAccum < 0f) m_StrideAccum = 0f;

            m_LastEmitTime      = Time.timeSinceLevelLoad;
            m_HasTakenFirstStep = true;      // 之后按 stride 走

            // ★ 脚步：受潜行影响（潜行 -> isSneak，怪物捕捉不到）
            //   地牢模式整组换成自己那套（dungeonWalkEcho 等）
            if (UseDungeonEchoSet)
            {
                preset = Mode == MoveMode.Sneak ? dungeonSneakEcho
                       : Mode == MoveMode.Run   ? dungeonRunEcho
                       :                          dungeonWalkEcho;
            }

            Emit(preset, affectedBySneak: true);

            CurrentNoiseLoudness = Mode == MoveMode.Sneak ? 0.25f
                                 : Mode == MoveMode.Run   ? 1.6f
                                 : 1.0f;
        }

        /// <summary>
        /// 判定"玩家是否在移动"的速度阈值（平方值）。
        ///
        /// 取一个很小的值，只要脚下一动就算移动。
        /// 用速度而不是位移来判断，是因为停下那一帧的位移也可能非零。
        /// </summary>
        private const float MovingThreshold = 0.0025f;   // 0.05 m/s 的平方

        /// <summary>
        /// 把一个 EchoPreset 参数集发出去。
        ///
        /// **所有四种声波（走 / 跑 / 潜行 / 空格）都走这里**，
        /// 所以脚印也挂在这一处 —— 每次释放声波就在脚下打一个，
        /// 左右自动交替。步行 / 冲刺 / 潜行的发射频率不同，
        /// 脚印的疏密自然也跟着不同。
        ///
        /// 发射方向统一由 SonarSystem 处理：先检测脚下地面法线，
        /// 再按 0° / 30° / 60° 三个固定仰角层撒射线。
        /// </summary>
        /// <param name="affectedBySneak">
        /// 这次发声是否受"潜行"影响。
        ///
        /// ★ 只有**脚步**（走路 / 跑步）才受潜行影响：
        ///   潜行时不发声，怪物也就捕捉不到。
        ///
        ///   空格（跺脚）是**独立**的声源，不该因为按着 Ctrl 就变成潜行声波 ——
        ///   否则玩家可以"潜行 + 跺脚"来放声波却不让怪物听到，破坏玩法。
        /// </param>
        private void Emit(EchoPreset preset, bool affectedBySneak)
        {
            EmitParams p = preset.ToEmitParams();

            // ★ 方位角起点 = 玩家正前方。
            //   这样"相对正前方偏转半格"才有意义；否则方位角是相对世界轴算的，
            //   玩家一转身，图案就跟着世界轴而不是跟着人。
            //   只取水平分量 —— 抬头低头不该改变方位角的起点。
            Vector3 facing = transform.forward;
            facing.y = 0f;
            p.referenceDir = facing.sqrMagnitude > 1e-6f ? facing.normalized : Vector3.forward;

            // ★ 潜行产生的**脚步**打上标记：怪物不会捕捉它，玩家位置因此不会暴露。
            //   其它声源（空格跺脚）不受影响 —— 见 affectedBySneak 的说明。
            //
            //   ★ 例外：在 Gravel（碎石区）里潜行时，脚步会暴露 ——
            //     碎石地上藏不住脚步，光带**照常能被怪物捕捉**。
            bool sneakSilent = affectedBySneak
                            && Mode == MoveMode.Sneak
                            && !Gravel.AnyPlayerInside();

            p.isSneak = sneakSilent;

            echo.Emit(FootPosition, p);

            if (footprints != null) footprints.Print();

            // 脚步声：走路 / 跑步各有各的音效，潜行不响。
            // 挂在这里是因为它和声波是同一个节奏 ——
            // 一步一声波，也就是一步一声响，不会对不上。
            switch (Mode)
            {
                case MoveMode.Walk: AudioSystem.Play(AudioSystem.Cue.Walk); break;
                case MoveMode.Run:  AudioSystem.Play(AudioSystem.Cue.Run);  break;

                case MoveMode.Sneak:
                    // ★ 例外：在 Gravel（碎石区）里潜行时会踩出声音 ——
                    //   碎石地本来就藏不住脚步声。用走路音效。
                    if (Gravel.AnyPlayerInside())
                        AudioSystem.Play(AudioSystem.Cue.Walk);
                    break;
            }
        }

        /// <summary>
        /// 当前应该用的相机枢轴高度。
        ///
        /// 基础值是 <see cref="cameraHeight"/>；当滚轮距离超过
        /// <see cref="zoomHeightStart"/> 之后，按比例往上一路加到
        /// <see cref="maxZoomExtraHeight"/>。拉近时（距离不大于起点）不加高。
        /// </summary>
        public float CurrentCameraHeight()
        {
            if (maxZoomExtraHeight <= 0f) return cameraHeight;

            float range = zoomMax - zoomHeightStart;
            if (range <= 0.01f) return cameraHeight;

            float extra = Mathf.Clamp01((cameraDistance - zoomHeightStart) / range);

            // 曲线：1 = 线性；>1 = 前段几乎不抬、后段抬得猛
            extra = Mathf.Pow(extra, Mathf.Max(0.01f, zoomHeightCurve));

            return cameraHeight + maxZoomExtraHeight * extra;
        }

        /// <summary>地牢模式是否正在用正交俯视相机。</summary>
        public bool IsDungeonTopDown =>
            dungeonOrthographicCamera && GameUI.IsDungeonMode;

        /// <summary>
        /// 地牢模式：把"屏幕坐标"转成"地面上的世界坐标"。
        ///
        /// ★ 正交俯视下这一步很直接：从相机沿射线打到 y = 0 的水平面即可。
        ///   给武器准星 / 武器瞄准用（武器朝鼠标所指的那个地面点）。
        /// </summary>
        /// <param name="screenPos">Input.mousePosition 那种屏幕坐标</param>
        /// <param name="groundY">地面高度，默认 0</param>
        /// <param name="world">输出的世界坐标</param>
        public bool ScreenToGround(Vector3 screenPos, out Vector3 world, float groundY = 0f)
        {
            world = Vector3.zero;

            if (playerCamera == null) playerCamera = Camera.main;
            if (playerCamera == null) return false;

            Ray ray = playerCamera.ScreenPointToRay(screenPos);

            // 正交相机：ray.origin 就在屏幕上对应的位置，direction 就是相机前向（正下方）
            var plane = new Plane(Vector3.up, new Vector3(0f, groundY, 0f));

            if (!plane.Raycast(ray, out float dist)) return false;

            world = ray.GetPoint(dist);
            return true;
        }

        private void LateUpdate()
        {
            if (m_CamPivot == null) return;

            // ★★ 地牢模式：走正交俯视分支，和下面的透视逻辑完全分开 ★★
            if (IsDungeonTopDown)
            {
                LateUpdateDungeonTopDown();
                return;
            }

            // 拉远时把镜头抬高：房间高的话平视会让顶部出画、地板下方空一大片。
            // 拉近时（距离 <= zoomHeightStart）完全不抬，保持看角色的低视角。
            float wantHeight = CurrentCameraHeight();
            Vector3 target = transform.position + Vector3.up * wantHeight;

            float t = 1f - Mathf.Exp(-cameraLag * Time.deltaTime);
            m_CamPivot.position = Vector3.Lerp(m_CamPivot.position, target, t);

            // ★ 高度**瞬时**到位，不参与上面的平滑。
            //
            //   为什么：滚轮改的是 cameraDistance，而相机位置是
            //   「枢轴 - 前方向 * 距离」，距离是瞬时的、枢轴高度如果还走 lerp，
            //   两者就会不同步 —— 看起来像玩家物体在下落。
            //   高度直接吸附到目标值，滚多少抬多少，和拉远是同一个瞬间完成的。
            Vector3 p = m_CamPivot.position;
            p.y = target.y;
            m_CamPivot.position = p;

            m_CamPivot.rotation = Quaternion.Slerp(m_CamPivot.rotation,
                Quaternion.Euler(m_Pitch, m_Yaw, 0f), t);

            if (playerCamera != null)
            {
                Transform ct = playerCamera.transform;

                // 相机允许穿墙 —— 否则玩家能靠相机被挡住的位置反推墙体轮廓
                float dist = cameraDistance;
                Vector3 back = -m_CamPivot.forward;

                if (cameraCollision &&
                    Physics.SphereCast(m_CamPivot.position, 0.28f, back,
                        out RaycastHit hit, cameraDistance, cameraCollisionMask,
                        QueryTriggerInteraction.Ignore))
                {
                    dist = Mathf.Max(0.6f, hit.distance - 0.05f);
                }

                ct.position = m_CamPivot.position + back * dist;
                ct.rotation = m_CamPivot.rotation;

                // ★ 投掷瞄准时镜头朝落点方向偏移（偏移量由 StoneThrowSystem 每帧算好）。
                //   放在相机位置定稿之后，保证本帧内不被其它逻辑覆盖。
                var thrower = StoneThrowSystem.Instance;
                if (thrower != null && thrower.IsAiming)
                {
                    Vector3 off = thrower.CameraOffset;
                    if (off.sqrMagnitude > 0.0001f) ct.position += off;
                }
            }
        }

        // ------------------------------------------------------------------ 地牢模式：正交俯视

        /// <summary>
        /// 地牢模式的相机后处理：正交、垂直向下、水平角固定。
        ///
        /// ★ 和上面那段透视逻辑完全独立 —— 探秘模式一行都不受影响。
        ///
        /// 组成：
        ///   · 枢轴跟着玩家（平滑），枢轴的高 = dungeonCameraHeight
        ///   · 枢轴朝向 = 俯仰 90°（正下方）+ dungeonCameraYaw（固定水平角）
        ///   · 相机在枢轴正下方？不 —— 正交相机直接放在枢轴上方往下看更直观：
        ///     相机位置 = 玩家正上方 dungeonCameraHeight，朝正下方
        ///   · 正交尺寸由滚轮控制（dungeonZoomMin ~ dungeonZoomMax）
        /// </summary>
        private void LateUpdateDungeonTopDown()
        {
            // ---- 1) 正交 / 透视切换（只在模式变化时才真正赋值）----
            ApplyDungeonCameraMode(true);

            if (playerCamera == null) return;

            // ---- 2) 枢轴跟随玩家（水平平滑，高度固定）----
            Vector3 target = transform.position + Vector3.up * Mathf.Max(1f, dungeonCameraHeight);

            // ★ 换地图 / 重生时**立即吸附**，不做平滑
            if (m_CameraSnapNextFrame)
            {
                m_CameraSnapNextFrame = false;
                m_CamPivot.position = target;
            }
            else
            {
                float t = 1f - Mathf.Exp(-cameraLag * Time.deltaTime);
                m_CamPivot.position = Vector3.Lerp(m_CamPivot.position, target, t);
            }

            // 高度不参与平滑 —— 否则拉远时人和画面不同步，看起来像在下坠
            Vector3 p = m_CamPivot.position;
            p.y = target.y;
            m_CamPivot.position = p;

            // ---- 3) 枢轴朝向：俯仰锁定 90°（垂直向下）+ 固定水平角 ----
            //
            //   ★ Rotate(90, yaw, 0) 的结果：
            //       前向 = (sin(yaw)·0? ...) 具体是 —— 绕自身 X 轴再转 90°，
            //       于是前向朝正下方，而"屏幕上方"由 yaw 决定。
            //     换句话说 yaw 控制的是**画面绕竖直轴转多少**，
            //     这正是"水平角"的含义。
            m_CamPivot.rotation = Quaternion.Euler(90f, dungeonCameraYaw, 0f);

            // ---- 4) 正交尺寸（缩放）----
            playerCamera.orthographicSize =
                Mathf.Clamp(cameraDistance, Mathf.Max(1f, dungeonZoomMin), Mathf.Max(1.1f, dungeonZoomMax));

            // ---- 5) 相机位置 = 枢轴位置（正上方），朝向 = 枢轴朝向 ----
            Transform ct = playerCamera.transform;
            ct.position = m_CamPivot.position;
            ct.rotation = m_CamPivot.rotation;
        }

        /// <summary>
        /// 把 <see cref="playerCamera"/> 切到正交 / 透视。
        ///
        /// ★ 只在状态真的变化时才写，避免每帧重复赋值（会触发 URP 重建相机数据）。
        /// </summary>
        private void ApplyDungeonCameraMode(bool orthographic)
        {
            if (playerCamera == null) playerCamera = Camera.main;
            if (playerCamera == null) return;

            if (playerCamera.orthographic != orthographic)
            {
                playerCamera.orthographic = orthographic;

                // 正交相机不需要很大的远裁剪面，但地牢相机在 60m 高，
                // 覆盖到地面以下要留够余量
                if (orthographic)
                {
                    if (playerCamera.nearClipPlane < 0.05f) playerCamera.nearClipPlane = 0.05f;
                    if (playerCamera.farClipPlane < 200f)   playerCamera.farClipPlane = 200f;
                }
            }
        }

        private void OnDestroy()
        {
            ExitDodgeVisual();
            if (m_DodgeMat != null) Destroy(m_DodgeMat);

            if (m_CamPivot  != null) Destroy(m_CamPivot.gameObject);
            if (m_AutoCamera != null) Destroy(m_AutoCamera);
            if (m_AutoSonar  != null) Destroy(m_AutoSonar);
        }
    }

    /// <summary>
    /// 一种发声方式的完整参数，全部可在 Inspector 里调。
    /// </summary>
    [System.Serializable]
    public struct EchoPreset
    {
        public bool enabled;

        [Tooltip("声波颜色。你想要纯白就保持白色")]
        public Color color;

        [Tooltip("初始不透明度 0..1。这是唯一区分四种步伐的刻度：\n" +
                 "衰减速度全局统一，所以初始值越低 = 越早暗到看不见 = 能持续的时间越短")]
        [Range(0f, 1f)] public float initialAlpha;

        [Tooltip("镜面反射类：与地面法线夹角 90°（即水平面）的射线数量。这类射线反射时走严格镜面")]
        public int rayCount90;

        [Tooltip("镜面反射类：与地面法线夹角 30°（即相对水平面仰起 60°）的射线数量")]
        public int rayCount30;

        [Tooltip("镜面反射类：与地面法线夹角 60°（即相对水平面仰起 30°）的射线数量")]
        public int rayCount60;

        [Tooltip("单根射线的最大传播距离（米）")]
        public float range;

        [Tooltip("声速（米/秒）。波前扩散的快慢")]
        public float speed;

        [Tooltip("光带采样间距（米）。越小越顺滑，几何量越大")]
        public float trailSpacing;

        [Tooltip("最大反射次数。注意开销是 2 的幂次增长，慎调大")]
        public int maxBounces;

        [Tooltip("每次反射后的不透明度衰减系数")]
        public float bounceFalloff;

        [Tooltip("【每次反射扣掉的波前时长（秒）】每碰一次墙，这一小段的波前存在时长就减这么多。\n" +
                 "所以绕得越远的声波，能继续推进的时间越短 —— 比单纯限制反射次数自然得多。\n\n" +
                 "扣到 0 以下时这条子射线**根本不会生成**，等于声波绕不动了。\n" +
                 "填 0 = 不扣（各次反射的时长都一样）。\n\n" +
                 "和 lifetimeCap 配合：例如 lifetimeCap = 1.5、本值 = 0.4，\n" +
                 "则第 1 次反射后只剩 1.1 秒、第 2 次 0.7 秒、第 4 次就没了。")]
        public float bounceLifeLoss;

        [Tooltip("【波前存在时长（秒）】从声波诞生起算。时间一到，波前**停止推进**，\n" +
                 "已经长出来的光带形状会钉在原地不再生长。\n\n" +
                 "四种声波各有一份，互不影响 —— 这是控制『每种声波能扫多远』的开关。\n" +
                 "填 0 或负数 = 不限时，一直推进到射程尽头。")]
        public float lifetimeCap;

        [Tooltip("【尾巴残留时长（秒）】波前停住之后，光带还能继续存在多久。\n" +
                 "这段时间里它会留在原地继续变暗，直到彻底看不见才消失 ——\n" +
                 "所以『波前没了』和『光带没了』是两个独立的时刻。\n\n" +
                 "填 0 = 波前一停就立刻一起消失，不留尾巴。\n" +
                 "填大 = 波前扫过的轨迹像余像一样挂很久。")]
        public float tailLinger;

        [Tooltip("漫反射类：与地面法线夹角 15°（接近朝天，几乎垂直向上）的射线数量。这类射线反射时走漫反射")]
        public int diffuseCount15;

        [Tooltip("漫反射类：与地面法线夹角 45°（即相对水平面仰起 45°）的射线数量")]
        public int diffuseCount45;

        [Tooltip("漫反射类：与地面法线夹角 75°（接近贴地，几乎水平）的射线数量")]
        public int diffuseCount75;

        [Tooltip("★ 全上半球采样的射线数量。0 = 不启用。\n\n" +
                 "与六层俯仰角采样**并行**：这一部分射线在整个上半球面上分布\n" +
                 "（余弦加权 Fibonacci，正对法线的方向更密），不受六层角度限制。\n" +
                 "方向同样以脚下地面法线为轴 —— 斜坡上会跟着坡面倾斜，和六层一致。")]
        public int hemisphereCount;

        [Tooltip("半球采样出来的射线，反射时是否走漫反射（朗伯）。\n" +
                 "勾选 = 漫反射（在命中面上半球均匀散射）；\n" +
                 "取消 = 严格镜面（和 90°/30°/60° 那三层一样）。")]
        public bool hemisphereDiffuse;

        public EmitParams ToEmitParams()
        {
            return new EmitParams
            {
                rayCount90     = Mathf.Max(0, rayCount90),
                rayCount30     = Mathf.Max(0, rayCount30),
                rayCount60     = Mathf.Max(0, rayCount60),
                range          = range,
                speed          = speed,
                trailSpacing     = trailSpacing,
                intensity      = initialAlpha,
                lifetime       = lifetimeCap,
                tailLinger     = tailLinger,
                bounceLifeLoss = bounceLifeLoss,
                maxBounces     = maxBounces,
                bounceFalloff  = bounceFalloff,
                emitMode       = EmitMode.ElevationLayers,
                emitAxis       = Vector3.zero,          // 零向量 = 由 SonarSystem 检测地面法线
                coneAngle      = 90f,
                diffuseCount15 = Mathf.Max(0, diffuseCount15),
                diffuseCount45 = Mathf.Max(0, diffuseCount45),
                diffuseCount75 = Mathf.Max(0, diffuseCount75),
                hemisphereCount   = Mathf.Max(0, hemisphereCount),
                hemisphereDiffuse = hemisphereDiffuse,
                color          = color,
            };
        }
    }
}
