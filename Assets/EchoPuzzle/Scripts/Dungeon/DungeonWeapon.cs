using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace EchoPuzzle.Dungeon
{
    /// <summary>
    /// 【地牢模式】玩家的武器：瞄准 + 射击 + 蓝耗。
    ///
    /// ── 操作（照文档第 19 段）────────────────────────────────
    ///   · 按住右键（aimKey）进入瞄准：脚下出现圆环，环上浮起一把武器
    ///   · 瞄准时**鼠标不再转镜头**，而是绕玩家旋转武器（和匕首/投石同一套极坐标）
    ///   · 按住 / 单击左键（fireKey）发射，受 fireInterval 限制
    ///     —— 单击再快也不会超过这个射速
    ///   · 松开右键退出瞄准
    ///
    /// ── 瞄准数学（照抄 DaggerSystem.HandleAimMouse）──────────
    ///   · 按下右键那一刻：记下相机方位角 m_AimBaseYaw，虚拟光标归零
    ///   · 之后每帧：m_VirtualCursor += (Mouse X, Mouse Y) × 20
    ///   · θ  = atan2(dy, dx)            （右=0°、上=90°、左=180°、下=270°）
    ///   · 方位角 = m_AimBaseYaw + (90° - θ)
    ///   · **只取 θ，无视半径 r** —— 和匕首完全一致（投石那边才用 r 当力度）
    ///
    ///   ★ 因此武器始终在水平面上绕玩家转，俯仰恒为 0。
    ///     这和匕首"永远在水平面上"是同一个设定；怪物受击圆柱高 4 米，
    ///     水平弹道从枪口高度打过去依然能命中。
    ///
    /// ── 镜头冻结 ────────────────────────────────────────────
    ///   需要 <see cref="PlayerController"/> 在 HandleLook 里放行，
    ///   写法和它已有的 StoneThrowSystem / DaggerSystem 判断同款（3 行）。
    ///
    /// ── 和探秘模式的关系 ────────────────────────────────────
    ///   ★ 只有 <see cref="DungeonWeaponBootstrap"/> 在地牢场景里会创建它；
    ///     PlayerController 里那句判断靠 Instance 判空，探秘模式下永远为 null，
    ///     所以对探秘模式零影响。
    /// </summary>
    [AddComponentMenu("Echo Puzzle/Dungeon/Dungeon Weapon")]
    [DefaultExecutionOrder(-50)]
    public sealed class DungeonWeapon : MonoBehaviour
    {
        public static DungeonWeapon Instance { get; private set; }

        // ------------------------------------------------------------------ 输入

        [Header("输入")]
        [Tooltip("按住进入瞄准（松开退出）")]
        public KeyCode aimKey = KeyCode.Mouse1;

        [Tooltip("瞄准时按住 / 单击都会发射")]
        public KeyCode fireKey = KeyCode.Mouse0;

        // ------------------------------------------------------------------ 瞄准

        [Header("瞄准")]
        [Tooltip("鼠标位移换算成虚拟像素的比例。只影响「要拖多远才转到位」，不影响 1:1 跟随")]
        [Min(1f)] public float virtualCursorPixelsPerUnit = 20f;

        [Tooltip("鼠标位移小于多少虚拟像素时视为「还没离开原点」，武器保持朝正前方")]
        [Min(0f)] public float dontMoveEps = 2f;

        // ------------------------------------------------------------------ 射击

        [Header("射击")]
        [Tooltip("两发之间的最小间隔（秒）。按住和连点都受它限制。\n\n" +
                 "0.18 ≈ 每秒 5.5 发（偏连发枪，太快）\n" +
                 "0.25 ≈ 每秒 4 发\n" +
                 "0.32 ≈ 每秒 3 发（半自动手感）\n" +
                 "0.45 ≈ 每秒 2.2 发（重枪）\n" +
                 "0.5  ≈ 每秒 2 发（**当前生效值**）\n\n" +
                 "★ 这是**基础值**，buff 的 fireRateMul 会再除它（射速倍率封顶 ×8）。")]
        [Min(0.02f)] public float fireInterval = 0.5f;

        // ── 蓝量经济（这几个数要一起看，单独调一个会失衡）──────────
        //
        //   怪物 12 血。当前一组默认值的账：
        //     3 发打死（damage 4）  -> 花 3 × 2 = 6 蓝
        //     击杀回 12 蓝          -> 一只怪净赚 +6
        //
        //   所以：
        //     · 满蓝 100 = 连开 50 发 = 约 16 只怪，**不需要中途回蓝**
        //     · 每只净赚 6，一场 10 只的怪潮净赚 60，蓝越打越多
        //     · 打空 3 发 = 亏掉一只怪的收益 -> 打得准仍然有意义
        //     · 另外 DungeonMana 还有 1.5/秒 的被动回复兜底（见那边注释）
        //
        //   ★ 调参口诀：想更宽松 -> 降 manaCost；想更硬核 -> 升 manaCost
        //     但 killManaRestore 要**明显大于**「杀一只所需的蓝」，
        //     否则杀得越多亏得越多（上一版 damage=1/蓝耗 5/回 15 就是这个错）。

        [Tooltip("单发蓝耗。★ 支持小数（保留三位）")]
        [Min(0f)] public float manaCost = 1f;

        [Header("连发装置")]
        [Tooltip("★ 「连发装置」后续每一轮之间的间隔（秒）。需求：0.1\n\n" +
                 "★ 第一轮是单击当帧立刻打出的，从第二轮起每隔这么久打一轮。")]
        [Min(0f)] public float volleyInterval = 0.1f;

        [Header("精准度")]
        [Tooltip("★ 子弹发射时的随机水平偏转角（度）。\n\n" +
                 "需求：随机偏转 5° ~ -5°\n" +
                 "（比如填 5，实际会在 -5° ~ +5° 之间均匀随机）\n\n" +
                 "★ 只影响**实际飞行方向**，不影响瞄准环 / 辅助线 ——\n" +
                 "  瞄准辅助线仍然画的是「完全不偏」的理想方向。")]
        [Min(0f)] public float bulletSpreadDegrees = 4f;

        [Tooltip("单发伤害。怪物血量见 PuzzleMonster.dungeonMaxHealth（默认 12）。\n★ 支持小数（保留三位）")]
        [Min(0.001f)] public float damage = 4f;

        [Tooltip("打死一只怪回复的蓝。★ 支持小数（保留三位）\n" +
                 "应与「杀一只所需的蓝」大致相当，明显更高就会越打越富")]
        [Min(0f)] public float killManaRestore = 12f;

        // ------------------------------------------------------------------ 子弹

        [Header("子弹")]
        [Tooltip("子弹飞行速度（米/秒）")]
        [Min(1f)] public float bulletSpeed = 35f;

        [Tooltip("最大射程（米）")]
        [Min(1f)] public float bulletRange = 40f;

        [Tooltip("拖尾宽度（米）")]
        [Min(0.01f)] public float bulletWidth = 0.1f;

        [Tooltip("子弹光带颜色")]
        public Color bulletColor = new Color(0.25f, 0.55f, 1f, 1f);

        [Tooltip("拖尾长度（米）。只画波前身后这么长的一段。\n\n" +
                 "★ 太长（比如跟射程一样）时，每秒好几发会首尾相接，\n" +
                 "看起来像一根连续的光柱，会让人误以为“发射没有间隔”。")]
        [Min(0.2f)] public float bulletTrailLength = 2f;

        // ★ 原来的 bulletFadeTime 已移除：
        //   现在光带上每个发光点都是**生成后 0.5 秒**自然淡出，
        //   由 DungeonBullet.pointFadeTime 控制（默认 0.5）。

        [Header("怪物受击圆柱（实体只有 0.5，这里放大到能打到）")]
        [Tooltip("水平半径（米）。普通怪物 1 米；boss 用 PuzzleMonster.dungeonHurtRadius 单独覆盖成 2 米")]
        [Min(0.1f)] public float monsterHurtRadius = 1f;

        [Tooltip("竖直总高（米）")]
        [Min(0.1f)] public float monsterHurtHeight = 4f;

        [Header("手刀（未瞄准时单击左键）")]
        [Tooltip("是否启用手刀：没按住右键瞄准时，单击左键挥出手刀")]
        public bool enableHandBlade = true;

        [Tooltip("手刀伤害（对扇形内的每只怪物）")]
        [Min(1)] public int handBladeDamage = 4;

        [Tooltip("★ 手刀伤害**每进入新一层**的倍率。需求：1.2\n\n" +
                 "★ 第 1 层 ×1.0，第 2 层 ×1.2，第 3 层 ×1.44 …")]
        [Min(1f)] public float handBladeDamagePerStage = 1.2f;

        [Tooltip("手刀半径（米，只看水平距离）")]
        [Min(0.5f)] public float handBladeRadius = 2.8f;

        [Tooltip("手刀扇形半角（度）。60 = 以玩家朝向为中心的 120° 扇形")]
        [Range(10f, 180f)] public float handBladeHalfAngle = 60f;

        [Tooltip("手刀冷却（秒）")]
        [Min(0.05f)] public float handBladeCooldown = 0.3f;

        [Tooltip("命中怪物后的冷却（秒）—— 需求：命中把 0.3s 缩到 0.1s")]
        [Min(0.02f)] public float handBladeHitCooldown = 0.1f;

        [Tooltip("手刀光效颜色")]
        public Color handBladeColor = new Color(0.35f, 0.65f, 1f, 1f);

        [Header("手刀特效（线段扫描）")]
        [Tooltip("★ 线段扫过整个扇形所需时间（秒）。需求：0.05")]
        [Min(0.02f)] public float bladeSweepSeconds = 0.05f;

        [Tooltip("★ 手刀扇形里画多少根线段（越多越密集）。\n\n" +
                 "★ 这些线段会沿扇形角度均匀铺开，然后一起淡出。\n" +
                 "★ 数值越大越密，也越费性能（池子会相应变大）。")]
        [Range(2f, 360f)] public float bladeSweepLineCount = 24f;

        [Tooltip("★ 每根线段留在原地后多久淡完（秒）。需求：0.18")]
        [Min(0.02f)] public float bladeLineLingerSeconds = 0.2f;

        [Tooltip("线段的粗细（米）")]
        [Min(0.002f)] public float bladeLineWidth = 0.1f;

        [Tooltip("（旧）整个扇形闪光的总时长。现在由上面三个参数控制，保留只为兼容")]
        [Min(0.05f)] public float handBladeFxTime = 0.18f;



        // ------------------------------------------------------------------ 瞄准表现

        [Header("瞄准表现")]
        [Tooltip("脚下圆环半径（米）")]
        [Min(0.2f)] public float ringRadius = 1.2f;

        public Color ringColor = new Color(1f, 1f, 1f, 0.55f);

        [Tooltip("圆环离地高度（米），抬高一点避免和地面 z-fighting")]
        public float ringGroundOffset = 0.03f;

        [Tooltip("武器绕玩家旋转的半径（米）。建议略小于 ringRadius")]
        [Min(0.1f)] public float weaponOrbitRadius = 0.9f;

        [Tooltip("武器离地高度（米），也就是枪口高度")]
        public float weaponHeight = 1.15f;

        [Tooltip("枪口相对武器再往前伸出的距离（米）")]
        public float muzzleForward = 0.54f;

        public Color weaponColor = new Color(1f, 1f, 1f, 0.9f);

        [Tooltip("瞄准辅助线长度（米）。瞄准时镜头被冻结、屏幕上没有准星，靠它指示子弹方向")]
        [Min(1f)] public float guideLength = 8f;

        [Tooltip("瞄准辅助线宽度（米）")]
        [Min(0.002f)] public float guideWidth = 0.015f;

        public Color guideColor = new Color(1f, 1f, 1f, 0.35f);

        // ------------------------------------------------------------------ 准星（跟随鼠标）

        [Header("准星（跟随鼠标）")]
        [Tooltip("是否显示准星。\n\n" +
                 "★ 地牢模式的光标是锁定的（屏幕上没有系统指针），" +
                 "所以由这里画一个准星告诉玩家\"鼠标在哪\"。")]
        public bool showCrosshair = true;

        [Header("短剑图标（未瞄准时）")]
        [Tooltip("★ 未处于右键瞄准状态时，在鼠标位置画一把短剑。\n\n" +
                 "需求：不瞄准时鼠标位置显示短剑图标，但仍然不显示指针。\n" +
                 "关闭后未瞄准时完全不画东西（和以前一样）。")]
        public bool showBladeIcon = true;

        [Tooltip("短剑图标颜色")] 
        public Color bladeIconColor = new Color(1f, 1f, 1f, 0.75f);

        [Tooltip("短剑图标大小（像素 @1080p 高度）")]
        [Min(4f)] public float bladeIconSize = 36f;

        [Tooltip("★ 剑身倾斜角度（度）。正数 = 剑身往右下延伸（看起来像指针那样向左上斜）。\n\n" +
                 "需求：约 30°。0 = 完全竖直。")]
        [Range(-80f, 80f)] public float bladeIconTiltDegrees = 30f;

        [Tooltip("★ 剑尖相对指针的微调偏移（像素，y 向下为正）。\n\n" +
                 "★ 图标以**剑尖**为锚点，所以默认 (0,0) 就是剑尖正好压在指针上。\n" +
                 "想让剑稍微偏一点再调这里。")]
        public Vector2 bladeIconOffset = new Vector2(0f, 0f);

        [Tooltip("准星半径（屏幕像素，按 1080 高换算）")]
        [Min(2f)] public float crosshairRadius = 13f;

        [Tooltip("圆心小点的半径（像素）。0 = 不画圆点")]
        [Min(0f)] public float crosshairDotRadius = 2f;

        [Tooltip("每一根刻线向外伸出多少像素")]
        [Min(0f)] public float crosshairTickLength = 7f;

        [Tooltip("刻线宽度（像素）")]
        [Min(1f)] public float crosshairThickness = 2f;

        [Tooltip("准星颜色（未瞄准时）")]
        public Color crosshairColor = new Color(1f, 1f, 1f, 0.65f);

        [Tooltip("准星颜色（瞄准时 —— 更亮，提示\"现在可以开火\"）")]
        public Color crosshairAimColor = new Color(1f, 0.95f, 0.5f, 0.95f);

        // ------------------------------------------------------------------ 运行时

        /// <summary>是否正在瞄准。</summary>
        public bool IsAiming { get; private set; }

        /// <summary>当前瞄准的世界方位角（度）。0 = +Z，90 = +X。</summary>
        public float AimYaw { get; private set; }

        /// <summary>俯仰恒为 0（和匕首一致，武器只在水平面绕玩家转）。</summary>
        public float AimPitch => 0f;

        /// <summary>最近一次成功发射的时间（Time.timeSinceLevelLoad）。</summary>
        public float LastFireTime { get; private set; } = -999f;

        /// <summary>累计发射数（调试 / buff 统计用）。</summary>
        public int ShotsFired { get; private set; }

        // ------------------------------------------------------------------ 当前有效数值（含 buff）

        // ★ 这里是「基础值 × buff 乘区」的唯一出处：
        //   开火、HUD 显示、以后做数值面板都读这几个属性，
        //   避免同一套公式在三个地方各写一遍然后慢慢写歪。

        private static float Mul(System.Func<DungeonBuffState, float> pick)
        {
            DungeonBuffState b = DungeonBuffState.Instance;
            return b != null ? pick(b) : 1f;
        }

        /// <summary>保留三位小数（顺便消掉 float 的表示误差）。</summary>
        public static float Round3(float v)
        {
            return Mathf.Round(v * 1000f) / 1000f;
        }

        /// <summary>
        /// 含 buff 后的单发蓝耗。
        ///
        /// ★★ 保留三位小数 ★★
        ///
        /// ★★ 计算顺序：(基础 + 加法加成) × 乘区 ★★
        ///
        ///   需求："『连发装置』增加的基础蓝耗应当能吃到其他蓝耗buff的乘算效果。"
        ///
        ///   ★ 以前是 manaCost × 乘区 + 加法 —— 加法那部分在乘区**之外**，
        ///     所以「连发装置 +0.5」不会因为「省电模式 ×0.75」而变少，
        ///     也不会因为「高爆弹头 ×1.75」而变多，等于游离在体系外。
        ///
        ///   ✔ 现在先把加法并进基础值，再整体乘乘区：
        ///       连发 ×1（+0.5）+ 省电 ×0.75  ->  (1 + 0.5) × 0.75 = 1.125
        ///       连发 ×1（+0.5）+ 高爆 ×1.75  ->  (1 + 0.5) × 1.75 = 2.625
        /// </summary>
        public float EffectiveManaCost
            => Round3(Mathf.Max(0f, (manaCost + ManaCostAdd)
                                     * Mul(s => s.ManaCostMul)
                                     * OverflowManaCostMul));

        // ------------------------------------------------------------------ 射速封顶（第十一周）

        /// <summary>
        /// 射速倍率上限。任务书："为了避免性能开销过高，限制玩家的射速最多 ×8"。
        /// </summary>
        public const float MaxFireRateMul = 8f;

        /// <summary>buff 叠出来的**原始**射速倍率（可能超过 8）。</summary>
        public float RawFireRateMul => Mul(s => s.FireRateMul);

        /// <summary>真正生效的射速倍率（封顶 ×8，保留三位小数）。</summary>
        public float EffectiveFireRateMul => Round3(Mathf.Min(MaxFireRateMul, RawFireRateMul));

        /// <summary>
        /// 溢出射速转成的**伤害倍率**：y = x / 8（x = 原始射速倍率）。
        ///
        /// ★ 例：13.122 倍 -> 13.122 / 8 = 1.64025 -> 保留三位 = 1.640
        /// </summary>
        public float OverflowDamageMul
        {
            get
            {
                float x = RawFireRateMul;
                if (x <= MaxFireRateMul) return 1f;

                return Round3(x / MaxFireRateMul);
            }
        }

        /// <summary>
        /// 溢出射速转成的**蓝耗倍率**：z = y² / 1.2（y = 上面的伤害倍率）。
        ///
        /// ★ 例：y = 1.640 -> 1.640² / 1.2 = 2.6896 / 1.2 = 2.24133 -> 2.241
        /// </summary>
        public float OverflowManaCostMul
        {
            get
            {
                float y = OverflowDamageMul;
                if (y <= 1f) return 1f;

                return Round3(y * y / 1.2f);
            }
        }

        /// <summary>
        /// 当前「霰弹改造」等级对应的扇形总角度（度）。
        ///
        /// ★ 需求："等级越高，扇形角度也越大，一级为 16°，二级为 32°，三级为 48°。"
        ///
        ///   ★ 等级来源：DungeonBuffState.ExtraPellets / 每级加的数量。
        ///     但更直接的做法是看「霰弹改造」这个 buff 的层数。
        ///   ★ 没拿过这个 buff 时返回数组第一项（16°），
        ///     反正那时只有 1 颗子弹，角度用不上。
        /// </summary>
        public float EffectiveShotgunArc
        {
            get
            {
                if (shotgunArcByLevel == null || shotgunArcByLevel.Length == 0)
                    return 16f;

                int lv = ShotgunLevel;                       // 0 = 没拿过

                int idx = Mathf.Clamp(lv - 1, 0, shotgunArcByLevel.Length - 1);

                return Mathf.Max(1f, shotgunArcByLevel[idx]);
            }
        }

        /// <summary>「霰弹改造」当前叠了几级（0 = 没拿过）。</summary>
        private static int ShotgunLevel
        {
            get
            {
                var b = DungeonBuffState.Instance;
                if (b == null) return 0;

                // ★ 第十一周：紫 / 橙的霰弹要**合并等级**，所以按 effectId 汇总
                return Mathf.Max(0, b.MechanicLevel("shotgun"));
            }
        }

        /// <summary>
        /// 含层数缩放后的手刀伤害。
        ///
        /// ★ 需求："让手刀的伤害每进入新一层后乘 1.2。"
        ///
        ///   ★ 层数缩放直接复用 <see cref="DungeonBuffState.StageScale"/> 的思路：
        ///     但那个是 1.25，手刀要 1.2，所以这里自己按层号算。
        ///   ★ 第 1 层 = ×1.0，第 2 层 = ×1.2，第 3 层 = ×1.44 …
        /// </summary>
        public int EffectiveHandBladeDamage
        {
            get
            {
                int stage = DungeonBuffState.StageIndex;

                float scale = 1f;
                for (int i = 1; i < stage; i++) scale *= handBladeDamagePerStage;

                return Mathf.Max(1, Mathf.RoundToInt(handBladeDamage * scale));
            }
        }

        /// <summary>buff 的蓝耗**加法**加成（「连发装置」每层 +0.5）。</summary>
        private static float ManaCostAdd
        {
            get
            {
                var b = DungeonBuffState.Instance;
                return b != null ? b.ManaCostAdd : 0f;
            }
        }

        /// <summary>这一次单击要打几轮（1 = 单轮；「连发装置」每层 +1）。</summary>
        public int EffectiveVolleys
        {
            get
            {
                var b = DungeonBuffState.Instance;
                return 1 + Mathf.Max(0, b != null ? b.ExtraVolleys : 0);
            }
        }

        /// <summary>子弹撞墙后能反弹几次。</summary>
        public int EffectiveBounces
        {
            get
            {
                var b = DungeonBuffState.Instance;
                return Mathf.Max(0, b != null ? b.BounceCount : 0);
            }
        }

        /// <summary>子弹能穿过几只怪。</summary>
        public int EffectivePierce
        {
            get
            {
                var b = DungeonBuffState.Instance;
                return Mathf.Max(0, b != null ? b.PierceCount : 0);
            }
        }

        /// <summary>
        /// 含 buff 后的单发伤害。
        ///
        /// ★★ 保留三位小数 ★★
        /// </summary>
        public float EffectiveDamage
            => Round3(Mathf.Max(0.001f, damage * Mul(s => s.DamageMul) * OverflowDamageMul));

        /// <summary>
        /// 含 buff 后的击杀回蓝。
        ///
        /// ★ 再乘当前层的缩放系数（每层 ×1.25）。
        /// ★★ 保留三位小数 ★★
        /// </summary>
        public float EffectiveKillMana
            => Round3(Mathf.Max(0f,
                   killManaRestore * Mul(s => s.KillManaMul) * DungeonBuffState.StageScale));

        /// <summary>含 buff 后的射击间隔（秒）。</summary>
        public float EffectiveFireInterval
            => Mathf.Max(0.02f, fireInterval / Mathf.Max(0.1f, EffectiveFireRateMul));

        /// <summary>含 buff 后的每秒射速（发/秒），给 HUD 显示用。</summary>
        public float EffectiveShotsPerSecond => 1f / Mathf.Max(0.02f, EffectiveFireInterval);

        /// <summary>含 buff 后的子弹速度。</summary>
        public float EffectiveBulletSpeed
            => bulletSpeed * Mul(s => s.BulletSpeedMul);

        /// <summary>含 buff 后的拖尾宽度。</summary>
        public float EffectiveBulletWidth
            => bulletWidth * Mul(s => s.BulletWidthMul);

        private PlayerController m_Player;

        private Transform m_RingRoot;
        private Transform m_WeaponRoot;
        private Mesh      m_RingMesh;
        private LineRenderer m_GuideLine;

        private Material m_RingMat;
        private Material m_WeaponMat;
        private Material m_BulletMat;
        private Material m_GuideMat;

        private float   m_NextFireTime;

        private float   m_NextBladeTime;

        /// <summary>
        /// 按 Mouse X/Y 增量积分出来的虚拟指针位置（屏幕像素，y 向上）。
        ///
        /// ★ 地牢模式光标是 Locked（屏幕上没有系统指针），
        ///   所以只能用增量自己积分出一个"逻辑指针"。
        ///   准星画在这里，武器也朝这里所指的地面点。
        /// </summary>
        private Vector2 m_VirtualCursor;

        /// <summary>
        /// 虚拟指针的屏幕位置（像素）。
        ///
        /// ★ 地牢模式光标是锁定的（屏幕上没有真实指针），
        ///   这个值由 Mouse X/Y 增量积分得到 —— 效果和真实指针一致。
        ///   准星画在这里，武器也朝这里。
        /// </summary>
        public Vector2 CrosshairScreen { get; private set; }

        private Texture2D m_CrossTex;

        // ------------------------------------------------------------------ 生命周期

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }

            Instance = this;

            m_RingMat   = EchoRuntimeMaterials.AimOverlay(ringColor);
            m_WeaponMat = EchoRuntimeMaterials.AimOverlay(weaponColor);
            m_BulletMat = EchoRuntimeMaterials.AimOverlay(bulletColor);
            m_GuideMat  = EchoRuntimeMaterials.AimOverlay(guideColor);

            BuildRing();
            BuildWeaponModel();
            BuildGuide();

            SetAimVisuals(false);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;

            if (m_RingMesh != null) Destroy(m_RingMesh);

            if (m_RingRoot   != null) Destroy(m_RingRoot.gameObject);
            if (m_WeaponRoot != null) Destroy(m_WeaponRoot.gameObject);
            if (m_GuideLine  != null) Destroy(m_GuideLine.gameObject);

            if (m_RingMat   != null) Destroy(m_RingMat);
            if (m_WeaponMat != null) Destroy(m_WeaponMat);
            if (m_BulletMat != null) Destroy(m_BulletMat);
            if (m_GuideMat  != null) Destroy(m_GuideMat);
            if (m_CrossTex  != null) Destroy(m_CrossTex);
        }

        // ------------------------------------------------------------------ 主循环

        private void Update()
        {
            // 只让单例驱动：万一场景里混进了第二个武器组件，不会变成双倍射速
            if (Instance != this) return;

            if (!GameUI.IsDungeonMode) { LeaveAim(); return; }

            // ★★ 虚拟指针必须**每帧都积分**，不能因为任何提前 return 而中断 ★★
            //
            //   需求："在选buff界面短剑仍然不能跟随鼠标移动。"
            //
            //   ★ 之前下面那段"三选一界面开着 -> return"排在 TickVirtualCursor 之前，
            //     结果界面一开就不会再积分指针，短剑就死死钉在原地。
            //   ★ 现在把指针积分提到最前面：不管是正常游玩、瞄准、
            //     还是任何全屏界面打开，指针都跟着鼠标走。
            TickVirtualCursor();

            // 强化三选一界面打开时世界是冻结的（timeScale = 0），武器也不该响应
            if (IsAnyDungeonUiOpen)
            {
                LeaveAim();
                return;
            }

            if (m_Player == null) m_Player = FindObjectOfType<PlayerController>();
            if (m_Player == null) { LeaveAim(); return; }

            if (!GameUI.PlayerControllable || !GameUI.CameraInputActive)
            {
                LeaveAim();
                return;
            }

            bool wantAim = Input.GetKey(aimKey);

            if (wantAim && !IsAiming) EnterAim();
            else if (!wantAim && IsAiming) LeaveAim();

            // ★★ 手刀（需求 2）：没有按住右键瞄准时，单击左键挥出手刀 ★★
            //     对鼠标方向的扇形造成伤害并清除区域内的怪物子弹。
            if (!IsAiming)
            {
                // ★ 指针已经在 Update 开头统一积分过了，这里直接用。

                if (enableHandBlade && CanUseHandBlade() &&
                    Input.GetKeyDown(fireKey) &&
                    Time.timeSinceLevelLoad >= m_NextBladeTime)
                {
                    TryHandBlade();
                }

                return;
            }

            HandleAimMouse();
            UpdateAimTransform();

            // ★ 按住 / 单击都走同一个节流，所以手速不会影响射速
            if (Input.GetKey(fireKey) && Time.timeSinceLevelLoad >= m_NextFireTime)
                Fire();
        }

        // ------------------------------------------------------------------ 准星绘制

        /// <summary>
        /// 在虚拟指针的位置画一个准星。
        ///
        /// ★ 地牢模式 Cursor 是 Locked（屏幕上没有系统指针），
        ///   所以必须自己画一个，玩家才知道鼠标在哪。
        ///
        /// 画法：一个圆 + 四根向外的短刻线（上/下/左/右），中间可选一个小点。
        /// 全部用 1×1 纹理 + GUI.color 染色，不依赖任何贴图资源。
        /// </summary>
        private void OnGUI()
        {
            if (!GameUI.IsDungeonMode) return;
            if (GameUI.Current != GameState.Playing) return;

            if (m_CrossTex == null) m_CrossTex = MakePixel();

            // 按 1080 高度等比缩放，和 HUD 一个口径
            float scale = Screen.height / 1080f;
            if (scale <= 0.0001f) return;

            // ★ 注意 IMGUI 的 y 轴是向下的，而 Input 积分出来的是屏幕坐标（y 向上），
            //   所以要翻转一次。
            float cx = CrosshairScreen.x;
            float cy = Screen.height - CrosshairScreen.y;

            // ★★ 未瞄准：画一把短剑，而不是准星圆环 ★★
            //
            //   需求："我希望在不处于右键瞄准状态时，鼠标位置会显示一把短剑的图标，
            //         不过仍然不显示指针。"
            //   —— 鼠标指针依然不显形（Cursor 仍是 Locked + 隐藏），
            //      只是用 IMGUI 在虚拟指针的位置画一个图形。
            if (!IsAiming)
            {
                if (showBladeIcon) DrawBladeIcon(cx, cy, scale);
                return;
            }

            if (!showCrosshair) return;

            float r     = Mathf.Max(2f, crosshairRadius) * scale;
            float tick  = Mathf.Max(0f, crosshairTickLength) * scale;
            float thick = Mathf.Max(1f, crosshairThickness) * scale;

            Color prev = GUI.color;
            GUI.color = IsAiming ? crosshairAimColor : crosshairColor;

            // ---- 四根刻线（从圆边缘往外伸）----
            if (tick > 0f)
            {
                // 上
                GUI.DrawTexture(new Rect(cx - thick * 0.5f, cy - r - tick, thick, tick), m_CrossTex);
                // 下
                GUI.DrawTexture(new Rect(cx - thick * 0.5f, cy + r,        thick, tick), m_CrossTex);
                // 左
                GUI.DrawTexture(new Rect(cx - r - tick, cy - thick * 0.5f, tick, thick), m_CrossTex);
                // 右
                GUI.DrawTexture(new Rect(cx + r, cy - thick * 0.5f,        tick, thick), m_CrossTex);
            }

            // ---- 圆环（用一圈短线段近似，24 段足够圆）----
            DrawCircle(cx, cy, r, thick);

            // ---- 圆心小点 ----
            if (crosshairDotRadius > 0f)
            {
                float d = crosshairDotRadius * scale;
                GUI.DrawTexture(new Rect(cx - d, cy - d, d * 2f, d * 2f), m_CrossTex);
            }

            GUI.color = prev;
        }

        /// <summary>
        /// 在指针位置画一把短剑图标（未瞄准时用）。
        ///
        /// ★ 全部用 1×1 纹理拼，不依赖任何图片资源。
        ///
        /// ── 几何 ─────────────────────────────────────────────
        ///   · **剑尖**正好落在指针那一点上（bladeIconOffset 可微调）
        ///   · 整个图标绕剑尖旋转 bladeIconTiltDegrees 度
        ///
        ///        指针 ●  ← 剑尖（旋转中心）
        ///            ╲
        ///             ╲   剑身
        ///              ╲
        ///             ─┴─  护手
        ///              │   剑柄
        ///              ○   柄尾
        ///
        /// ── ⚠ 之前为什么倾斜后会"糊成一团" ─────────────────
        ///   旧实现没有旋转，而是给每一段算了一个**轴对齐包围盒**：
        ///
        ///       extX = |dx|·halfAlong + |px|·halfAcross
        ///       extY = |dy|·halfAlong + |py|·halfAcross
        ///
        ///   倾斜 0° 时这个盒子正好贴合形状，所以看着还行；
        ///   一旦倾斜，盒子两个方向都变大 —— 剑身 60×10 像素的条
        ///   会被画成 38×57 的方块，整把剑就变成一团。
        ///
        ///   ✔ 现在改用真正的**旋转矩阵**（GUI.matrix + Matrix4x4.TRS），
        ///     每段就是一个规规矩矩的矩形，旋转后仍是细长的剑身。
        /// </summary>
        private void DrawBladeIcon(float cx, float cy, float scale)
        {
            if (m_CrossTex == null) return;

            float s = Mathf.Max(4f, bladeIconSize) * scale;

            // ---- 剑尖锚点（旋转中心）----
            float tipX = cx + bladeIconOffset.x * scale;
            float tipY = cy + bladeIconOffset.y * scale;

            // 旋转角：IMGUI 的 y 向下，所以"顺时针"对应正角度
            float tiltDeg = Mathf.Clamp(bladeIconTiltDegrees, -80f, 80f);

            // ---- 各段尺寸（沿剑身方向 / 垂直剑身方向，单位：像素）----
            float tipLen   = s * 0.18f;
            float bladeLen = s * 0.50f;
            float guardLen = s * 0.10f;
            float gripLen  = s * 0.24f;

            float bladeW = Mathf.Max(1f, s * 0.13f);
            float gripW  = Mathf.Max(1f, s * 0.09f);
            float guardW = s * 0.60f;

            // 保存/恢复画布矩阵与颜色
            Matrix4x4 savedMatrix = GUI.matrix;
            Color     savedColor  = GUI.color;

            // ★ 以剑尖为原点建一个旋转坐标系：
            //     先平移到剑尖，再旋转。
            //   之后所有绘制都用"沿剑身向下为正 y"的局部坐标，
            //   完全不用手算包围盒。
            GUI.matrix = savedMatrix
                       * Matrix4x4.TRS(new Vector3(tipX, tipY, 0f),
                                       Quaternion.Euler(0f, 0f, -tiltDeg),
                                       Vector3.one);

            GUI.color = bladeIconColor;

            // 局部坐标里：y 轴沿剑身向下，x 轴垂直剑身
            float t = 0f;

            // 画一个中心在 (0, y中心)、宽 across、高 along 的矩形
            void Rect2(float across, float along, float centerAlong)
            {
                float w = Mathf.Max(1f, across);
                float h = Mathf.Max(1f, along);
                GUI.DrawTexture(new Rect(-w * 0.5f, centerAlong - h * 0.5f, w, h), m_CrossTex);
            }

            // ---- 剑尖：3 段逐渐变宽，形成三角 ----
            for (int i = 0; i < 3; i++)
            {
                float seg   = tipLen / 3f;
                float width = Mathf.Max(1f, bladeW * ((i + 1) / 3f));
                Rect2(width, seg, t + seg * 0.5f);
                t += seg;
            }

            // ---- 剑身 ----
            Rect2(bladeW, bladeLen, t + bladeLen * 0.5f);
            t += bladeLen;

            // ---- 护手（垂直剑身的横条）----
            Rect2(guardW, guardLen, t + guardLen * 0.5f);
            t += guardLen;

            // ---- 剑柄 ----
            Rect2(gripW, gripLen, t + gripLen * 0.5f);
            t += gripLen;

            // ---- 柄尾球 ----
            {
                float r = Mathf.Max(1f, s * 0.05f);
                GUI.DrawTexture(new Rect(-r, t - r, r * 2f, r * 2f), m_CrossTex);
            }

            GUI.color    = savedColor;
            GUI.matrix   = savedMatrix;
        }



        /// <summary>用一个 1×1 纹理拼出一个圆环。</summary>
        private void DrawCircle(float cx, float cy, float radius, float thickness)
        {
            const int Segments = 24;

            float step = Mathf.PI * 2f / Segments;

            // 每段用一个小方块表示，方块边长略大于弧长，保证接缝不明显
            float arc     = radius * step;
            float segLen  = Mathf.Max(thickness, arc * 1.35f);
            float segWide = thickness;

            for (int i = 0; i < Segments; i++)
            {
                float a = i * step;
                float x = cx + Mathf.Cos(a) * radius;
                float y = cy + Mathf.Sin(a) * radius;

                GUI.DrawTexture(new Rect(x - segLen * 0.5f, y - segWide * 0.5f, segLen, segWide),
                                m_CrossTex);
            }
        }

        private static Texture2D MakePixel()
        {
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave
            };

            tex.SetPixel(0, 0, Color.white);
            tex.Apply();
            return tex;
        }

        // ------------------------------------------------------------------ 瞄准状态

        private void EnterAim()
        {
            IsAiming = true;

            // ★★ 不再把指针重置到屏幕中心 ★★
            //
            //   需求："现在按下右键会让鼠标回到屏幕中心，把这个效果去掉。"
            //
            //   ★ 虚拟指针现在是**持续存在**的：未瞄准时（短剑图标）也在积分，
            //     所以按下右键时指针本来就在玩家刚看的位置，直接沿用即可，
            //     不需要任何重置 —— 重置反而会让准星"跳"到屏幕中间。

            SetAimVisuals(true);
        }

        private void LeaveAim()
        {
            if (!IsAiming)
            {
                SetAimVisuals(false);
                return;
            }

            IsAiming = false;

            // 退出时清节流：下次进瞄准立刻能打
            m_NextFireTime = 0f;

            SetAimVisuals(false);
        }

        private void SetAimVisuals(bool on)
        {
            if (m_RingRoot   != null) m_RingRoot.gameObject.SetActive(on);
            if (m_WeaponRoot != null) m_WeaponRoot.gameObject.SetActive(on);
            if (m_GuideLine  != null) m_GuideLine.enabled = on;
        }

        /// <summary>
        /// 强制退出瞄准并隐藏所有瞄准表现。
        ///
        /// ★「重新开始」时要调这个 —— 否则圆环会留在场上、
        ///   而且因为 IsAiming 还是 false 而不再跟随玩家。
        /// </summary>
        public void ForceLeaveAim()
        {
            IsAiming = false;
            m_NextFireTime = 0f;

            SetAimVisuals(false);
        }

        /// <summary>
        /// 读取鼠标、更新 <see cref="AimYaw"/> 与 <see cref="CrosshairScreen"/>。
        ///
        /// ★★ 地牢模式的正交俯视下，瞄准方式改成"**跟随鼠标位置**" ★★
        ///
        ///   光标本身是锁定的（屏幕上没有指针），所以这里读的是
        ///   用 Mouse X/Y 增量积分出来的**虚拟指针**——效果和真实指针一致。
        ///
        ///   然后把指针位置经正交相机打到地面，得到一个世界点，
        ///   武器就朝那个点：
        ///       AimYaw = atan2(目标点.x - 玩家.x, 目标点.z - 玩家.z)
        ///
        ///   ★ 和旧实现的区别：旧的是"按下右键那一刻的相机方向 + 相对角度"，
        ///     新的纯粹由鼠标**在屏幕上的位置**决定，不需要基准角。
        /// </summary>
        /// <summary>
        /// 积分虚拟指针（屏幕像素坐标）。
        ///
        /// ★ 光标是锁定的，屏幕上没有系统指针，所以用 Mouse X/Y 增量自己积一个出来。
        /// ★ 抽成独立方法，是因为**没瞄准时也要跟着鼠标走** ——
        ///   未瞄准状态下要画一把短剑图标，并且手刀要朝鼠标方向出。
        /// </summary>
        private void TickVirtualCursor()
        {
            // ★★ 光标此时是**解锁**的（全屏 UI / 瞄准等）★☆
            //
            //   这种情况下直接读真实鼠标位置，而不是积分增量 ——
            //   1) 边界不会漂移
            //   2) 和 IMGUI 的 GUI.Button 命中判定用的是同一个坐标，
            //      所以短剑指哪儿就能点哪儿
            //
            //   ★ 光标仍然是 Cursor.visible = false（屏幕上看不到系统指针），
            //     只是 lockState 是 None，于是 Input.mousePosition 有效。
            if (Cursor.lockState != CursorLockMode.Locked)
            {
                m_VirtualCursor = Input.mousePosition;
                CrosshairScreen = m_VirtualCursor;
                return;
            }

            // ---- 锁定状态（正常游玩）：用增量积分 ----
            float mx = Input.GetAxisRaw("Mouse X");
            float my = Input.GetAxisRaw("Mouse Y");

            m_VirtualCursor += new Vector2(mx, my) * Mathf.Max(1f, virtualCursorPixelsPerUnit);

            // 夹在屏幕内，避免指针跑到画面外
            m_VirtualCursor.x = Mathf.Clamp(m_VirtualCursor.x, 0f, Screen.width);
            m_VirtualCursor.y = Mathf.Clamp(m_VirtualCursor.y, 0f, Screen.height);

            CrosshairScreen = m_VirtualCursor;
        }

        /// <summary>
        /// 虚拟指针 -> 地面世界点。失败返回 false。
        /// </summary>
        private bool VirtualCursorToGround(out Vector3 groundPoint)
        {
            groundPoint = Vector3.zero;

            if (m_Player == null) return false;

            return m_Player.ScreenToGround(m_VirtualCursor, out groundPoint);
        }

        /// <summary>
        /// 虚拟指针方向（从玩家水平指向指针所在的地面点）。
        ///
        /// ★ 指针离玩家太近时返回 false —— 那种情况下角度没意义。
        /// </summary>
        public bool TryGetCursorDirection(out Vector3 dir)
        {
            dir = Vector3.forward;

            if (!VirtualCursorToGround(out Vector3 groundPoint)) return false;

            Vector3 p = m_Player.transform.position;
            Vector3 d = groundPoint - p;
            d.y = 0f;

            if (d.sqrMagnitude < 0.04f) return false;

            dir = d.normalized;
            return true;
        }


        /// <summary>
        /// 把虚拟指针挪回屏幕中心。
        ///
        /// ★ 给商店 / buff 三选一这类"全屏 UI"用：
        ///   这些界面是 IMGUI 画的按钮，点按钮走的是**系统鼠标位置**。
        ///   虽然我们让系统指针保持隐藏（Cursor 仍是 Locked），
        ///   但 IMGUI 的点击判定仍然发生在屏幕中心，
        ///   所以把短剑图标也挪到中心，"短剑就是指针"才不会错位。
        /// </summary>
        public void RecenterCursor()
        {
            m_VirtualCursor = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            CrosshairScreen = m_VirtualCursor;
        }

        /// <summary>
        /// 现在能不能出手刀。
        ///
        /// ★ 需求："在点击离开商店的按钮时会触发一次手刀，因为两者都要点击左键，
        ///         我希望在商店界面不要触发手刀。"
        ///
        ///   除了商店，buff 三选一 / 暂停 / loadout 这些"用鼠标点按钮"的
        ///   IMGUI 界面也一律不触发出刀 —— 否则点一次按钮就顺手挥一刀。
        /// </summary>
        /// <summary>
        /// 地牢里是否有"全屏界面"开着（buff 三选一 / 商店 / loadout）。
        ///
        /// ★ 这些界面用 timeScale = 0 冻结世界，武器和手刀都不该响应。
        /// </summary>
        public bool IsAnyDungeonUiOpen
        {
            get
            {
                if (DungeonBuffSelectUI.Instance != null && DungeonBuffSelectUI.Instance.IsOpen) return true;
                if (DungeonShopUI.Instance       != null && DungeonShopUI.Instance.IsOpen)       return true;
                if (DungeonLoadoutUI.Instance    != null && DungeonLoadoutUI.Instance.IsOpen)    return true;
                return false;
            }
        }

        private bool CanUseHandBlade()
        {
            // 商店
            if (DungeonShopUI.Instance != null && DungeonShopUI.Instance.IsOpen) return false;

            // 开局 / 换层的 buff 三选一
            if (DungeonBuffSelectUI.Instance != null && DungeonBuffSelectUI.Instance.IsOpen) return false;

            // 装填（loadout）界面
            if (DungeonLoadoutUI.Instance != null && DungeonLoadoutUI.Instance.IsOpen) return false;

            return true;
        }

        private void HandleAimMouse()
        {
            TickVirtualCursor();

            if (m_Player == null) return;

            // 指针 -> 地面世界点
            if (!VirtualCursorToGround(out Vector3 groundPoint))
                return;

            Vector3 p   = m_Player.transform.position;
            Vector3 d   = groundPoint - p;
            d.y = 0f;

            // 离玩家太近时角度没意义：保持上一个朝向，别乱抖
            if (d.sqrMagnitude < 0.04f) return;

            AimYaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
        }

        /// <summary>当前瞄准的水平单位方向。</summary>
        public Vector3 AimDirection()
        {
            float rad = AimYaw * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));
        }

        /// <summary>
        /// 给理想方向叠加随机散布（精准度机制）。
        ///
        /// ★ 需求："子弹发射时会随机偏转 5° ~ -5°。"
        ///
        ///   在水平面内绕 Y 轴随机转一个角度，
        ///   范围 [-EffectiveSpreadDegrees, +EffectiveSpreadDegrees]，均匀分布。
        ///
        /// ★ EffectiveSpreadDegrees 已经乘过 buff 的 BulletSpreadMul：
        ///     高爆弹头 ×0.75（更准）
        ///     快扳机   ×1.25（更飘）
        ///
        ///   最终为 0 时原样返回（完全精准）。
        /// </summary>
        private Vector3 ApplyBulletSpread(Vector3 aimDir)
        {
            float spread = EffectiveSpreadDegrees;

            if (spread <= 0f) return aimDir;

            float angle = Random.Range(-spread, spread);

            return Quaternion.Euler(0f, angle, 0f) * aimDir;
        }

        /// <summary>
        /// 含 buff 后的**最大偏转角**（度）。
        ///
        /// ★ 基础值 = bulletSpreadDegrees（默认 5），
        ///   再乘 DungeonBuffState.BulletSpreadMul（无 buff 时是 1）。
        /// ★ 保留三位小数，和别的乘区一个口径。
        /// </summary>
        public float EffectiveSpreadDegrees
            => Round3(Mathf.Max(0f, bulletSpreadDegrees * Mul(s => s.BulletSpreadMul)));

        // ------------------------------------------------------------------ 霰弹 / 范围伤害（第十周任务 4）

        [Header("霰弹（任务 4）")]
        [Tooltip("★ 霰弹扇形总角度（度），**按「霰弹改造」等级逐级变大**。\n\n" +
                 "需求：1 级 16°，2 级 32°，3 级 48°\n" +
                 "★ 数组第 n 项 = 拥有 n+1 级时的角度，最后一项会一直用下去。")]
        public float[] shotgunArcByLevel = { 16f, 32f, 48f };

        [Tooltip("每颗弹丸额外的随机抖动（度）")]
        [Min(0f)] public float shotgunJitterDegrees = 1.5f;

        [Header("范围伤害（任务 4）")]
        [Tooltip("★ 爆炸半径（米），**按「爆炸弹头」等级逐级变大**。\n\n" +
                 "需求：1 级 3.5，2 级 4.5，3 级 5.5\n" +
                 "★ 数组第 n 项 = 拥有 n+1 级时的半径，最后一项会一直用下去。")]
        public float[] aoeRadiusByLevel = { 3.5f, 4.5f, 5.5f };

        [Tooltip("基础爆炸半径（米）。0 = 没有范围伤害。\n" +
                 "★ 正常用 aoeRadiusByLevel 就够了，这个只是兜底。")]
        [Min(0f)] public float baseAoeRadius;

        /// <summary>这一次开火会打出几颗弹丸（1 = 单发）。</summary>
        public int EffectivePellets
        {
            get
            {
                var b = DungeonBuffState.Instance;
                return 1 + Mathf.Max(0, b != null ? b.ExtraPellets : 0);
            }
        }

        /// <summary>子弹的范围伤害半径（米）。0 = 没有范围伤害。</summary>
        /// <summary>
        /// 当前「爆炸弹头」等级对应的爆炸半径（米）。
        ///
        /// ★ 需求："每级增加的伤害范围不同，一级为 2.5，二级增至 3.5，三级增至 4.5。"
        ///
        ///   ★ 以前是"基础值 + 每级固定 +1.5"（加法），
        ///     现在改成直接查等级表 aoeRadiusByLevel。
        ///   ★ 没有这个 buff 时返回 0 = 没有范围伤害。
        /// </summary>
        public float EffectiveAoeRadius
        {
            get
            {
                int lv = AoeLevel;                       // 0 = 没拿过

                if (lv <= 0) return Round3(Mathf.Max(0f, baseAoeRadius));

                if (aoeRadiusByLevel == null || aoeRadiusByLevel.Length == 0)
                    return Round3(Mathf.Max(0f, baseAoeRadius));

                int idx = Mathf.Clamp(lv - 1, 0, aoeRadiusByLevel.Length - 1);

                return Round3(Mathf.Max(0f, aoeRadiusByLevel[idx]));
            }
        }

        /// <summary>「爆炸弹头」当前叠了几级（0 = 没拿过）。</summary>
        private static int AoeLevel
        {
            get
            {
                var b = DungeonBuffState.Instance;
                if (b == null) return 0;

                // ★ 第十一周：紫 / 橙的爆炸弹头合并等级（按 effectId 汇总）
                return Mathf.Max(0, b.MechanicLevel("aoe"));
            }
        }

        /// <summary>
        /// 第 <paramref name="index"/> 颗弹丸的方向：在扇形里均匀铺开 + 轻微抖动。
        /// </summary>
        private Vector3 PelletDirection(Vector3 aimDir, int index, int count)
        {
            float arc   = EffectiveShotgunArc;
            float step  = count > 1 ? arc / (count - 1) : 0f;
            float angle = -arc * 0.5f + step * index;

            if (shotgunJitterDegrees > 0f)
                angle += Random.Range(-shotgunJitterDegrees, shotgunJitterDegrees);

            return Quaternion.Euler(0f, angle, 0f) * aimDir;
        }

        /// <summary>把圆环放到脚下、武器摆到环上朝瞄准方向。</summary>
        private void UpdateAimTransform()
        {
            if (m_Player == null) return;

            Vector3 dir = AimDirection();
            Vector3 feet = m_Player.transform.position;

            // 圆环：始终水平，位置跟着玩家
            if (m_RingRoot != null)
            {
                m_RingRoot.position = feet + Vector3.up * ringGroundOffset;
                m_RingRoot.rotation = Quaternion.identity;
            }

            // 武器：在环上绕玩家转（和匕首一样，永远水平面）
            if (m_WeaponRoot != null)
            {
                Vector3 pos = feet + Vector3.up * weaponHeight + dir * Mathf.Max(0.1f, weaponOrbitRadius);

                m_WeaponRoot.position = pos;
                m_WeaponRoot.rotation = Quaternion.LookRotation(dir, Vector3.up);
            }

            // 瞄准辅助线：从枪口往前一段，告诉玩家「子弹会往哪飞」
            // （瞄准时镜头被冻结、没有屏幕准星，这条线就是唯一的准星）
            if (m_GuideLine != null)
            {
                Vector3 muzzle = MuzzlePosition(dir);
                m_GuideLine.SetPosition(0, muzzle);
                m_GuideLine.SetPosition(1, muzzle + dir * Mathf.Max(1f, guideLength));
            }
        }

        /// <summary>
        /// 枪口位置。
        ///
        /// ★ 这里之前是个 bug：直接取「玩家 + 方向 × (环半径 + 枪管长度)」= 身前 1.18 米，
        ///   一旦玩家贴着墙站，枪口就落在**墙里面** —— 子弹一出生就撞墙消失，
        ///   表现就是"明明对着怪打，却打不中"。
        ///
        ///   现在先朝这个方向探一下：墙比枪口近，就把出生点拉回到墙前一点点
        ///   （最远不超过玩家身前 0.45 米，也就是胶囊体边缘外一点）。
        /// </summary>
        private Vector3 MuzzlePosition(Vector3 dir)
        {
            if (m_Player == null) return transform.position;

            Vector3 center = m_Player.transform.position + Vector3.up * weaponHeight;

            float want = Mathf.Max(0.1f, weaponOrbitRadius) + Mathf.Max(0f, muzzleForward);

            // 探墙：别让枪口生在墙里 / 生在目标身后
            if (Physics.SphereCast(center, 0.12f, dir, out RaycastHit hit, want,
                                   ~((1 << 2) | (1 << 4) | (1 << 5)),
                                   QueryTriggerInteraction.Ignore))
            {
                want = Mathf.Max(0.45f, hit.distance - 0.15f);
            }

            return center + dir * want;
        }

        // ------------------------------------------------------------------ 开火

        private void Fire()
        {
            // ── 0) 取含 buff 的有效数值（唯一出处见上面的 Effective* 属性）──
            //
            // ★★ 全部是 float，保留三位小数 —— 不要转 int，否则又被截断 ★★
            float cost     = EffectiveManaCost;
            float dmg      = EffectiveDamage;
            float killBack = EffectiveKillMana;

            // 1) 蓝不够就不打，也不占用节流（免得没蓝时连按把节奏卡死）
            DungeonMana mana = DungeonMana.Instance;
            if (mana != null && !mana.CanAfford(cost)) return;
            if (mana != null && cost > 0f && !mana.TrySpend(cost)) return;

            // 2) 节流
            m_NextFireTime = Time.timeSinceLevelLoad + EffectiveFireInterval;

            // 3) 出膛：方向由 AimYaw 决定（和相机没关系），再叠加散布
            //
            // ★ 单发：在 [-bulletSpreadDegrees, +bulletSpreadDegrees] 随机偏一个角。
            //   AimDirection() 本身**不改** —— 瞄准环和辅助线仍指向理想方向。
            //
            // ★ 多弹丸（霰弹，第十周任务 4）：在 shotgunArcDegrees 的扇形里均匀铺开，
            //   每颗再加一点随机抖动，否则每次都是同样的图案。
            Vector3 aimDir = AimDirection();

            int   pellets = EffectivePellets;
            float aoe     = EffectiveAoeRadius;
            int   volleys = EffectiveVolleys;
            int   bounce  = EffectiveBounces;
            int   pierce  = EffectivePierce;

            // 第一轮：本帧立刻打出去（保持单击的即时反馈）
            FireOneVolley(aimDir, pellets, dmg, killBack, aoe, bounce, pierce);

            // ★★ 「连发装置」后续几轮：每轮之间隔 volumeInterval 秒 ★★
            //
            //   需求："buff『连发装置』打出的两发子弹之间应当有时间间隔才对，
            //         你检查一下有没有，如果没有就加个 0.1s 的间隔，有就改成 0.1s。"
            //
            //   ★ 检查结果：原来是一个 for 循环**同一帧**全部打完，没有任何间隔。
            //   ★ 现在除了第一轮，其余轮次都交给协程，每隔 volleyInterval 秒打一轮。
            if (volleys > 1)
                StartCoroutine(VolleyRoutine(aimDir, pellets, volleys,
                                             dmg, killBack, aoe, bounce, pierce));

            // ★★ 开火 = 暴露自己：场上所有怪物立即捕获玩家位置 ★★
            //
            //   需求原文："我希望玩家开火时，场上所有怪物都会立即捕捉到玩家位置
            //             并将 DungeonPlayerFound 置为 1。"
            //
            //   ★ 只置这个标志，**不碰**每只怪自己的行为状态
            //     （停顿计时 / 移动目标 / 视线快照全都原样保留）。
            AlertAllMonsters();

            LastFireTime = Time.timeSinceLevelLoad;
            ShotsFired++;
        }

        /// <summary>
        /// 打出**一轮**齐射（pellets 颗子弹，各自算散布）。
        /// </summary>
        private void FireOneVolley(Vector3 aimDir, int pellets,
                                   float dmg, float killBack, float aoe,
                                   int bounce, int pierce)
        {
            for (int i = 0; i < pellets; i++)
            {
                Vector3 dir = pellets <= 1
                            ? ApplyBulletSpread(aimDir)
                            : PelletDirection(aimDir, i, pellets);

                Vector3 muzzle = MuzzlePosition(dir);

                DungeonBullet.Spawn(muzzle, dir,
                                    EffectiveBulletSpeed, bulletRange,
                                    EffectiveBulletWidth,
                                    dmg, killBack,
                                    monsterHurtRadius, monsterHurtHeight,
                                    m_BulletMat, bulletTrailLength,
                                    aoe, bounce, pierce);
            }
        }

        /// <summary>
        /// 「连发装置」的后续轮次：每隔 <see cref="volleyInterval"/> 秒打一轮。
        ///
        /// ★ 第一轮已经在 Fire() 里立刻打掉了，所以这里从 v = 1 开始。
        /// ★ 用 Time.deltaTime 而不是 unscaled —— 界面冻结（timeScale = 0）时
        ///   连发应该跟着一起停。
        /// </summary>
        private IEnumerator VolleyRoutine(Vector3 aimDir, int pellets, int volleys,
                                          float dmg, float killBack, float aoe,
                                          int bounce, int pierce)
        {
            for (int v = 1; v < volleys; v++)
            {
                float wait = Mathf.Max(0f, volleyInterval);
                float t    = 0f;

                while (t < wait)
                {
                    t += Time.deltaTime;
                    yield return null;
                }

                // 中途如果退出了瞄准 / 游戏结束，就不再补打
                if (this == null) yield break;

                FireOneVolley(aimDir, pellets, dmg, killBack, aoe, bounce, pierce);
            }
        }

        /// <summary>
        /// 让场上**所有**怪物立刻知道玩家在哪（DungeonPlayerFound = 1）。
        ///
        /// ★ 只写这一个字段，不改任何行为状态 ——
        ///   怪物接下来怎么动，仍由它们自己的状态机决定。
        /// </summary>
        private static void AlertAllMonsters()
        {
            DungeonEnemy[] enemies = FindObjectsOfType<DungeonEnemy>(true);

            for (int i = 0; i < enemies.Length; i++)
            {
                DungeonEnemy e = enemies[i];
                if (e == null) continue;
                if (!e.isActiveAndEnabled) continue;

                e.DungeonPlayerFound = 1;
            }
        }

        /// <summary>
        /// 手刀（需求 2）：未瞄准时单击左键，
        /// 对玩家前方一小片扇形区域造成范围伤害并清除区域内的怪物子弹。
        ///
        ///   · 伤害：handBladeDamage（默认 4）× 扇形内每只怪；
        ///   · ★ 打墙：怪物技能墙（MonsterSkillWall）也吃手刀伤害；
        ///   · 清子弹：区域内怪物子弹被清除，但保留爆炸视觉
        ///     （需求："仍然会产生爆炸、地面伤害等效果"——
        ///      爆炸视觉在 MonsterBulletRenderer.ClearArea 里触发，不掉血；
        ///      伤害地面是独立物体，本来就不受影响）；
        ///   · CD：handBladeCooldown（默认 0.3s）；
        ///         **命中怪物或怪物墙**都缩到 handBladeHitCooldown（默认 0.1s）。
        /// </summary>
        private void TryHandBlade()
        {
            float now = Time.timeSinceLevelLoad;

            // 先按普通 CD 占位（命中怪物后再缩到 0.1s）
            m_NextBladeTime = now + Mathf.Max(0.05f, handBladeCooldown);

            // ---- 出刀方向：★ 朝鼠标方向 ★ ----
            //
            //   需求："手刀目前固定朝玩家朝向出刀，
            //         我希望改成朝鼠标方向出刀。"
            //
            //   ★ 鼠标指针是虚拟的（光标被锁定，屏幕上没有系统指针），
            //     方向取自 TickVirtualCursor 积出来的虚拟指针，
            //     再经正交相机打到地面得到世界方向。
            //   ★ 指针离玩家太近（< 0.2 米）时角度没意义，
            //     这时退回"玩家当前朝向"，避免方向乱抖。
            Vector3 origin = m_Player.transform.position;
            Vector3 dir;

            if (!TryGetCursorDirection(out dir))
            {
                dir = m_Player.transform.forward;
                dir.y = 0f;

                if (dir.sqrMagnitude < 1e-4f)
                {
                    Camera cam = Camera.main;
                    dir = cam != null ? cam.transform.forward : Vector3.forward;
                    dir.y = 0f;
                }

                if (dir.sqrMagnitude < 1e-4f) dir = Vector3.forward;
                dir.Normalize();
            }

            // ---- 1) 伤害扇形内的怪物 ----
            bool hitAny = false;

            PuzzleMonster[] monsters = FindObjectsOfType<PuzzleMonster>(true);

            for (int i = 0; i < monsters.Length; i++)
            {
                PuzzleMonster mon = monsters[i];
                if (mon == null || mon.DungeonIsDead) continue;

                Vector2 d = new Vector2(mon.transform.position.x - origin.x,
                                        mon.transform.position.z - origin.z);

                float dist = d.magnitude;

                // ★ 手刀按**玩家子弹的受击圆柱**判定，不再要求砍到怪物中心。
                //
                //   以前是 `dist > handBladeRadius` 就直接跳过 —— 等于要求刀锋够到
                //   怪物的**球心**；boss 球半径 1.5，站在 3 米外就永远砍不到。
                //   现在把受击圆柱的半径（这只怪自己的 dungeonHurtRadius，
                //   没有就用 monsterHurtRadius）算进刀锋射程：刀锋够到圆柱边缘就算命中。
                float hurtR = mon.dungeonHurtRadius > 0.01f
                            ? mon.dungeonHurtRadius
                            : monsterHurtRadius;

                if (dist - hurtR > handBladeRadius) continue;

                Vector2 f = new Vector2(dir.x, dir.z).normalized;

                float ang = 0f;
                if (dist > 0.01f)
                {
                    ang = Mathf.Acos(Mathf.Clamp(Vector2.Dot(f, d.normalized), -1f, 1f))
                        * Mathf.Rad2Deg;

                    // 扇形边缘同理：圆柱边缘蹭到扇形也算命中
                    ang -= Mathf.Asin(Mathf.Clamp01(hurtR / dist)) * Mathf.Rad2Deg;
                }

                if (ang > handBladeHalfAngle) continue;

                mon.DungeonTakeDamage(EffectiveHandBladeDamage);

                // ★★ 手刀命中 -> 在怪物身上**随机位置**冒溅血 ★★
                //
                //   需求："手刀命中敌人时……会在怪物身上随机位置触发一次溅血光带。"
                //
                //   ★ 以前用的是 mon.transform.position（固定一个点），
                //     每次血花都开在同一个位置，看得出是"贴图"。
                BloodSplashSonar.Emit(BulletTrailRenderer.RandomPointOnMonster(mon), dir);

                hitAny = true;
            }

            // ---- 2) ★ 打怪物技能墙 ★ ----
            //
            //   需求 1："让玩家的手刀可以对怪物用技能制造的怪物墙造成伤害，
            //           并且手刀命中怪物墙或怪物时都会让手刀 cd 减至 0.1s。"
            //   需求 2："手刀只要扇形面积碰到怪物墙的墙体就算打到了墙。"
            //
            //   ★ 以前只看「墙心」是否落在扇形里 —— 墙长 5 米，
            //     站在墙的一端挥刀可能打不到，手感很差。
            //   ✔ 现在做真正的**扇形 vs 墙体矩形**相交判定：
            //     把墙的地面投影看成矩形（长 × 厚），任一角在扇形内、
            //     玩家在矩形内、或矩形边与扇形的两条射线/圆弧相交，都算命中。
            //   ★ 伤害直接走墙自己的 AbsorbPlayerBullet（和子弹同一条承伤路径），
            //     所以血量、透明度、「血越多越实」那套效果全部自动生效。
            MonsterSkillWall[] walls = FindObjectsOfType<MonsterSkillWall>(true);

            for (int i = 0; i < walls.Length; i++)
            {
                MonsterSkillWall wall = walls[i];
                if (wall == null) continue;

                if (!SectorHitsWall(origin, dir, handBladeRadius, handBladeHalfAngle, wall))
                    continue;

                wall.AbsorbPlayerBullet(EffectiveHandBladeDamage);

                hitAny = true;
            }

            // ---- 3) 清除扇形内的怪物子弹（保留爆炸视觉，不掉血）----
            if (MonsterBulletRenderer.Instance != null)
            {
                MonsterBulletRenderer.Instance.ClearArea(origin, handBladeRadius,
                                                         handBladeHalfAngle, dir);
            }

            // ---- 4) 命中了怪物**或怪物墙**：CD 缩到 0.1s ----
            if (hitAny)
                m_NextBladeTime = now + Mathf.Max(0.02f, handBladeHitCooldown);

            // ---- 5) 视觉：扇形闪光 ----
            StartCoroutine(BladeFxRoutine(origin, dir));
        }

        // ---------------------------------------------------------------- 几何：扇形 vs 墙体

        /// <summary>
        /// 手刀扇形（圆心 origin、半径 radius、朝 dir、半角 halfAngleDeg）
        /// 是否与这面墙的**墙体矩形**相交。
        ///
        /// ★ 需求："手刀只要扇形面积碰到怪物墙的墙体就算打到了墙。"
        ///
        /// ★ 墙的建模（见 MonsterSkillWall.Spawn）：
        ///     · 本地 +X = 长度方向（scale.x）
        ///     · 本地 +Z = 厚度方向（scale.z）
        ///     · 姿态是 Quaternion.LookRotation(dir)，所以 +Z 正对 dir
        ///   所以地面投影是一个「长 × 厚」的矩形，中心在墙的 transform.position。
        ///
        /// ★ 判定分四步（任一成立即算命中）：
        ///     ① 矩形任一角落在扇形内
        ///     ② 玩家自己在矩形内（贴脸挥刀）
        ///     ③ 矩形任一边与扇形的两条边界射线相交
        ///     ④ 矩形任一边与扇形的圆弧相交
        /// </summary>
        private static bool SectorHitsWall(Vector3 origin, Vector3 dir,
                                           float radius, float halfAngleDeg,
                                           MonsterSkillWall wall)
        {
            if (wall == null) return false;

            Transform t = wall.transform;

            // ---- 墙的地面投影：中心 + 两条本地轴 + 半长/半厚 ----
            Vector3 wp = t.position;
            Vector3 lp = new Vector3(origin.x, 0f, origin.z);
            Vector2 c  = new Vector2(wp.x, wp.z);

            Vector3 axX = t.right;   axX.y = 0f;      // 长度方向
            Vector3 axZ = t.forward; axZ.y = 0f;      // 厚度方向

            if (axX.sqrMagnitude < 1e-6f) axX = Vector3.right;
            if (axZ.sqrMagnitude < 1e-6f) axZ = Vector3.forward;

            axX.Normalize();
            axZ.Normalize();

            Vector3 ls = t.lossyScale;
            float halfLen = Mathf.Abs(ls.x) * 0.5f;   // 沿 axX 的半长
            float halfThk = Mathf.Abs(ls.z) * 0.5f;   // 沿 axZ 的半厚

            Vector2 ux = new Vector2(axX.x, axX.z);
            Vector2 uz = new Vector2(axZ.x, axZ.z);

            // ---- 矩形的四个角 ----
            Vector2[] corners = new Vector2[4];
            corners[0] = c + ux * halfLen + uz * halfThk;
            corners[1] = c + ux * halfLen - uz * halfThk;
            corners[2] = c - ux * halfLen - uz * halfThk;
            corners[3] = c - ux * halfLen + uz * halfThk;

            Vector2 o2 = new Vector2(lp.x, lp.z);
            Vector2 f2 = new Vector2(dir.x, dir.z);
            if (f2.sqrMagnitude < 1e-6f) f2 = Vector2.up;
            f2.Normalize();

            // ---- ① 任一角在扇形内 ----
            for (int i = 0; i < 4; i++)
            {
                if (PointInSector(o2, f2, radius, halfAngleDeg, corners[i]))
                    return true;
            }

            // ---- ② 玩家在矩形内 ----
            //   把玩家位置投到墙的两条本地轴上，看是否都在半长/半厚范围内
            Vector2 rel = o2 - c;
            float px = Vector2.Dot(rel, ux);
            float pz = Vector2.Dot(rel, uz);

            if (Mathf.Abs(px) <= halfLen && Mathf.Abs(pz) <= halfThk)
                return true;

            // ---- 扇形的两条边界射线（用线段表示，长度 = radius） ----
            float rad = halfAngleDeg * Mathf.Deg2Rad;

            Vector2 edgeA = o2 + Rotate2(f2,  rad) * radius;
            Vector2 edgeB = o2 + Rotate2(f2, -rad) * radius;

            for (int i = 0; i < 4; i++)
            {
                Vector2 a = corners[i];
                Vector2 b = corners[(i + 1) % 4];

                // ---- ③ 边 vs 两条射线 ----
                if (SegSecIntersect(a, b, o2, edgeA)) return true;
                if (SegSecIntersect(a, b, o2, edgeB)) return true;

                // ---- ④ 边 vs **圆弧**（交点必须落在楔形内，不能只碰整圆）----
                if (SegArcIntersect(a, b, o2, f2, radius, halfAngleDeg)) return true;
            }

            return false;
        }

        /// <summary>点在扇形内？（用世界半径 + 角度双条件）</summary>
        private static bool PointInSector(Vector2 origin, Vector2 dir,
                                          float radius, float halfAngleDeg, Vector2 p)
        {
            Vector2 d = p - origin;

            float sq = d.sqrMagnitude;
            if (sq > radius * radius) return false;
            if (sq < 1e-8f) return true;                 // 就在圆心

            float ang = Mathf.Acos(Mathf.Clamp(Vector2.Dot(dir, d.normalized), -1f, 1f))
                      * Mathf.Rad2Deg;

            return ang <= halfAngleDeg;
        }

        /// <summary>2D 向量绕原点旋转。</summary>
        private static Vector2 Rotate2(Vector2 v, float rad)
        {
            float cs = Mathf.Cos(rad);
            float sn = Mathf.Sin(rad);

            return new Vector2(v.x * cs - v.y * sn,
                               v.x * sn + v.y * cs);
        }

        /// <summary>两条线段是否相交（含端点接触）。</summary>
        private static bool SegSecIntersect(Vector2 p1, Vector2 p2, Vector2 q1, Vector2 q2)
        {
            Vector2 r = p2 - p1;
            Vector2 s = q2 - q1;

            float denom = r.x * s.y - r.y * s.x;

            // 平行（含共线）：按"不交"处理就够用了 ——
            // 共线重叠的情况在墙这种尺寸下几乎不会漏判（圆弧那步会兜住）
            if (Mathf.Abs(denom) < 1e-9f) return false;

            Vector2 d = q1 - p1;

            float t = (d.x * s.y - d.y * s.x) / denom;
            float u = (d.x * r.y - d.y * r.x) / denom;

            return t >= 0f && t <= 1f && u >= 0f && u <= 1f;
        }

        /// <summary>
        /// 线段是否与**圆弧**相交。
        ///
        /// ★ 和"与整圆相交"的区别：圆弧只是圆落在扇形张角内的那一段。
        ///   如果只判整圆，墙在玩家正侧方（超出张角）也会被误判成命中。
        ///
        /// ★ 做法：先求线段与圆的交点，再逐个检查交点是否落在楔形内。
        /// </summary>
        private static bool SegArcIntersect(Vector2 a, Vector2 b, Vector2 c, Vector2 dir,
                                            float r, float halfAngleDeg)
        {
            if (r <= 0f) return false;

            Vector2 d = b - a;
            Vector2 f = a - c;

            float A = Vector2.Dot(d, d);
            if (A < 1e-9f)
            {
                // 退化成一个点：直接看这个点在不在圆弧上
                return PointInSector(c, dir, r, halfAngleDeg, a);
            }

            float B = 2f * Vector2.Dot(f, d);
            float C = Vector2.Dot(f, f) - r * r;

            float disc = B * B - 4f * A * C;
            if (disc < 0f) return false;

            float sq = Mathf.Sqrt(disc);
            float t1 = (-B - sq) / (2f * A);
            float t2 = (-B + sq) / (2f * A);

            if (t1 >= 0f && t1 <= 1f)
            {
                Vector2 p1 = a + d * t1;
                if (PointInSector(c, dir, r, halfAngleDeg, p1)) return true;
            }

            if (t2 >= 0f && t2 <= 1f)
            {
                Vector2 p2 = a + d * t2;
                if (PointInSector(c, dir, r, halfAngleDeg, p2)) return true;
            }

            return false;
        }

        /// <summary>
        /// 手刀特效：一根**从玩家出发的线段**，在 0.1 秒内绕玩家扫过一个扇形。
        ///
        /// ── 需求（第 4 条）──────────────────────────────────
        ///   "我希望它现在会出现一根线段，一端为玩家物体，点击发动手刀后，
        ///    它会绕着玩家物体，在 0.1s 内扫过一个扇形，方向为顺时针和逆时针中随机一个，
        ///    每扫过一小段距离都会留下一根滞留在原地的线段，在 0.18s 内渐渐淡出。
        ///    手刀的伤害范围与这些线段无关，仅仅将视觉效果修改一下。
        ///    线段扫过形成的扇形与原来的扇形的大小和颜色一样。"
        ///
        /// ── 实现 ────────────────────────────────────────────
        ///   · 每条"留下的线段"是一个独立的细长四边形（从玩家指向扇区某角度）
        ///   · 扫过的角度范围 = [-handBladeHalfAngle, +handBladeHalfAngle]（和伤害扇形一致）
        ///   · 扫动时长 = sweepSeconds（默认 0.1s）
        ///   · 每条线段在 lingerSeconds（默认 0.18s）内线性淡出
        ///   · 方向每帧随机选一次：顺时针（从 -half 到 +half）或逆时针
        /// </summary>
        private IEnumerator BladeFxRoutine(Vector3 origin, Vector3 dir)
        {
            // 扇形中线朝向（水平）
            float centerYaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;

            float half = Mathf.Max(1f, handBladeHalfAngle);
            float radius = Mathf.Max(0.1f, handBladeRadius);

            // 随机扫动方向
            bool cw = Random.value < 0.5f;

            float from = cw ? -half :  half;
            float to   = cw ?  half : -half;

            float sweep = Mathf.Max(0.02f, bladeSweepSeconds);
            float linger = Mathf.Max(0.02f, bladeLineLingerSeconds);

            // 贴地高度
            float groundY = 0.06f;
            Vector3 probe = origin + Vector3.up * 6f;
            if (Physics.Raycast(probe, Vector3.down, out RaycastHit hit, 12f,
                                ~((1 << 2) | (1 << 4) | (1 << 5)),
                                QueryTriggerInteraction.Ignore))
            {
                groundY = hit.point.y + 0.06f;
            }

            Vector3 basePos = new Vector3(origin.x, groundY, origin.z);

            // 已经留下的线段
            var lines = new List<BladeLine>();

            // ★★ 一条线段绕玩家扫过扇形，**沿途逐根留下痕迹** ★★
            //
            //   需求："我希望手刀时用一条线段绕玩家扫出一个扇形，并且每扫过一定角度
            //         就留下一个痕迹，但我现在发现这些痕迹似乎是同时留下的，
            //         并不是随着线段扫过去留下的。"
            //
            //   ★ 上一轮为了修"线条不密"改成了"一帧内全部铺开"——
            //     结果是所有痕迹同时出现，看起来像瞬间亮一个扇形，
            //     没有"扫过去"的感觉。
            //
            //   ✔ 现在改成：
            //     · 整段扫动耗时 bladeSweepSeconds（默认 0.05 秒）
            //     · 在这段时间内**逐帧**推进角度
            //     · 每当累计扫过 1/bladeSweepLineCount 的角度，就留一根痕迹
            //     · 每帧还会画一根"正在移动的那条线"（临时的），
            //       扫过之后它就地变成痕迹 —— 这样肉眼能看清线段在动

            int totalLines = Mathf.Clamp(Mathf.RoundToInt(bladeSweepLineCount), 2, 360);

            // 每扫过多少**度**留一根
            float degPerLine = Mathf.Abs(to - from) / Mathf.Max(1, totalLines);

            float swept      = 0f;      // 已经扫过的角度（绝对值）
            float nextMarkAt = 0f;      // 下一次留痕的角度阈值

            // 正在移动的那条线（每帧重新摆位置，不新增对象）
            object tipToken = HandBladeLineRenderer.Rent(
                basePos, centerYaw + from, radius, bladeLineWidth, handBladeColor, 0.85f);

            float t0 = Time.timeSinceLevelLoad;

            while (true)
            {
                float now = Time.timeSinceLevelLoad;
                float k   = Mathf.Clamp01((now - t0) / sweep);

                float ang = Mathf.Lerp(from, to, k);

                // 摆到当前角度
                if (tipToken != null)
                {
                    HandBladeLineRenderer.SetTransform(
                        tipToken, basePos, centerYaw + ang, radius);
                }

                // 累计扫过的角度
                swept = Mathf.Abs(ang - from);

                // 到阈值就留一根痕迹
                while (swept >= nextMarkAt && nextMarkAt <= Mathf.Abs(to - from) + 0.0001f)
                {
                    float markAng = Mathf.Lerp(from, to, nextMarkAt / Mathf.Max(0.0001f, Mathf.Abs(to - from)));

                    lines.Add(CreateBladeLine(basePos, centerYaw + markAng, radius, linger));

                    nextMarkAt += degPerLine;
                }

                UpdateBladeLines(lines);

                if (k >= 1f) break;

                yield return null;
            }

            // 收尾：最后一条痕迹正好落在扇形边界上
            lines.Add(CreateBladeLine(basePos, centerYaw + to, radius, linger));

            // 正在移动的那条线也就可以下班了
            if (tipToken != null) HandBladeLineRenderer.Release(tipToken);

            // 等所有痕迹淡完
            while (lines.Count > 0)
            {
                UpdateBladeLines(lines);
                yield return null;
            }
        }

        /// <summary>一根"留在原地"的手刀线段（渲染器那边的 token）。</summary>
        private sealed class BladeLine
        {
            public object token;
            public float  dieAt;
        }

        /// <summary>
        /// 借一根线段（由 <see cref="HandBladeLineRenderer"/> 池化，不新建网格/材质）。
        ///
        /// ★ 需求："手刀会创建一堆 handbladeline。我希望建两个渲染器将它们分别容纳，
        ///         免得大量创建和删除东西。"
        /// </summary>
        private BladeLine CreateBladeLine(Vector3 center, float yawDeg, float radius, float life)
        {
            if (HandBladeLineRenderer.Instance == null) return null;

            var line = new BladeLine();

            line.token = HandBladeLineRenderer.Rent(
                center, yawDeg, radius, bladeLineWidth, handBladeColor, 0.85f);

            line.dieAt = Time.timeSinceLevelLoad + life;

            return line;
        }

        /// <summary>刷新所有线段的透明度，并把过期的还回池里。</summary>
        private void UpdateBladeLines(List<BladeLine> lines)
        {
            float now = Time.timeSinceLevelLoad;

            for (int i = lines.Count - 1; i >= 0; i--)
            {
                BladeLine b = lines[i];
                if (b == null) { lines.RemoveAt(i); continue; }

                float remain = b.dieAt - now;
                float t = Mathf.Clamp01(remain / Mathf.Max(0.02f, bladeLineLingerSeconds));

                // 不透明度从 0.85 线性降到 0（和旧的扇形特效同亮度口径）
                HandBladeLineRenderer.SetColor(b.token, handBladeColor, t * 0.85f);

                if (remain <= 0f)
                {
                    HandBladeLineRenderer.Release(b.token);
                    lines.RemoveAt(i);
                }
            }
        }



        /// <summary>以 +Z 为扇形中线、绕原点展开的水平扇形网格（扇心在原点）。</summary>
        private static Mesh BuildSectorMesh(float radius, float halfAngleDeg)
        {
            int seg = 14;

            float a0 = -halfAngleDeg * Mathf.Deg2Rad;
            float a1 =  halfAngleDeg * Mathf.Deg2Rad;

            var verts = new Vector3[seg + 2];
            var tris  = new int[seg * 3];

            verts[0] = Vector3.zero;

            for (int i = 0; i <= seg; i++)
            {
                float a = Mathf.Lerp(a0, a1, (float)i / seg);
                verts[i + 1] = new Vector3(Mathf.Sin(a) * radius, 0f, Mathf.Cos(a) * radius);
            }

            for (int i = 0; i < seg; i++)
            {
                tris[i * 3 + 0] = 0;
                tris[i * 3 + 1] = i + 1;
                tris[i * 3 + 2] = i + 2;
            }

            var mesh = new Mesh { name = "HandBladeSector" };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        // ------------------------------------------------------------------ 建模

        /// <summary>脚下圆环：一个 Ring（圆环带）网格，材质用 AimOverlay（永远画在最上层）。</summary>
        private void BuildRing()
        {
            var go = new GameObject("DungeonAimRing");

            // ★ 纯视觉的运行时物体：不要让 WorldSnapshot 记录/还原它
            //   （否则"重新开始"时快照会把它的 activeSelf 翻回来，
            //     圆环就会留在场上，而这时的 IsAiming 是 false，它不再跟随玩家）
            go.hideFlags = HideFlags.DontSave;

            m_RingRoot = go.transform;
            m_RingRoot.SetParent(transform, false);

            float outer = Mathf.Max(0.3f, ringRadius);
            float inner = outer * 0.93f;

            m_RingMesh = BuildRingMesh(64, inner, outer);

            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = m_RingMesh;

            var mr = go.AddComponent<MeshRenderer>();
            if (m_RingMat != null) mr.sharedMaterial = m_RingMat;
            mr.shadowCastingMode     = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows        = false;
            mr.lightProbeUsage       = UnityEngine.Rendering.LightProbeUsage.Off;
            mr.reflectionProbeUsage  = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        }

        /// <summary>悬浮武器：一个枪管 + 一个握把 + 一个准星，够看出朝向就行。</summary>
        private void BuildWeaponModel()
        {
            var root = new GameObject("DungeonWeaponModel");

            // ★ 同上：纯视觉，不进快照
            root.hideFlags = HideFlags.DontSave;

            m_WeaponRoot = root.transform;
            m_WeaponRoot.SetParent(transform, false);

            AddPart(m_WeaponRoot, "Barrel", new Vector3(0f, 0f, 0.20f),   new Vector3(0.055f, 0.055f, 0.46f));
            AddPart(m_WeaponRoot, "Grip",   new Vector3(0f, -0.10f, 0.02f), new Vector3(0.05f, 0.20f, 0.07f));
            AddPart(m_WeaponRoot, "Sight",  new Vector3(0f, 0.045f, 0.12f), new Vector3(0.02f, 0.02f, 0.02f));
        }

        private void AddPart(Transform parent, string name, Vector3 localPos, Vector3 localScale)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;

            // ★★★ 关键：零件也必须标记 DontSave ★★★
            //
            //   Barrel / Grip / Sight 虽然是 DungeonWeaponModel 的子物体，
            //   但它们是**独立的 GameObject**，WorldSnapshot.Capture 会把它们
            //   也各自记一条（坐标因为 hasParent 会跳过，但 hiding / activeSelf
            //   的还原是**无条件**的）。
            //
            //   于是"重新开始"时快照把它们 SetActive(true)：
            //     · 父物体 DungeonWeaponModel 还关着，理论上不该显示 —— 但它们
            //       一旦被单独激活就脱离父级的隐藏，直接画在世界原点
            //     · 而且这时 IsAiming = false，UpdateAimTransform 不跑，
            //       它们就永远钉在原地、不跟随玩家
            //   —— 这正是"重新开始后武器建模一直显示且不跟随"的原因。
            go.hideFlags = HideFlags.DontSave;

            // 只是好看，不需要碰撞
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);

            var t = go.transform;
            t.SetParent(parent, false);
            t.localPosition = localPos;
            t.localScale    = localScale;
            t.localRotation = Quaternion.identity;

            var mr = go.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                if (m_WeaponMat != null) mr.sharedMaterial = m_WeaponMat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows    = false;
            }
        }

        /// <summary>
        /// 瞄准辅助线：一条从枪口往前的细线。
        ///
        /// 为什么要它：瞄准时镜头是冻结的（照文档），屏幕上没有准星，
        /// 玩家只能靠"枪口朝哪"去猜子弹会往哪飞。这条线把猜测变成可见。
        /// 和投石的轨迹预览是同一个思路，只是枪走直线，一段就够。
        /// </summary>
        private void BuildGuide()
        {
            var go = new GameObject("DungeonAimGuide");

            // ★ 同上：纯视觉，不进快照
            go.hideFlags = HideFlags.DontSave;

            go.transform.SetParent(transform, false);

            m_GuideLine = go.AddComponent<LineRenderer>();
            m_GuideLine.useWorldSpace      = true;
            m_GuideLine.positionCount      = 2;
            m_GuideLine.loop               = false;
            m_GuideLine.textureMode        = LineTextureMode.Stretch;
            m_GuideLine.alignment          = LineAlignment.View;
            m_GuideLine.numCapVertices     = 0;
            m_GuideLine.startWidth         = Mathf.Max(0.002f, guideWidth);
            m_GuideLine.endWidth           = Mathf.Max(0.002f, guideWidth) * 0.4f;
            m_GuideLine.shadowCastingMode  = UnityEngine.Rendering.ShadowCastingMode.Off;
            m_GuideLine.receiveShadows     = false;
            m_GuideLine.generateLightingData = false;

            if (m_GuideMat != null) m_GuideLine.sharedMaterial = m_GuideMat;

            m_GuideLine.SetPosition(0, Vector3.zero);
            m_GuideLine.SetPosition(1, Vector3.zero);
            m_GuideLine.enabled = false;
        }

        /// <summary>生成一个水平放置的圆环带（annulus）网格。</summary>
        private static Mesh BuildRingMesh(int segments, float inner, float outer)
        {
            segments = Mathf.Clamp(segments, 8, 256);

            var verts = new Vector3[(segments + 1) * 2];
            var tris  = new int[segments * 6];

            for (int i = 0; i <= segments; i++)
            {
                float a = (float)i / segments * Mathf.PI * 2f;
                float c = Mathf.Cos(a);
                float s = Mathf.Sin(a);

                verts[i * 2 + 0] = new Vector3(c * inner, 0f, s * inner);
                verts[i * 2 + 1] = new Vector3(c * outer, 0f, s * outer);
            }

            for (int i = 0; i < segments; i++)
            {
                int b = i * 2;
                int t = i * 6;

                tris[t + 0] = b + 0;
                tris[t + 1] = b + 1;
                tris[t + 2] = b + 2;

                tris[t + 3] = b + 2;
                tris[t + 4] = b + 1;
                tris[t + 5] = b + 3;
            }

            var mesh = new Mesh { name = "DungeonAimRing" };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}