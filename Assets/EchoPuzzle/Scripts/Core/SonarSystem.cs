using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace EchoPuzzle
{
    /// <summary>声波从声源出发时，射线往哪个方向撒。</summary>
    public enum EmitMode
    {
        /// <summary>整个球面（呼喊 / 空中声源）。</summary>
        Sphere = 0,

        /// <summary>以某根轴为中心的一个半球。脚步 = 轴朝上（贴地辐射）。</summary>
        Hemisphere = 1,

        /// <summary>以某根轴为中心的一个圆锥。</summary>
        Cone = 2,

        /// <summary>固定仰角分层：只朝 0°（水平面）、30°、60° 三个仰角发射。</summary>
        ElevationLayers = 3,
    }

    /// <summary>一次发声的全部参数。</summary>
    [System.Serializable]
    public struct EmitParams
    {
        [Tooltip("镜面反射类：与地面法线夹角 90°（即水平面）的射线数量。这类射线反射时走严格镜面")]
        public int rayCount90;

        [Tooltip("镜面反射类：与地面法线夹角 30°（即相对水平面仰起 60°）的射线数量")]
        public int rayCount30;

        [Tooltip("镜面反射类：与地面法线夹角 60°（即相对水平面仰起 30°）的射线数量")]
        public int rayCount60;

        [Tooltip("单根射线的最大传播距离（米）")]
        public float range;

        [Tooltip("声速（米/秒）。决定波前扩散快慢")]
        public float speed;

        [Tooltip("沿射线的采样间距（米）。越小光带越顺滑，几何量越大")]
        public float trailSpacing;

        [Tooltip("初始不透明度 0..1。所有声波的衰减速度完全一致，" +
                 "所以这个值同时决定了这条光带能亮多久：越亮 = 撑得越久")]
        public float intensity;

        [Tooltip("【波前存在时长（秒）】从声波诞生起算，到点后波前**停止推进**，\n" +
                 "已经长出来的光带形状会钉在原地不再生长（但还会继续存在，见下一项）。\n" +
                 "填 0 或负数 = 波前不限时，一直推进到射程尽头。\n" +
                 "四种声波可以传不同的值，由 PlayerController 各自的 lifetimeCap 提供。")]
        public float lifetime;

        [Tooltip("【尾巴残留时长（秒）】波前停住之后，光带还能继续存在多久。\n" +
                 "这段时间里它会留在原地继续变暗，直到彻底看不见才消失。\n" +
                 "填 0 = 波前一停就立刻一起消失，不留尾巴。\n" +
                 "四种声波可以传不同的值，由 PlayerController 各自的 tailLinger 提供。")]
        public float tailLinger;

        [Tooltip("【每次反射扣掉的波前时长（秒）】每碰一次墙，这一小段的波前存在时长就减这么多。\n" +
                 "所以绕得越远的声波，能继续推进的时间越短 —— 比单纯限制反射次数自然得多。\n" +
                 "扣到 0 以下时，这条子射线**根本不会生成**，等于声波绕不动了。\n" +
                 "填 0 = 不扣（各次反射的时长都一样）。\n" +
                 "由 PlayerController 各自的 bounceLifeLoss 提供。")]
        public float bounceLifeLoss;

        [Tooltip("最大反射次数")]
        public int maxBounces;

        [Tooltip("每次反射后的强度衰减系数")]
        public float bounceFalloff;

        [Tooltip("初始射线的撒播方式。脚步用 Hemisphere（轴朝上）")]
        public EmitMode emitMode;

        [Tooltip("Hemisphere / Cone 模式下的中心轴（世界方向）。零向量 = 世界向上")]
        public Vector3 emitAxis;

        [Tooltip("Cone 模式下的半角（度）")]
        public float coneAngle;

        [Tooltip("反射之后子射线的扩散方式。单根镜面反射用 Sphere + 0 根子射线")]
        public EmitMode spreadMode;

        [Tooltip("Hemisphere / Cone 扩散的半角（度）。Cone=0 时退化为严格镜面反射")]
        public float spreadAngle;

        [Tooltip("漫反射类：与地面法线夹角 15°（接近朝天，几乎垂直向上）的射线数量。这类射线反射时走漫反射")]
        public int diffuseCount15;

        [Tooltip("漫反射类：与地面法线夹角 45°（即相对水平面仰起 45°）的射线数量")]
        public int diffuseCount45;

        [Tooltip("漫反射类：与地面法线夹角 75°（接近贴地，几乎水平）的射线数量")]
        public int diffuseCount75;

        // ---- 全上半球采样（与上面六层**并行**发射） ----

        [Tooltip("★ 全上半球采样的射线数量。0 = 不启用。\n\n" +
                 "与六层俯仰角采样**并行**：这一部分射线在整个上半球面上分布\n" +
                 "（余弦加权 Fibonacci，正对法线的方向更密），不受六层角度限制。\n" +
                 "方向同样以**脚下地面法线**为轴 —— 斜坡上会跟着坡面倾斜，和六层一致。\n\n" +
                 "这些射线反射时走镜面还是漫反射，由下面的开关决定。")]
        public int hemisphereCount;

        [Tooltip("半球采样出来的射线，反射时是否走漫反射（朗伯）。\n" +
                 "勾选 = 漫反射（在命中面上半球均匀散射）；\n" +
                 "取消 = 严格镜面（和 90°/30°/60° 那三层一样）。")]
        public bool hemisphereDiffuse;

        [Tooltip("声波线颜色。想要纯白就保持白色")]
        public Color color;

        [Tooltip("方位角的起点参考方向（世界方向，一般传玩家正前方的水平分量）。\n\n" +
                 "六层锥环的方位角、以及半球采样，都以这个方向为 0° 起算，\n" +
                 "所以\"相对正前方偏转半格\"才会跟着玩家转。\n" +
                 "零向量 = 回退到由地面法线推出的默认朝向。")]
        public Vector3 referenceDir;

        [Tooltip("★ 波前维持不透明度。\n\n" +
                 "勾选 = 刚被波前生成的那一段保持初始亮度，不会因为『波前冻住』而整体变暗；\n" +
                 "       但已经被扫过的轨迹照常渐渐变淡消失。\n" +
                 "取消 = 常规行为（波前冻住后整条一起变暗）。")]
        public bool holdAlpha;

        [Tooltip("光带宽度（米）。填 <= 0 = 沿用渲染器上的全局宽度。\n" +
                 "用来让某种声波比其它的更粗 / 更细")]
        public float width;

        [Tooltip("潜行产生的声波。怪物不会捕捉这种声波，玩家位置因此不会暴露")]
        public bool isSneak;

        [Tooltip("★ 这种声波**不会被怪物捕捉**，但仍会正常显示。\n\n" +
                 "用于环境 / 机关类的声波（比如 OperateObjects 触发区、\n" +
                 "怪物被移除时的消散声波）—— 它们只是为了视觉反馈，\n" +
                 "不该把怪物引过去。")]
        public bool ignoreMonster;

        [Tooltip("★ 怪物被刺杀时发出的声波。\n\n" +
                 "用途有两个：\n" +
                 "  1) 其它怪物可以捕捉它 —— 捕捉到就会进入受惊状态\n" +
                 "  2) 只要场上还有这种声波，怪物就不会因『匕首收回』而受惊")]
        public bool isKillSonar;

        /// <summary>默认值：一次标准脚步（贴地、向上半球辐射）。
        /// 注意声速只有 1.4 m/s —— 这是刻意的慢速，让肉眼能看清波前推进。</summary>
        public static EmitParams Default => new EmitParams
        {
            rayCount90     = 32,
            rayCount30     = 32,
            rayCount60     = 32,
            range          = 40f,
            speed          = 1.4f,
            trailSpacing   = 0.30f,
            intensity      = 0.55f,          // 初始不透明度：决定这条光带能亮多久
            lifetime       = 8f,             // 波前推进多久后钉住
            tailLinger     = 4f,             // 钉住之后尾巴还能残留多久
            maxBounces     = 3,
            bounceFalloff  = 0.65f,
            emitMode       = EmitMode.Hemisphere,
            emitAxis       = Vector3.zero,   // -> 世界向上
            coneAngle      = 90f,
            spreadMode     = EmitMode.Hemisphere,
            spreadAngle    = 90f,            // 反射后从命中面向外扩散
            diffuseCount15 = 8,
            diffuseCount45 = 8,
            diffuseCount75 = 8,
            color          = Color.white,
        };
    }

    /// <summary>
    /// 一套完整的声波发射参数，可在 Inspector 里逐项编辑，并能转成 <see cref="EmitParams"/>。
    ///
    /// 用途：让**每个 TriggerVolume 各自带一份**独立的声波数值，
    /// 触发时在玩家脚下放一次特殊声波。采样方式、消失方式和常规声波完全一致
    /// （同样六层俯仰角 + 上半球、同样受 lifetime / tailLinger / 不透明度影响），
    /// 只是数值可以单独调。
    ///
    /// 默认值按"空格发声"（shoutEcho）填。
    /// </summary>
    [System.Serializable]
    public struct SonarEmitSettings
    {
        [Header("射线数量（与常规声波同一套采样：六层 + 上半球）")]
        [Tooltip("与地面法线夹角 90°（水平）的射线数量。这类走严格镜面")]
        public int rayCount90;

        [Tooltip("与地面法线夹角 30°（相对水平面仰起 60°）的射线数量")]
        public int rayCount30;

        [Tooltip("与地面法线夹角 60°（相对水平面仰起 30°）的射线数量")]
        public int rayCount60;

        [Tooltip("与地面法线夹角 15°（接近朝天）的射线数量。是否漫反射见下面的开关")]
        public int diffuseCount15;

        [Tooltip("与地面法线夹角 45° 的射线数量")]
        public int diffuseCount45;

        [Tooltip("与地面法线夹角 75°（接近贴地）的射线数量")]
        public int diffuseCount75;

        [Tooltip("整个上半球均匀分布的射线数量（与上面六层并行发射）。0 = 不启用")]
        public int hemisphereCount;

        [Header("传播")]
        [Tooltip("单根射线的最大传播距离（米）")]
        public float range;

        [Tooltip("声速（米/秒）。波前扩散的快慢")]
        public float speed;

        [Tooltip("光带采样间距（米）。越小越顺滑，几何量越大")]
        public float trailSpacing;

        [Tooltip("最大反射次数")]
        public int maxBounces;

        [Tooltip("每次反射后的强度衰减系数")]
        public float bounceFalloff;

        [Tooltip("每次反射扣掉的波前存在时长（秒）。扣光后子射线不再生成")]
        public float bounceLifeLoss;

        [Header("寿命与不透明度")]
        [Tooltip("波前存在时长（秒）：到点后停止推进，形状钉住。0 = 不限时")]
        public float lifetime;

        [Tooltip("波前钉住后，光带还能残留多久（秒）")]
        public float tailLinger;

        [Tooltip("初始不透明度 0..1")]
        [Range(0f, 1f)] public float intensity;

        [Tooltip("★ 波前维持不透明度。\n\n" +
                 "勾选 = 刚被波前生成的那一段保持初始亮度，不会因为『波前冻住』而整体变暗；\n" +
                 "       但已经被扫过的轨迹照常渐渐变淡消失。\n" +
                 "取消 = 常规行为（波前冻住后整条一起变暗）。")]
        public bool holdAlpha;

        [Header("外观")]
        [Tooltip("光带颜色")]
        public Color color;

        [Tooltip("光带宽度（米）")]
        public float width;

        /// <summary>
        /// Win（终点）类触发区的默认光带样式：琥珀色强光柱，波前常亮。
        /// 和 <see cref="DefaultShout"/> 同为触发区标准样式；
        /// NextStage（地牢下一层）触发区默认也用这一套（与 Win 相同）。
        /// </summary>
        public static SonarEmitSettings DefaultWin => new SonarEmitSettings
        {
            // 只发水平那一层，而且发得很密 —— 贴着地面铺开一圈亮线
            rayCount90      = 240,
            rayCount30      = 80,
            rayCount60      = 0,
            diffuseCount15  = 0,
            diffuseCount45  = 0,
            diffuseCount75  = 0,
            hemisphereCount = 0,

            range           = 80f,
            speed           = 8f,
            trailSpacing    = 0.30f,
            maxBounces      = 6,
            bounceFalloff   = 1f,      // 每次反射不衰减
            bounceLifeLoss  = 0.35f,

            lifetime        = 10f,
            tailLinger      = 20f,
            intensity       = 1f,
            holdAlpha       = true,    // 波前常亮

            color           = new Color(255f / 255f, 185f / 255f, 0f, 1f),   // 琥珀色
            width           = 0f,      // 0 = 沿用渲染器的默认宽度
        };

        /// <summary>默认值 = 为空触发区定的一套"强光柱"参数。</summary>
        public static SonarEmitSettings DefaultShout => new SonarEmitSettings
        {
            // 只发水平那一层，而且发得很密 —— 贴着地面铺开一圈亮线
            rayCount90      = 240,
            rayCount30      = 80,
            rayCount60      = 0,
            diffuseCount15  = 0,
            diffuseCount45  = 0,
            diffuseCount75  = 0,
            hemisphereCount = 0,

            range           = 80f,
            speed           = 8f,
            trailSpacing    = 0.30f,
            maxBounces      = 6,
            bounceFalloff   = 1f,      // 每次反射不衰减
            bounceLifeLoss  = 0.35f,

            lifetime        = 10f,
            tailLinger      = 20f,
            intensity       = 1f,
            holdAlpha       = true,    // 波前常亮

            color           = new Color(255f / 255f, 185f / 255f, 0f, 1f),   // 琥珀色
            width           = 0f,      // 0 = 沿用渲染器的默认宽度
        };

        /// <summary>
        /// 死亡那一刻在玩家脚下放的那一次声波（怪物击杀也用它）。
        /// 沿用之前给 Lose 类触发区配的样式：纯红、细一些。
        /// </summary>
        public static SonarEmitSettings DefaultTrap => new SonarEmitSettings
        {
            rayCount90      = 120,
            rayCount30      = 40,
            rayCount60      = 0,
            diffuseCount15  = 0,
            diffuseCount45  = 0,
            diffuseCount75  = 0,
            hemisphereCount = 0,

            range           = 60f,
            speed           = 8f,
            trailSpacing    = 0.30f,
            maxBounces      = 4,
            bounceFalloff   = 1f,
            bounceLifeLoss  = 0.35f,

            lifetime        = 8f,
            tailLinger      = 12f,
            intensity       = 1f,
            holdAlpha       = true,

            color           = new Color(1f, 0f, 0f, 1f),   // 纯红
            width           = 0.15f,
        };

        /// <summary>
        /// 转成 <see cref="EmitParams"/>。
        /// <paramref name="referenceDir"/> 传玩家正前方，方位角才会相对玩家。
        /// 反射方式：六层按原设计（90/30/60 镜面、15/45/75 漫反射），
        /// 半球部分默认走镜面（想要漫反射可以在 TriggerVolume 上自行调整）。
        /// </summary>
        public EmitParams ToEmitParams(Vector3 referenceDir)
        {
            return new EmitParams
            {
                rayCount90     = Mathf.Max(0, rayCount90),
                rayCount30     = Mathf.Max(0, rayCount30),
                rayCount60     = Mathf.Max(0, rayCount60),
                diffuseCount15 = Mathf.Max(0, diffuseCount15),
                diffuseCount45 = Mathf.Max(0, diffuseCount45),
                diffuseCount75 = Mathf.Max(0, diffuseCount75),
                hemisphereCount   = Mathf.Max(0, hemisphereCount),
                hemisphereDiffuse = false,

                range          = Mathf.Max(0.5f, range),
                speed          = Mathf.Max(0.05f, speed),
                trailSpacing   = Mathf.Max(0.05f, trailSpacing),
                maxBounces     = Mathf.Clamp(maxBounces, 0, 8),
                bounceFalloff  = bounceFalloff,
                bounceLifeLoss = Mathf.Max(0f, bounceLifeLoss),

                lifetime       = Mathf.Max(0f, lifetime),
                tailLinger     = Mathf.Max(0f, tailLinger),
                intensity      = Mathf.Clamp01(intensity),
                holdAlpha      = holdAlpha,
                width          = Mathf.Max(0f, width),
                color          = color,
                referenceDir   = referenceDir,
                // 轴留零 -> SonarSystem 自己检测脚下地面法线（斜坡上跟着坡面）
                emitMode       = EmitMode.ElevationLayers,
                emitAxis       = Vector3.zero,
            };
        }
    }

    /// <summary>
    /// 声波传播核心。
    ///
    /// 传播模型：
    ///   1. Emit() 按 emitMode 在声源处撒出初始射线，每根射线作为一个事件按“路程/声速”排队。
    ///   2. 波前真正抵达某个事件点时，才在该点做一次 Physics raycast
    ///      —— 所以反射发生在正确的时刻与位置，而不是提前算好。
    ///   3. 命中后把这一整段交给 SonarTrailRenderer，变成一条会向前生长的连续光带；
    ///      再按“入射方向关于法线的反射”生成子射线，在命中面外侧的半球里扩散，继续排队。
    ///
    /// 物理查询走 RaycastCommand 批处理。
    /// </summary>
    public sealed class SonarSystem : MonoBehaviour
    {
        private const float CatchUpEpsilon    = 2f / 60f; // 允许略早发射，避免慢帧漏事件
        private const int   MaxRaysPerInvoke  = 1024;
        private const float SurfaceOffset     = 0.012f;   // 子射线起点沿法线抬起，避免自交
        private const int   MaxEventsPerWave  = 1600;     // 单个声源的射线事件上限，防止指数爆炸占用主线程

        [Tooltip("声波光带的渲染器（SonarTrailRenderer）")]
        public SonarTrailRenderer trailRenderer;

        [Header("射线物理")]
        [Tooltip("参与声波反射的碰撞层。默认排除 Ignore Raycast / Water / UI")]
        public LayerMask raycastMask = ~((1 << 2) | (1 << 4) | (1 << 5));

        [Tooltip("★ 玩家所在的层，会被**从声波射线里排除掉**。\n\n" +
                 "为什么必须排除：声源就在玩家脚下，也就是**角色控制器胶囊体的内部**。\n" +
                 "越接近竖直的射线（15°/30° 层）会在 0.02s 内打到自己的胶囊体，\n" +
                 "于是刚发射就被反射成向下的方向。\n\n" +
                 "⚠ 注意：如果你的玩家和关卡几何都在 Default 层，这里是**没法排除的** ——\n" +
                 "因为那样会连墙和地板一起排除掉。请先给玩家建一个专用层：\n" +
                 "菜单「EchoPuzzle → 给玩家创建并指定专用层」")]
        public LayerMask playerLayer;

        /// <summary>是否已经成功识别出玩家层（避免每帧重复查找）。</summary>
        private bool m_PlayerLayerResolved;

        /// <summary>
        /// 自动识别玩家所在的层。
        ///
        /// 为什么需要：SonarSystem 是运行时由 PlayerController 自动创建的，
        /// 所以它的 playerLayer 没法存进 Prefab、每次开新局都是 0。
        /// 这里在运行时读一次玩家所在的层补上。
        ///
        /// ★ 安全护栏：只有玩家**不在 Default(0) 层**时才接受。
        ///   因为关卡几何和玩家都在 0 层时，排除 0 层会把墙和地板也一起排除掉 ——
        ///   那比"打到玩家自己"严重得多。这种情况宁可维持现状并给出提示。
        /// </summary>
        private void ResolvePlayerLayer()
        {
            if (m_PlayerLayerResolved) return;

            // 检查器里已经手动指定过就不动它
            if (playerLayer.value != 0)
            {
                m_PlayerLayerResolved = true;
                return;
            }

            var player = FindObjectOfType<PlayerController>();
            if (player == null) return;

            int layer = player.gameObject.layer;

            if (layer == 0)
            {
                m_PlayerLayerResolved = true;
                Debug.LogWarning(
                    "[EchoPuzzle] 玩家还在 Default(0) 层，声波**无法**排除它 ——\n" +
                    "  关卡几何也都在 0 层，排除 0 层会连墙和地板一起排除掉。\n" +
                    "  表现：15°/30° 这两层刚发射(0.02s)就撞到自己的胶囊体，被反射成朝下。\n" +
                    "  修法：执行菜单 EchoPuzzle → 给玩家创建并指定专用层");
                return;
            }

            playerLayer        = 1 << layer;
            m_PlayerLayerResolved = true;

        }

        /// <summary>
        /// 声波射线实际用的掩码 = <see cref="raycastMask"/> 去掉 <see cref="playerLayer"/>。
        ///
        /// 只用在声波射线上（<see cref="ScheduleBatch"/>）。
        /// 发射轴的地面法线检测仍用 <see cref="raycastMask"/> ——
        /// 那条射线是往下打地板的，本来也够不到玩家头顶。
        /// </summary>
        public int EffectiveRaycastMask
        {
            get
            {
                ResolvePlayerLayer();

                int m = raycastMask.value;

                int p = playerLayer.value;
                if (p != 0) m &= ~p;

                return m;
            }
        }

        public int ActiveWaveCount => m_Waves.Count;

        // ---------------------------------------------------------------- 内部结构

        /// <summary>一个待发射的射线事件：波前什么时候到、从哪儿、往哪儿。</summary>
        private sealed class Node
        {
            public float   castTime;    // 绝对时间（Time.timeSinceLevelLoad）
            public Vector3 origin;
            public Vector3 dir;         // 单位向量
            public int     bounce;
            public float   energy;
            public float   travelDist;  // 波前从声源走到 origin 已经走过的路程（米）
            public float   spawnTime;   // **这一小段**诞生的绝对时刻（每次反射都会更新）
            public float   lifetime;    // **这一小段**剩余的波前存在时长（每次反射扣掉 bounceLifeLoss）
            public bool    isDiffuse;   // true = 漫反射类（反射时走朗伯漫反射）；false = 镜面反射类（走严格镜面）
        }

        private sealed class Wave
        {
            public EmitParams p;
            public Vector3    source;
            public float      speedInv;
            public float      originTime;   // 发射那一刻，用 Time.timeSinceLevelLoad 口径
            public float      lifetime;     // 初始波前存在时长（后续每次反射递减）
            public int        InFlight;     // 还有几个批次在飞（没落地前不能删这个波源）
            public bool       Finished;     // 已无待发射事件，且所有批次都已回收
            public readonly List<Node> Pending = new List<Node>();
        }

        private sealed class PendingBatch
        {
            public JobHandle                   handle;
            public NativeArray<RaycastCommand> cmds;
            public NativeArray<RaycastHit>     hits;
            public Wave                        wave;
            public Node[]                      nodes;
        }

        // ---------------------------------------------------------------- 状态

        private readonly List<Wave>         m_Waves   = new List<Wave>();
        private readonly List<PendingBatch> m_Batches = new List<PendingBatch>();
        private readonly List<Node>         m_Due     = new List<Node>(2048);

        private bool m_Ready;

        private void Awake()
        {
            m_Ready = true;
        }

        private void OnDestroy()
        {
            for (int i = 0; i < m_Batches.Count; i++) DisposeBatch(m_Batches[i]);
            m_Batches.Clear();
            m_Waves.Clear();
        }

        // ---------------------------------------------------------------- 公共 API

        /// <summary>发声。发射轴自动取脚下地面法线（斜坡上会跟着坡面走）。</summary>
        public void Emit(Vector3 position, EmitParams p)
        {
            Emit(position, p, Vector3.zero);
        }

        /// <summary>
        /// 如果玩家正**在 Gravel 区域内潜行**，就把这次的声速和波前存在时长换成 Gravel 指定的值。
        ///
        /// ★ "Lifetime Cap" 对应 <see cref="EmitParams.lifetime"/> ——
        ///   波前存在多久之后钉住不动。改成 1 秒意味着波前只推进 1 秒就停。
        /// </summary>
        private static void ApplyGravelSneakOverride(ref EmitParams p, Vector3 position)
        {
            var all = Gravel.All;

            for (int i = 0; i < all.Count; i++)
            {
                Gravel g = all[i];
                if (g == null) continue;
                if (!g.isActiveAndEnabled) continue;

                // 触发点必须在区域内，且玩家正在潜行
                if (!g.Contains(position)) continue;
                if (!g.SneakingInside) continue;

                p.speed    = Mathf.Max(0.05f, g.sneakSpeedOverride);
                p.lifetime = Mathf.Max(0f,   g.sneakLifetimeOverride);
                return;
            }
        }

        /// <summary>发声。explicitAxis 非零时用它代替脚下地面法线（一般不需要）。</summary>
        public void Emit(Vector3 position, EmitParams p, Vector3 explicitAxis)
        {
            if (!m_Ready) return;

            p.rayCount90  = Mathf.Clamp(p.rayCount90, 0, MaxRaysPerInvoke);
            p.rayCount30  = Mathf.Clamp(p.rayCount30, 0, MaxRaysPerInvoke);
            p.rayCount60  = Mathf.Clamp(p.rayCount60, 0, MaxRaysPerInvoke);
            p.range       = Mathf.Max(0.5f, p.range);
            p.speed       = Mathf.Max(0.05f, p.speed);
            p.trailSpacing = Mathf.Max(0.05f, p.trailSpacing);
            p.lifetime    = Mathf.Max(0f, p.lifetime);
            p.tailLinger  = Mathf.Max(0f, p.tailLinger);
            p.bounceLifeLoss = Mathf.Max(0f, p.bounceLifeLoss);
            p.maxBounces  = Mathf.Clamp(p.maxBounces, 0, 8);
            p.intensity   = Mathf.Clamp01(p.intensity);
            p.spreadAngle = Mathf.Clamp(p.spreadAngle, 0f, 90f);
            p.coneAngle   = Mathf.Clamp(p.coneAngle, 0f, 90f);
            p.diffuseCount15 = Mathf.Clamp(p.diffuseCount15, 0, MaxRaysPerInvoke);
            p.diffuseCount45 = Mathf.Clamp(p.diffuseCount45, 0, MaxRaysPerInvoke);
            p.diffuseCount75 = Mathf.Clamp(p.diffuseCount75, 0, MaxRaysPerInvoke);
            p.hemisphereCount = Mathf.Clamp(p.hemisphereCount, 0, MaxRaysPerInvoke);

            // ★ Gravel（碎石区）+ 潜行：覆盖声速与波前存在时长。
            //
            //   条件：玩家**在 Gravel 区域内**且**正在潜行**。
            //   只改这一次发声的参数，不影响其它区域的声波。
            ApplyGravelSneakOverride(ref p, position);

            // 发射轴：先取脚下地面法线（斜坡上随坡面倾斜），外部显式传轴时优先
            Vector3 axis = GetGroundNormal(position);
            if (explicitAxis.sqrMagnitude > 1e-6f) axis = explicitAxis.normalized;
            if (axis.sqrMagnitude < 1e-6f) axis = Vector3.up;

            var wave = new Wave
            {
                p          = p,
                source     = position,
                speedInv   = 1f / p.speed,
                originTime = Time.timeSinceLevelLoad,
                lifetime   = p.lifetime,
            };

            // 六层固定仰角，各自独立计数、独立算水平角。
            // 注意：角度是"与地面法线的夹角"——法线朝上，所以水平面 = 90°。
            //   镜面反射类（反射时走严格镜面）：90°（水平）、30°、60°
            //   漫反射类（反射时走漫反射）：15°、45°、75°
            //
            // 方位角的 0° 起点 = p.referenceDir（玩家正前方的水平分量），
            // 所以每层都"相对正前方偏转半格"，玩家转身图案跟着转。
            Vector3 refDir = p.referenceDir;
            float phase = PickRingPhase(axis, refDir, p);

            AddRing(wave, position, axis, refDir, 90f, p.rayCount90,     false, phase);
            AddRing(wave, position, axis, refDir, 30f, p.rayCount30,     false, phase);
            AddRing(wave, position, axis, refDir, 60f, p.rayCount60,     false, phase);
            AddRing(wave, position, axis, refDir, 15f, p.diffuseCount15, true, phase);
            AddRing(wave, position, axis, refDir, 45f, p.diffuseCount45, true, phase);
            AddRing(wave, position, axis, refDir, 75f, p.diffuseCount75, true, phase);

            // 与六层**并行**的另一路：整个上半球面采样。
            // 同样以脚下地面法线为轴，所以斜坡上会和六层一起倾斜。
            AddHemisphere(wave, position, axis, refDir, p.hemisphereCount, p.hemisphereDiffuse, phase);

            m_Waves.Add(wave);
        }

        /// <summary>
        /// 在整个上半球面上撒射线（余弦加权 Fibonacci，正对法线的方向更密）。
        ///
        /// 和 <see cref="AddRing"/> 并行使用：六层负责"固定俯仰角"的结构化探测，
        /// 这一路负责"整个上半球"的均匀覆盖。两者共用同一条地面法线轴，
        /// 也共用同一个方位角起点 <paramref name="referenceDir"/>。
        /// </summary>
        private void AddHemisphere(Wave wave, Vector3 origin, Vector3 axis,
                                   Vector3 referenceDir, int count, bool diffuse, float phaseDeg)
        {
            if (count <= 0) return;

            for (int i = 0; i < count; i++)
            {
                Vector3 dir = DirectionOnHemisphere(axis, referenceDir, i, count, phaseDeg);

                wave.Pending.Add(new Node
                {
                    castTime   = 0f,
                    origin     = origin,
                    dir        = dir,
                    bounce     = 0,
                    energy     = 1f,
                    travelDist = 0f,
                    spawnTime  = wave.originTime,
                    lifetime   = wave.lifetime,
                    isDiffuse  = diffuse,
                });
            }
        }

        /// <summary>
        /// 在某个固定仰角的锥环上撒一圈射线（水平角均匀分布）。
        /// 方位角起点 = <paramref name="referenceDir"/>。diffuse = 漫反射类标记。
        /// </summary>
        private void AddRing(Wave wave, Vector3 origin, Vector3 axis, Vector3 referenceDir,
                             float elevDeg, int count, bool diffuse, float phaseDeg)
        {
            if (count <= 0) return;

            for (int i = 0; i < count; i++)
            {
                Vector3 dir = DirectionOnRing(axis, referenceDir, elevDeg, i, count, phaseDeg);

                wave.Pending.Add(new Node
                {
                    castTime   = 0f,
                    origin     = origin,
                    dir        = dir,
                    bounce     = 0,
                    energy     = 1f,
                    travelDist = 0f,
                    spawnTime  = wave.originTime,
                    lifetime   = wave.lifetime,
                    isDiffuse  = diffuse,
                });
            }
        }

        private static float PickRingPhase(Vector3 axis, Vector3 referenceDir, EmitParams p)
        {
            int[] counts = { p.rayCount90, p.rayCount30, p.rayCount60,
                             p.diffuseCount15, p.diffuseCount45, p.diffuseCount75 };

            float stepMin = float.MaxValue;

            for (int i = 0; i < counts.Length; i++)
            {
                if (counts[i] <= 0) continue;

                stepMin = Mathf.Min(stepMin, 360f / counts[i]);
            }

            if (stepMin > 180f) return 0f;

            BuildTangentBasis(axis, referenceDir, out Vector3 t1, out Vector3 t2);
            float baseAz = Mathf.Atan2(t1.x, t1.z) * Mathf.Rad2Deg;

            float[] fracs = { 0f, 0.5f, 0.25f, 0.75f, 0.125f, 0.625f, 0.375f, 0.875f };

            float bestDelta = 0f;
            float bestGap   = -1f;

            for (int f = 0; f < fracs.Length; f++)
            {
                float delta = fracs[f] * stepMin;
                float gap   = MinCardinalGap(baseAz, delta, counts);

                if (gap > bestGap + 1e-4f)
                {
                    bestGap   = gap;
                    bestDelta = delta;
                }
            }

            return bestDelta;
        }

        private static float MinCardinalGap(float baseAz, float delta, int[] counts)
        {
            float minGap = 90f;

            for (int c = 0; c < counts.Length; c++)
            {
                int count = counts[c];

                if (count <= 0) continue;

                float step = 360f / count;

                for (int i = 0; i < count; i++)
                {
                    float m   = Mathf.Repeat(baseAz + delta + (i + 0.5f) * step, 90f);
                    float gap = Mathf.Min(m, 90f - m);

                    if (gap < minGap) minGap = gap;
                }
            }

            return minGap;
        }

        /// <summary>检测脚下地面法线：从位置上方一点点向下打一条短射线。失败时回退世界向上。</summary>
        private Vector3 GetGroundNormal(Vector3 pos)
        {
            if (Physics.Raycast(pos + Vector3.up * 0.05f, Vector3.down,
                                out RaycastHit hit, 0.5f, raycastMask,
                                QueryTriggerInteraction.Ignore))
            {
                if (hit.normal.sqrMagnitude > 1e-6f) return hit.normal.normalized;
            }
            return Vector3.up;
        }

        // ---------------------------------------------------------------- 主循环

        private void Update()
        {
            if (!m_Ready) return;

            float now = Time.timeSinceLevelLoad;

            // 1) 收拢所有到期事件，按波源分批调度
            for (int w = 0; w < m_Waves.Count; w++)
            {
                Wave wave = m_Waves[w];
                List<Node> pending = wave.Pending;

                m_Due.Clear();

                // 从后往前扫。命中就"交换删除"：把末尾元素搬到当前位置再砍掉末尾。
                // 因为是从后往前、且末尾元素已经被检查过（i 递减，末尾那项要么已处理、
                // 要么索引 >= i），直接 RemoveAt(i) 会让后面所有元素整体前移，
                // pending 上千时就是 O(n²)。交换删除是 O(1)。
                //
                // 顺序对结果没影响：m_Due 只是"这一批要发射的射线"，谁先谁后无所谓。
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    if (pending[i].castTime > now + CatchUpEpsilon) continue;

                    m_Due.Add(pending[i]);

                    int last = pending.Count - 1;
                    pending[i] = pending[last];
                    pending.RemoveAt(last);
                }

                if (m_Due.Count > 0) ScheduleBatch(wave, m_Due);
            }

            // 2) 回收已完成的批次
            for (int i = m_Batches.Count - 1; i >= 0; i--)
            {
                PendingBatch b = m_Batches[i];
                if (!b.handle.IsCompleted) continue;
                ProcessBatch(b);
                m_Batches.RemoveAt(i);
            }

            // 3) 收尾：**必须等所有批次都回收完**才能删波源。
            //
            //    这里以前是错的：批次刚调度出去（还没落地）就把波源从列表里删了，
            //    结果 ProcessBatch 命中墙面后生成的子射线全部挂在一个已经脱离列表的
            //    Wave 上 —— 反射从来没生效过，光带数永远是 0。
            for (int w = 0; w < m_Waves.Count; w++)
            {
                Wave wave = m_Waves[w];
                if (wave.InFlight == 0 && wave.Pending.Count == 0) wave.Finished = true;
            }
            m_Waves.RemoveAll(x => x.Finished);
        }

        // ---------------------------------------------------------------- 调度

        private void ScheduleBatch(Wave wave, List<Node> nodes)
        {
            int count = nodes.Count;
            if (count <= 0) return;

            var cmds = new NativeArray<RaycastCommand>(count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var hits = new NativeArray<RaycastHit>(count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

            // QueryParameters(layerMask, hitMultipleFaces, triggerInteraction, defaultDistance)
            //
            // ★ 用 EffectiveRaycastMask（= raycastMask 去掉 playerLayer）。
            //   声源在玩家脚下、也就是胶囊体内部，不排除的话最陡的两层会在
            //   0.02s 内打到自己的身体，刚发射就被反射成向下的方向。
            var qp = new QueryParameters(EffectiveRaycastMask, false, QueryTriggerInteraction.Ignore, false);

            // ★ 必须命中背面。
            //   声源在房间**内部**，射线打到穹顶 / 圆墙时命中的是内表面的背面
            //   （非凸 MeshCollider 不会把"从里面打出去"当成正面）。
            //   关掉的话射线会直接穿过墙体 —— 表现就是"波前碰到穹顶就消失"：
            //   没命中 -> 不铺光带，也不生成反射。
            qp.hitBackfaces = true;

            for (int i = 0; i < count; i++)
                cmds[i] = new RaycastCommand(nodes[i].origin, nodes[i].dir, qp);

            var handle = RaycastCommand.ScheduleBatch(cmds, hits, 32, 1, default);

            wave.InFlight++;
            wave.Finished = false;

            m_Batches.Add(new PendingBatch
            {
                handle = handle,
                cmds   = cmds,
                hits   = hits,
                wave   = wave,
                nodes  = nodes.ToArray(),
            });
        }

        private void ProcessBatch(PendingBatch b)
        {
            b.handle.Complete();

            Wave  wave     = b.wave;
            float speedInv = wave.speedInv;
            float range    = wave.p.range;

            for (int i = 0; i < b.nodes.Length; i++)
            {
                Node       n   = b.nodes[i];
                RaycastHit hit = b.hits[i];

                bool    didHit = hit.distance > 0f;
                Vector3 end    = didHit ? hit.point : n.origin + n.dir * range;
                float   segmentLen = didHit ? Vector3.Distance(n.origin, end) : range;

                // 没打到东西的射线不铺光带 —— 否则会向虚空里拖出一条长线
                if (didHit) AddTrailSegment(wave, n, end);

                if (!didHit) continue;
                if (n.bounce >= wave.p.maxBounces) continue;

                // 射线事件总量封顶，避免漫反射在长走廊里指数爆炸
                if (wave.Pending.Count >= MaxEventsPerWave) continue;

                float childEnergy = n.energy * wave.p.bounceFalloff;
                if (childEnergy < 0.05f) continue;

                // 每次反射扣掉一截波前存在时长。扣光了这条子射线就不生成 ——
                // 声波"绕不动了"，比单纯限制反射次数更自然。
                float childLifetime = n.lifetime - wave.p.bounceLifeLoss;
                if (wave.lifetime > 0f && childLifetime <= 0f) continue;

                Vector3 inDir = n.dir;
                Vector3 nrm   = hit.normal.sqrMagnitude > 1e-6f ? hit.normal.normalized : -inDir;

                // 按法线反射
                Vector3 refl = Vector3.Reflect(inDir, nrm);
                if (refl.sqrMagnitude < 1e-6f) refl = -inDir;
                refl.Normalize();

                Vector3 childOrigin = hit.point + nrm * SurfaceOffset;
                float   childCastAt = n.castTime + segmentLen * speedInv;

                // 反射后只产生一根子射线（不再分叉）：
                //   镜面反射类的射线 → 严格镜面方向 refl
                //   漫反射类的射线   → 在**命中面的上半球**上均匀取一个方向
                Vector3 childDir;
                if (n.isDiffuse)
                {
                    // ★ 与入射角**无关**：直接以命中面法线为轴，在上半球内均匀采样。
                    //
                    //   之前是"先算镜面方向，再按入射角限制最大偏移"，
                    //   所以出射角会跟着入射角变。现在改成纯上半球均匀分布。
                    childDir = DirectionOnHemisphereUniform(nrm);
                }
                else
                {
                    childDir = refl;
                }

                wave.Pending.Add(new Node
                {
                    castTime   = childCastAt,
                    origin     = childOrigin,
                    dir        = childDir,
                    bounce     = n.bounce + 1,
                    energy     = childEnergy,
                    travelDist = n.travelDist + segmentLen,
                    spawnTime  = Time.timeSinceLevelLoad,
                    lifetime   = childLifetime,
                    isDiffuse  = n.isDiffuse,
                });
            }

            DisposeBatch(b);

            b.wave.InFlight--;
        }

        private void DisposeBatch(PendingBatch b)
        {
            b.handle.Complete();
            if (b.cmds.IsCreated) b.cmds.Dispose();
            if (b.hits.IsCreated) b.hits.Dispose();
        }

        // ---------------------------------------------------------------- 光带生成

        /// <summary>
        /// 把 origin -> end 这一整段登记成一条声波光带（曳光弹）。
        ///
        /// 时间口径：整条波形共用同一个出发时刻 wave.originTime，
        /// 每个点的年龄严格等于 (当前时间 - 出发时刻) - 该点到声源的路程 / 声速。
        /// 所以不需要用 now 去反推 —— 反推会引入帧延迟误差。
        /// </summary>
        private void AddTrailSegment(Wave wave, Node n, Vector3 end)
        {
            EmitParams p = wave.p;

            Vector3 delta = end - n.origin;
            float segmentLength = delta.magnitude;
            if (segmentLength < 1e-4f) return;

            // 初始不透明度：纯白，只有 alpha 在变。反射越多次越淡
            float alpha = Mathf.Clamp01(p.intensity * n.energy);

            if (trailRenderer != null)
            {
                // 注意这里传的是 **n.spawnTime / n.lifetime**（这一小段自己的），
                // 不是 wave.originTime / p.lifetime。
                // 因为每反射一次 lifetime 都会被扣掉一截，波前应该从"这一小段诞生"起算，
                // 而不是从最初发声那一刻起算。
                trailRenderer.AddTrail(n.origin, delta / segmentLength, segmentLength,
                                       wave.originTime, p.speed, n.travelDist,
                                       alpha, p.trailSpacing, n.spawnTime, n.lifetime,
                                       p.tailLinger, p.holdAlpha, p.width,
                                       wave.source, p.isSneak, p.color, p.ignoreMonster, p.isKillSonar);
            }
        }

        // ---------------------------------------------------------------- 方向采样
        //
        // 初始射线只用两种采样，都以"脚下地面法线"为轴：
        //   DirectionOnRing        —— 六层固定俯仰角的锥环
        //   DirectionOnHemisphere  —— 整个上半球（Fibonacci 余弦加权）
        // 另外 DirectionOnHemisphereUniform 用于漫反射出射方向（以命中面法线为轴）。

        /// <summary>
        /// 用"轴 + 参考方向"构造一组正交切基 (t1, t2)。
        ///
        /// t1 = 参考方向在垂直于 axis 的平面上的投影，也就是**方位角 0° 所在的方向**。
        /// 这样方位角就是相对参考方向（玩家正前方）算的，玩家转身图案跟着转。
        ///
        /// 退化处理：参考方向与轴几乎平行时（投影长度趋零）无法定出 t1，
        /// 这时回退到"取任意垂直轴"—— 此时各方位角等价，取哪个都一样。
        /// </summary>
        private static void BuildTangentBasis(Vector3 axis, Vector3 referenceDir,
                                              out Vector3 t1, out Vector3 t2)
        {
            Vector3 n = axis.sqrMagnitude > 1e-6f ? axis.normalized : Vector3.up;

            // 参考方向投影到垂直于 n 的平面
            Vector3 refProj = referenceDir - n * Vector3.Dot(referenceDir, n);

            if (refProj.sqrMagnitude > 1e-6f)
            {
                t1 = refProj.normalized;
            }
            else
            {
                // 参考方向与轴平行 -> 任意取一个垂直方向
                Vector3 helper = Mathf.Abs(n.y) > 0.9f ? Vector3.right : Vector3.up;
                t1 = Vector3.Cross(n, helper).normalized;
            }

            t2 = Vector3.Cross(n, t1);
        }

        /// <summary>
        /// 以 axis 为中心的上半球上**均匀**取一个方向（按立体角均匀）。
        ///
        /// 做法：cosθ 在 [0,1] 上均匀取值 —— 这正是"立体角均匀"的充要条件
        /// （若按 cosθ 做余弦加权就退化成朗伯漫反射了）。
        /// 方位角 φ 均匀取 [0, 2π)。
        ///
        /// 用于漫反射：出射方向只由命中面法线决定，**不受入射角影响**。
        /// </summary>
        private static Vector3 DirectionOnHemisphereUniform(Vector3 axis)
        {
            Vector3 n = axis.sqrMagnitude > 1e-6f ? axis.normalized : Vector3.up;

            Vector3 helper = Mathf.Abs(n.y) > 0.9f ? Vector3.right : Vector3.up;
            Vector3 t1 = Vector3.Cross(n, helper).normalized;
            Vector3 t2 = Vector3.Cross(n, t1);

            float cosT = Random.value;                       // [0,1] 均匀 -> 立体角均匀
            float sinT = Mathf.Sqrt(Mathf.Max(0f, 1f - cosT * cosT));
            float phi  = Random.value * Mathf.PI * 2f;

            return (n * cosT + t1 * (sinT * Mathf.Cos(phi)) + t2 * (sinT * Mathf.Sin(phi)))
                   .normalized;
        }

        /// <summary>
        /// 以 axis 为中心的一个半球。用 Fibonacci 采样 + 余弦权重，
        /// 等价于朗伯（漫反射）分布：正对轴的方向更密。
        ///
        /// 方位角的 0° 由 <paramref name="referenceDir"/> 决定（见 BuildTangentBasis）。
        /// </summary>
        private static Vector3 DirectionOnHemisphere(Vector3 axis, Vector3 referenceDir,
                                                     int index, int total, float phaseDeg)
        {
            if (total <= 1) return axis.sqrMagnitude > 1e-6f ? axis.normalized : Vector3.up;

            Vector3 n = axis.sqrMagnitude > 1e-6f ? axis.normalized : Vector3.up;

            BuildTangentBasis(n, referenceDir, out Vector3 t1, out Vector3 t2);

            float k     = (index + 0.5f) / total;
            float cosT  = Mathf.Sqrt(1f - k);          // 余弦权重
            float sinT  = Mathf.Sqrt(Mathf.Max(0f, 1f - cosT * cosT));
            float theta = Mathf.PI * (1f + Mathf.Sqrt(5f)) * (index + 0.5f) + phaseDeg * Mathf.Deg2Rad;

            return (n * cosT + t1 * (sinT * Mathf.Cos(theta)) + t2 * (sinT * Mathf.Sin(theta))).normalized;
        }

        /// <summary>
        /// 固定仰角的单个锥环：绕 axis 转一圈，水平角公式 (index+0.5)/count × 360°。
        /// 也就是**相对参考方向偏转"间隔的一半"** —— count = 8 时偏 22.5°。
        ///
        /// 方位角的 0° 由 <paramref name="referenceDir"/> 决定（见 BuildTangentBasis），
        /// 传玩家正前方时整个锥环就跟着玩家转。
        ///
        /// count 小于等于 1 时只有一条，落在 180° 方位角。
        /// </summary>
        private static Vector3 DirectionOnRing(Vector3 axis, Vector3 referenceDir,
                                                float elevationDeg, int index, int count, float phaseDeg)
        {
            Vector3 n = axis.sqrMagnitude > 1e-6f ? axis.normalized : Vector3.up;

            BuildTangentBasis(n, referenceDir, out Vector3 t1, out Vector3 t2);

            float elev = elevationDeg * Mathf.Deg2Rad;
            float phi  = count <= 1
                       ? Mathf.PI + phaseDeg * Mathf.Deg2Rad
                       : (index + 0.5f) / count * Mathf.PI * 2f + phaseDeg * Mathf.Deg2Rad;

            float cosT = Mathf.Cos(elev);
            float sinT = Mathf.Sin(elev);

            return (n * cosT
                  + t1 * (sinT * Mathf.Cos(phi))
                  + t2 * (sinT * Mathf.Sin(phi))).normalized;
        }
    }
}
