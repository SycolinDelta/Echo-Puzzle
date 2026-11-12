using System.Collections.Generic;
using UnityEngine;

namespace EchoPuzzle
{
    /// <summary>
    /// 匕首刺出技能。
    ///
    /// ── 操作 ────────────────────────────────────────────────
    ///   · 按一次 <see cref="aimKey"/>（E）   -> 进入瞄准模式
    ///   · 再按一次 E                        -> 退出瞄准模式
    ///
    ///   和投石的区别：投石是**长按**右键才瞄准，匕首是**切换式**。
    ///
    /// ── 瞄准状态下的表现 ──────────────────────────────────
    ///   · 玩家身边出现一个**小准星圈**（半径 aimRingRadius，默认 1m），
    ///     和投石的准星圈同高、同风格，但要小得多
    ///   · 一把匕首（lightsaber 模型）漂浮在**与准星圈相同的高度**
    ///   · 按左键 -> 匕首朝玩家正前方快速刺出，然后缓慢回到原位
    ///
    /// ── 匕首外观 ─────────────────────────────────────────
    ///   统一换成 <c>EchoPuzzle/Outline</c> 材质（和玩家角色同一个），
    ///   所以在"透明墙体 + 关灯"的场景里，它和玩家一样呈现为白色轮廓，
    ///   而不是一个黑块。
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed class DaggerSystem : MonoBehaviour
    {
        public static DaggerSystem Instance { get; private set; }

        // ---------------------------------------------------------------- 瞄准

        [Header("瞄准")]
        [Tooltip("按一次进入瞄准、再按一次退出")]
        public KeyCode aimKey = KeyCode.E;

        [Tooltip("瞄准状态下用这个键刺出匕首")]
        public KeyCode thrustKey = KeyCode.Mouse0;

        [Tooltip("是否正在瞄准（只读）")]
        public bool IsAiming { get; private set; }

        /// <summary>
        /// 是否正在**收回**匕首（刺到底之后往回收的那一段）。
        ///
        /// ★ 怪物用来判定"受惊状态"：玩家在收回过程中，
        ///   光刃扫过 KillRadiusMoving 圆柱就会触发受惊。
        ///   刺出的过程**不算**。
        /// </summary>
        public bool IsRecalling => m_ThrustActive && m_ThrustReturning;

        /// <summary>
        /// 瞄准是否被**冻结**（受惊死亡倒计时期间）。
        ///
        /// 冻结时：圆环和匕首保留、仍跟随玩家、保持原角度，
        /// 但鼠标不再控制它们（鼠标改为控制镜头）。
        /// </summary>
        public bool IsAimFrozen => m_AimFrozen;

        [Tooltip("冻结后圆环 / 匕首淡出所需时间（秒）")]
        [Min(0.05f)] public float aimFadeOutOnFright = 1f;

        private bool  m_AimFrozen;
        private float m_AimFadeTimer;

        /// <summary>光刃中点（世界坐标）。收回判定用它扫范围。</summary>
        public bool TryGetBladeMid(out Vector3 worldMid)
        {
            worldMid = Vector3.zero;

            if (m_Blade == null || !m_Blade.gameObject.activeInHierarchy) return false;
            if (!TryGetWorldBounds(m_Blade.gameObject, out Bounds b, out _)) return false;

            worldMid = b.center;
            return true;
        }

        [Tooltip("Debug: show the OS cursor while aiming and use the real pointer position for polar coordinates.")]
        public bool showCursorForDebug = false;

        /// <summary>Which cursor source was used last frame (debug only).</summary>
        private string m_LastPolarSource = "virtual";

        /// <summary>匕首当前的瞄准水平角（世界方位角，0 = +Z）。</summary>
        public float AimYaw { get; private set; }

        /// <summary>匕首当前的瞄准俯仰角（度）。</summary>
        public float AimPitch { get; private set; }
        /// <summary>虚拟光标位置（Locked 下由 Mouse X/Y 增量积分）。</summary>
        private Vector2 m_VirtualCursor;

        /// <summary>按下 E 那一刻的虚拟光标位置 —— 极坐标原点（恒为 0）。</summary>
        private Vector2 m_AimOrigin;


        /// <summary>按下 E 那一刻**相机**朝向的方位角 —— 极坐标 θ=90° 对应的世界方向。</summary>
        private float   m_AimBaseYaw;

        // ---------------------------------------------------------------- 准星圈

        [Header("准星圈")]
        [Tooltip("准星圈的半径（米）。比投石的准星圈小得多")]
        [Min(0.05f)] public float aimRingRadius = 1f;

        [Tooltip("准星圈颜色")]
        public Color aimRingColor = new Color(1f, 1f, 1f, 0.9f);

        [Tooltip("准星圈粗细（0..1，环带厚度占半径的比例）")]
        [Range(0.005f, 0.9f)] public float aimRingThickness = 0.015f;

        [Tooltip("圈与匕首相对**玩家胶囊中心**的高度偏移（米）。0 = 胶囊中心高度")]
        public float aimHeightOffset = 0f;

        // ---------------------------------------------------------------- 匕首

        [Header("匕首")]
        [Tooltip("匕首模型。推荐把 Assets/lightsaber.fbx 直接拖进来；\n" +
                 "留空时代码会在编辑器下按路径 Assets/lightsaber.fbx 加载。")]
        public GameObject daggerPrefab;

        [Tooltip("匕首相对玩家的朝向修正（度）。\n" +
                 "模型默认朝 +Z，如果它横躺着就试 (90,0,0) 或 (0,0,90)")]
        public Vector3 daggerEuler = Vector3.zero;

        [Tooltip("匕首【整体额外的姿态旋转】（度），**每帧生效**，可在 Play 模式实时调。\n\n" +
                 "★ 绕模型自己的中点转，不改变位置。\n" +
                 "  (0,0,0)  = 保持 daggerEuler 设定的姿态\n" +
                 "  (0,0,90) = 让光剑横过来\n" +
                 "  (90,0,0) = 让光剑立起来\n\n" +
                 "与 daggerEuler 的区别：\n" +
                 "  daggerEuler   —— 只在 Awake 时设一次（模型初始姿态）\n" +
                 "  modelRotation —— 每帧叠加，Play 模式下改立刻看到效果")]
        public Vector3 modelRotation = Vector3.zero;

        [Tooltip("Model Rotation 的旋转参考系：\n\n" +
                 "  【勾选】世界轴 —— 绕世界 X/Y/Z 转。\n" +
                 "          尖端永远朝世界上方翘，yaw 转到背后时会变成向下倾。\n" +
                 "  【不勾】模型本地轴 —— 绕匕首自己的 X/Y/Z 转。\n" +
                 "          倾斜相对剑身固定，会跟着鼠标一起转。\n\n" +
                 "切换时请把 Model Rotation 设成同一个值对比效果。")]
        public bool rotateInWorldSpace = true;

        // ---------------------------------------------------------------- 尺寸

        [Header("尺寸")]
        [Tooltip("匕首的【目标长度】（米）—— 模型的最长边会被缩放到正好这个值。\n\n" +
                 "★ 这是把 fbx 的任意单位统一成米的**唯一开关**。\n" +
                 "   lightsaber.fbx 由几十个子网格组成，导入缩放受\n" +
                 "   globalScale / useFileScale 多重影响，很难在 Inspector 里算准。\n" +
                 "   这里直接用包围盒最长边反算，所以填 0.55 就一定是 0.55m。")]
        [Min(0.01f)] public float daggerTargetLength = 0.55f;

        [Tooltip("在目标长度基础上的整体倍数。默认 1 = 正好是目标长度")]
        public float daggerScale = 1f;

        [Tooltip("**【比例】三轴分别的拉伸倍数**，乘在整体尺寸之上。\n\n" +
                 "   (1,1,1) = 保持模型原始比例\n" +
                 "   (1.5,1,1) = 沿模型 X 轴拉长 50%\n" +
                 "   (1,0.7,0.7) = 把 Y/Z 压细 30%（变纤细）\n\n" +
                 "★ 注意这是**模型本地轴**，不是世界轴。\n" +
                 "   光剑长轴通常是 Y 或 Z —— 不确定就三轴轮流试一下，\n" +
                 "   哪个方向明显变长就是它。")]
        public Vector3 daggerScaleXYZ = Vector3.one;

        [Tooltip("开启后按 daggerScaleXYZ 三轴分别缩放；\n" +
                 "关闭则保持模型原始比例（等效 1,1,1）。")]
        public bool daggerStretch = false;

        // ---------------------------------------------------------------- 光刃长度

        [Header("光刃（BladeMesh）")]
        [Tooltip("要找的那个零件名（精确匹配优先，其次包含匹配，不分大小写）。\n" +
                 "fbx 里可选：Blade / Blade_Neck / BladeMesh")]
        public string bladeNodeName = "BladeMesh";

        [Tooltip("光刃的【缩放倍数】（三轴，乘在模型原始缩放上）。\n\n" +
                 "  (1,1,1)  = 原始尺寸\n" +
                 "  (1,2,1)  = 沿模型 Y 轴拉长一倍\n" +
                 "  (1,1,0.5) = 压短一半\n\n" +
                 "★ 这是**模型本地空间**，会跟着匕首整体缩放一起变。")]
        public Vector3 bladeScaleMultiplier = Vector3.one;

        [Tooltip("光刃的【位置偏移】，单位是**世界米**。\n\n" +
                 "用它把光刃挪回去贴住剑身。\n" +
                 "  填 0.25 = 把光刃沿该轴移动 0.25 米（不管模型内部缩放多大）。\n\n" +
                 "方向按**模型本地轴**：光剑沿哪个轴摆放，就改哪个轴的分量。\n" +
                 "（代码会自动把它换算成 BladeMesh 的父节点局部单位）")]
        public Vector3 bladePositionOffset = Vector3.zero;

        // ---------------------------------------------------------------- 光刃击杀

        [Header("光刃击杀")]
        [Tooltip("光刃的中点到怪物球心的水平距离 ≤ 这个值、且高度差 ≤ killHeight/2 时，移除怪物。")]
        [Min(0.01f)] public float bladeKillRadius = 0.3f;

        [Tooltip("光刃击杀圆柱的总高（米）。以怪物球心为中点上下各一半。")]
        [Min(0.01f)] public float bladeKillHeight = 4f;


        // ---------------------------------------------------------------- 外观

        [Header("外观")]
        [Tooltip("匕首的颜色。默认白色 —— 在透明墙 + 关灯的场景里和玩家一样醒目")]
        public Color daggerColor = Color.white;

        [Tooltip("边缘增强强度。0 = 纯平色，越大边缘越亮、越有立体感")]
        [Range(0f, 4f)] public float daggerRimStrength = 0.35f;

        [Tooltip("边缘增强的锐利程度。越大亮边越窄")]
        [Range(0.5f, 8f)] public float daggerRimPower = 3f;

        // ---------------------------------------------------------------- 位置

        [Header("位置")]
        [Tooltip("匕首在瞄准时静止的位置偏移（相对玩家胶囊中心，**玩家本地空间**）。\n" +
                 "  X = 左右，Y = 上下，Z = 前后（+Z 是朝前）")]
        public Vector3 daggerRestOffset = new Vector3(0f, 0f, 0.35f);

        // ---------------------------------------------------------------- 刺出

        [Header("刺出动作")]
        [Tooltip("刺出距离（米）")]
        public float thrustDistance = 0.5f;

        [Tooltip("刺出耗时（秒）。越短越快")]
        [Min(0.01f)] public float thrustOutTime = 0.06f;

        [Tooltip("收回耗时（秒）。越长越慢")]
        [Min(0.01f)] public float thrustBackTime = 0.45f;

        [Tooltip("收回的缓动曲线")]
        public AnimationCurve thrustBackCurve = AnimationCurve.EaseInOut(0f, 1f, 1f, 0f);

        // ---------------------------------------------------------------- 引用

        [Header("引用")]
        [Tooltip("留空会自动找场景里的 PlayerController")]
        public PlayerController player;

        // ---------------------------------------------------------------- 运行时

        private Transform  m_Dagger;
        private MeshRenderer m_AimRing;
        private Mesh       m_RingMesh;
        private Material   m_RingMat;

        // ---------------------------------------------------------------- 光刃

        /// <summary>找到的 BladeMesh 变换（没找到为 null）。</summary>
        private Transform m_Blade;

        /// <summary>BladeMesh 的原始局部缩放 / 位置，用于叠加手动偏移。</summary>
        private Vector3 m_BladeBaseScale    = Vector3.one;
        private Vector3 m_BladeBasePosition = Vector3.zero;

        /// <summary>基准是否已记录过（只记录一次，防止偏移叠乘）。</summary>
        private bool m_BladeBaseCaptured;

        /// <summary>上一帧的 Inspector 值，用来检测"参数变了就重算"。</summary>
        private Vector3 m_LastBladeScale;
        private Vector3 m_LastBladeOffset;
        private bool    m_BladeValuesCached;

        /// <summary>刺出进度：0 = 原位，1 = 完全刺出。</summary>
        private float m_Thrust01;

        /// <summary>
        /// 是否正在执行一次完整的刺击（0→1→0）。
        ///
        /// ★ 这个标志是必需的。旧版本没有它，StepThrust() 每帧无条件推进，
        ///   于是只要不在"收回"阶段就开始自动刺出 ——
        ///   刺完收回后又回到"未收回"状态，再刺一次，无限循环，
        ///   表现为"没按左键也一直在刺"。
        ///   现在只有 BeginThrust() 把它置 true，收回结束才置回 false。
        /// </summary>
        private bool m_ThrustActive;

        /// <summary>
        /// 本次刺击**是否已经杀过怪**。
        ///
        /// 需求：一次刺击只能击杀一只怪物 ——
        ///   匕首中点先后穿过两只怪物的核心区，只杀第一只。
        /// 在 BeginThrust 里清零。
        /// </summary>
        private bool m_KilledThisThrust;

        /// <summary>是否处于"收回"阶段。</summary>
        private bool m_ThrustReturning;

        private float m_ThrustTimer;

        // ---------------------------------------------------------------- 生命周期

        private void Awake()
        {
            Instance = this;

            if (player == null) player = FindObjectOfType<PlayerController>();

            // ★★ 地牢模式：武器是枪，匕首整套都不建 ★★
            //
            //   以前只在 Update 里 return，但 Awake 已经把
            //   "Dagger"（匕首模型）和 "DaggerAimRing"（瞄准圆环）建出来了 ——
            //   圆环开着 AimOverlay 材质（ZTest Always + Overlay 队列），
            //   会在地图上显示成一个漂浮的环。
            //   所以这里直接在源头不建。
            if (GameUI.IsDungeonMode) return;

            EnsureDagger();
            EnsureAimRing();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;

            if (m_RingMesh != null) Destroy(m_RingMesh);
            if (m_RingMat  != null) Destroy(m_RingMat);    // AimOverlay 返回的是新建实例
            if (m_DaggerMat != null) Destroy(m_DaggerMat); // 匕首专用 Outline 材质是克隆出来的
        }

        /// <summary>
        /// 加载并归一化匕首模型。
        ///
        /// ★ 为什么要"归一化"而不是直接用 Instantiate 的结果：
        ///
        ///   lightsaber.fbx 不是单一网格，而是由几十个零件组成的层级模型
        ///   （Grip_Ring_xx、Heat_Ring_x、Pommel_Knob、Switch_Button...）。
        ///   这种 fbx 有三个坑：
        ///     1) 根节点常常是**空壳**（没有 MeshRenderer），真正模型在若干层子节点下
        ///     2) 各子节点自带本地缩放 / 偏移，根节点缩放不代表模型尺寸
        ///     3) 枢轴（原点）很可能不在模型中心，甚至远在模型之外 ——
        ///        于是"把根节点摆到玩家面前"时，模型本体跑到别处，只露出一点点
        ///
        ///   所以这里不信任根节点，而是：
        ///     · 实例化后算出所有 renderer 的**世界包围盒**
        ///     · 把模型挂到一个空的"枢轴容器"下，并平移子物体，
        ///       使包围盒中心正好落在容器原点
        ///     · 再按包围盒的最长边缩放到 <see cref="daggerTargetLength"/>
        ///
        ///   这样无论 fbx 原始尺寸是 0.01 还是 100 单位、枢轴在哪，
        ///   最终拿到的都是一个**长度固定、枢轴居中**的干净物件。
        /// </summary>
        private void EnsureDagger()
        {
            if (m_Dagger != null) return;

            GameObject model = LoadDaggerModel();

            if (model == null)
            {
                Debug.LogWarning("[EchoPuzzle] 找不到匕首模型。\n" +
                                 "  请在 DaggerSystem 的 Dagger Prefab 字段里拖入 Assets/lightsaber.fbx。");
                return;
            }

            // ---- 1) 建一个空的枢轴容器，DaggerSystem 只操作它 ----
            var pivotGo = new GameObject("Dagger");
            pivotGo.transform.SetParent(transform, false);
            pivotGo.transform.localPosition = Vector3.zero;
            pivotGo.transform.localRotation = Quaternion.identity;
            pivotGo.transform.localScale    = Vector3.one;

            // ---- 2) 把模型实例化到容器下 ----
            var go = Instantiate(model, pivotGo.transform);
            go.name = "Model";
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale    = Vector3.one;

            // 必须先强制刷新一次，包围盒才是最新值
            go.SetActive(true);

            // ---- 3) 算世界包围盒（此时容器在原点、未缩放） ----
            if (!TryGetWorldBounds(go, out Bounds bounds, out float longestSide))
            {
                Debug.LogWarning($"[EchoPuzzle] 匕首模型《{model.name}》里找不到任何 Renderer，无法归一化。");
                Destroy(pivotGo);
                return;
            }

            // ---- 4) 平移模型，让包围盒中心对准容器原点 ----
            //
            //   这是修"只出现一小部分"的关键：枢轴不在模型上时，
            //   把模型整体平移 -center，之后就绕着模型真正的中心转了。
            //
            //   ★ TryGetWorldBounds 返回的是**世界**包围盒，而此时容器在原点、
            //     模型 localScale = 1、无旋转，所以"世界中心" = "容器本地中心"。
            //     用 InverseTransformPoint 转一次更稳妥，将来改容器初始变换也不会失效。
            Vector3 worldCenter = bounds.center;
            Vector3 localCenter = pivotGo.transform.InverseTransformPoint(worldCenter);

            Vector3 modelLocalPos = go.transform.localPosition;
            go.transform.localPosition = modelLocalPos - localCenter;

            // ---- 5) 缩放到目标长度 ----
            //
            //   目标长度由 daggerTargetLength 决定（默认 0.55m），
            //   不管 fbx 原本是 100 单位还是 0.01 单位，最后都统一成这个长度。
            float scale = 1f;
            if (longestSide > 1e-5f && daggerTargetLength > 1e-4f)
                scale = daggerTargetLength / longestSide;

            scale *= daggerScale;      // 再乘上手动微调倍数

            // 三轴比例：只有勾了 daggerStretch 才生效，否则保持原始比例
            Vector3 axis = daggerStretch ? daggerScaleXYZ : Vector3.one;

            go.transform.localScale = new Vector3(
                scale * Mathf.Max(0.0001f, axis.x),
                scale * Mathf.Max(0.0001f, axis.y),
                scale * Mathf.Max(0.0001f, axis.z));

            // ---- 5b) 旋转 ----
            //
            //   ★ 旋转必须在这里、在**模型**上做，不能放到 pivotGo 上：
            //     pivotGo 的朝向由 UpdateVisuals 每帧按"玩家前方"设置，
            //     放上去会被覆盖。
            //
            //   但同时还要保证旋转后**不偏离中心** —— 因为包围盒中心已经
            //   对齐到 pivotGo 原点了，绕原点旋转不会跑偏。
            go.transform.localRotation = Quaternion.Euler(daggerEuler);

            // 旋转/拉伸后包围盒会变，重新校正一次中心，避免模型偏出 pivot
            if (TryGetWorldBounds(go, out Bounds after, out _))
            {
                Vector3 afterLocal = pivotGo.transform.InverseTransformPoint(after.center);
                go.transform.localPosition -= afterLocal;
            }

            // ---- 5c) 光刃（BladeMesh）单独拉伸 ----
            ApplyBladeLength(go);

            // ---- 6) 运行时不需要碰撞体 ----
            RemoveColliders(pivotGo);

            // ---- 7) 换成匕首专用材质 ----
            ApplyDaggerMaterial(pivotGo);

            // ---- 8) 初始隐藏，只在瞄准时显示 ----
            pivotGo.SetActive(false);

            m_Dagger = pivotGo.transform;

            // 记录归一化后的信息（Inspector / 调试面板可以读）
            m_ModelScale  = scale;
            m_ModelLength = daggerTargetLength * daggerScale;

        }

        /// <summary>
        /// 取匕首模型。
        ///
        /// 优先级：
        ///   1) Inspector 里拖进来的 Dagger Prefab（**推荐**，也是唯一稳定可靠的来源）
        ///   2) 编辑器下按路径找 Assets/lightsaber.fbx
        ///
        /// ★ 为什么不再走 Resources：
        ///   之前为了"自动加载"，我把 Assets/lightsaber.fbx **复制**了一份到
        ///   Resources/Dagger/。结果你在 Inspector 里调的是**原文件**的导入设置
        ///   （bakeAxisConversion / globalScale），而代码加载的是**副本** ——
        ///   副本的 meta 还是旧值，所以你所有调整都没生效。
        ///   现在副本已删除，只认你手上那一份。
        /// </summary>
        private GameObject LoadDaggerModel()
        {
            if (daggerPrefab != null) return daggerPrefab;

#if UNITY_EDITOR
            // 兜底：直接按路径取原始 fbx
            const string path = "Assets/lightsaber.fbx";

            var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asset != null) return asset;

            Debug.LogWarning($"[EchoPuzzle] 在 {path} 找不到匕首模型。\n" +
                             "  请把 lightsaber.fbx 拖到 DaggerSystem 的 Dagger Prefab 字段。");
#else
            Debug.LogWarning("[EchoPuzzle] DaggerSystem 的 Dagger Prefab 字段是空的。" +
                             "打包后的游戏无法自动找模型，请先在 Inspector 里指定。");
#endif
            return null;
        }

        /// <summary>
        /// 算出模型所有 renderer 的世界包围盒，以及最长边长度。
        /// </summary>
        private static bool TryGetWorldBounds(GameObject root, out Bounds bounds, out float longestSide)
        {
            bounds = default;
            longestSide = 0f;

            var renderers = root.GetComponentsInChildren<Renderer>(true);

            bool has = false;

            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r == null) continue;
                if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;

                if (!has)
                {
                    bounds = r.bounds;
                    has    = true;
                }
                else
                {
                    bounds.Encapsulate(r.bounds);
                }
            }

            if (!has) return false;

            Vector3 s = bounds.size;
            longestSide = Mathf.Max(s.x, Mathf.Max(s.y, s.z));
            return true;
        }


        // ---------------------------------------------------------------- 光刃

        /// <summary>
        /// 找到 BladeMesh 并记录它的原始缩放 / 位置。
        /// 之后由 <see cref="ApplyBladeTransform"/> 按 Inspector 参数叠加调整。
        /// </summary>
        private void ApplyBladeLength(GameObject modelRoot)
        {
            m_Blade = null;

            if (string.IsNullOrWhiteSpace(bladeNodeName)) return;

            if (!TryFindChildByName(modelRoot.transform, bladeNodeName, out Transform blade))
            {
                Debug.LogWarning($"[EchoPuzzle] 光剑里找不到名为「{bladeNodeName}」的零件。\n" +
                                 "  fbx 里可选的名字：Blade / Blade_Neck / BladeMesh");
                return;
            }

            m_Blade = blade;

            // 记录原始缩放与位置，方便运行时还原
            //
            // ★ 基准**只记录一次**。
            //
            //   如果每次都记录，第二次调用时读到的 localPosition
            //   已经是"原始值 + 上次的手动偏移" —— 再拿它当基准，
            //   就成了"原始值 + 偏移 + 偏移"，越叠越远直到飞出视野，
            //   表现就是"改 BladePositionOffset 之后光刃消失了"。
            if (!m_BladeBaseCaptured)
            {
                m_BladeBaseScale    = blade.localScale;
                m_BladeBasePosition = blade.localPosition;
                m_BladeBaseCaptured = true;
            }

            ApplyBladeTransform();
        }

        /// <summary>
        /// 按 Inspector 参数调整光刃的**缩放**和**位置**。
        ///
        /// ★ 这里完全**手动**，不做任何自动对齐。
        ///
        ///   之前尝试过"自动把根部贴回剑身"，但判断"哪一端是根部"只能靠
        ///   "离模型重心更近"来猜 —— 光刃的形状不规则时这个猜测经常出错，
        ///   结果就是把光刃推到更奇怪的位置。
        ///
        ///   现在改成：
        ///     · Blade Scale Multiplier  直接乘在原始缩放上
        ///     · Blade Position Offset   直接加在原始位置上
        ///   你自己在 Play 模式下拖到满意为止，然后把数值告诉我或记下来。
        ///
        ///   注意这些是**模型本地空间**的量，所以会跟着匕首整体缩放一起变。
        ///   举例：光刃原本 0.1m，匕首整体缩放 ×0.5，你在这里填 2 倍，
        ///   最终看上去是 0.1m。
        /// </summary>
        private void ApplyBladeTransform()
        {
            if (m_Blade == null) return;

            // 缩放
            m_Blade.localScale = Vector3.Scale(m_BladeBaseScale,
                                               bladeScaleMultiplier);

            // ---- 位置 ----
            //
            // ★ BladePositionOffset 的单位是【世界米】，不是局部单位。
            //
            //   BladeMesh 的 localPosition 是相对**父节点**的，而父节点链上
            //   有多层缩放（fbx 的 globalScale 0.01、匕首整体归一化 ×0.4583…）。
            //   直接 `localPosition = base + offset` 会让人误以为在填米，
            //   实际被缩放链放大 / 缩小，结果完全对不上
            //   （实测要输 0.0025 才能顶得上 0.25 米，就是 100 倍差）。
            //
            //   这里把"米"换算成"父节点局部单位"：
            //       localDelta = offsetWorld / 父节点在世界中的缩放
            //
            //   父节点的世界缩放用 lossyScale 取，逐轴相除，非等比缩放也正确。
            Vector3 parentScale = m_Blade.parent != null
                ? m_Blade.parent.lossyScale
                : Vector3.one;

            Vector3 localDelta = new Vector3(
                Mathf.Approximately(parentScale.x, 0f) ? 0f : bladePositionOffset.x / parentScale.x,
                Mathf.Approximately(parentScale.y, 0f) ? 0f : bladePositionOffset.y / parentScale.y,
                Mathf.Approximately(parentScale.z, 0f) ? 0f : bladePositionOffset.z / parentScale.z);

            // 夹一下，避免误填一个巨大的值把光刃推飞
            localDelta = Vector3.ClampMagnitude(localDelta, Mathf.Max(1f, m_ModelLength * 3f));

            m_Blade.localPosition = m_BladeBasePosition + localDelta;

            // 记住本次用的值，供 UpdateVisuals 检测 Inspector 是否被改动
            m_LastBladeScale    = bladeScaleMultiplier;
            m_LastBladeOffset   = bladePositionOffset;
            m_BladeValuesCached = true;
        }

        /// <summary>按名字（包含匹配、不分大小写）在层级里找子物体。</summary>
        private static bool TryFindChildByName(Transform root, string name, out Transform found)
        {
            found = null;

            var all = root.GetComponentsInChildren<Transform>(true);

            // 先精确匹配
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == root) continue;
                if (string.Equals(all[i].name, name, System.StringComparison.OrdinalIgnoreCase))
                {
                    found = all[i];
                    return true;
                }
            }

            // 再包含匹配（fbx 有时会给名字加后缀，如 "BladeMesh.001"）
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == root) continue;
                if (all[i].name.IndexOf(name, System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    found = all[i];
                    return true;
                }
            }

            return false;
        }

        /// <summary>归一化后模型的实际缩放倍数（只读，供调试）。</summary>
        private float m_ModelScale;
        private float m_ModelLength;

        /// <summary>匕首专用的 Outline 材质实例（自己克隆的，需自行销毁）。</summary>
        private Material m_DaggerMat;

        /// <summary>移除匕首身上所有 Collider（包括子物体）。</summary>
        private static void RemoveColliders(GameObject root)
        {
            var cols = root.GetComponentsInChildren<Collider>(true);

            for (int i = 0; i < cols.Length; i++)
            {
                if (cols[i] == null) continue;
                Destroy(cols[i]);
            }
        }

        /// <summary>
        /// 给匕首换上"纯白实体"材质（EchoPuzzle/DaggerWhite）。
        ///
        /// ★ 为什么不再用 EchoPuzzle/Outline：
        ///
        ///   Outline 靠"沿法线膨胀外壳 + Cull Front"画描边：
        ///       posOS = positionOS + normalize(normalOS) * _OutlineWidth
        ///   这对玩家那种圆润的胶囊体很好用，但对 lightsaber.fbx 这种
        ///   **几十个小零件紧密拼接**的细长模型是灾难 ——
        ///   每个零件各自膨胀，膨胀量超过零件间距时外壳互相穿插、
        ///   把本体遮住，看起来就是"从模型上不规则地切掉一块"。
        ///   而且 _OutlineWidth 作用在模型本地空间，模型缩得越小越严重。
        ///
        ///   再怎么调 _OutlineWidth 都只是把穿插减轻一点，治不了根。
        ///   所以匕首改用单 Pass 纯色 + 菲涅尔，**完全不膨胀**，
        ///   模型长什么样就画成什么样。
        /// </summary>
        private void ApplyDaggerMaterial(GameObject root)
        {
            Shader sh = Shader.Find("EchoPuzzle/DaggerWhite");

            if (sh == null)
            {
                Debug.LogWarning("[EchoPuzzle] 找不到 shader「EchoPuzzle/DaggerWhite」，" +
                                 "匕首会显示成粉色（shader 丢失）。");
                return;
            }

            Material mat = new Material(sh) { hideFlags = HideFlags.DontSave };
            m_DaggerMat = mat;

            if (mat.HasProperty("_Color"))       mat.SetColor("_Color", daggerColor);
            if (mat.HasProperty("_RimStrength")) mat.SetFloat("_RimStrength", daggerRimStrength);
            if (mat.HasProperty("_RimPower"))    mat.SetFloat("_RimPower",    daggerRimPower);

            var renderers = root.GetComponentsInChildren<MeshRenderer>(true);

            for (int i = 0; i < renderers.Length; i++)
            {
                MeshRenderer r = renderers[i];
                if (r == null) continue;

                // 逐槽替换成同一份匕首材质（共享，避免每个 renderer 再克隆一份）
                Material[] mats = r.sharedMaterials;
                if (mats == null || mats.Length == 0) continue;

                for (int k = 0; k < mats.Length; k++)
                    mats[k] = mat;

                r.sharedMaterials = mats;

                r.shadowCastingMode          = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows             = false;
                r.lightProbeUsage            = UnityEngine.Rendering.LightProbeUsage.Off;
                r.reflectionProbeUsage       = UnityEngine.Rendering.ReflectionProbeUsage.Off;
                r.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            }
        }

        /// <summary>准星圈：一个躺在水平面上的细环。</summary>
        private void EnsureAimRing()
        {
            if (m_AimRing != null) return;

            var go = new GameObject("DaggerAimRing");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = Vector3.zero;

            var filter = go.AddComponent<MeshFilter>();
            m_RingMesh = BuildRingMesh(48, aimRingThickness);
            filter.sharedMesh = m_RingMesh;

            m_AimRing = go.AddComponent<MeshRenderer>();
            m_AimRing.shadowCastingMode          = UnityEngine.Rendering.ShadowCastingMode.Off;
            m_AimRing.receiveShadows             = false;
            m_AimRing.lightProbeUsage            = UnityEngine.Rendering.LightProbeUsage.Off;
            m_AimRing.reflectionProbeUsage       = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            m_AimRing.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;

            // 和投石的准星圈用同一个"永远在最上层"的材质，
            // 这样它也不会被墙体 / 怪物实体挡住。
            m_RingMat = EchoRuntimeMaterials.AimOverlay(aimRingColor);
            m_AimRing.sharedMaterial = m_RingMat;

            m_AimRing.enabled = false;
        }

        /// <summary>生成一个躺在 XZ 平面上的圆环（单位半径）。</summary>
        private static Mesh BuildRingMesh(int segments, float thickness)
        {
            var verts = new List<Vector3>();
            var tris  = new List<int>();

            thickness = Mathf.Clamp(thickness, 0.005f, 0.9f);

            float outer = 1f;
            float inner = 1f - thickness;

            for (int i = 0; i <= segments; i++)
            {
                float a = (float)i / segments * Mathf.PI * 2f;
                float c = Mathf.Cos(a), s = Mathf.Sin(a);

                verts.Add(new Vector3(c * inner, 0f, s * inner));
                verts.Add(new Vector3(c * outer, 0f, s * outer));
            }

            for (int i = 0; i < segments; i++)
            {
                int b = i * 2;
                tris.Add(b);     tris.Add(b + 1); tris.Add(b + 2);
                tris.Add(b + 2); tris.Add(b + 1); tris.Add(b + 3);
            }

            var mesh = new Mesh { name = "DaggerAimRing" };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        // ---------------------------------------------------------------- 主循环

        private void Update()
        {
            // ★★ 地牢模式（DungeonMode）禁用匕首 ★★
            //
            //   地牢模式是独立的肉鸽射击玩法，武器是枪（待实现）。
            //   匕首 / 光刃 / 电池属于探秘模式的战斗系统，这里整个关掉：
            //   不响应瞄准、不响应刺击、不画匕首与圆环。
            //
            //   ★ 交接说明：地牢模式的枪械请新建
            //     Scripts/Dungeon/DungeonGun.cs，不要改这个脚本。
            if (GameUI.IsDungeonMode)
            {
                // 把匕首和瞄准圆环都收起来
                if (m_Dagger != null && m_Dagger.gameObject.activeSelf)
                    m_Dagger.gameObject.SetActive(false);

                if (m_AimRing != null && m_AimRing.enabled)
                    m_AimRing.enabled = false;

                return;
            }

            bool canAct = GameUI.Current == GameState.Playing;

            HandleAimToggle(canAct);
            HandleAimMouse(canAct);

            if (IsAiming && canAct && Input.GetKeyDown(thrustKey))
            {
                BeginThrust();
            }

            StepThrust();
            UpdateVisuals();

            // 光刃击杀：只有"有电池 + 有光刃 + 正在刺出"时才可能命中
            if (canAct) CheckBladeKill();
        }

        /// <summary>
        /// 匕首瞄准：极坐标（θ, r），**忽略 r，只取 θ**。
        ///
        /// ── 严格按需求实现 ──────────────────────────────────
        ///   1) 按下 E 时，以当时的鼠标位置为**原点**建极坐标系
        ///   2) 鼠标离开原点后得到 (θ, r)，**r 无视**，只用 θ
        ///   3) 匕首立刻"跳"到鼠标 θ 对应的角度
        ///   4) θ 变化时匕首 1:1 跟随
        ///
        /// ── θ 的定义（和投石一致） ──────────────────────────
        ///   原点在屏幕中心，屏幕"上"= 相机正前方、右 = 右转 90°、
        ///   下 = 正后方、左 = 左转 90°。
        ///
        /// ── 为什么用"虚拟光标" ──────────────────────────────
        ///   光标保持 Locked（玩家看不到指针），
        ///   用 Mouse X/Y 的增量积分出一个虚拟位置来算 θ。
        ///   如果解锁真实光标，屏幕上就会出现指针。
        /// </summary>
        private void HandleAimMouse(bool canAct)
        {
            if (!canAct)
            {
                IsAiming = false;
                return;
            }

            if (!IsAiming) return;

            // ★ 冻结时**不读鼠标** —— 角度保持在进入受惊时的值。
            //   鼠标这时归镜头用（PlayerController.HandleLook 已放行）。
            if (m_AimFrozen) return;

            // ---- 取极坐标原点与当前位置 ----
            Vector2 d;
            string  source;

            if (showCursorForDebug)
            {
                // ★ 调试模式：用**真实指针位置**，屏幕上能看到指针。
                //   这样可以直接肉眼确认"匕首是否跟着指针转"。
                d      = (Vector2)Input.mousePosition - m_AimOrigin;
                source = "真实指针";
            }
            else
            {
                // 正式模式：光标锁定，用 Mouse X/Y 增量积分出虚拟位置算 θ。
                float mx = Input.GetAxisRaw("Mouse X");
                float my = Input.GetAxisRaw("Mouse Y");

                m_VirtualCursor += new Vector2(mx, my) * VirtualCursorPixelsPerUnit;

                d      = m_VirtualCursor - m_AimOrigin;
                source = "虚拟光标";
            }

            // 鼠标没离开原点：匕首朝基准方向（进入瞄准时记下的相机前方）
            if (d.sqrMagnitude < DontMoveEps * DontMoveEps)
            {
                AimYaw   = m_AimBaseYaw;
                AimPitch = 0f;
                return;
            }

            // ---- 只取 θ，无视 r ----
            //
            //   Mathf.Atan2(d.y, d.x) 逆时针为正：
            //     右 = 0°、上 = 90°、左 = 180°、下 = 270°
            //
            //   转成世界方位角（0 = +Z，右 = +90°）：
            //     θ = 90°  ->  90-90  = 0    -> 相机正前方 ✅
            //     θ = 0°   ->  90-0   = +90  -> 相机正右方 ✅
            //     θ = 270° ->  90-270 = -180 -> 相机正后方 ✅
            //     θ = 180° ->  90-180 = -90  -> 相机正左方 ✅
            float thetaDeg = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;

            AimYaw = Mathf.Repeat(m_AimBaseYaw + (90f - thetaDeg) + 180f, 360f) - 180f;

            // 俯仰恒为 0：匕首永远在水平面上
            AimPitch = 0f;

            m_LastPolarSource = source;
        }

        /// <summary>鼠标位移小于多少虚拟像素时视为"还没离开原点"。</summary>
        private const float DontMoveEps = 2f;

        /// <summary>
        /// 把 GetAxisRaw 的量纲换算成虚拟像素。
        ///
        /// 只影响"要多大的鼠标行程才能转到某个 θ"，**不影响 1:1 关系**
        /// （θ 由 atan2 算，鼠标转多少度 θ 就变多少度）。
        /// </summary>
        private const float VirtualCursorPixelsPerUnit = 20f;

        /// <summary>按 E 切换瞄准状态。</summary>
        private void HandleAimToggle(bool canAct)
        {
            if (!canAct)
            {
                LeaveAim();
                return;
            }

            // ★ 受惊死亡倒计时期间：**冻结**瞄准 ——
            //   圆环和匕首保留、仍跟随玩家、保持原角度，
            //   但鼠标不再控制它们（鼠标改为控制镜头）。
            //   由 UpdateVisuals 负责 1 秒淡出。
            if (GameUI.FrightenedDeathActive)
            {
                if (IsAiming && !m_AimFrozen)
                {
                    m_AimFrozen     = true;
                    m_AimFadeTimer  = aimFadeOutOnFright;
                }

                if (!IsAiming) return;
            }
            else if (m_AimFrozen)
            {
                // 倒计时结束（无论死活）都解除冻结
                m_AimFrozen = false;
            }

            // ★ 互斥：按住右键（投石瞄准）时不响应 E。
            //   已经处于匕首瞄准时，按住右键也会被投石那边 CancelAim 掉。
            if (Input.GetKey(KeyCode.Mouse1)) return;

            if (!Input.GetKeyDown(aimKey)) return;

            if (IsAiming)
            {
                LeaveAim();
            }
            else
            {
                EnterAim();
            }
        }

        /// <summary>
        /// 进入瞄准。
        ///
        /// ★ 光标**保持锁定/隐藏**，不做任何解锁动作。
        /// </summary>
        private void EnterAim()
        {
            IsAiming = true;


            // 光标与极坐标原点
            if (showCursorForDebug)
            {
                // Debug: unlock and show the OS cursor; origin = real pointer at E press
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible   = true;

                m_AimOrigin = new Vector2(Input.mousePosition.x, Input.mousePosition.y);
            }
            else
            {
                // Normal: cursor stays locked; virtual cursor starts at zero
                m_VirtualCursor = Vector2.zero;
                m_AimOrigin     = Vector2.zero;
            }

            // ★ 基准朝向 = **相机**正前方（不是玩家朝向）。
            //   鼠标"朝上"对应相机看向的方向。
            m_AimBaseYaw = CameraYaw();
            AimYaw       = m_AimBaseYaw;
            AimPitch     = 0f;

            // 复位刺出状态
            m_Thrust01        = 0f;
            m_ThrustReturning = false;
            m_ThrustTimer     = 0f;

            NotifyAimStateChanged();
        }

        /// <summary>退出瞄准：调试模式下把指针锁回隐藏。</summary>
        private void LeaveAim()
        {
            bool was = IsAiming;
            IsAiming = false;

            if (was && showCursorForDebug)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible   = false;
            }

            m_Thrust01        = 0f;
            m_ThrustReturning = false;
            m_ThrustTimer     = 0f;
        }

        /// <summary>
        /// 当前**镜头**朝向的水平方位角（0 = +Z，90 = +X）。
        ///
        /// ★ 匕首的极坐标基准取这个 —— 需求明确说"瞄准圈的前方是镜头方向的前方，
        ///   不是玩家朝向"。
        /// </summary>
        private float CameraYaw()
        {
            Camera cam = Camera.main;
            if (cam == null && player != null) cam = player.playerCamera;

            Vector3 fwd;

            if (cam != null)
                fwd = cam.transform.forward;
            else if (player != null)
                fwd = player.transform.forward;
            else
                return 0f;

            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
            fwd.Normalize();

            return Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// 告诉其它系统"匕首瞄准开 / 关了"。
        /// 投石和匕首都占用鼠标左键，所以进入匕首瞄准时要让投石退出，
        /// 避免两套瞄准同时生效。
        /// </summary>
        private void NotifyAimStateChanged()
        {
            if (IsAiming)
            {
                var thrower = StoneThrowSystem.Instance;
                if (thrower != null) thrower.CancelAim();
            }
        }

        // ---------------------------------------------------------------- 刺出

        /// <summary>左键按下时调用：开始一次刺击。</summary>
        private void BeginThrust()
        {
            // 已经在刺了就忽略（避免连点打断动作）
            if (m_ThrustActive) return;

            m_ThrustActive    = true;
            m_ThrustReturning = false;
            m_ThrustTimer     = 0f;
            m_Thrust01        = 0f;

            // 新的一次刺击：允许再杀一只
            m_KilledThisThrust = false;
        }

        /// <summary>
        /// 推进刺击动画。**只有 m_ThrustActive 时才做任何事** ——
        /// 这样没按左键时匕首完全静止在原位。
        /// </summary>
        private void StepThrust()
        {
            if (!m_ThrustActive)
            {
                m_Thrust01 = 0f;
                return;
            }

            if (!m_ThrustReturning)
            {
                // ---- 刺出：thrustOutTime 内 0 -> 1 ----
                m_ThrustTimer += Time.deltaTime;
                m_Thrust01 = Mathf.Clamp01(m_ThrustTimer / Mathf.Max(0.01f, thrustOutTime));

                if (m_Thrust01 >= 1f)
                {
                    m_ThrustReturning = true;
                    m_ThrustTimer     = 0f;
                }
            }
            else
            {
                // ---- 收回：thrustBackTime 内 1 -> 0 ----
                m_ThrustTimer += Time.deltaTime;

                float k = Mathf.Clamp01(m_ThrustTimer / Mathf.Max(0.01f, thrustBackTime));
                m_Thrust01 = thrustBackCurve.Evaluate(k);

                if (k >= 1f)
                {
                    // 一次刺击完整结束
                    m_Thrust01        = 0f;
                    m_ThrustReturning = false;
                    m_ThrustActive    = false;
                    m_ThrustTimer     = 0f;
                }
            }
        }

        // ---------------------------------------------------------------- 可视化

        private void UpdateVisuals()
        {
            if (player == null) return;

            bool show = IsAiming;

            if (m_AimRing != null) m_AimRing.enabled = show;

            if (m_Dagger != null && m_Dagger.gameObject.activeSelf != show)
                m_Dagger.gameObject.SetActive(show);

            if (!show) return;

            // ---- 冻结后的淡出（受惊死亡倒计时期间） ----
            //
            //   ★ 圆环和匕首**不立刻消失**，而是 1 秒内淡出；
            //     这 1 秒里它们仍然跟着玩家、保持进入冻结时的角度。
            //   用 AimOverlay 材质的 _Color.a 控制透明度。
            if (m_AimFrozen && m_AimFadeTimer > 0f)
            {
                m_AimFadeTimer -= Time.deltaTime;

                float k = Mathf.Clamp01(m_AimFadeTimer / Mathf.Max(0.05f, aimFadeOutOnFright));
                ApplyAimAlpha(k);

                if (m_AimFadeTimer <= 0f)
                {
                    // 淡完了：彻底收起
                    ApplyAimAlpha(0f);
                    LeaveAim();
                    m_AimFrozen = false;
                    return;
                }
            }
            else if (!m_AimFrozen)
            {
                // 正常状态：不透明度拉满
                ApplyAimAlpha(1f);
            }


            // ---- 光刃显隐：没电池时 BladeMesh 消失 ----
            UpdateBladeVisibility();

            // 让 Play 模式下改 Inspector 参数能立刻看到效果
            if (m_BladeValuesCached &&
                (bladeScaleMultiplier != m_LastBladeScale ||
                 bladePositionOffset != m_LastBladeOffset))
            {
                ApplyBladeTransform();
            }

            // ---- 基准点：玩家胶囊中心（+ 高度偏移） ----
            Vector3 basePos = player.transform.position;

            var cc = player.GetComponent<CharacterController>();
            if (cc != null) basePos += cc.center;

            basePos += Vector3.up * aimHeightOffset;

            // ---- 准星圈：躺在基准点高度的水平面上 ----
            //
            //   注意准星圈是"躺平"的（rotation = identity），
            //   不管瞄准方向怎么变，它都表示"以玩家为中心的水平范围"。
            if (m_AimRing != null)
            {
                m_AimRing.transform.position = basePos;
                m_AimRing.transform.rotation = Quaternion.identity;
                m_AimRing.transform.localScale = new Vector3(aimRingRadius, 1f, aimRingRadius);
            }

            // ---- 匕首 ----
            if (m_Dagger != null)
            {
                // ★ 分两个概念，绝不能混在一起：
                //
                //   yawRot  = **只在水平面内**的朝向（绕世界 Y 轴）
                //             鼠标只改这个，所以匕首永远绕竖直轴转
                //
                //   之前直接用 Quaternion.LookRotation(aimDir) 当整体朝向，
                //   一旦 aimDir 有 Y 分量、或者再乘一个带 X/Z 的 modelRotation，
                //   pivotGo 就带上了俯仰角 —— 于是绕它转 yaw 时轴是歪的，
                //   看起来就是"不在水平面上旋转"。
                float yawRad = AimYaw * Mathf.Deg2Rad;
                Quaternion yawRot = Quaternion.Euler(0f, AimYaw, 0f);

                Vector3 flatDir = new Vector3(Mathf.Sin(yawRad), 0f, Mathf.Cos(yawRad));

                // 静止位（水平面上，绕竖直轴摆开）+ 刺出位移
                Vector3 rest = basePos + yawRot * daggerRestOffset;
                Vector3 tip  = rest + flatDir * (thrustDistance * m_Thrust01);

                m_Dagger.position = tip;

                // ---- 朝向 ----
                //
                //   1) yawRot        —— 只在水平面内绕**世界 Y 轴**转（鼠标控制）
                //   2) modelRotation —— 在 yaw 之上再叠一个姿态旋转（Inspector 控制）
                //
                //   ★ 乘法顺序决定 modelRotation 绕的是哪套轴：
                //
                //     【rotateInWorldSpace = true】  extra * yawRot   （左乘）
                //        extra 作用在**世界轴**上。
                //        尖端永远朝世界某个固定方向翘，yaw 转到背后会看成向下倾。
                //
                //     【rotateInWorldSpace = false】 yawRot * extra   （右乘）
                //        extra 作用在**模型本地轴**上。
                //        倾斜相对剑身固定，整个"翘姿"跟着 yaw 一起转。
                //
                //   两者都不改变中点位置 —— 模型枢轴已归一化到包围盒中心。
                Quaternion extra = Quaternion.Euler(modelRotation);

                Quaternion finalRot;

                if (rotateInWorldSpace)
                    finalRot = extra * yawRot;      // 世界轴
                else
                    finalRot = yawRot * extra;      // 模型本地轴

                m_Dagger.rotation = finalRot;
            }
        }

        // ---------------------------------------------------------------- 光刃显隐 / 击杀

        /// <summary>
        /// 没电池时把 BladeMesh 藏起来。
        ///
        /// ★ 只藏 BladeMesh，剑柄照常显示 ——
        ///   这样"没电"的视觉反馈是"光剑只剩剑柄"，而不是整把武器消失。
        /// </summary>
        private void UpdateBladeVisibility()
        {
            if (m_Blade == null) return;

            var battery = BatterySystem.Instance;
            bool hasPower = battery == null || battery.HasBattery;

            if (m_Blade.gameObject.activeSelf != hasPower)
                m_Blade.gameObject.SetActive(hasPower);
        }

        /// <summary>
        /// 光刃中点进入怪物周围的圆柱 -> 移除该怪物，并消耗一点电池。
        ///
        /// 圆柱：以**怪物球心**为中轴
        ///   · 水平半径 = bladeKillRadius（默认 0.3m）
        ///   · 竖直总高 = bladeKillHeight（默认 4m，上下各 2m）
        ///
        /// 判定点 = BladeMesh 包围盒的中心。
        ///
        /// ★ 没有电池时直接跳过：光刃已经消失，自然不该杀怪。
        /// </summary>
        private void CheckBladeKill()
        {
            // ★ 条件 1：**只有在"刺出"阶段**才能移除怪物。
            //
            //   m_ThrustActive    —— 正在执行一次完整刺击
            //   m_ThrustReturning —— true = 已经刺到底、正在收回
            //
            //   收回途中、静止时、刚按下还没刺出时，都不判定。
            if (!m_ThrustActive || m_ThrustReturning) return;

            // ★ 一次刺击**只能杀一只**。
            //   杀到之后置位，本次刺击剩余时间里不再判定任何怪物 ——
            //   所以"先后穿过两只怪物核心区"只会杀掉第一只。
            if (m_KilledThisThrust) return;

            if (m_Blade == null) return;
            if (!m_Blade.gameObject.activeInHierarchy) return;

            var battery = BatterySystem.Instance;
            if (battery != null && !battery.HasBattery) return;

            // 玩家位置（只用 x/z）
            if (player == null) return;
            Vector3 pPos = player.transform.position;

            // 光刃中点（世界坐标）
            if (!TryGetWorldBounds(m_Blade.gameObject, out Bounds bb, out _)) return;

            Vector3 bladeMid = bb.center;

            float r2    = bladeKillRadius * bladeKillRadius;
            float halfH = Mathf.Max(0.01f, bladeKillHeight) * 0.5f;

            var monsters = Object.FindObjectsByType<PuzzleMonster>(
                FindObjectsSortMode.None);

            // ---- 挑出所有"符合条件"的怪物，按球心到光刃中点的**水平距离**排序 ----
            //
            //   ★ 因为怪物可以重叠，同一时刻可能有好几只都在核心区里。
            //     需求：优先杀**球心离光刃中点最近**的那只；
            //           距离完全相同就随机杀一只。
            int   bestIndex = -1;
            float bestDist2 = float.MaxValue;
            int   tieCount  = 0;

            for (int i = 0; i < monsters.Length; i++)
            {
                PuzzleMonster m = monsters[i];
                if (m == null || !m.isActiveAndEnabled) continue;

                Vector3 c = m.transform.position;      // 怪物球心

                // ---- 竖直：圆柱范围内 ----
                if (Mathf.Abs(bladeMid.y - c.y) > halfH) continue;

                // ---- 水平：核心区域（半径内）----
                float dx = bladeMid.x - c.x;
                float dz = bladeMid.z - c.z;
                float d2 = dx * dx + dz * dz;
                if (d2 > r2) continue;

                // ---- 必须从**背面**进入 ----
                //
                //   在 XZ 平面上：
                //     · 怪物朝向 = 怪物 forward 的水平分量
                //     · 怪物 -> 玩家 的水平向量
                //   点积 < 0 表示玩家在怪物背后 -> 触发移除；
                //   否则移除失败（**不消耗电池**）。
                if (!IsBehind(m.transform, pPos, c)) continue;

                // ---- 距离比较（带"几乎相等"的容差）----
                const float DistEps = 1e-4f;

                if (bestIndex < 0 || d2 < bestDist2 - DistEps)
                {
                    bestIndex = i;
                    bestDist2 = d2;
                    tieCount  = 1;
                }
                else if (Mathf.Abs(d2 - bestDist2) <= DistEps)
                {
                    // 距离相同：以 1/n 的概率换掉当前候选 ->
                    // 结束时每只等概率被选中
                    tieCount++;

                    if (Random.Range(0, tieCount) == 0) bestIndex = i;
                }
            }

            if (bestIndex < 0) return;

            PuzzleMonster target = monsters[bestIndex];
            if (target == null) return;

            // 命中：移除怪物，并消耗一点电池
            target.RemoveByBlade();

            if (battery != null) battery.Consume();

            // ★ 击杀成功 -> 潜行速度立刻暴增（默认 4 倍），1 秒内回落
            if (player != null) player.NotifyMonsterRemoved();

            // ★ 锁住本次刺击：后续帧再碰到别的怪物也不杀
            m_KilledThisThrust = true;
        }

        /// <summary>
        /// 玩家是否位于怪物的**背后**（仅在 XZ 平面上判断）。
        ///
        /// 取两个水平向量：
        ///   forward  = 怪物朝向的水平分量
        ///   toPlayer = 怪物 -> 玩家 的水平向量
        ///
        /// 点积 dot = forward · toPlayer。
        ///   dot &lt; 0  <=>  夹角 &gt; 90°  <=>  玩家在怪物背后  -> true
        ///   dot &gt;= 0 <=>  夹角 &lt;= 90° <=>  玩家在正 / 侧面 -> false
        ///
        /// 需求里写的是"怪物和玩家的连线与怪物朝向的夹角"，
        /// 那条线在方向上有两种取法；用 (怪物 -> 玩家) 与朝向求夹角，
        /// "&gt;90°" 正好对应"玩家在怪物背后"，符合"从背面进入"的语义。
        /// </summary>
        private static bool IsBehind(Transform monster, Vector3 playerPos, Vector3 monsterPos)
        {
            // 怪物朝向的水平分量
            Vector3 fwd = monster.forward;
            fwd.y = 0f;

            if (fwd.sqrMagnitude < 1e-6f) return false;   // 朝向退化，当作不成立
            fwd.Normalize();

            // 怪物 -> 玩家的水平向量
            Vector3 toPlayer = new Vector3(playerPos.x - monsterPos.x, 0f,
                                           playerPos.z - monsterPos.z);

            if (toPlayer.sqrMagnitude < 1e-6f) return false;  // 水平重合，方向无意义
            toPlayer.Normalize();

            // dot < 0 -> 夹角 > 90° -> 玩家在背后
            return Vector3.Dot(fwd, toPlayer) < 0f;
        }

        /// <summary>
        /// 设置瞄准圈 + 匕首的整体不透明度（0..1）。
        /// 冻结淡出时用它把目标层渐渐淡掉。
        /// </summary>
        private void ApplyAimAlpha(float alpha)
        {
            float a = Mathf.Clamp01(alpha);

            // 准星圈
            if (m_RingMat != null && m_RingMat.HasProperty("_Color"))
            {
                Color c = aimRingColor;
                c.a *= a;
                m_RingMat.SetColor("_Color", c);
            }

            // 匕首模型
            if (m_DaggerMat != null && m_DaggerMat.HasProperty("_Color"))
            {
                Color c = daggerColor;
                c.a *= a;
                m_DaggerMat.SetColor("_Color", c);
            }
        }

        /// <summary>
        /// 当前瞄准方向（单位向量，含俯仰）。
        /// 目前只用水平分量做朝向；保留这个接口供以后做俯仰瞄准。
        /// </summary>
        public Vector3 AimDirection()
        {
            float yawRad   = AimYaw   * Mathf.Deg2Rad;
            float pitchRad = AimPitch * Mathf.Deg2Rad;

            Vector3 dir = new Vector3(
                Mathf.Sin(yawRad) * Mathf.Cos(pitchRad),
                Mathf.Sin(pitchRad),
                Mathf.Cos(yawRad) * Mathf.Cos(pitchRad));

            if (dir.sqrMagnitude < 1e-6f) return Vector3.forward;
            return dir.normalized;
        }

        // ---------------------------------------------------------------- 外部接口

        /// <summary>强制退出瞄准（重玩 / 回首页时用）。</summary>
        public void CancelAim()
        {
            LeaveAim();
            m_ThrustActive = false;
        }
    }
}
