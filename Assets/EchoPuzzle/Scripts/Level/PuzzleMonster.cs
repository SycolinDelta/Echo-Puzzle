using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace EchoPuzzle
{
    /// <summary>
    /// 怪物。
    ///
    /// 组成：
    ///   · **实体**：半径 <see cref="bodyRadius"/> 的球，漂浮在离地 <see cref="floatHeight"/> 处。
    ///     会被墙体阻挡（用 Collider + CharacterController 推挤），且**不可见**。
    ///   · **感知圆柱**：以实体为轴心、水平半径 <see cref="senseRadius"/>、
    ///     竖直总高 <see cref="senseHeight"/> 的圆柱形范围。
    ///     和实体不同，它**不会**被墙体阻挡，也不可见。
    ///     它会随机生成怪物光带。
    ///   · **变色球**：半径 <see cref="colorRadius"/> 的球形范围，**只**决定
    ///     光带在这里要不要变色 / 地牢模式下球要不要被照亮。
    ///     它**没有感知能力** —— 感知只由感知圆柱负责。
    ///
    /// 行为：
    ///   1. 声波光带进入感知圆柱 -> 把"该光带的产生点"告诉怪物；
    ///      同时如果它落在变色球里，外观会变得和陷阱一样
    ///   2. 怪物朝那个点移动，到达距离 <see cref="stopDistance"/> 时停下
    ///   3. 收到更新的地点时会掉头去新的
    ///   4. 玩家进入击杀范围 -> 击杀（触发光带 + 跳"你死了"）
    ///   5. 潜行中产生的声波不会被捕捉（所以也不会暴露位置）
    ///
    // ══════════════════════════════════════════════════════════════
    // ★★ 交接说明（给接手地牢模式的人看）★★
    //
    //   这个脚本只服务于【探秘模式 PuzzleMode】。
    //
    //   地牢模式（DungeonMode）是**独立的肉鸽射击玩法**，怪物行为完全不同：
    //     · 不再近身追击玩家
    //     · 不再秒杀玩家
    //     · 在地图上随机移动 + 发射子弹
    //     · 玩家有生命值（6 点），被打掉才扣血
    //
    //   所以地牢模式的怪物 **不要改这个脚本**，请新建：
    //     Assets/EchoPuzzle/Scripts/Dungeon/DungeonEnemy.cs
    //
    //   可以参考这个脚本里的：
    //     · 光带发射（EmitBurst / StepBeams）
    //     · 感知与受惊
    //   但**不要**复用追击 / 击杀 / Anger 状态机。
    // ══════════════════════════════════════════════════════════════

    /// 怪物光带的规则和声波光带不同：
    ///   · 波前从球心外 <see cref="headRadius"/> 处朝四周发射
    ///   · 波前**不反射**，撞到墙就消失，光带残留在原地淡出
    ///   · 波前的衰减快于光带本体
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public sealed class PuzzleMonster : MonoBehaviour
    {
        // ---------------------------------------------------------------- 尺寸

        [Header("尺寸")]
        [Tooltip("实体球半径（米）")]
        public float bodyRadius = 0.5f;

        [Tooltip("漂浮高度：实体球心离地多少米")]
        public float floatHeight = 2f;

        [Tooltip("怪物光带的波前从球心外多远开始（米）")]
        public float headRadius = 1f;

        // ---------------------------------------------------------------- 感知圆柱

        [Header("感知圆柱范围")]
        [Tooltip("感知圆柱的半径（米）。\n\n" +
                 "声波光带落进这个圆柱时，怪物会捕捉到它的发声位置并前去查看。\n" +
                 "（光带**变色**用的是另一个球，见下面『声波光带进入周围空间时的外观』）")]
        public float senseRadius = 2f;

        [Tooltip("感知圆柱的**总高**（米）。以怪物球心为中心，4 = 上下各 2")]
        public float senseHeight = 4f;

        // ---------------------------------------------------------------- 移动

        [Header("移动")]
        [Tooltip("移动速度（米/秒）")]
        public float moveSpeed = 2.2f;

        [Tooltip("朝目标点移动时，距离目标多近就停下（米）")]
        public float stopDistance = 1.5f;

        [Tooltip("是否移动")]
        public bool canMove = true;

        [Header("重力与悬浮")]
        [Tooltip("悬浮的刚度。越大越快回到目标高度，但太大会抖")]
        public float hoverStiffness = 6f;

        [Tooltip("悬浮时升降的最大速度（米/秒）")]
        public float hoverMaxSpeed = 4f;

        [Tooltip("悬浮速度的平滑程度。越大越跟手")]
        public float hoverDamping = 8f;

        [Tooltip("找地面时向下探多远（米）。初始摆很高时也要能找到地面")]
        public float groundProbeDistance = 60f;

        [Tooltip("算作『地面』的层。默认和声波射线一样排除 Ignore Raycast / Water / UI")]
        public LayerMask groundMask = ~((1 << 2) | (1 << 4) | (1 << 5));

        // ---------------------------------------------------------------- 击杀

        [Header("击杀")]
        [Tooltip("玩家接触怪物就击杀")]
        public bool killOnPlayerEnter = true;

        [Tooltip("移动中：击杀圆柱的**水平半径**（米）。\n\n" +
                 "圆柱：以怪物球心为中轴，水平半径 = 本值，竖直总高 = killHeight。\n" +
                 "判据：玩家胶囊表面到圆柱轴线的水平距离 ≤ 本值。\n" +
                 "玩家碰撞半径（0.37）已扣掉，所以贴身触发所需的最小值 ≈ 0.51")]
        public float killRadiusMoving = 2f;

        [Tooltip("静止时：击杀圆柱的**水平半径**（米）。\n\n" +
                 "圆柱：以怪物球心为中轴，水平半径 = 本值，竖直总高 = killHeight。\n" +
                 "判据：玩家胶囊表面到圆柱轴线的水平距离 ≤ 本值。\n" +
                 "玩家碰撞半径（0.37）已扣掉，所以贴身触发所需的最小值 ≈ 0.51")]
        public float killRadiusIdle = 0.5f;

        [Tooltip("击杀圆柱的**竖直总高**（米），以怪物球心为中点上下各一半。\n\n" +
                 "默认 4：怪物球心离地 2m -> 覆盖 0m ~ 4m，完整包住玩家胶囊（0 ~ 1.8m），\n" +
                 "所以竖直方向通常不会限制击杀。")]
        public float killHeight = 4f;

        [Tooltip("击杀时的光带参数（数值取自陷阱 TriggerVolume 的那一套）")]
        public SonarEmitSettings killSonar = SonarEmitSettings.DefaultTrap;

        // ---------------------------------------------------------------- 光带

        [Header("怪物光带")]
        [Tooltip("每隔多久在感知圆柱里随机发一次光带（秒）。填 0 = 不自动发")]
        public float emitInterval = 0.45f;

        [Tooltip("每次发几根")]
        public int raysPerBurst = 6;

        [Tooltip("初始不透明度（波前刚出现时）")]
        [Range(0f, 1f)] public float trailInitialAlpha = 0.85f;

        [Tooltip("光带粗细（米）")]
        public float trailWidth = 0.10f;

        [Tooltip("光带颜色")]
        public Color trailColor = new Color(1f, 0.35f, 0.15f, 1f);

        [Tooltip("光带**本体**的淡出速度（1/秒）。越小残留越久")]
        public float trailBodyFadeSpeed = 1.2f;

        [Tooltip("**波前**的淡出速度（1/秒）。应当大于本体速度")]
        public float trailHeadFadeSpeed = 3.5f;

        [Tooltip("单条光带最长铺多远（米）")]
        public float trailMaxLength = 26f;

        [Tooltip("采样间距（米）")]
        public float trailSpacing = 0.35f;

        [Tooltip("参与光带碰撞的层。默认排除 Ignore Raycast / Water / UI")]
        public LayerMask raycastMask = ~((1 << 2) | (1 << 4) | (1 << 5));

        // ---------------------------------------------------------------- 受惊状态

        [Header("受惊状态")]
        [Tooltip("受惊时发射间隔（秒），暂时覆盖 Emit Interval")]
        [Min(0.01f)] public float emitIntervalFrightened = 0.1f;

        [Tooltip("受惊时光带最长长度（米），暂时覆盖 Trail Max Length")]
        [Min(0.1f)] public float trailMaxLengthFrightened = 4f;

        [Tooltip("受惊持续时间（秒）。到点自动退出")]
        [Min(0.1f)] public float frightenedDuration = 3f;

        [Tooltip("【只读】当前受惊值。0 = 正常，1 = 受惊")]
        [SerializeField] private int frightened;

        /// <summary>受惊值：0 = 正常，1 = 受惊。</summary>
        public int Frightened => frightened;

        /// <summary>是否处于受惊状态。</summary>
        public bool IsFrightened => frightened != 0;

        /// <summary>受惊剩余时间（秒）。</summary>
        private float m_FrightenedTimer;


        // ---------------------------------------------------------------- 被捕捉时的外观

        [Header("声波光带进入周围空间时的外观")]
        [Tooltip("**变色球**的半径（米）。\n\n" +
                 "声波光带落进以怪物为中心、这个半径的**球**内时，外观会变成下面这些值。\n" +
                 "这个球独立于『感知圆柱』—— 变色范围可以比感知范围大或小。")]
        public float colorRadius = 2f;

        [Tooltip("勾上 = 把光带宽度改成下面的值")]
        public bool overrideWidth = true;

        [Tooltip("固定宽度（米）")]
        public float waveWidth = 0.22f;

        [Tooltip("勾上 = 把光带不透明度改成下面的值")]
        public bool overrideAlpha = true;

        [Range(0f, 1f)] public float waveAlpha = 1f;

        [Tooltip("勾上 = 把光带颜色改成下面的值")]
        public bool overrideColor = true;

        [Tooltip("固定颜色")]
        public Color waveColor = new Color(1f, 0.15f, 0.15f, 1f);

        // ---------------------------------------------------------------- 引用

        [Header("引用")]
        [Tooltip("怪物光带的渲染器。留空会自动创建")]
        public MonsterTrailRenderer trailRenderer;

        // ---------------------------------------------------------------- 内部

        private CharacterController m_Controller;
        private PlayerController    m_Player;

        /// <summary>缓存的玩家引用（MonsterAudio 等外部系统读距离用）。</summary>
        public PlayerController Player => m_Player;

        /// <summary>
        /// 击杀范围当前按 **KillRadiusMoving** 算吗？
        /// （受惊时固定按 Idle，所以这里会返回 false）
        /// MonsterAudio 靠它在 Idle / Moving 切换时播 Searching 音效。
        /// </summary>
        public bool IsKillRangeMoving => m_KillRangeMovingLastFrame;

        /// <summary>上一帧 BodyTouchesPlayer 里算出的"击杀范围是否按 Moving 计"。</summary>
        private bool m_KillRangeMovingLastFrame;

        /// <summary>NavMeshAgent：只用来**寻路**（绕墙），不托管位置（垂直是重力悬浮）。</summary>
        private NavMeshAgent m_Agent;

        /// <summary>agent 是否可用于寻路（有 agent、启用、且实体附着在 NavMesh 上）。</summary>
        private bool m_NavOK;

        /// <summary>当前要去的点。Vector3.zero + hasTarget 判断有没有目标。</summary>
        private Vector3 m_Target;
        private bool    m_HasTarget;

        /// <summary>当前目标这条光带的**产生时间**（波前离开声源的时刻）。
        /// 只有产生时间更晚的新光带才能替换掉它。</summary>
        private float   m_TargetBirthTime = float.NegativeInfinity;

        /// <summary>怪物当前是否在朝目标移动。击杀范围（移动大 / 静止小）按它切换。</summary>
        private bool    m_IsMoving;

        /// <summary>
        /// 停下后**继续保持"移动中"**的剩余时间（秒）。
        ///
        /// 需求：停止移动后 3 秒内仍然持续发光、
        /// 击杀范围仍然是 KillRadiusMoving。
        /// </summary>
        private float   m_MovingGraceTimer;

        /// <summary>停下后的宽限时长（秒）。</summary>
        private const float MovingGraceDuration = 3f;

        /// <summary>
        /// "是否算作移动中"：真实在移动，或停下后的 3 秒宽限期内。
        /// 击杀范围与发光频率都用它判断。
        /// </summary>
        private bool MovingOrGrace => m_IsMoving || m_MovingGraceTimer > 0f;

        /// <summary>
        /// 这一帧有没有"移动意图"（期望速度非零）。
        /// 与 <see cref="m_IsMoving"/> 的区别：被墙挡住时实际位移为 0，
        /// 但这里仍为 true —— 用于让怪物在卡住时继续发光，方便发现异常。
        /// </summary>
        private bool    m_WantMove;

        private float   m_NextEmitTime;
        private bool    m_Killed;

        /// <summary>垂直速度（受重力驱动，悬浮时由悬浮弹簧接管）。</summary>
        private float m_VerticalVelocity;

        /// <summary>是否已经"落到过悬浮高度"。
        /// 在掉到目标高度之前只受重力、不悬浮 —— 这样从高处往下掉是自然下落，
        /// 而不是一开场就被弹簧硬拽到目标高度。
        /// </summary>
        private bool m_Suspended;

        /// <summary>
        /// 扫描"感知圆柱里有没有声波光带"的间隔（秒）。
        /// 扫描是 O(光带数)，降频执行可以省掉大量开销而不影响手感。
        /// </summary>
        [Tooltip("多久扫描一次『有没有声波进入感知圆柱』（秒）。越小越灵敏、开销越大")]
        [Min(0.02f)] public float captureInterval = 0.1f;

        private float        m_NextCaptureTime;
        private SonarSystem  m_CachedSonar;

        /// <summary>正在推进的怪物光带：下标 -> 发射方向。</summary>
        private struct LiveBeam
        {
            public int     index;
            public Vector3 dir;
            public float   length;      // 当前长度
            public bool    stopped;
        }

        private readonly List<LiveBeam> m_Beams = new List<LiveBeam>(32);

        /// <summary>
        /// 出生位置与朝向。开局记一次，重玩 / 读档时用来归位。
        /// </summary>
        private Vector3    m_SpawnPosition;
        private Quaternion m_SpawnRotation;
        private bool       m_SpawnCaptured;

        // ---------------------------------------------------------------- 生命周期

        private void Awake()
        {
            // ★ 诊断：Awake 里任何异常都会中断整个初始化，
            //   表现为"点 Play 直接卡死、按钮点不了"。
            //   这里把堆栈完整打出来。
            try
            {
                AwakeInternal();
            }
            catch (System.Exception e)
            {
                Debug.LogError("[地牢] PuzzleMonster.Awake 出错：" + e);
            }
        }

        private void AwakeInternal()
        {
            m_Controller = GetComponent<CharacterController>();
            if (m_Controller != null)
            {
                m_Controller.radius = bodyRadius;

                // ★★ 碰撞体只包住悬浮球体本身 ★★
                //
                //   怪物与地形的交互**不靠物理碰撞**，而是靠悬浮逻辑：
                //     TryGetGroundY() 每帧向下打射线，找到脚下地面 / 坡面的高度，
                //     再把球心维持在 (地面高度 + floatHeight)。
                //
                //   所以坡面的抬升是"飘"上去的，不需要胶囊去踩它。
                //
                //   ★ 千万不要把胶囊往下延伸到地面 —— 那样怪物一出生，
                //     胶囊就插进地板 floatHeight 那么深，CharacterController
                //     会把它硬挤到几乎贴地的位置（表现为"掉下去、地板没接住"）。
                //
                //   短胶囊（height = 2*半径、center 在原点）能保证：
                //     碰撞半径 = bodyRadius，能挤过任何比球大的缝隙。
                m_Controller.height = bodyRadius * 2f;
                m_Controller.center = Vector3.zero;

                m_Controller.isTrigger = false;

                // ★ 爬坡能力。三处都要放开，缺一不可：
                //     1) NavMesh 的 agentSlope（见 NavMeshRuntimeBaker）—— 决定"有没有路"
                //     2) 这里的 slopeLimit —— 决定"物理上能不能站着往上走"
                //     3) stepOffset —— 陡坡边缘的小台阶不至于把人卡住
                //
                //   Unity 的 slopeLimit 上限是 89°，用 80° 给 70° 的坡留余量。
                m_Controller.slopeLimit = 80f;

                // 台阶高度按半径给（0.5m 半径 -> 0.35m），
                // 太小的话陡坡上每一处凸起都会顶住。
                m_Controller.stepOffset = Mathf.Clamp(bodyRadius * 0.7f, 0.3f, 0.6f);

                // ★ skinWidth 要小。
                //
                //   CharacterController 会按 skinWidth **向外扩**去做碰撞检测，
                //   实际"撞墙半径" = radius + skinWidth。
                //
                //   而 NavMesh 是按 agentRadius 从墙面**向内收缩**烘焙的，
                //   可走区域的边界距墙面正好 agentRadius。
                //
                //   只要 radius + skinWidth < agentRadius，怪物就永远不会被
                //   导航到"物理上挤不进去"的位置。
                //
                //   当前配置：bodyRadius 0.5 + skinWidth 0.01 = 0.51
                //             agentRadius = 0.53（见 NavMeshRuntimeBaker）
                //             -> 余量 0.02m
                //
                //   ★ skinWidth 不要取 0：Unity 明确说明 0 会导致抖动与穿模，
                //     它同时也是"判断是否已接触"的缓冲区。0.01 已经足够薄。
                m_Controller.skinWidth = 0.01f;
            }

            // ★ 这里**不**调用 EnsureAgent()。
            //
            //   NavMesh 是运行时在 Start 里烘焙的（NavMeshRuntimeBaker.Ensure）。
            //   如果在 Awake 就 AddComponent<NavMeshAgent>()，那一刻场上还没有
            //   任何 NavMesh 数据，Unity 立刻会报：
            //     "Failed to create agent because there is no valid NavMesh"
            //   而且这个 agent 会处于失效状态，之后即便烘好了也不会自己恢复。
            //
            //   所以改成"烘焙完成后再挂 agent"，见 Start() 里的 DeferredAgentSetup。

            EnsureTrailRenderer();
            EnsureBodyOccluder();
            EnsureFacingArrow();

            // ★ 音效：新建的怪物默认就带 MonsterAudio（含 3D AudioSource）。
            //   已经有了就不重复加。
            EnsureMonsterAudio();

            // ★★ 地牢模式：变色球可视化（球被光带切开的红色截面）★★
            //
            //   地牢模式下"变色的是球本身"，不是光带 —— 所以这里挂上
            //   SenseSphereVisual，并把原来的光带变色关掉（见 overrideColor）。
            EnsureSenseSphereVisual();

            // ★ 地牢模式：初始化血量
            DungeonResetHealth();

            // ★ 怪物默认也带 Hide（Hiding 默认 0 = 启用，不影响开局显示）。
            AddComponentIfMissing<Hide>();

            CaptureSpawn();

            // ★ 怪物彼此**不碰撞**，可以完全重叠。
            //   这里注册到全局忽略列表，IgnoreMonsterCollisions() 会切物理层。
            MonsterCollisionGroup.Register(this);
        }

        private void OnDestroy()
        {
            MonsterCollisionGroup.Unregister(this);
        }

        // ---------------------------------------------------------------- 朝向箭头

        [Header("朝向箭头（运行时可见）")]
        [Tooltip("在游戏运行时显示一个指向怪物**正面**的箭头。\n\n" +
                 "★ 和 Gizmo 不同 —— Gizmo 只在 Scene 视图可见，\n" +
                 "   这个箭头是真实的世界物体，Play 模式的 Game 视图里也看得到，\n" +
                 "   方便测试「从背面击杀」时确认怪物朝哪边。\n\n" +
                 "发布前关掉即可。")]
        public bool showFacingArrow = false;

        [Tooltip("箭头颜色（指向前方）")]
        public Color facingArrowColor = new Color(1f, 0.25f, 0.25f, 1f);

        [Tooltip("箭头长度（米），会按怪物球心向外延伸")]
        [Min(0.1f)] public float facingArrowLength = 1.5f;

        private Transform m_FacingArrow;

        /// <summary>
        /// 创建一个指向怪物正面的箭头（世界物体，运行时可见）。
        ///
        /// ★ 为什么不用 Gizmo：
        ///   Gizmos 只在编辑器的 Scene 视图里画，
        ///   Play 模式的 Game 视图完全看不到。
        ///   而"从背面击杀"必须在游戏中验证，所以需要一个真实物体。
        ///
        ///   材质用 EchoRuntimeMaterials.AimOverlay（ZTest Always），
        ///   所以它不会被墙体 / 怪物实体遮挡，永远看得见。
        /// </summary>
        private void EnsureFacingArrow()
        {
            if (m_FacingArrow != null) return;

            var go = new GameObject("FacingArrow");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale    = Vector3.one;

            var filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = BuildArrowMesh();

            var rend = go.AddComponent<MeshRenderer>();
            rend.shadowCastingMode          = UnityEngine.Rendering.ShadowCastingMode.Off;
            rend.receiveShadows             = false;
            rend.lightProbeUsage            = UnityEngine.Rendering.LightProbeUsage.Off;
            rend.reflectionProbeUsage       = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            rend.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            rend.sharedMaterial             = EchoRuntimeMaterials.AimOverlay(facingArrowColor);

            m_FacingArrow = go.transform;

            // 先按当前开关状态设置显隐
            UpdateFacingArrow();
        }

        /// <summary>生成一个朝 +Z 的扁平箭头（长 1，最宽处 0.5）。</summary>
        private static Mesh BuildArrowMesh()
        {
            // 箭头 = 一个三角形头 + 一条矩形杆，全部躺在 XZ 平面上
            float halfW  = 0.10f;   // 杆半宽（总宽 0.2）
            float headW  = 0.25f;   // 箭头半宽（总宽 0.5）
            float headZ  = 0.70f;   // 箭头起点（沿 +Z）

            var verts = new List<Vector3>
            {
                // 杆的四角
                new Vector3(-halfW, 0f, 0f),
                new Vector3( halfW, 0f, 0f),
                new Vector3( halfW, 0f, headZ),
                new Vector3(-halfW, 0f, headZ),

                // 箭头三角形的三个顶点
                new Vector3(-headW, 0f, headZ),
                new Vector3( headW, 0f, headZ),
                new Vector3( 0f,    0f, 1f),
            };

            var tris = new List<int>
            {
                // 杆（两个三角形）
                0, 2, 1,
                0, 3, 2,
                // 箭头
                4, 6, 5,
            };

            var mesh = new Mesh { name = "FacingArrow" };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>每帧同步箭头的显隐、位置与朝向。</summary>
        private void UpdateFacingArrow()
        {
            if (m_FacingArrow == null) return;

            bool show = showFacingArrow && isActiveAndEnabled;

            if (m_FacingArrow.gameObject.activeSelf != show)
                m_FacingArrow.gameObject.SetActive(show);

            if (!show) return;

            // ★ 箭头必须用**父物体的 forward**，而不是自己的 ——
            //   如果它自己跟着父物体转，再用 transform.forward 会变成自我参照。
            Vector3 fwd = transform.forward;
            fwd.y = 0f;

            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
            fwd.Normalize();

            // 位置：怪物球心，稍微抬到球体下缘，避免和实体球重叠看不清
            m_FacingArrow.position = transform.position - Vector3.up * (bodyRadius * 0.5f);

            // 朝向：+Z 对齐 forward
            m_FacingArrow.rotation = Quaternion.LookRotation(fwd, Vector3.up);

            // 长度
            float len = Mathf.Max(0.1f, facingArrowLength);
            m_FacingArrow.localScale = new Vector3(len, 1f, len);
        }


        /// <summary>
        /// 准备 NavMeshAgent：**半托管**模式。
        /// updatePosition / updateRotation = false：位置和朝向全由我们自己管
        /// （垂直是重力 + 悬浮、朝向不做），agent 只负责**寻路** ——
        /// 每帧把 SetDestination 的目标拆成沿路径的期望速度（agent.velocity），
        /// 我们拿这个速度的水平分量去推 CharacterController。
        /// 这样绕墙由 NavMesh 保证，而悬浮 / 被墙挡的物理行为全部保留。
        /// </summary>
        private void EnsureAgent()
        {
            m_Agent = GetComponent<NavMeshAgent>();
            if (m_Agent == null) m_Agent = gameObject.AddComponent<NavMeshAgent>();

            m_Agent.updatePosition   = false;
            m_Agent.updateRotation   = false;
            m_Agent.radius           = bodyRadius;
            m_Agent.height           = 1f;
            m_Agent.speed            = moveSpeed;
            m_Agent.angularSpeed     = 720f;
            m_Agent.acceleration     = 999f;
            m_Agent.stoppingDistance = 0f;
            m_Agent.autoBraking      = false;
            m_Agent.autoRepath       = true;

            // ★ 关闭 NavMesh 的**局部避障** ——
            //   否则两只怪物会互相"绕开"，永远叠不到一起。
            //   需求：怪物彼此不碰撞，可以完全重叠。
            m_Agent.obstacleAvoidanceType = ObstacleAvoidanceType.NoObstacleAvoidance;

            m_Agent.enabled          = true;

            m_NavOK = m_Agent.isOnNavMesh;

            // agent 刚建出来时通常不在 NavMesh 上（怪物悬浮在空中），
            // 把它吸附到最近的可走表面上，否则 SetDestination 全部无效。
            TrySnapToNavMesh();
        }

        /// <summary>
        /// 把怪物吸附到最近的 NavMesh 可走点（水平位置），竖直高度继续由悬浮负责。
        /// 找不到就保持 m_NavOK = false，退回直线移动。
        /// </summary>
        private void TrySnapToNavMesh()
        {
            if (m_Agent == null || !m_Agent.isActiveAndEnabled) return;

            if (NavMesh.SamplePosition(transform.position, out NavMeshHit hit, 8f, NavMesh.AllAreas))
            {
                // updatePosition = false，所以要自己把 transform 挪过去
                transform.position = new Vector3(hit.position.x,
                                                 transform.position.y,
                                                 hit.position.z);
                m_NavOK = true;
            }
            else
            {
                m_NavOK = false;
            }
        }

        private void Start()
        {
            // NavMesh 运行时烘焙放到 Start：此时场景所有 Awake 已完成、
            // UI 系统已由 EchoRuntimeBootstrap 补齐，烘焙不阻塞场景加载；
            // 且 Ensure 内部有 try-catch，失败也不会影响游戏启动。
            NavMeshRuntimeBaker.Ensure();

            // ★ 烘焙完成之后才挂 NavMeshAgent —— 否则 Awake 里 AddComponent 会因为
            //   "此刻场上还没有任何 NavMesh 数据" 而报
            //   Failed to create agent because there is no valid NavMesh，
            //   而且那个失效的 agent 之后不会自己恢复。
            EnsureAgent();

            m_NextEmitTime = Time.timeSinceLevelLoad;

            // 开局诊断：确认怪物确实出现在场景里
            Vector3 p = transform.position;
            string groundInfo = TryGetGroundY(out float gy)
                ? string.Format("ground y={0:F2}, expect {1:F2}", gy, gy + floatHeight)
                : "no ground found";

            Debug.Log(string.Format("[EchoPuzzle] monster {0} at ({1:F2},{2:F2},{3:F2}), {4}",
                name, p.x, p.y, p.z, groundInfo));
        }

        private void OnEnable()
        {
            ZoneSystem.RegisterMonster(this);

            // ★ 监听"解除隐藏"：一旦 hiding 从 1 变成 0，
            //   立刻丢弃隐藏期间记住的一切。
            Hide.Unhidden -= OnOwnerUnhidden;
            Hide.Unhidden += OnOwnerUnhidden;

            // ★ 重玩时 GameUI 会挨个 ResetMonster()，那也会触发 OnEnable。
            //   那种情况不算"被 TriggerVolume 显现"，不能误判。
            if (m_SuppressAppearGrace)
            {
                m_SuppressAppearGrace = false;
                return;
            }

            // ★ 被 TriggerVolume 显现（SetActive(true)）时：
            //   · 不触发"停下后继续发光 3 秒"的余韵
            //   · 不触发 Searching 音效
            //   做法：把宽限期直接清零，并标记一次"刚显现"。
            m_MovingGraceTimer = 0f;
            m_AngerTailTimer   = 0f;
            m_JustAppeared     = true;

            // ★ 显现的那一刻就把隐藏期间记住的东西清干净
            DiscardRememberedSonar();
        }
        private void OnDisable()
        {
            ZoneSystem.UnregisterMonster(this);
            m_JustAppeared = false;

            Hide.Unhidden -= OnOwnerUnhidden;

            // ★ 被隐藏的那一刻就把记住的东西全清掉。
            //
            //   这样从"隐藏"开始，到"解除隐藏"为止，
            //   怪物身上不存在任何可以拿来当目标的旧坐标 ——
            //   包括击杀光带的延迟坐标。
            DiscardRememberedSonar();
        }

        /// <summary>
        /// <see cref="Hide.Unhidden"/> 的回调：自己这个物体从"删除"变回"启用"。
        ///
        /// ★ 直接丢弃隐藏期间记住的所有声源坐标 —— 不解时间戳的弯。
        /// </summary>
        private void OnOwnerUnhidden(Hide h)
        {
            if (h == null) return;
            if (h.gameObject != gameObject) return;      // 只处理自己

            DiscardRememberedSonar();
        }

        /// <summary>
        /// 丢弃"记住的声波坐标"。隐藏期间记住的一切全部清空。
        ///
        /// ★ 这是"hiding = 1 时不许接受任何光带"的直接落实。
        ///
        /// ★★ 特别注意 m_PendingKillSource / m_HasPendingKillSource ★★
        ///
        ///   这是"击杀光带"的**延迟坐标**：捕捉到击杀光带时先存起来，
        ///   等受惊状态结束那一刻才用它当目标（见 UpdateFrightened）。
        ///
        ///   隐藏期间 Update 不跑，受惊计时器停住，这条 pending 记录会
        ///   **一直挂着**。等解除隐藏、计时器走完，它就变成目标 ——
        ///   表现为"怪物凭空朝一个旧坐标出发"。
        ///   必须在这里一起清掉。
        /// </summary>
        private void DiscardRememberedSonar()
        {
            // 记住的位置
            m_Target             = Vector3.zero;
            m_StopPoint          = Vector3.zero;
            m_HasTarget          = false;
            m_TargetDirty        = false;
            m_TargetBirthTime    = float.NegativeInfinity;
            m_LastSonarBirthTime = float.NegativeInfinity;

            // ★ 击杀光带的"延迟坐标" —— 必须一起清
            m_PendingKillSource    = Vector3.zero;
            m_PendingKillBirthTime = float.NegativeInfinity;
            m_HasPendingKillSource = false;

            // ★ 受惊状态也一起复位：
            //   否则解除隐藏后它还带着"受惊中"的标志，
            //   计时器一归零就立刻把上面那条 pending 坐标拿来用。
            frightened        = 0;
            m_FrightenedTimer = 0f;

            // 捕捉节奏
            m_NextCaptureTime    = 0f;

            // 持续追逐锁定
            captured             = 0;

            // 卡住 / 移动状态
            m_StuckTime          = 0f;
            m_StuckTries         = 0;
            m_IsMoving           = false;
            m_WantMove           = false;

            // NavMesh 上的旧路径
            if (m_Agent != null && m_Agent.isActiveAndEnabled && m_Agent.isOnNavMesh)
            {
                if (m_Agent.hasPath) m_Agent.ResetPath();
            }
        }

        /// <summary>
        /// 怪物是否处于"删除"（hiding = 1）状态。
        ///
        /// ★ 隐藏的怪物**不捕捉任何光带** —— 这是"它一出现就往旧位置跑"的根治办法。
        /// </summary>
        public bool IsHidden()
        {
            Hide h = GetComponent<Hide>();
            return h != null && h.hiding != 0;
        }


        /// <summary>把 captured 归 0（重新开始时用）。</summary>
        public void ResetCaptured()
        {
            captured = 0;
        }

        /// <summary>记住出生位置（只记第一次）。</summary>
        private void CaptureSpawn()
        {
            if (m_SpawnCaptured) return;

            m_SpawnPosition  = transform.position;
            m_SpawnRotation  = transform.rotation;
            m_SpawnCaptured  = true;
        }

        /// <summary>
        /// 复位到开局状态：位置、朝向、移动目标、击杀标记、悬浮状态、光带。
        ///
        /// ★ 重玩 / 读档时必须调它，否则：
        ///   · 怪物停在上一把追到的位置（不会回到出生点）
        ///   · m_Killed 还是 true -> Kill() 里 `if (m_Killed) return;` 直接跳过，
        ///     所以"击杀玩家"彻底失效
        /// </summary>
        public void ResetMonster()
        {
            CaptureSpawn();

            // ★ 第一步：先把自己重新激活。
            //
            //   被光刃移除时我们做的是 SetActive(false)，
            //   此时 NavMeshAgent 也处于非激活状态 ——
            //   对它调用 ResetPath() 会抛：
            //     "ResetPath" can only be called on an active agent
            //      that has been placed on a NavMesh.
            //   所以必须**先**激活，再碰 agent。
            bool wasInactive = !gameObject.activeSelf;

            // ★ 重玩时的激活不是"被 TriggerVolume 显现"，
            //   不能让 OnEnable 里的显现处理生效。
            if (wasInactive) m_SuppressAppearGrace = true;

            if (wasInactive) gameObject.SetActive(true);

            // ---- 位置与朝向 ----
            // CharacterController 会缓存"内部位置"，直接改 transform 之后要 disable/enable 一次
            // 才能同步，否则下一次 Move() 会把它拉回去。
            bool hadController = m_Controller != null && m_Controller.enabled;
            if (hadController) m_Controller.enabled = false;

            transform.position = m_SpawnPosition;
            transform.rotation = m_SpawnRotation;

            if (hadController) m_Controller.enabled = true;

            // NavMeshAgent 同步回出生点：清掉旧路径，再把内部位置 Warp 回去
            // （Warp 用贴地高度 —— agent 内部只在 NavMesh 表面上行走，实体悬浮由我们管）。
            //
            // ★ 每个调用都要先确认 agent 真的可用：
            //     · isActiveAndEnabled —— 组件本身没被禁用
            //     · isOnNavMesh        —— agent 已经落在 NavMesh 上
            //   两个条件缺一都会抛异常。
            if (m_Agent != null && m_Agent.isActiveAndEnabled)
            {
                if (m_Agent.isOnNavMesh && m_Agent.hasPath)
                    m_Agent.ResetPath();

                if (m_Agent.isOnNavMesh)
                {
                    Vector3 wp = m_SpawnPosition;
                    if (NavMesh.SamplePosition(wp, out NavMeshHit wh, 4f, NavMesh.AllAreas))
                        wp.y = wh.position.y;

                    m_Agent.Warp(wp);
                }
                else
                {
                    // 刚重新激活的 agent 可能还没落回 NavMesh —— 让它自己找一下
                    TrySnapToNavMesh();
                }
            }

            // ---- 状态 ----
            m_HasTarget        = false;
            m_Target           = Vector3.zero;
            m_TargetBirthTime  = float.NegativeInfinity;
            m_TargetDirty      = false;
            m_StuckTime        = 0f;
            m_IsMoving         = false;
            m_Killed           = false;

            // 被光刃移除的怪物，重玩时要复活
            // （SetActive(true) 已经在本方法开头做过了，这里只清标志）
            m_RemovedByBlade   = false;

            m_VerticalVelocity = 0f;
            m_Suspended        = false;      // 让重力重新生效 -> 会落回悬浮高度

            m_NextEmitTime    = Time.timeSinceLevelLoad;
            m_NextCaptureTime = 0f;
            m_LastSonarBirthTime = float.NegativeInfinity;   // 重玩：允许重新响应任何声音
            m_CachedSonar     = null;

            // 持续追逐：captured 归 0（重新开始时不再追玩家）
            captured = 0;

            // Anger / Approaching / 特殊动作 全部复位
            anger            = 0;
            approaching      = 0;
            m_AngerTailTimer = 0f;
            m_MovingGraceTimer = 0f;
            m_HasPendingKillSource = false;
            CancelSpecialAction();

            // 音效状态复位（停掉正在播的，重新开始计时）
            var ma = GetComponent<MonsterAudio>();
            if (ma != null) ma.ResetAudioState();

            // ---- 光带 ----
            m_Beams.Clear();
            if (trailRenderer != null) trailRenderer.ClearTrails();
        }

        /// <summary>
        /// 确保怪物身上有 3D 音源（<see cref="MonsterAudio"/>）。
        ///
        /// ★ 这样"新建一个 PuzzleMonster"就自动带音效组件，
        ///   不需要手动挂。已有的不会重复加。
        /// </summary>
        /// <summary>如果身上没有 T 就加一个。</summary>
        private T AddComponentIfMissing<T>() where T : Component
        {
            T c = GetComponent<T>();
            if (c == null) c = gameObject.AddComponent<T>();
            return c;
        }

        /// <summary>
        /// 地牢模式下挂变色球可视化组件。
        ///
        /// ★ 同时把 <see cref="overrideColor"/> 关掉 ——
        ///   地牢模式不再让**光带**变红，改成让**球**被照亮。
        ///   探秘模式保持原样（光带变色）。
        /// </summary>
        private void EnsureSenseSphereVisual()
        {
            if (!GameUI.IsDungeonMode) return;

            try
            {
                // 光带不再变色（改由 SenseSphereVisual 画球）
                overrideColor = false;

                if (GetComponent<EchoPuzzle.Dungeon.SenseSphereVisual>() == null)
                    gameObject.AddComponent<EchoPuzzle.Dungeon.SenseSphereVisual>();

                // ★ 血条（屏幕空间，画在怪物"相机视角的上方"）
                if (GetComponent<EchoPuzzle.Dungeon.MonsterHealthBar>() == null)
                    gameObject.AddComponent<EchoPuzzle.Dungeon.MonsterHealthBar>();
            }
            catch (System.Exception e)
            {
                // ★ 这里绝不能把异常往外抛 —— Awake 里抛异常会中断
                //   整个怪的初始化，连带影响其它系统
                Debug.LogError("[地牢] 挂 SenseSphereVisual 失败：" + e);
            }
        }

        private void EnsureMonsterAudio()
        {
            var audio = GetComponent<MonsterAudio>();
            if (audio == null) audio = gameObject.AddComponent<MonsterAudio>();

            // AudioSource 由 MonsterAudio 的 [RequireComponent] 自动带上，
            // 但保险起见再确认一次。
            if (GetComponent<AudioSource>() == null) gameObject.AddComponent<AudioSource>();
        }

        private void EnsureTrailRenderer()
        {            if (trailRenderer == null)
                trailRenderer = GetComponentInChildren<MonsterTrailRenderer>(true);

            if (trailRenderer == null)
            {
                var go = new GameObject("MonsterTrailRenderer");
                go.transform.SetParent(transform, false);
                go.transform.localPosition = Vector3.zero;

                trailRenderer = go.AddComponent<MonsterTrailRenderer>();
            }

            // 把 Inspector 上的参数同步给渲染器
            trailRenderer.trailSpacing    = trailSpacing;
            trailRenderer.bodyFadeSpeed   = trailBodyFadeSpeed;
            trailRenderer.headFadeSpeed   = trailHeadFadeSpeed;
            trailRenderer.maxLength       = trailMaxLength;
        }

        // ---------------------------------------------------------------- 不透明实体

        private MeshRenderer m_BodyRenderer;

        [Header("实体外观")]
        [Tooltip("实体球网格使用的材质。留空则用 EchoPuzzle/Occluder 材质（纯黑、不透明）。\n" +
                 "它负责**遮挡**穿过实体球那一段光带。")]
        public Material bodyMaterial;

        /// <summary>
        /// 给实体挂一个**不透明**的球网格，用来遮挡穿过它的光带。
        ///
        /// 为什么用 EchoPuzzle/Occluder 而不是普通材质：
        ///   光带（SonarTrail / Marker / Footprint）都是 `ZTest LEqual` + 透明队列。
        ///   Occluder 是 ZWrite On、不透明、纯黑，正好能挡住它们 ——
        ///   和"不透明墙体挡住光带"是同一套机制。
        /// </summary>
        private void EnsureBodyOccluder()
        {
            if (m_BodyRenderer != null) return;

            var bodyGo = new GameObject("Body");
            bodyGo.transform.SetParent(transform, false);
            bodyGo.transform.localPosition = Vector3.zero;
            bodyGo.transform.localRotation = Quaternion.identity;
            bodyGo.transform.localScale    = Vector3.one;

            // 程序化生成一个球网格（半径由 bodyRadius 决定，直径 = 2r）
            var filter = bodyGo.AddComponent<MeshFilter>();
            filter.sharedMesh = BuildSphereMesh(bodyRadius, 16, 24);

            m_BodyRenderer = bodyGo.AddComponent<MeshRenderer>();

            Material mat = bodyMaterial != null ? bodyMaterial : EchoRuntimeMaterials.Occluder;
            if (mat != null)
            {
                m_BodyRenderer.sharedMaterial = mat;
            }
            else
            {
                Debug.LogWarning("[EchoPuzzle] 怪物实体拿不到 Occluder 材质（Resources/EchoRuntime/EchoOccluder）。\n" +
                                 "实体将不可见，也就挡不住光带。请执行菜单：EchoPuzzle → 生成运行时材质资产");
            }

            m_BodyRenderer.shadowCastingMode          = UnityEngine.Rendering.ShadowCastingMode.Off;
            m_BodyRenderer.receiveShadows             = false;
            m_BodyRenderer.lightProbeUsage            = UnityEngine.Rendering.LightProbeUsage.Off;
            m_BodyRenderer.reflectionProbeUsage       = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            m_BodyRenderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        }

        /// <summary>生成一个以原点为中心、半径 radius 的球网格。</summary>
        private static Mesh BuildSphereMesh(float radius, int rings, int segments)
        {
            var verts = new List<Vector3>();
            var tris  = new List<int>();
            var norms = new List<Vector3>();

            // 顶点：从上到下按纬度分环
            for (int r = 0; r <= rings; r++)
            {
                float v     = (float)r / rings;
                float phi   = v * Mathf.PI;            // 0..PI
                float y     = Mathf.Cos(phi);
                float ringR = Mathf.Sin(phi);

                for (int s = 0; s <= segments; s++)
                {
                    float u = (float)s / segments;
                    float theta = u * Mathf.PI * 2f;

                    var n = new Vector3(ringR * Mathf.Cos(theta), y, ringR * Mathf.Sin(theta));
                    norms.Add(n);
                    verts.Add(n * radius);
                }
            }

            // 索引
            int stride = segments + 1;
            for (int r = 0; r < rings; r++)
            {
                for (int s = 0; s < segments; s++)
                {
                    int a = r * stride + s;
                    int b = a + stride;

                    tris.Add(a); tris.Add(b); tris.Add(a + 1);
                    tris.Add(a + 1); tris.Add(b); tris.Add(b + 1);
                }
            }

            var mesh = new Mesh { name = "MonsterBody" };
            mesh.SetVertices(verts);
            mesh.SetNormals(norms);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        // ---------------------------------------------------------------- 主循环

        private void Update()
        {
            // ★ 朝向箭头要在**所有状态下**更新（暂停 / 结算时也要跟着怪物），
            //   所以放在 m_ReadyToRun() 之前。
            UpdateFacingArrow();

            // ★★★ 被"删除"（hiding = 1）的怪物：**什么都不做** ★★★
            //
            //   需求原文：hiding 为 1 时的怪物不应当捕捉任何光带。
            //
            //   这里直接整体返回 —— 不捕捉脚步、不捕捉石子声波、
            //   不捕捉击杀光带、不进入受惊、不移动、不发光。
            //
            //   ★ 每次进来都再清一次记住的坐标：隐藏期间万一有个别环节
            //     （比如别的脚本直接调 public 方法）写进了目标，
            //     也会在下一帧被抹掉。
            if (IsHidden())
            {
                DiscardRememberedSonar();
                return;
            }

            if (!m_ReadyToRun()) return;

            float now = Time.timeSinceLevelLoad;

            UpdatePlayerRef();

            // ★★★ 地牢模式：完全独立的一套行为 ★★★
            //
            //   和探秘模式的差别（需求原文）：
            //     · 仍然**捕捉玩家坐标**   -> CaptureSonarSource 照常跑
            //     · **不追逐**玩家         -> 不调 ChasePlayerDirectly / MoveTowardTarget
            //     · **击杀范围失效**       -> 不调 Kill()
            //     · **不进入受惊状态**     -> 不调 UpdateFrightened
            //     · 伤害由子弹机制负责（见 Scripts/Dungeon/DungeonEnemy.cs 的交接说明）
            //
            //   所以这里只保留：捕捉声源 + 原地巡视 + 发光带 + 推进光带。
            if (GameUI.IsDungeonMode)
            {
                UpdateDungeonMode(now);
                return;
            }

            // 0) 受惊状态的进入 / 维持 / 退出
            UpdateFrightened(now);

            // 1) 感知圆柱里的声波光带 -> 捕捉产生点 / 改变外观
            //    ★ 受惊状态下**仍然捕捉**，之后退出时会朝最新目标前进
            CaptureSonarSource();

            // 1.2) ★★ captured == 1：每帧强制把目标刷成玩家当前位置 ★★
            //
            //   放在这里（CaptureSonarSource 之后、所有移动逻辑之前），
            //   保证任何"上一次写进 m_Target / m_StopPoint 的旧坐标"
            //   都会在移动之前被玩家坐标覆盖掉。
            //
            //   不受 captureInterval 限制 —— 每帧都刷，追得才跟得上。
            if (IsCaptured)
            {
                ChasePlayerDirectly();
            }

            // 1.5) 特殊动作（Anger / Approaching 状态机）
            //    ★ captured == 1 时不跑 —— 否则它会改写目标 / 停车点
            if (!IsCaptured)
            {
                UpdateSpecialAction(now);
            }

            // 2) 朝目标移动
            //    ★ 受惊时不可移动 —— MoveTowardTarget 内部会跳过水平位移，
            //      但仍然会跑重力 / 悬浮，避免卡在半空。
            MoveTowardTarget();

            // 3) 击杀：实体接触到玩家胶囊体才算
            if (killOnPlayerEnter && BodyTouchesPlayer()) Kill();

            // 4) 随机发光带
            //
            //    ★ 受惊时：**无条件**持续发光，间隔用 emitIntervalFrightened
            //    ★ 平时：只在"想动"时发光（被墙卡住也算想动，好暴露异常）
            if (IsFrightened)
            {
                if (emitIntervalFrightened > 0f && now >= m_NextEmitTime)
                {
                    m_NextEmitTime = now + emitIntervalFrightened;
                    EmitBurst();
                }
            }
            else if (emitInterval > 0f && (m_WantMove || MovingOrGrace || InAngerActivity)
                     && now >= m_NextEmitTime)
            {
                m_NextEmitTime = now + emitInterval;
                EmitBurst();
            }

            // 5) 推进 / 收尾已有的光带
            StepBeams(now);
        }

        // ---------------------------------------------------------------- 地牢模式：血量

        // ★ 只有地牢模式有意义（探秘模式怪物是被匕首一击必杀的，没有血量概念）。
        //
        //   交给子弹机制：开火命中时调 DungeonTakeDamage(伤害值) 即可。

        [Header("地牢模式 · 血量")]
        [Tooltip("地牢模式下怪物的最大血量。探秘模式不使用这个值")]
        [Min(1)] public int dungeonMaxHealth = 12;

        [Tooltip("地牢模式下被打死时是否隐藏自己（走 Hide 机制）。勾掉则只是停止响应")]
        public bool dungeonHideOnDeath = true;

        /// <summary>【地牢模式】当前血量。</summary>
        public int DungeonHealth { get; private set; }

        /// <summary>【地牢模式】最大血量。</summary>
        public int DungeonMaxHealth => Mathf.Max(1, dungeonMaxHealth);

        /// <summary>【地牢模式】血量比例（0~1），给血条画长度用。</summary>
        public float DungeonHealth01 => Mathf.Clamp01((float)DungeonHealth / DungeonMaxHealth);

        /// <summary>【地牢模式】已经死了。</summary>
        public bool DungeonIsDead { get; private set; }

        /// <summary>血量变化时触发（参数：当前血量，最大血量）。</summary>
        public event System.Action<int, int> DungeonHealthChanged;

        /// <summary>
        /// 【地牢模式】扣血。
        ///
        /// ★ 探秘模式调用它没有任何效果（那边没有血量概念）。
        /// ★ 已经死了再打也无效。
        /// </summary>
        public void DungeonTakeDamage(int amount)
        {
            if (!GameUI.IsDungeonMode) return;
            if (DungeonIsDead) return;
            if (amount <= 0) return;

            DungeonHealth = Mathf.Max(0, DungeonHealth - amount);

            DungeonHealthChanged?.Invoke(DungeonHealth, DungeonMaxHealth);

            if (DungeonHealth <= 0) DungeonDie();
        }

        /// <summary>【地牢模式】回血。</summary>
        public void DungeonHeal(int amount)
        {
            if (!GameUI.IsDungeonMode) return;
            if (DungeonIsDead) return;
            if (amount <= 0) return;

            DungeonHealth = Mathf.Min(DungeonMaxHealth, DungeonHealth + amount);

            DungeonHealthChanged?.Invoke(DungeonHealth, DungeonMaxHealth);
        }

        /// <summary>【地牢模式】重置血量（开局 / 重新开始用）。</summary>
        public void DungeonResetHealth()
        {
            DungeonIsDead = false;
            DungeonHealth = DungeonMaxHealth;

            DungeonHealthChanged?.Invoke(DungeonHealth, DungeonMaxHealth);
        }

        /// <summary>【地牢模式】死亡。</summary>
        private void DungeonDie()
        {
            DungeonIsDead = true;

            Debug.Log($"[地牢] 怪物死亡（{name}）。");

            // ★★ 死亡表现和探秘模式一致 ★★
            //
            //   探秘模式里玩家用匕首杀怪走的是 RemoveByBlade()，它做两件事：
            //     1) AudioSystem.Play(AudioSystem.Cue.Death)   —— 死亡音效
            //     2) EmitRemoveSonar()                          —— 在脚下炸一大片光带
            //
            //   地牢模式这里照搬这两件事，保证"怪物死掉"的反馈是一样的。
            AudioSystem.Play(AudioSystem.Cue.Death);

            if (emitSonarOnRemove)
            {
                EmitRemoveSonar();
            }

            if (dungeonHideOnDeath)
            {
                // 走和其它物体一样的"删除"机制
                Hide h = GetComponent<Hide>();
                if (h != null) h.SetHiding(1);
                else gameObject.SetActive(false);
            }
        }

        // ---------------------------------------------------------------- 地牢模式专用状态

        // ★★ 这三个是**给子弹机制用的公开数据**（交接给实现子弹的人）★★
        //
        //   地牢模式里怪物不追人、不近身秒杀，伤害交给子弹。
        //   实现子弹的人从这里取"怪物现在瞄着哪个点"。

        /// <summary>【地牢模式】怪物最近捕捉到的目标点（一般就是玩家坐标）。</summary>
        public Vector3 DungeonPatrolTarget { get; private set; }

        /// <summary>【地牢模式】最后一次捕捉到的玩家坐标。</summary>
        public Vector3 DungeonLastPlayerPos { get; private set; }

        /// <summary>【地牢模式】有没有捕捉到过玩家。</summary>
        public bool DungeonHasPlayerPos { get; private set; }

        // ---- 地牢模式 · 行为接管 ----
        //
        //   ★ DungeonEnemy（Scripts/Dungeon/DungeonEnemy.cs）挂在本物体上时接管移动：
        //     检测到玩家后随机运动 + 定时开火，而不是像探秘模式那样冲向玩家。
        //   ★ 探秘模式没有 DungeonEnemy，下面这些字段/方法永远不会被用到。

        [Header("地牢模式 · 行为接管")]
        [Tooltip("true = 移动由 DungeonEnemy 接管（随机巡逻 / 寻路到可命中点）。\n探秘模式永远为 false")]
        [System.NonSerialized] public bool DungeonMoveControlled;

        /// <summary>缓存的地牢行为组件（没有则每帧重查一次，避免 AddComponent 时序问题）。</summary>
        private Dungeon.DungeonEnemy m_DungeonEnemy;

        /// <summary>
        /// 【地牢模式】由 DungeonEnemy 设置巡逻 / 射击目标点。
        /// 写入 m_Target / m_StopPoint，后续 MoveTowardTarget 会朝它走（NavMesh 寻路）。
        /// </summary>
        public void DungeonSetMoveTarget(Vector3 pos)
        {
            m_Target             = pos;
            m_StopPoint          = pos;
            m_TargetBirthTime    = Time.timeSinceLevelLoad;
            m_LastSonarBirthTime = m_TargetBirthTime;
            m_HasTarget          = true;
            m_TargetDirty        = true;
        }

        /// <summary>【地牢模式】清除移动目标（原地停下，重力 / 悬浮照常）。</summary>
        public void DungeonClearMoveTarget()
        {
            m_HasTarget = false;

            if (m_Agent != null && m_Agent.isActiveAndEnabled &&
                m_Agent.isOnNavMesh && m_Agent.hasPath)
            {
                m_Agent.ResetPath();
            }
        }

        /// <summary>
        /// 【地牢模式】只做重力 / 悬浮，**不做水平移动**。
        ///
        /// ★ 地牢模式怪物不追人，但也不能掉下去或飘走 ——
        ///   所以仍然要跑悬浮逻辑，只是不给它水平位移。
        /// </summary>
        private void UpdateHoverOnly()
        {
            float vertical = ApplyGravityAndHover();

            Vector3 move = Vector3.up * (vertical * Time.deltaTime);

            if (m_Controller != null && m_Controller.enabled)
            {
                m_Controller.Move(move);
            }
            else
            {
                transform.position += move;
            }

            // 没有水平位移 —— 明确置成"没在动"，
            // 这样击杀范围的移动/静止判定不会因为残留状态出错
            m_IsMoving = false;
            m_WantMove = false;
        }

        /// <summary>
        /// 【地牢模式】怪物的每帧行为 —— 和探秘模式完全独立。
        ///
        /// 做的：
        ///   · 捕捉声波（含玩家脚步 -> 记下玩家坐标，供别的系统读取）
        ///   · 原地巡视（不追人，只在附近慢慢晃 / 转向）
        ///   · 定期发光带（让玩家能看见它）
        ///   · 推进已有的光带
        ///
        /// 不做的：
        ///   · **不追逐玩家**（不调 ChasePlayerDirectly / MoveTowardTarget 的追人分支）
        ///   · **不击杀**（击杀范围失效，伤害交给子弹机制）
        ///   · **不进入受惊状态**
        ///
        /// ★★ 交接说明（子弹机制由别人实现）★★
        ///
        ///   需要的接口都在下面：
        ///     · <see cref="DungeonPatrolTarget"/>  —— 怪物现在盯着的点（可能为空）
        ///     · <see cref="DungeonLastPlayerPos"/> —— 最后一次捕捉到的玩家坐标
        ///     · <see cref="DungeonHasPlayerPos"/>  —— 有没有捕捉到过
        ///
        ///   想在这里接开火逻辑，直接在 UpdateDungeonMode 末尾加即可。
        /// </summary>
        private void UpdateDungeonMode(float now)
        {
            // ---- 1) 捕捉声波光带（和探秘模式共用同一套）----
            //    ★ 内部会把捕捉到的产生点写进 m_Target / m_StopPoint。
            //      地牢模式下这两个字段不再用于"追人"，
            //      改由下面的 DungeonPatrolTarget 给别的系统读。
            CaptureSonarSource();

            // ---- 2) 把捕捉到的玩家坐标单独记一份（供子弹机制用）----
            //
            //   CaptureSonarSource 里如果捕捉到的是玩家脚步，
            //   m_LastPlayerSonarTime 会被刷新。这里据此认定"发现玩家"。
            if (m_HasTarget && m_Target != Vector3.zero)
            {
                DungeonPatrolTarget  = m_Target;
                DungeonHasPlayerPos  = true;
                DungeonLastPlayerPos = m_Target;
            }

            // ---- 3) 移动（地牢模式）----
            //
            //   ★ 挂了 DungeonEnemy 时由它接管：检测到玩家后随机运动，
            //     看不到玩家时寻路到"能直接命中玩家"的位置（见 Dungeon/PuzzleEnemy 交接注释）。
            //   ★ 场上没挂 DungeonEnemy 的纯测试怪保持旧的追击行为。
            //
            //   ★ 无论哪种，都不击杀 —— 本方法里没有击杀判定，
            //     BodyTouchesPlayer() 只在探秘分支调用。
            if (m_DungeonEnemy == null)
                m_DungeonEnemy = GetComponent<Dungeon.DungeonEnemy>();

            if (m_DungeonEnemy != null)
            {
                m_DungeonEnemy.PatrolTick(now);
            }

            MoveTowardTarget();

            // ---- 4) 随机发光带 ----
            //
            //   ★ 条件 = **想动 / 刚停下（3 秒宽限期）/ 特殊动作中**，
            //     和探秘模式完全一样。
            //
            //   也就是说：怪物在移动时会持续发光（暴露自己），
            //   静止不动时不发光。
            //
            //   ★ 之前这里被整个删掉了，理由是"地牢模式怪物不移动"——
            //     但后来恢复了追击，怪物会移动了，于是变成
            //     "一边追一边完全不出声"，看着像发光带坏了。
            //
            //   ★ 地牢模式**没有受惊状态**，所以不跑 emitIntervalFrightened 那个分支。
            if (emitInterval > 0f && (m_WantMove || MovingOrGrace || InAngerActivity)
                && now >= m_NextEmitTime)
            {
                m_NextEmitTime = now + emitInterval;
                EmitBurst();
            }

            // ---- 5) 开火（DungeonEnemy：检测到玩家且视线通畅时，定时发射数颗子弹）----
            if (m_DungeonEnemy != null)
            {
                m_DungeonEnemy.CombatTick(now);
            }

            // ---- 6) 推进 / 收尾已有的光带 ----
            StepBeams(now);
        }

        // ---------------------------------------------------------------- 受惊

        /// <summary>
        /// 受惊状态的每帧处理：进入判定、计时、自动退出。
        ///
        /// ── 两种进入方式（彼此独立）────────────────────────────
        ///   方式 1：玩家正在**收回匕首**，且玩家本体或匕首的任意一点
        ///           位于本怪物的 KillRadiusMoving 圆柱范围内，
        ///           且场上**没有**任何击杀声波光带。
        ///           -> 额外触发玩家死亡流程（见 TriggerPlayerFrightDeath）
        ///   方式 2：捕捉到**其它怪物被刺杀时**发出的声波光带（isKillSonar），
        ///           且 frightened == 0。
        ///
        /// ── 退出 ────────────────────────────────────────────────
        ///   进入后计时 frightenedDuration（默认 3 秒），到点自动退出。
        ///
        /// ── 受惊期间 ────────────────────────────────────────────
        ///   · 不可移动
        ///   · 持续发光，间隔 / 长度分别被 *Frightened 字段覆盖
        ///   · 击杀范围按 KillRadiusIdle 算
        ///   · 仍然捕捉声波光带、更新目标
        /// </summary>
        private void UpdateFrightened(float now)
        {
            SonarTrailRenderer trails = GetSonarTrails();

            // ---- 已经受惊：只处理计时与退出 ----
            if (IsFrightened)
            {
                m_FrightenedTimer -= Time.deltaTime;

                if (m_FrightenedTimer <= 0f)
                {
                    frightened = 0;
                    m_FrightenedTimer = 0f;

                    // ★ 受惊结束 -> 现在才把 Anger / Approaching 置 1，
                    //   并把"当初吓到它的那个击杀声源"当成目标。
                    if (m_HasPendingKillSource)
                    {
                        Vector3 src = m_PendingKillSource;
                        float   bt  = m_PendingKillBirthTime;
                        m_HasPendingKillSource = false;

                        EnterAnger(src, bt);
                    }
                }

                return;
            }

            // ---- 未受惊：检查两种进入方式 ----

            // 方式 2：捕捉到击杀声波
            if (trails != null && trails.TryCaptureKillSonar(
                    out Vector3 killSrc, out float killBirth,
                    transform.position, senseRadius, senseHeight))
            {
                // ★ 这里**只进入受惊**，不立刻 Anger / Approaching。
                //   需求：Anger 和 Approaching 应该在**受惊状态退出时**才置 1。
                //   所以先把击杀声源记下来，等受惊结束再用。
                m_PendingKillSource    = killSrc;
                m_PendingKillBirthTime = killBirth;
                m_HasPendingKillSource = true;

                EnterFrightened(byBladeRecall: false, now);
                return;
            }

            // 方式 1：玩家收回匕首，且本怪物范围内
            var dagger = DaggerSystem.Instance;

            if (dagger == null || !dagger.IsRecalling) return;

            // ★ 场上只要有击杀声波，方式 1 就不成立
            if (trails != null && trails.AnyKillSonar()) return;

            if (m_Player == null) return;

            // 把「玩家本体」和「光刃中点」都当作判定点
            if (IsInKillRadius(m_Player.transform.position, killRadiusMoving))
            {
                EnterFrightened(byBladeRecall: true, now);
                return;
            }

            if (dagger.TryGetBladeMid(out Vector3 bladeMid) &&
                IsInKillRadius(bladeMid, killRadiusMoving))
            {
                EnterFrightened(byBladeRecall: true, now);
            }
        }

        /// <summary>进入受惊状态。</summary>
        private void EnterFrightened(bool byBladeRecall, float now)
        {
            if (IsFrightened) return;

            frightened        = 1;
            m_FrightenedTimer = Mathf.Max(0.1f, frightenedDuration);

            // 受惊不可移动 -> 特殊动作也没法继续
            CancelSpecialAction();

            // 让下一次发光立刻发生，视觉上"瞬间炸开"
            m_NextEmitTime = now;

            // ★ 方式 1：玩家进入死亡倒计时（默认 3 秒）
            if (byBladeRecall)
            {
                GameUI.BeginFrightenedDeath(frightenedDeathDelay);

                // 记住"由谁触发的"，3 秒后由它来播击杀演出
                GameUI.RegisterFrightenedKiller(this);
            }
        }

        [Tooltip("方式 1 触发的玩家死亡倒计时（秒）。减速 / 变色都在这段时间内完成")]
        [Min(0.1f)] public float frightenedDeathDelay = 2f;

        /// <summary>
        /// 某个世界坐标是否落在「以球心为轴、水平半径 radius、总高 killHeight」的圆柱内。
        /// 与 BodyTouchesPlayer 用的是同一套竖直判定口径。
        /// </summary>
        private bool IsInKillRadius(Vector3 worldPos, float radius)
        {
            Vector3 mon = transform.position;

            // 竖直
            float halfH = Mathf.Max(0.01f, killHeight) * 0.5f;
            if (Mathf.Abs(worldPos.y - mon.y) > halfH) return false;

            // 水平
            float dx = worldPos.x - mon.x;
            float dz = worldPos.z - mon.z;

            return dx * dx + dz * dz <= radius * radius;
        }

        /// <summary>取声波光带渲染器（缓存，避免每帧 Find）。</summary>
        private SonarTrailRenderer GetSonarTrails()
        {
            var sonar = m_CachedSonar;

            if (sonar == null)
            {
                sonar = FindObjectOfType<SonarSystem>();
                m_CachedSonar = sonar;
            }

            return sonar != null ? sonar.trailRenderer : null;
        }

        private bool m_ReadyToRun()
        {
            if (GameUI.Current != GameState.Playing) return false;
            return true;
        }

        private void UpdatePlayerRef()
        {
            if (m_Player == null) m_Player = FindObjectOfType<PlayerController>();
        }

        // ---------------------------------------------------------------- 击杀判定

        /// <summary>
        /// 怪物是否击杀玩家。
        ///
        /// 击杀范围 = 以怪物球心为轴的**竖直圆柱**：
        ///   · 水平：半径 killRadius（移动中 killRadiusMoving / 静止时 killRadiusIdle）
        ///   · 竖直：总高 killHeight，以怪物球心为中点，上下各一半
        ///
        /// 判定标准：**玩家胶囊体有任意一部分进入这个圆柱**，就击杀。
        ///
        /// 拆成两步：
        ///   ① 竖直：胶囊的 y 范围是否与圆柱的 y 范围重叠
        ///   ② 水平：胶囊的水平投影（半径 pc.radius 的圆）是否与圆柱的水平投影
        ///             （半径 killRadius 的圆）相交
        ///             —— 即 两心水平距 - 胶囊半径 <= killRadius
        ///
        /// ★ killRadius 是「从怪物球心沿水平方向量起的半径」，**包含**怪物自身半径。
        ///   所以玩家贴住怪物时，两心距离 ≈ bodyRadius(0.51) + 玩家半径(0.37) ≈ 0.88，
        ///   要把 killRadius 设到 ≈0.88 才能做到"贴身才杀"。
        ///   这是"以怪物球心为圆心"的必然结果，不是 bug。
        /// </summary>
        private bool BodyTouchesPlayer()
        {
            if (m_Player == null) return false;

            var pc = m_Player.GetComponent<CharacterController>();
            if (pc == null) return false;


            // ★ 受惊状态下**固定按 KillRadiusIdle** 计算（不管实际在不在动）
            //   平时用"是否算作移动中"（含停下后的 3 秒宽限）
            // ★ 特殊动作 / 余韵期间固定按 KillRadiusMoving（当作"在移动中"）
            bool active = MovingOrGrace || InAngerActivity;
            float killRadius = (IsFrightened || !active) ? killRadiusIdle : killRadiusMoving;

            // 记录"击杀范围按 Moving 还是 Idle 计" —— MonsterAudio 靠它检测切换。
            m_KillRangeMovingLastFrame = active && !IsFrightened;

            Vector3 mon = transform.position;

            // ---- ① 竖直：胶囊范围 vs 圆柱范围 ----
            Vector3 colCenter = pc.transform.position + pc.center;
            float   colHalfH  = pc.height * 0.5f;

            float colLo = colCenter.y - colHalfH;
            float colHi = colCenter.y + colHalfH;

            float cylHalfH = Mathf.Max(0.01f, killHeight) * 0.5f;
            float cylLo    = mon.y - cylHalfH;
            float cylHi    = mon.y + cylHalfH;

            if (colHi < cylLo || colLo > cylHi) return false;   // 竖直不重叠

            // ---- ② 水平：两圆相交 ----
            //   胶囊的水平投影是半径 pc.radius 的圆
            float playerRadius = pc.radius + pc.skinWidth;

            float dx = mon.x - colCenter.x;
            float dz = mon.z - colCenter.z;
            float centerDist = Mathf.Sqrt(dx * dx + dz * dz);

            // 两心距 <= killRadius + 胶囊半径 -> 胶囊表面有一部分进了圆柱
            return centerDist - playerRadius <= killRadius;
        }

        /// <summary>
        /// 某个世界坐标是否落在**变色球**内。给 ZoneSystem 查声波外观用。
        ///
        /// ★ 这里用 colorRadius（球），不是 senseRadius（圆柱）——
        ///   "光带变色"和"怪物感知"是两套独立范围，各自可调。
        /// </summary>
        public bool SenseContains(Vector3 worldPos)
        {
            // ★★ 地牢模式：只看 **xz 两个轴** ★★
            //
            //   需求：判定依据从 xyz 三轴变成 xz 两轴 ——
            //   光带只要在 xz 投影上落进这个圆就算"在球附近"，
            //   不管它在球上方、球下方还是和球同高。
            //   （地牢模式的光带都是贴地走的，y 方向跟球心差得很远，
            //     按三轴算永远判不中。）
            if (GameUI.IsDungeonMode)
            {
                float dx = worldPos.x - transform.position.x;
                float dz = worldPos.z - transform.position.z;

                return dx * dx + dz * dz <= colorRadius * colorRadius;
            }

            // 探秘模式：保持原来的三轴球形判定
            return (worldPos - transform.position).sqrMagnitude <= colorRadius * colorRadius;
        }

        /// <summary>
        /// 扫描当前所有声波光带，如果某条进入了感知圆柱，就把它所属声源的**产生点**
        /// 记为怪物要去的地方。
        ///
        /// "产生点" = 那一次发声的声源位置（玩家当时的脚下）。
        /// 潜行产生的声波不算 —— 判断方式见 SonarTrailRenderer 里的 isSneak 标记。
        ///
        /// ★ 这个扫描是 O(光带数)，很贵。所以**降频**执行（默认每秒 10 次），
        ///   而不是每帧都扫。10Hz 对"怪物发现你"这种反馈已经足够快，
        ///   玩家感觉不出延迟，但开销降到 1/6。
        /// </summary>
        private void CaptureSonarSource()
        {
            // 隐藏的怪物不捕捉任何光带
            if (IsHidden()) return;

            float now = Time.timeSinceLevelLoad;
            if (now < m_NextCaptureTime) return;
            m_NextCaptureTime = now + Mathf.Max(0.02f, captureInterval);

            // ★ captured == 1：已经捕捉到玩家，直接追玩家的**最新**坐标，
            //   完全不看声波光带。
            if (IsCaptured)
            {
                ChasePlayerDirectly();
                return;
            }

            var sonar = m_CachedSonar;
            if (sonar == null)
            {
                sonar = m_CachedSonar = FindObjectOfType<SonarSystem>();
                if (sonar == null) return;
            }

            SonarTrailRenderer tr = sonar.trailRenderer;
            if (tr == null) return;

            // ★ 两种光带是**两套完全独立的渲染器**，天然不会混淆：
            //
            //     · 怪物自己的光带  -> MonsterTrailRenderer（trailRenderer 字段）
            //                          **完全不进** SonarTrailRenderer，
            //                          所以 TryCaptureNearest 永远扫不到它。
            //     · 怪物击杀光带    -> SonarTrailRenderer（isKillSonar = true）
            //                          可以被捕捉 -> 触发"方式 2 受惊"。
            //
            //   因此这里不需要任何"排除自己"的过滤。
            if (tr.TryCaptureNearest(transform.position, senseRadius, senseHeight,
                                     out Vector3 source, out float birthTime))
            {

                // ★ 把"墙里的声源点"投影到 NavMesh 上，得到**实际可站立的点**。
                //
                //   石头打在墙上时，声源点就在墙表面甚至墙体内部。
                //   如果直接拿它当目标，NavMesh 会规划到"离它最近的可走点"，
                //   而那个点可能紧贴墙根 —— 怪物走到那里就被墙顶住，
                //   表现为"想走却走不动"，光带不停。
                //
                //   这里先采样一次：找到 source 附近最近的可走点。
                //   如果采样失败（离得太远 / 不在任何 NavMesh 上），
                //   就说明这个声源根本无法到达，直接**忽略**它，
                //   而不是让怪物对着一个永远到不了的点较劲。
                // ★ 先做**可达性检查** —— 两条路径共用，行为才一致。
                //
                //   声源在墙里 / 悬空 / 隔着无法通行的墙时，
                //   NavMesh.SamplePosition 会失败，这时应该**完全忽略**这条光带：
                //     · 普通模式：不设目标（和以前一样）
                //     · keepChasing：也不置 captured（以前漏了这个检查，
                //       导致捕捉到"脚下那圈机关声波"就立刻锁定玩家）
                if (!NavMesh.SamplePosition(source, out NavMeshHit hit, SnapSearchRadius,
                                            NavMesh.AllAreas))
                {
                    // 不可达的声源：不设为目标，也不触发 keepChasing 锁定
                    return;
                }

                // ★ keepChasing：捕捉到**可达的**光带 -> captured 置 1，
                //   之后一直追玩家的最新坐标。
                if (keepChasing)
                {
                    captured = 1;
                    ChasePlayerDirectly();
                    return;
                }

                // 普通模式：把目标设到声源所在的可走点
                // 保留原始 Y 没有意义（目标只看 x/z），直接用可走点
                SetTarget(hit.position, birthTime);
            }
        }

        /// <summary>
        /// 把声源点吸附到 NavMesh 上的搜索半径（米）。
        /// 墙上的点通常距离最近可走点不到 1m，取 3m 足够宽容。
        /// 超过这个距离说明声源在很厚的墙里或悬空，视为不可达。
        /// </summary>
        private const float SnapSearchRadius = 3f;

        /// <summary>
        /// 收到一条光带的两个标签：产生点 + 产生时间。
        ///
        /// 怪物不是走向那个点，而是走向**过该点的竖直垂线**，
        /// 并停在距这条垂线水平距离 <see cref="stopDistance"/> 的地方：
        /// 目标只看 x/z，y 完全不参与（由重力 + 悬浮高度单独负责）。
        ///
        /// ★ 换目标只看**时间**：新光带的产生时间必须**晚于**当前目标才会替换。
        ///   相同或更早的（同一条残留光带被反复扫到）一律忽略 ——
        ///   这天然取代了以前"5cm 死区"的去重作用，也避免了
        ///   怪物在半路被旧残留光带反复改目标导致的抖动。
        /// </summary>
        public void SetTarget(Vector3 point, float birthTime)
        {
            // ★ captured == 1 时目标只能由 ChasePlayerDirectly() 写，
            //   任何其它来源（旧的声源坐标、击杀光带坐标……）一律拒绝。
            if (IsCaptured) return;

            // ★ 同一条光带只处理一次。
            //
            //   原来的守卫是 m_HasTarget && birthTime <= m_TargetBirthTime，
            //   但怪物**停下后 m_HasTarget 会变成 false**，守卫立刻失效 ——
            //   于是同一条残留在感知圆柱里的旧光带会被反复捕捉，
            //   每次都会重算停车点，表现为"明明已经停在终点，却还在反复计算"。
            //
            //   改用一个**独立于 m_HasTarget** 的水位线 m_LastSonarBirthTime：
            //   只要这条光带的产生时间不晚于上次处理过的，就直接忽略。
            if (birthTime <= m_LastSonarBirthTime) return;

            // 产生时间更新的光带 -> 立刻放弃旧目标，朝新的去
            m_LastSonarBirthTime = birthTime;
            m_Target             = point;
            m_TargetBirthTime    = birthTime;
            m_HasTarget          = true;

            // ★ Anger = 1 时收到新坐标：Approaching 置 1（不论原本是几），
            //   并**立即中断**特殊动作，直接赶往新坐标。
            if (anger == 1)
            {
                approaching = 1;
                CancelSpecialAction();
            }

            // ★ 新目标 -> 立刻算一次停车点（避开附近已停下的怪物）
            RecomputeStopPoint();

            // ★ 关键：把"目标变了"这件事告诉移动逻辑，让它强制重算路径。
            //
            //   原来这里只改了 m_Target，但 NavMesh 分支里有这一句：
            //       if (!m_Agent.hasPath && !m_Agent.pathPending) SetDestination(...)
            //   路径还在的时候不会重设目标 —— 于是怪物会**先把旧路径走完**
            //   （到达 A 点），之后才去 B 点。
            //   设了这个标志，下一帧 MoveTowardTarget 就会强制 ResetPath + SetDestination。
            m_TargetDirty = true;
        }


        // ---------------------------------------------------------------- Anger / Approaching
        //
        // Anger（愤怒）：0 = 平静，1 = 听到了同类被杀的声音。
        // Approaching（接近中）：0 = 不在赶往目标，1 = 正在赶往目标。
        // 两者初始都是 0。
        //
        // 捕捉到**击杀光带**时 -> Anger = 1，Approaching = 1。
        // 赶到目标时：
        //   · 若 Approaching 从 1 变成 0 -> 触发**特殊动作**
        //   · 否则就是普通停下
        //
        // 特殊动作（只有 Anger = 1 并且刚赶到击杀声源时才发生）：
        //   1) 随机停留 0.5~1.5 秒
        //   2) 在目标坐标周围 5m 内随机移动 2~4 次
        //      每次移动后随机停留 0.5~1.5 秒
        //      ★ **最后一次移动**才计算与其它怪物的停车间距
        //   3) 全部走完后，像正常停下一样继续发光 + 维持击杀范围 3 秒
        //   4) 3 秒结束 -> 停止发光 / 维持，并把 Anger 清 0
        //
        // 特殊动作全程：持续发光、击杀范围固定用 killRadiusMoving。
        //
        // Anger = 1 时若收到新坐标 -> Approaching 置 1，立即中断特殊动作，赶往新坐标。

        [Header("Anger / Approaching")]

        [Tooltip("【只读】愤怒值：0 = 平静，1 = 听到了同类被杀的声音")]
        [SerializeField] private int anger;

        [Tooltip("【只读】接近中：0 = 不在赶往目标，1 = 正在赶往目标")]
        [SerializeField] private int approaching;

        /// <summary>愤怒值（0 / 1）。</summary>
        public int Anger => anger;

        /// <summary>接近中（0 / 1）。</summary>
        public int Approaching => approaching;

        [Tooltip("特殊动作：每次停顿的时长范围（秒）")]
        public Vector2 specialPauseRange = new Vector2(0.5f, 1.5f);

        [Tooltip("特殊动作：随机移动的次数范围（含两端）")]
        public Vector2Int specialMoveCountRange = new Vector2Int(2, 4);

        [Tooltip("特殊动作：在目标坐标周围多大半径内随机移动（米）")]
        [Min(0.5f)] public float specialMoveRadius = 5f;

        [Tooltip("特殊动作：每次移动的**最小**距离（米）。找不到合格点时此限制会被放弃")]
        [Min(0f)] public float minSpecialMoveDistance = 1f;

        /// <summary>
        /// 受惊期间记下的**击杀声源**（方式 2 吓到它的那条声波）。
        /// 受惊结束时才用它触发 Anger / Approaching。
        /// </summary>
        private Vector3 m_PendingKillSource;
        private float   m_PendingKillBirthTime;
        private bool    m_HasPendingKillSource;

        /// <summary>特殊动作是否正在进行。</summary>
        private bool  m_SpecialActive;

        /// <summary>特殊动作：剩余待执行的移动次数。</summary>
        private int   m_SpecialMovesLeft;

        /// <summary>特殊动作：当前停顿的剩余时间。</summary>
        private float m_SpecialPauseTimer;

        /// <summary>特殊动作：是否处于停顿阶段（否则在朝随机点走）。</summary>
        private bool  m_SpecialPausing;

        /// <summary>特殊动作结束后"仍在发光 + 维持击杀范围"的剩余时间。</summary>
        private float m_AngerTailTimer;

        /// <summary>这一次随机移动是不是**最后一次**（最后一次才算停车间距）。</summary>
        private bool  m_SpecialLastMove;

        /// <summary>捕捉到击杀光带：进入愤怒 + 接近中。</summary>
        public void EnterAnger(Vector3 point, float birthTime)
        {
            anger       = 1;
            approaching = 1;

            // ★ 立即中断正在进行的特殊动作
            CancelSpecialAction();

            // 击杀声源 -> 作为新目标（这里走 SetTarget 的通用路径，
            // 由 Update 里的逻辑决定要不要算间距）
            SetTarget(point, birthTime);
        }

        /// <summary>中断特殊动作（收到新坐标 / 受惊 / 重玩时）。</summary>
        public void CancelSpecialAction()
        {
            m_SpecialActive   = false;
            m_SpecialPausing  = false;
            m_SpecialPauseTimer = 0f;
            m_SpecialMovesLeft  = 0;
        }

        /// <summary>
        /// 赶到目标时调用（替代原来的 OnArrived 直接通知）。
        /// 
        /// 判定：
        ///   · Approaching 从 1 变成 0 -> 触发特殊动作
        ///   · 否则普通停下
        /// </summary>
        private void OnArrivedAtTarget()
        {
            bool wasApproaching = approaching == 1;
            approaching = 0;

            // 清掉移动状态（和原来 OnArrived 一样）
            m_HasTarget   = false;
            m_TargetDirty = false;
            m_StuckTime   = 0f;
            m_StuckTries  = 0;

            if (m_Agent != null && m_Agent.isActiveAndEnabled)
            {
                if (m_Agent.hasPath) m_Agent.ResetPath();
            }

            // ★ Approaching 刚刚从 1 置为 0 -> 触发特殊动作
            if (wasApproaching && anger == 1)
            {
                BeginSpecialAction();
            }
            else
            {
                // 普通停下：通知其它怪物重算停车点
                NotifyStopped();
            }
        }

        /// <summary>开始特殊动作：先随机停留一次，再开始 2~4 次随机移动。</summary>
        private void BeginSpecialAction()
        {
            int lo = Mathf.Min(specialMoveCountRange.x, specialMoveCountRange.y);
            int hi = Mathf.Max(specialMoveCountRange.x, specialMoveCountRange.y);

            m_SpecialActive     = true;
            m_SpecialPausing    = true;
            m_SpecialPauseTimer = RandomPauseDuration();
            m_SpecialMovesLeft  = Random.Range(lo, hi + 1);   // int 版是 [lo, hi)

            // 特殊动作期间持续发光：让下一次发光立刻发生
            m_NextEmitTime = Time.timeSinceLevelLoad;
        }

        private float RandomPauseDuration()
        {
            float a = Mathf.Max(0f, specialPauseRange.x);
            float b = Mathf.Max(a, specialPauseRange.y);
            return Random.Range(a, b);
        }

        /// <summary>
        /// 每帧推进特殊动作：停顿计时、生成随机移动目标、推进移动次数。
        /// 返回 true 表示特殊动作正在接管本帧的移动逻辑。
        /// </summary>
        private bool UpdateSpecialAction(float now)
        {
            // ---- 未在特殊动作：只处理"结束后的 3 秒余韵" ----
            if (!m_SpecialActive)
            {
                if (m_AngerTailTimer > 0f)
                {
                    m_AngerTailTimer -= Time.deltaTime;

                    if (m_AngerTailTimer <= 0f)
                    {
                        m_AngerTailTimer = 0f;
                        anger = 0;      // ★ 余韵结束才清 Anger
                    }
                }

                return false;
            }

            // ---- 停顿阶段 ----
            if (m_SpecialPausing)
            {
                m_SpecialPauseTimer -= Time.deltaTime;

                if (m_SpecialPauseTimer > 0f) return true;    // 还在停

                // 停顿结束
                if (m_SpecialMovesLeft <= 0)
                {
                    EndSpecialAction();
                    return false;
                }

                // 生成下一个随机移动目标
                if (!TryPickSpecialMoveTarget(out Vector3 point))
                {
                    // 找不到合法点（周围全是墙）-> 少走一次，避免卡死
                    m_SpecialMovesLeft--;
                    m_SpecialPauseTimer = RandomPauseDuration();
                    return true;
                }

                m_SpecialLastMove = m_SpecialMovesLeft == 1;

                m_Target          = point;
                m_TargetBirthTime = now;
                m_HasTarget       = true;
                approaching       = 1;      // 赶路中

                // ★ 最后一次移动才计算与其它怪物的停车间距
                if (m_SpecialLastMove) RecomputeStopPoint();
                else                   m_StopPoint = point;   // 其余直接走到随机点

                m_TargetDirty  = true;
                m_SpecialPausing = false;

                return true;
            }

            // ---- 移动阶段：等 MoveTowardTarget 判定到达 ----
            return true;
        }

        /// <summary>特殊动作里的一次移动到达了随机点。</summary>
        private void OnSpecialMoveArrived()
        {
            m_HasTarget   = false;
            m_TargetDirty = false;
            m_StuckTime   = 0f;
            m_StuckTries  = 0;

            if (m_Agent != null && m_Agent.isActiveAndEnabled)
            {
                if (m_Agent.hasPath) m_Agent.ResetPath();
            }

            m_SpecialMovesLeft--;
            m_SpecialPausing    = true;
            m_SpecialPauseTimer = RandomPauseDuration();
            approaching         = 0;

            if (m_SpecialMovesLeft <= 0)
            {
                // 最后一次移动完成 -> 进入余韵（发光 + 维持击杀范围 3 秒）
                EndSpecialAction();
            }
        }

        /// <summary>特殊动作全部结束：进入 3 秒余韵。</summary>
        private void EndSpecialAction()
        {
            m_SpecialActive  = false;
            m_SpecialPausing = false;
            m_SpecialPauseTimer = 0f;
            m_SpecialMovesLeft  = 0;

            approaching = 0;

            // ★ 像正常停下一样：继续发光 + 维持击杀范围 3 秒
            m_MovingGraceTimer = MovingGraceDuration;
            m_AngerTailTimer   = MovingGraceDuration;

            // 停下 -> 通知其它怪物重算停车点
            NotifyStopped();
        }

        /// <summary>
        /// 在"死亡声源附近"随机取一个点作为本次移动的目标。
        /// 用 NavMesh 筛掉到不了的点。
        /// </summary>
        private bool TryPickSpecialMoveTarget(out Vector3 point)
        {
            point = Vector3.zero;

            Vector3 center = m_StopPoint;   // 击杀声源 / 上一次的落点作为圆心

            float radius = Mathf.Max(0.5f, specialMoveRadius);
            float minSep = Mathf.Max(0f, minSpecialMoveDistance);

            // 半径至少要比"最小移动距离"大，否则永远选不出合格点
            if (minSep > radius) radius = minSep;

            Vector3 self = transform.position;

            // 收集所有**满足约束**的候选（半径内 + 可达 + 离自己够远）
            m_SpecialCandidates.Clear();

            for (int attempt = 0; attempt < 24; attempt++)
            {
                float ang = Random.Range(0f, Mathf.PI * 2f);
                float r   = Mathf.Sqrt(Random.value) * radius;

                Vector3 cand = new Vector3(
                    center.x + Mathf.Cos(ang) * r,
                    center.y,
                    center.z + Mathf.Sin(ang) * r);

                if (!TryGetReachablePoint(cand, out Vector3 goal)) continue;

                // ★ 每次至少要走 minSep 米（水平距离）
                float ddx = goal.x - self.x;
                float ddz = goal.z - self.z;
                if (ddx * ddx + ddz * ddz < minSep * minSep) continue;

                m_SpecialCandidates.Add(goal);
            }

            // ---- 有合格点：随机挑一个 ----
            if (m_SpecialCandidates.Count > 0)
            {
                point = m_SpecialCandidates[Random.Range(0, m_SpecialCandidates.Count)];
                return true;
            }

            // ---- 没有满足全部约束的点：退而求其次 ----
            //   需求："除非没有符合所有限制的点位"。
            //   这时忽略"至少 1m"这一条，只要可达就认。
            for (int attempt = 0; attempt < 24; attempt++)
            {
                float ang = Random.Range(0f, Mathf.PI * 2f);
                float r   = Mathf.Sqrt(Random.value) * radius;

                Vector3 cand = new Vector3(
                    center.x + Mathf.Cos(ang) * r,
                    center.y,
                    center.z + Mathf.Sin(ang) * r);

                if (!TryGetReachablePoint(cand, out Vector3 goal)) continue;

                point = goal;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 把一个"想要去的位置"校正成**真实可到达**的点。
        ///
        /// ★ 为什么不能只用 NavMesh.SamplePosition：
        ///     · 它只回答"附近有没有 NavMesh 面"，不回答"能不能走过去"。
        ///       隔着墙的另一侧也可能采样成功 —— 于是怪物会朝墙猛撞。
        ///     · 它还会把**墙里 / 墙外**的点吸附到最近的 NavMesh 边上，
        ///       即使用户看到的是"房间里"，实际被吸到墙背面去了。
        ///
        ///   所以这里做三道校验：
        ///     1) SamplePosition：候选点附近必须有 NavMesh 面
        ///     2) NavMesh.CalculatePath：从怪物当前位置到目标**必须完整可走**
        ///        （status == PathComplete，且不被截断）
        ///     3) 直线视野：从怪物到目标之间不能被墙挡住
        ///        （目标点就在墙里 / 墙后面时，这条会直接否掉）
        ///
        ///   三道都过才返回 true。
        /// </summary>
        private bool TryGetReachablePoint(Vector3 want, out Vector3 goal)
        {
            goal = Vector3.zero;

            // ---- 1) 附近必须有 NavMesh ----
            //   采样半径给得**小**（0.6m）：宁可找不到，也不要把墙里的点
            //   吸附到墙外的可走面上 —— 那样看起来"能到"，实际在墙背面。
            if (!NavMesh.SamplePosition(want, out NavMeshHit hit, 0.6f, NavMesh.AllAreas))
                return false;

            Vector3 cand = hit.position;

            // 采样把点挪得太远 -> 说明原始位置根本不在可走面上（多半在墙里）
            float dx = cand.x - want.x;
            float dz = cand.z - want.z;
            if (dx * dx + dz * dz > 0.36f) return false;   // > 0.6m

            // ---- 2) 直线视野：中间不能有墙 ----
            Vector3 from = transform.position;
            Vector3 dir  = cand - from;
            dir.y = 0f;
            float dist = dir.magnitude;

            if (dist > 0.01f)
            {
                dir /= dist;

                // 抬高一点再射，避免贴着地面的射线打到地板本身
                Vector3 probe = new Vector3(from.x, Mathf.Max(from.y, cand.y) + 0.5f, from.z);

                if (Physics.Raycast(probe, dir, out RaycastHit block, dist - 0.2f,
                                    raycastMask, QueryTriggerInteraction.Ignore))
                    return false;   // 中间被墙挡住
            }

            // ---- 3) NavMesh 路径必须完整可达 ----
            if (!UseNavMesh())
            {
                // 没有 NavMesh（退化模式）：只要视野通就认
                goal = cand;
                return true;
            }

            var path = new NavMeshPath();

            Vector3 start = transform.position;
            if (NavMesh.SamplePosition(start, out NavMeshHit startHit, 4f, NavMesh.AllAreas))
                start = startHit.position;   // agent 需要从 NavMesh 上出发

            if (!NavMesh.CalculatePath(start, cand, NavMesh.AllAreas, path))
                return false;

            // 路径没算完（被墙完全隔开时会变成 PathPartial 或 PathInvalid）
            if (path.status != NavMeshPathStatus.PathComplete) return false;

            goal = cand;
            return true;
        }

        /// <summary>特殊动作 / 余韵期间是否视为"持续发光 + 维持击杀范围"。</summary>
        private bool InAngerActivity => m_SpecialActive || m_AngerTailTimer > 0f;

        // ---------------------------------------------------------------- 停车点避让
        //
        // ★ 需求：怪物之间不碰撞，所以它们可以重叠；
        //   但我们不希望它们**停在同一个位置**。
        //
        //   规则：
        //     每只怪物都有"目标坐标点"m_Target（声源位置）。
        //     它真正要停下的位置 m_StopPoint 必须满足：
        //       · 距 m_Target 不超过 stopDistance
        //       · 与**附近所有静止怪物**的球心水平距离 >= minStopSeparation（默认 0.7m）
        //
        //     如果找不到这样的点（周围被别的怪占满了），
        //     就取"离 m_Target 最近、且也满足 0.7m 间距"的点 —— 也就是放弃
        //     "不超过 stopDistance" 这一条，宁可停远一点也不和别人重合。
        //
        //     所有计算**只涉及 x / z**。


        // ---------------------------------------------------------------- 持续追逐

        [Header("持续追逐")]
        [Tooltip("勾上 = 一旦捕捉到**任何**声波光带，captured 就变成 1，\n" +
                 "之后怪物会持续锁定并追逐玩家的最新坐标，不再理会其它声波光带。\n\n" +
                 "不勾 = 正常行为（只追声波来源）。")]
        public bool keepChasing = false;

        [Tooltip("【只读】是否已经捕捉到玩家。\n\n" +
                 "0 = 未捕捉（正常追声波来源）\n" +
                 "1 = 已捕捉（持续追逐玩家的最新位置，不理其它光带）\n\n" +
                 "★ 只有在 keepChasing 勾着、且捕捉到光带时才会变成 1。\n" +
                 "  重新开始会归 0；存档 / 读档会保存与还原。")]
        [SerializeField, Range(0, 1)] private int captured;

        /// <summary>捕捉值：0 = 未捕捉，1 = 已捕捉（持续追逐玩家）。</summary>
        public int Captured => captured;

        /// <summary>是否已捕捉到玩家。</summary>
        public bool IsCaptured => captured != 0;

        /// <summary>设置 captured（给存档读档用）。</summary>
        public void SetCaptured(int value)
        {
            captured = value != 0 ? 1 : 0;
        }

        [Tooltip("怪物停下时，与附近**静止**怪物的球心至少保持多远（米，仅水平距离）")]
        [Min(0f)] public float minStopSeparation = 0.7f;

        /// <summary>本怪物真正要停下的位置（世界坐标）。</summary>
        private Vector3 m_StopPoint;

        /// <summary>本怪物是否处于静止状态（没有目标）。</summary>
        public bool IsStopped => !m_HasTarget;

        /// <summary>
        /// 刚被 TriggerVolume 显现出来的那一帧。
        ///
        /// ★ 用途：这个"出现"动作本身不应该被当成
        ///   从移动中停下 —— 所以
        ///     · 不播 Searching 音效
        ///     · 不触发 3 秒的发光/击杀范围余韵
        ///   音效与余韵逻辑会检查这个标志，消费一次后自动清零。
        /// </summary>
        public bool ConsumeJustAppeared()
        {
            if (!m_JustAppeared) return false;
            m_JustAppeared = false;
            return true;
        }

        private bool m_JustAppeared;
        private bool m_SuppressAppearGrace;

        /// <summary>暂存其它静止怪物位置的缓冲（避免每帧分配内存）。</summary>
        private readonly System.Collections.Generic.List<Vector3> m_SepBuffer
            = new System.Collections.Generic.List<Vector3>(16);

        /// <summary>特殊动作随机移动的合格候选点。</summary>
        private readonly System.Collections.Generic.List<Vector3> m_SpecialCandidates
            = new System.Collections.Generic.List<Vector3>(32);

        /// <summary>合法落点候选（落在 stopDistance 内的那一档）。</summary>
        private readonly System.Collections.Generic.List<Vector3> m_CandInRange
            = new System.Collections.Generic.List<Vector3>(64);

        /// <summary>合法落点候选（不限距离，兜底用）。</summary>
        private readonly System.Collections.Generic.List<Vector3> m_CandAnywhere
            = new System.Collections.Generic.List<Vector3>(64);

        /// <summary>
        /// 重新计算停车点 m_StopPoint。
        ///
        /// 思路：
        ///   1) 收集"其它静止怪物"的水平位置
        ///   2) 目标点本身若已满足间距 -> 直接停在目标点
        ///   3) 否则在目标点周围**由近到远**扫圆环，找第一个既满足间距、
        ///      又落在 stopDistance 内的点 -> 这就是"圈内离目标最近"的
        ///   4) 如果整圈都找不到，就用"满足间距的点里离目标最近的"
        ///      （可能超出 stopDistance，但绝不与别的怪重合）
        /// </summary>
        public void RecomputeStopPoint()
        {
            // ★ captured == 1 时**不许**重算停车点。
            //   停车点必须永远等于玩家当前位置，
            //   由 ChasePlayerDirectly() 每帧直接写入。
            if (IsCaptured) return;

            if (!m_HasTarget) return;

            Vector3 target = m_Target;
            float   sep    = Mathf.Max(0f, minStopSeparation);
            float   stopAt = Mathf.Max(0.05f, stopDistance);

            // 没有间距要求就停在目标点
            if (sep <= 0f)
            {
                m_StopPoint = target;
                return;
            }

            // ---- 收集其它静止怪物的位置 ----
            m_SepBuffer.Clear();

            var monsters = Object.FindObjectsByType<PuzzleMonster>(FindObjectsSortMode.None);

            for (int i = 0; i < monsters.Length; i++)
            {
                PuzzleMonster o = monsters[i];
                if (o == null || o == this) continue;
                if (!o.isActiveAndEnabled) continue;
                if (!o.IsStopped) continue;          // 只看静止的

                m_SepBuffer.Add(o.transform.position);
            }

            // 附近没有静止怪物 -> 停在目标点
            if (m_SepBuffer.Count == 0)
            {
                m_StopPoint = target;
                return;
            }

            // ---- 先看目标点本身是否就满足间距 ----
            //   （满足的话它本身就是"合法点"之一，会被一起纳入随机池）
            bool targetIsValid = IsFarEnoughFromAll(target, sep);

            // ---- 收集**所有**合法候选点 ----
            //
            //   ★ 需求：落点应当是"能满足要求的所有位置中的随机一处"，
            //     所以这里先扫出全部合法点，再从中等概率抽一个。
            //
            //   两档优先级仍保留：
            //     · 先找落在 stopDistance 内的合法点
            //     · 一个都没有，才退而求其次，用"间距够但超出 stopDistance"的点
            //     每一档内部**随机**。

            m_CandInRange.Clear();
            m_CandAnywhere.Clear();

            if (targetIsValid) m_CandInRange.Add(target);

            float maxR = Mathf.Max(stopAt, sep) + sep * 3f;

            const int angleSteps = 24;                                  // 每圈 24 个方向（15°）
            int ringSteps = Mathf.CeilToInt(maxR / Mathf.Max(0.05f, sep * 0.5f));
            ringSteps = Mathf.Clamp(ringSteps, 1, 32);

            for (int ring = 1; ring <= ringSteps; ring++)
            {
                float r = maxR * ring / ringSteps;

                for (int a = 0; a < angleSteps; a++)
                {
                    float ang = Mathf.PI * 2f * a / angleSteps;

                    Vector3 cand = new Vector3(
                        target.x + Mathf.Cos(ang) * r,
                        target.y,
                        target.z + Mathf.Sin(ang) * r);

                    if (!IsFarEnoughFromAll(cand, sep)) continue;

                    // 落在 stopDistance 内 -> 第一档
                    if (r <= stopAt) m_CandInRange.Add(cand);

                    // 不管远近都记一份，供"第一档为空"时兜底
                    m_CandAnywhere.Add(cand);
                }
            }

            // ---- 从合法点里随机抽一个 ----
            if (m_CandInRange.Count > 0)
            {
                m_StopPoint = m_CandInRange[Random.Range(0, m_CandInRange.Count)];
            }
            else if (m_CandAnywhere.Count > 0)
            {
                // 兜底：stopDistance 内没有合法点 -> 宁可停远一点，也不和别人重合
                m_StopPoint = m_CandAnywhere[Random.Range(0, m_CandAnywhere.Count)];
            }
            else
            {
                m_StopPoint = target;
            }
        }


        /// <summary>
        /// 到达停车点：彻底停下，并**通知其它正在移动的怪物重新算停车点**。
        /// </summary>

        /// <summary>
        /// captured == 1 时的追逐：**只**追玩家的当前坐标。
        ///
        /// ★ 每帧刷新，玩家跑到哪它就跟到哪。
        /// ★ 同时写 m_Target 和 m_StopPoint —— 真正驱动移动的是 m_StopPoint，
        ///   只写 m_Target 的话怪物会继续走 m_StopPoint 里的旧点。
        /// ★ 不走 SetTarget 的"产生时间"判断 —— 那样会被旧时间戳挡住。
        /// ★ 不走 RecomputeStopPoint 的"停车间距"计算 —— 那样会故意偏离玩家。
        /// </summary>
        private void ChasePlayerDirectly()
        {
            UpdatePlayerRef();
            if (m_Player == null) return;

            // 用玩家脚下的地面点，保证目标落在 NavMesh 上
            Vector3 p = m_Player.transform.position;

            // 采样失败（玩家站在墙里 / 悬空）时不要再往前走，
            // 保持上一个有效目标即可。
            if (!NavMesh.SamplePosition(p, out NavMeshHit hit, SnapSearchRadius, NavMesh.AllAreas))
                return;

            p = hit.position;

            float now = Time.timeSinceLevelLoad;

            // ★ 目标和停车点**都**设成玩家当前位置。
            //   MoveTowardTarget 用的是 m_StopPoint，不是 m_Target。
            m_Target             = p;
            m_StopPoint          = p;
            m_TargetBirthTime    = now;
            m_LastSonarBirthTime = now;
            m_HasTarget          = true;
            m_TargetDirty        = true;

            // ★ 直接给 NavMesh 下"去玩家那里"的指令，
            //   绕过所有"停车点 / 间距 / 绕路"逻辑。
            if (m_Agent != null && m_Agent.isActiveAndEnabled && m_Agent.isOnNavMesh)
            {
                m_Agent.SetDestination(p);
            }
        }
        /// <summary>某个点是否与所有缓冲里的静止怪物球心保持 sep 以上的**水平**距离。</summary>
        private bool IsFarEnoughFromAll(Vector3 p, float sep)
        {
            float sep2 = sep * sep;

            for (int i = 0; i < m_SepBuffer.Count; i++)
            {
                Vector3 q = m_SepBuffer[i];
                float dx = p.x - q.x;
                float dz = p.z - q.z;

                if (dx * dx + dz * dz < sep2) return false;
            }

            return true;
        }

        /// <summary>
        /// 通知**所有其它正在移动的怪物**：有一只怪物停下了，
        /// 大家重新算一遍停车点（避开这只刚停下的）。
        /// </summary>
        private void NotifyStopped()
        {
            var monsters = Object.FindObjectsByType<PuzzleMonster>(FindObjectsSortMode.None);

            for (int i = 0; i < monsters.Length; i++)
            {
                PuzzleMonster o = monsters[i];
                if (o == null || o == this) continue;
                if (!o.isActiveAndEnabled) continue;
                if (o.IsStopped) continue;      // 只通知还在动的

                o.RecomputeStopPoint();
                o.m_TargetDirty = true;         // 停车点变了 -> 立刻重算路径
            }
        }

        /// <summary>目标变更标志：下一帧强制重算 NavMesh 路径。</summary>
        private bool m_TargetDirty;

        /// <summary>
        /// 已经处理过的**最后一条光带的产生时间**。
        ///
        /// ★ 独立于 m_HasTarget：怪物停下后 m_HasTarget 会被清掉，
        ///   但这条水位线要保留，否则同一条旧光带会被反复处理。
        /// </summary>
        private float m_LastSonarBirthTime = float.NegativeInfinity;

        // ---------------------------------------------------------------- 移动

        /// <summary>
        /// 水平朝目标移动 + 垂直受重力。
        ///
        /// 垂直方向：始终施加重力，落到地面上之后再**悬浮**到 floatHeight。
        /// 所以：
        ///   · 初始摆得太高 -> 会自然下落
        ///   · 掉到地面后 -> 抬升到离地 floatHeight，靠重力维持（不是硬编码位置）
        ///   · 地面起伏 / 有台阶 -> 也会跟着调整高度
        /// </summary>
        private void MoveTowardTarget()
        {
            Vector3 horizontal = Vector3.zero;

            // ★ 受惊状态下不可移动 —— 但仍要走下面的重力 / 悬浮，
            //   否则会卡在半空。只跳过水平寻路这一段。
            if (m_HasTarget && canMove && !IsFrightened)
            {
                // ★ 到达判定：**只看 X / Z**，完全忽略 Y。
                //
                //   声音的产生点通常在墙面、地面或半空，它的 Y 和怪物悬浮高度
                //   （离地 floatHeight）永远对不上。所以 Y 绝不能参与这个判断，
                //   否则水平上已经贴到墙了，却因为高度差几米而永远判定不到达。
                Vector3 flatMe  = new Vector3(transform.position.x, 0f, transform.position.z);

                // ★ captured == 1：直接朝玩家当前位置走，不读 m_StopPoint。
                //   这样无论别处怎么改 m_StopPoint，追逐方向都只由玩家决定。
                //   ★ 地牢模式例外：移动由 DungeonEnemy 接管（随机巡逻 / 射击位），
                //     必须读 m_StopPoint，不能直追玩家 —— 否则接管失效。
                Vector3 flatGo  = IsCaptured && m_Player != null && !DungeonMoveControlled
                                ? new Vector3(m_Player.transform.position.x, 0f, m_Player.transform.position.z)
                                : new Vector3(m_StopPoint.x, 0f, m_StopPoint.z);

                float dist = Vector3.Distance(flatMe, flatGo);

                // ★ 到达判定用的是"停车点"到自身的距离，不再直接用 stopDistance ——
                //   因为停车点可能被推到 stopDistance 以外（周围被占满时），
                //   所以这里用一个很小的到达半径即可。
                const float ArriveRadius = 0.15f;

                if (dist <= ArriveRadius)
                {
                    // ---- 到达 ----
                    //
                    //   ★ 分两种：
                    //     · 特殊动作里的随机移动 -> OnSpecialMoveArrived
                    //     · 正常赶往声源        -> OnArrivedAtTarget
                    if (m_SpecialActive && !m_SpecialPausing)
                        OnSpecialMoveArrived();
                    else
                        OnArrivedAtTarget();
                }
                else if (UseNavMesh())
                {
                    // ★ NavMesh 寻路：agent 半托管，我们只拿它的期望速度推水平移动。
                    //   updatePosition=false 时 agent 不会自动读 transform，
                    //   每帧把 nextPosition 同步成真实位置，路径才能跟着实体走。
                    //   同步时把 y 贴到 NavMesh 表面 —— 实体悬浮 2m，若把悬浮高度
                    //   直接给 agent，它可能把自己判成 off-NavMesh，寻路就失效了。
                    Vector3 np = transform.position;
                    if (NavMesh.SamplePosition(np, out NavMeshHit surf, 4f, NavMesh.AllAreas))
                        np.y = surf.position.y;
                    m_Agent.nextPosition = np;

                    // ★ 目标变了（收到了更晚的光带）就**立刻重算路径**，
                    //   而不是等旧路径走完。否则会出现"先到 A 才转头去 B"。
                    if (m_TargetDirty)
                    {
                        m_TargetDirty = false;
                        if (m_Agent.hasPath) m_Agent.ResetPath();
                        m_Agent.SetDestination(m_StopPoint);
                    }
                    else if (!m_Agent.hasPath && !m_Agent.pathPending)
                    {
                        m_Agent.SetDestination(m_StopPoint);
                    }

                    horizontal = m_Agent.velocity * Time.deltaTime;

                    // 不要一步跨过停车点（否则会在边界来回抖）。
                    // 到停车点的剩余距离就是 room。
                    float mlen = horizontal.magnitude;
                    float room = Mathf.Max(0f, dist);

                    if (mlen > room && mlen > 1e-6f)
                        horizontal = horizontal * (room / mlen);
                }
                else
                {
                    // 退化（没有可用 NavMesh）：直线朝停车点走，撞墙贴墙
                    Vector3 dir = (flatGo - flatMe).normalized;

                    // 不要一步跨过停止线（否则会在边界来回抖）
                    float step = Mathf.Min(moveSpeed * Time.deltaTime, Mathf.Max(0f, dist));
                    horizontal = dir * step;
                }
            }

            // ---- 垂直：重力 + 悬浮高度 ----
            float vertical = ApplyGravityAndHover();

            Vector3 move = horizontal + Vector3.up * (vertical * Time.deltaTime);

            // ★ 记录这一帧**实际移动了多远**，而不是"期望速度是否非零"。
            //
            //   之前是 `m_IsMoving = horizontal.sqrMagnitude > 1e-8f` ——
            //   只看意图。撞墙时 horizontal 非零、但 CharacterController.Move
            //   被墙挡住，坐标一动不动，于是状态一直显示"在移动"：
            //     · 击杀范围一直用"移动中"的大半径
            //     · 看起来像卡住却还在走
            Vector3 before = transform.position;

            if (m_Controller != null && m_Controller.enabled)
            {
                m_Controller.Move(move);
            }
            else
            {
                transform.position += move;   // 没有 CharacterController 时退化成直接位移
            }

            // 只看水平方向的实际位移
            Vector3 delta = transform.position - before;
            delta.y = 0f;

            float actualMove = delta.magnitude;
            m_IsMoving = actualMove > 1e-4f;

            // 记录本帧的水平位移方向 —— 前方坡面探针要用它
            m_HorizontalVelocityLastFrame = delta / Mathf.Max(Time.deltaTime, 1e-4f);

            // ★ 停下后**继续保持"移动中"状态 3 秒**（需求）：
            //   这 3 秒里仍然持续发光、击杀范围仍是 killRadiusMoving。
            //   一旦再次真的移动，计时重置。
            if (m_IsMoving)
            {
                m_MovingGraceTimer = MovingGraceDuration;
            }
            else if (m_MovingGraceTimer > 0f)
            {
                m_MovingGraceTimer -= Time.deltaTime;
            }

            // "是否算作移动中" —— 真实移动，或停下的宽限期内
            bool movingOrGrace = m_IsMoving || m_MovingGraceTimer > 0f;

            // ---- 朝向：只在**真的动了**的时候更新 ----
            //
            // ★ 跳跃式：直接 snap 到移动方向，没有任何插值 / 转身动画。
            //   （NavMeshAgent.updateRotation = false，它不会插手）
            //
            // ★ 静止时**不更新** —— 停下后朝向保持最后一次移动的方向。
            //   用 actualMove（实际位移）而不是 horizontal（移动意图）判断：
            //   被墙卡住时意图非零但没挪动，这时不该转向。
            if (m_IsMoving)
            {
                Vector3 flatDelta = new Vector3(delta.x, 0f, delta.z);

                if (flatDelta.sqrMagnitude > 1e-8f)
                {
                    transform.rotation = Quaternion.LookRotation(flatDelta.normalized, Vector3.up);
                }
            }

            // ★ 卡住检测：想走但实际没挪动
            //   连续 N 秒推不动，就认为卡在墙里/拐角，让 agent 重新规划路径。
            bool wantMove = horizontal.sqrMagnitude > 1e-8f;

            // 记录"这一帧有没有移动意图"，供发光判断使用：
            // 被墙卡住时实际位移为 0，但仍算"想动"，要继续发光暴露异常。
            m_WantMove = wantMove;

            if (wantMove && actualMove < 1e-4f)
            {
                m_StuckTime += Time.deltaTime;

                // ---- 第一级：短暂卡住 -> 重算路径 ----
                if (m_StuckTime > StuckTimeout)
                {
                    OnStuck();
                    m_StuckTime = 0f;
                    m_StuckTries++;
                }

                // ---- 第二级：反复卡住 -> 放弃这个目标 ----
                //
                //   重算路径几次还是走不动，说明目标本身就到不了
                //   （典型情况：声源在墙里、或者贴着怪物进不去的角落）。
                //
                //   这时候继续尝试只会让怪物永久"想动却不动"：
                //     · 光带一直发（看起来像异常）
                //     · agent 每秒被 ResetPath 好几次
                //     · 退出引擎时这些未完成的任务可能就是卡死的原因
                //
                //   所以直接判定"已到达"并清空目标，让怪物停下来。
                //   玩家下次发声时它会被重新激活。
                if (m_StuckTries >= StuckTriesBeforeGiveUp && m_HasTarget)
                {
                    // ★ 这也是一次"停止运动"，同样要通知其它怪物重算停车点
                    if (m_SpecialActive && !m_SpecialPausing)
                        OnSpecialMoveArrived();
                    else
                        OnArrivedAtTarget();
                }
            }
            else
            {
                m_StuckTime = 0f;
                m_StuckTries = 0;
            }
        }

        /// <summary>连续卡住多久后重算一次路径。</summary>
        private const float StuckTimeout = 0.6f;

        /// <summary>重算路径多少次仍走不动，就放弃这个目标。</summary>
        private const int StuckTriesBeforeGiveUp = 3;

        private float m_StuckTime;
        private int   m_StuckTries;

        /// <summary>
        /// 卡住时的脱困：
        ///   1. 重置 agent 路径，让它重新规划
        ///   2. 若仍然不行，把目标稍微偏一点，避免一直对着同一条撞不动的线
        /// </summary>
        /// <summary>
        /// 卡住时的脱困。
        ///
        /// ★ 只重算路径，**绝不改动目标点**。
        ///
        ///   旧实现会 `m_Target += side * ≥1m` 把目标推到侧面绕墙角。
        ///   后果是灾难性的：
        ///     · 怪物本来已经到达目标（dist ≤ stopDistance），被一推就又能"走"了
        ///     · 走两步再次卡住 -> 又推一次 -> 无限循环
        ///     · 表现就是"明明到了却还在寻路"，而且永远停不下来
        ///
        ///   正确做法：卡住只说明**当前路径**走不通，让 agent 重新规划即可。
        ///   目标位置是声源位置，不该被引擎修改。
        /// </summary>
        private void OnStuck()
        {
            if (m_Agent != null && m_Agent.isActiveAndEnabled)
            {
                if (m_Agent.hasPath) m_Agent.ResetPath();

                // 强制下一帧重算路径
                m_TargetDirty = true;
            }
        }

        /// <summary>agent 是否可用于寻路：有 agent、启用中、且当前实体附着在 NavMesh 上。
        /// isOnNavMesh 会随 nextPosition 变化，每帧现查、发现变化就同步缓存。</summary>
        private bool UseNavMesh()
        {
            if (m_Agent == null || !m_Agent.enabled) return false;

            if (m_Agent.isOnNavMesh != m_NavOK) m_NavOK = m_Agent.isOnNavMesh;
            return m_NavOK;
        }

        /// <summary>
        /// 计算当前帧的垂直速度。
        ///
        /// 逻辑：
        ///   1. 永远先加重力（所以从高处会掉下来）
        ///   2. 向下打一条射线找到地面
        ///   3. 如果已经落到"离地 floatHeight"或更低，就把 `m_Suspended` 打开
        ///   4. 悬浮状态中，目标高度 = 地面 + floatHeight，
        ///      用弹簧式速度把它拉回去（比硬设位置自然，也不会和 CharacterController 打架）
        /// </summary>
        private float ApplyGravityAndHover()
        {
            float dt = Time.deltaTime;

            // 1) 重力
            m_VerticalVelocity += Physics.gravity.y * dt;

            // 2) 找地面
            //
            //   ★ 除了"脚下"，还要看"前方"：
            //     怪物是悬浮的，脚下的射线要等它飘到坡的正上方才会打到坡面。
            //     在那之前它已经水平推进到坡面前了，射线还是打到低处的地板，
            //     于是球心停在低处 —— 表现为"撞在坡上不动"。
            //
            //     所以这里同时探测"+移动方向的落脚点"，取更高的那个作为地面高度。
            //     这样怪物靠近坡时会**提前抬起**，平滑地顺着坡爬上去。
            float groundY;
            bool  hasGround = TryGetGroundAhead(out groundY);

            if (hasGround)
            {
                float targetY = groundY + floatHeight;
                float y       = transform.position.y;

                // 3) 已经掉到目标高度附近（或更低）-> 进入悬浮
                if (y <= targetY + 0.05f) m_Suspended = true;

                // 4) 悬浮：朝目标高度靠拢
                if (m_Suspended)
                {
                    float diff = targetY - y;

                    // 位置差 -> 期望速度；再缓一点，避免抖动
                    float wantVel = Mathf.Clamp(diff * hoverStiffness, -hoverMaxSpeed, hoverMaxSpeed);

                    // 平滑趋近期望速度
                    m_VerticalVelocity = Mathf.Lerp(m_VerticalVelocity, wantVel,
                                                    1f - Mathf.Exp(-hoverDamping * dt));
                }
            }

            return m_VerticalVelocity;
        }

        /// <summary>
        /// 找"接下来要站的地面高度"：
        ///   · 先看脚正下方
        ///   · 再看**移动方向前方一点点**（一个身体半径的距离）
        /// 取两者中**更高**的那个 —— 这样上坡时球心会提前抬起来。
        ///
        /// ★ 这解决了"怪物悬浮着、够不到坡面，于是原地顶在坡前"的问题。
        /// </summary>
        private bool TryGetGroundAhead(out float groundY)
        {
            groundY = 0f;

            bool hasHere = TryGetGroundY(out float here);

            // ---- 前方探针 ----
            bool hasAhead = false;
            float aheadY = 0f;

            Vector3 vel = m_HorizontalVelocityLastFrame;
            vel.y = 0f;

            if (vel.sqrMagnitude > 1e-6f)
            {
                Vector3 dir = vel.normalized;

                // 探一个身体半径那么远：足够提前预判坡面，又不会悬空到别的平台上
                Vector3 probe = transform.position + dir * (bodyRadius * 1.2f);

                hasAhead = ProbeGroundAt(probe, out aheadY);
            }

            if (hasHere && hasAhead)
            {
                groundY = Mathf.Max(here, aheadY);
                return true;
            }

            if (hasHere)  { groundY = here;   return true; }
            if (hasAhead) { groundY = aheadY; return true; }

            return false;
        }

        /// <summary>在指定的 xz 位置上向下探地面高度。</summary>
        private bool ProbeGroundAt(Vector3 worldPos, out float groundY)
        {
            groundY = 0f;

            Vector3 from = new Vector3(worldPos.x, transform.position.y - bodyRadius - 0.01f, worldPos.z);
            float   maxDist = Mathf.Max(3f, groundProbeDistance + floatHeight);

            if (Physics.Raycast(from, Vector3.down, out RaycastHit hit, maxDist,
                                groundMask, QueryTriggerInteraction.Ignore))
            {
                groundY = hit.point.y;
                return true;
            }

            return false;
        }

        /// <summary>上一帧的水平位移方向（用于前方探针）。</summary>
        private Vector3 m_HorizontalVelocityLastFrame;

        /// <summary>向下投射找地面高度。找不到返回 false。</summary>
        private bool TryGetGroundY(out float groundY)
        {
            groundY = 0f;

            // 从身体下方一点点开始，往下探足够远（初始可能摆得很高，这里用大距离保证能找到）
            Vector3 from = transform.position - Vector3.up * (bodyRadius + 0.01f);
            float   maxDist = Mathf.Max(3f, groundProbeDistance + floatHeight);

            if (Physics.Raycast(from, Vector3.down, out RaycastHit hit, maxDist,
                                groundMask, QueryTriggerInteraction.Ignore))
            {
                groundY = hit.point.y;
                return true;
            }

            return false;
        }

        // ---------------------------------------------------------------- 光带

        /// <summary>在感知圆柱里随机发一批光带。</summary>
        private void EmitBurst()
        {
            if (trailRenderer == null) return;

            for (int i = 0; i < raysPerBurst; i++)
            {
                // 随机方向，但不要太贴近正上/正下（那样的光带在地上几乎看不到）
                Vector3 dir = Random.onUnitSphere;
                if (Mathf.Abs(dir.y) > 0.92f) dir.y *= 0.3f;
                dir.Normalize();

                Vector3 origin = transform.position + dir * headRadius;

                // ★ 这条光带走 MonsterTrailRenderer（怪物专用渲染器），
                //   **不**进 SonarTrailRenderer，所以怪物本来就不可能捕捉到它 ——
                //   不需要任何标记。
                int idx = trailRenderer.AddTrail(origin, dir, trailWidth, trailColor, trailInitialAlpha);
                if (idx < 0) continue;

                m_Beams.Add(new LiveBeam
                {
                    index   = idx,
                    dir     = dir,
                    length  = 0f,
                    stopped = false,
                });
            }
        }

        /// <summary>
        /// 每帧推进光带波前，并检测撞墙。
        /// 撞到就 StopHead（钉住长度，开始残留淡出）。
        /// </summary>
        private void StepBeams(float now)
        {
            if (trailRenderer == null) return;

            float speed = Mathf.Max(0.1f, moveSpeed) * 4f;   // 光带推进比怪物快，才像"扫描"

            for (int i = m_Beams.Count - 1; i >= 0; i--)
            {
                LiveBeam b = m_Beams[i];

                if (b.stopped)
                {
                    // 已停：交给渲染器按残留时间自行淘汰
                    if (trailRenderer.TrailCount == 0 || b.index >= trailRenderer.TrailCount)
                        m_Beams.RemoveAt(i);
                    continue;
                }

                float prevLen = b.length;
                float newLen  = prevLen + speed * Time.deltaTime;

                // 撞墙检测：从上一帧末端往前探
                Vector3 from = transform.position + b.dir * (headRadius + prevLen);
                float   delta = newLen - prevLen;

                float finalLen = newLen;
                if (delta > 0.0001f)
                {
                    if (Physics.Raycast(from, b.dir, out RaycastHit hit, delta,
                                        raycastMask, QueryTriggerInteraction.Ignore))
                    {
                        finalLen = prevLen + hit.distance;
                        b.stopped = true;
                        b.length  = finalLen;
                        trailRenderer.StopHead(b.index, finalLen);
                        m_Beams[i] = b;
                        continue;
                    }
                }

                // 超过最大长度也停
                //
                // ★ 受惊状态下用较短的 trailMaxLengthFrightened，
                //   所以光带会明显变短 —— 视觉上一眼能看出"这只怪物不一样了"。
                float maxLen = IsFrightened ? trailMaxLengthFrightened : trailMaxLength;

                if (finalLen >= maxLen)
                {
                    finalLen = maxLen;
                    b.stopped = true;
                    b.length  = finalLen;
                    trailRenderer.StopHead(b.index, finalLen);
                    m_Beams[i] = b;
                    continue;
                }

                b.length = finalLen;
                m_Beams[i] = b;
                trailRenderer.SetLength(b.index, finalLen);
            }
        }

        // ---------------------------------------------------------------- 击杀

        /// <summary>击杀玩家：在脚下放一次光带 + 跳"你死了"。</summary>
        /// <summary>
        /// 播放「怪物击杀玩家」的那套演出：**大量光带 + 击杀音效**。
        ///
        /// 抽出来是因为有两个地方要用：
        ///   1) Kill() —— 实体接触到玩家
        ///   2) 受惊死亡（方式 1）—— 3 秒倒计时结束时
        ///
        /// 只做演出，**不改游戏状态**（不调 NotifyPlayerDied）。
        /// </summary>
        public void PlayKillEffects()
        {
            UpdatePlayerRef();

            var sonar = m_CachedSonar;
            if (sonar == null)
            {
                sonar = FindObjectOfType<SonarSystem>();
                m_CachedSonar = sonar;
            }

            if (sonar != null && m_Player != null)
            {
                Vector3 facing = m_Player.transform.forward;
                facing.y = 0f;
                if (facing.sqrMagnitude < 1e-6f) facing = Vector3.forward;
                facing.Normalize();

                sonar.Emit(m_Player.FootPosition, killSonar.ToEmitParams(facing));
            }

            AudioSystem.Play(AudioSystem.Cue.Death);
        }

        private void Kill()
        {
            if (m_Killed) return;
            m_Killed = true;

            PlayKillEffects();
            GameUI.NotifyPlayerDied();
        }

        /// <summary>
        /// 被匕首的光刃移除。
        ///
        /// 与 <see cref="Kill"/>（怪物杀玩家）方向相反：这里是玩家杀怪物。
        ///
        /// 处理：
        ///   · 播一次击杀音效
        ///   · 整个物体 SetActive(false) —— 不 Destroy，
        ///     这样重玩 / 读档时 ResetMonster() 还能把它复位并重新启用
        /// </summary>
        public void RemoveByBlade()
        {
            if (m_RemovedByBlade) return;
            m_RemovedByBlade = true;

            AudioSystem.Play(AudioSystem.Cue.Death);

            // ★ 消散声波：在怪物**正下方**的地面高度上炸一次。
            //
            //   · 产生位置：怪物球心正下方，高度 = 脚下地面
            //     （和玩家脚步 / 触发区声波同一个高度口径）
            //   · **可以被其它怪物捕捉** —— 这是有意的：
            //     一个怪物被杀就是一次响亮的动静，本来就该引来同伴。
            //     （对比：OperateObjects 触发区的声波是 ignoreMonster = true）
            if (emitSonarOnRemove)
            {
                EmitRemoveSonar();
            }

            gameObject.SetActive(false);
        }

        /// <summary>被移除时是否在脚下炸一次声波。</summary>
        [Header("被移除时")]
        [Tooltip("被匕首光刃移除时，是否在怪物正下方地面炸一次声波。\n" +
                 "这种声波**会被其它怪物捕捉**（故意设计：死亡是响亮的动静）。")]
        public bool emitSonarOnRemove = true;

        [Tooltip("消散声波的参数。留空(默认)时用 killSonar 那一套。")]
        public bool useCustomRemoveSonar = false;

        [Tooltip("自定义消散声波参数（勾上 Use Custom Remove Sonar 才生效）。")]
        public SonarEmitSettings removeSonar = SonarEmitSettings.DefaultShout;

        /// <summary>在怪物正下方的地面高度炸一次声波。</summary>
        private void EmitRemoveSonar()
        {
            var sonar = m_CachedSonar;

            if (sonar == null)
            {
                sonar = FindObjectOfType<SonarSystem>();
                m_CachedSonar = sonar;
            }

            if (sonar == null) return;

            // ★ 高度必须和**玩家走路时**的声源一致 ——
            //   否则两者在竖直方向对不上，怪物/受惊判定会出偏差。
            //
            //   玩家用的是 PlayerController.FootPosition：
            //       transform.position.y + skinWidth + 0.01
            //   而玩家的 CharacterController 圆心在 +0.9、半高 0.9，
            //   所以 transform.position.y 就是**脚底**高度。
            //   => 玩家的发声高度 = 地面 y + skinWidth(0.03) + 0.01 = 地面 y + 0.04
            //
            //   这里用同一个偏移量（0.04m）贴在怪物脚下的地面上。
            const float FootSonarLift = 0.04f;

            Vector3 pos = transform.position;

            if (TryGetGroundY(out float groundY))
                pos.y = groundY + FootSonarLift;
            else
                pos.y -= floatHeight;      // 探不到地面时的退路：球心往下 floatHeight

            SonarEmitSettings set = useCustomRemoveSonar ? removeSonar : killSonar;

            EmitParams p = set.ToEmitParams(Vector3.forward);

            // ★ 标记成「击杀声波」：
            //   1) 其它怪物捕捉到它 -> 进入受惊状态
            //   2) 只要场上还有它，怪物就不会因「匕首收回」而受惊
            p.isKillSonar = true;

            sonar.Emit(pos, p);
        }

        /// <summary>是否已被光刃移除。</summary>
        private bool m_RemovedByBlade;

        // ---------------------------------------------------------------- 编辑期 Gizmo

        /// <summary>是否在 Scene 视图里画怪物的辅助框线。</summary>
        [Header("编辑期 Gizmo")]
        [Tooltip("在 Scene 视图里画出这个怪物的框线，方便摆地图时定位。\n" +
                 "只在编辑状态下显示，**游戏运行时看不到**。")]
        public bool showGizmo = true;

        [Tooltip("框线颜色")]
        public Color gizmoColor = new Color(1f, 0.15f, 0.15f, 1f);

        /// <summary>
        /// 在 Scene 视图里画出怪物的位置与各范围。
        ///
        /// ★ Gizmos 只存在于编辑器 / Scene 视图，**游戏运行时完全不渲染** ——
        ///   所以不需要任何运行时开关，进了 Play 模式自然就看不到。
        ///
        /// 画了三样东西：
        ///   · 实体球（红框）
        ///   · 落地参考线（从球心垂直向下，方便判断悬浮高度）
        ///   · 选中时额外画出感知圆柱与击杀圆柱
        /// </summary>
        private void OnDrawGizmos()
        {
            if (!showGizmo) return;

            Vector3 c = transform.position;

            // ---- 实体球框线 ----
            Gizmos.color = gizmoColor;
            Gizmos.DrawWireSphere(c, Mathf.Max(0.01f, bodyRadius));

            // ---- 球心到地面的参考线 ----
            //   怪物是悬浮的（离地 floatHeight），摆地图时看这条线
            //   能立刻判断它浮在哪儿、会不会穿地板。
            float groundY = c.y - Mathf.Max(0f, floatHeight);

            Gizmos.color = new Color(gizmoColor.r, gizmoColor.g, gizmoColor.b, 0.55f);
            Gizmos.DrawLine(c, new Vector3(c.x, groundY, c.z));

            // 地面上点一个小方块，标出"脚底投影"
            float m = Mathf.Max(0.05f, bodyRadius * 0.25f);
            Gizmos.DrawWireCube(new Vector3(c.x, groundY, c.z), new Vector3(m, 0f, m));

            // ---- 朝向指示 ----
            //
            // ★ 怪物**从不旋转**：移动用 CharacterController.Move，
            //   NavMeshAgent.updateRotation = false。
            //   所以 transform.forward 永远是出生时（场景里摆的那个 Rotation）
            //   的朝向，跟运动方向无关。
            //
            //   匕首"从背面击杀"用的就是这个 forward，
            //   所以这里必须能在 Scene 视图里看见它。
            DrawFacingGizmo(c, groundY);
        }

        /// <summary>
        /// 画朝向指示：
        ///   · 红色箭头 = 正面（怪物 forward 的水平分量）
        ///   · 蓝色箭头 = 背面（正面取反）—— 匕首**必须从这一侧**进入才能击杀
        ///   · 黄色扇区 = 背面 180° 的范围
        /// </summary>
        private void DrawFacingGizmo(Vector3 center, float groundY)
        {
            Vector3 fwd = transform.forward;
            fwd.y = 0f;

            if (fwd.sqrMagnitude < 1e-6f) return;
            fwd.Normalize();

            float len = Mathf.Max(0.5f, bodyRadius * 2.5f);

            // 都画在地面高度，方便和踩在地板上的视角对照
            Vector3 from = new Vector3(center.x, groundY, center.z);

            // ---- 正面（红）----
            Vector3 frontTip = from + fwd * len;
            Gizmos.color = new Color(1f, 0.2f, 0.2f, 1f);
            Gizmos.DrawLine(from, frontTip);

            // 箭头
            Vector3 side = Vector3.Cross(Vector3.up, fwd).normalized;
            Gizmos.DrawLine(frontTip, frontTip - fwd * (len * 0.25f) + side * (len * 0.15f));
            Gizmos.DrawLine(frontTip, frontTip - fwd * (len * 0.25f) - side * (len * 0.15f));

            // ---- 背面（蓝）—— 匕首要从这边刺入 ----
            Vector3 backTip = from - fwd * len;
            Gizmos.color = new Color(0.2f, 0.6f, 1f, 1f);
            Gizmos.DrawLine(from, backTip);

            Gizmos.DrawLine(backTip, backTip + fwd * (len * 0.25f) + side * (len * 0.15f));
            Gizmos.DrawLine(backTip, backTip + fwd * (len * 0.25f) - side * (len * 0.15f));

            // ---- 背面 180° 扇区（黄）----
            Gizmos.color = new Color(1f, 0.9f, 0.2f, 0.45f);

            const int seg = 16;
            Vector3 prev = from + side * len;   // 90° 方向

            for (int i = 1; i <= seg; i++)
            {
                float a = Mathf.PI * i / seg;   // 0..PI 从 +side 扫到 -side（经过背面）
                Vector3 dir = (side * Mathf.Cos(a) - fwd * Mathf.Sin(a)).normalized;
                Vector3 cur = from + dir * len;

                Gizmos.DrawLine(prev, cur);
                prev = cur;
            }
        }

        /// <summary>
        /// 选中时额外画出感知圆柱 / 击杀圆柱，方便对照调试。
        /// 这两个范围又高又大，平时和实体球一起画会太乱，
        /// 所以只在**选中这个怪物**时显示。
        /// </summary>
        private void OnDrawGizmosSelected()
        {
            if (!showGizmo) return;

            Vector3 c = transform.position;

            // ---- 感知圆柱（青色） ----
            DrawWireCylinder(c, Mathf.Max(0.01f, senseRadius),
                             Mathf.Max(0.01f, senseHeight),
                             new Color(0.3f, 0.9f, 1f, 0.8f));

            // ---- 击杀圆柱（橙色），静止时画 idle 半径 ----
            float kr = Application.isPlaying && m_IsMoving ? killRadiusMoving : killRadiusIdle;

            DrawWireCylinder(c, Mathf.Max(0.01f, kr),
                             Mathf.Max(0.01f, killHeight),
                             new Color(1f, 0.6f, 0.1f, 0.8f));

            // ---- 变色球（黄色） ----
            Gizmos.color = new Color(1f, 0.95f, 0.2f, 0.5f);
            Gizmos.DrawWireSphere(c, Mathf.Max(0.01f, colorRadius));
        }

        /// <summary>画一个竖直圆柱线框（以 center 为中轴，上下各 halfHeight）。</summary>
        private static void DrawWireCylinder(Vector3 center, float radius, float height, Color color)
        {
            float half = height * 0.5f;

            Vector3 bottom = center - Vector3.up * half;
            Vector3 top    = center + Vector3.up * half;

            Gizmos.color = color;

            // 上下两个圆
            DrawWireCircle(bottom, radius, 32);
            DrawWireCircle(top,    radius, 32);

            // 四根竖线（0° / 90° / 180° / 270°）
            for (int i = 0; i < 4; i++)
            {
                float angle = i * Mathf.PI * 0.5f;
                float dx = Mathf.Cos(angle) * radius;
                float dz = Mathf.Sin(angle) * radius;

                Gizmos.DrawLine(bottom + new Vector3(dx, 0f, dz),
                                top    + new Vector3(dx, 0f, dz));
            }
        }

        /// <summary>在水平面上画一个圆。</summary>
        private static void DrawWireCircle(Vector3 center, float radius, int segments)
        {
            float step = Mathf.PI * 2f / segments;
            Vector3 prev = center + new Vector3(radius, 0f, 0f);

            for (int i = 1; i <= segments; i++)
            {
                float a = i * step;
                Vector3 cur = center + new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);

                Gizmos.DrawLine(prev, cur);
                prev = cur;
            }
        }
    }
}
