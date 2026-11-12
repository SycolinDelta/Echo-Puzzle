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
                 "0.25 ≈ 每秒 4 发（当前）\n" +
                 "0.32 ≈ 每秒 3 发（半自动手感）\n" +
                 "0.45 ≈ 每秒 2.2 发（重枪）\n\n" +
                 "★ 这是**基础值**，buff 的 fireRateMul 会再除它。")]
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

        [Header("精准度")]
        [Tooltip("★ 子弹发射时的随机水平偏转角（度）。\n\n" +
                 "需求：随机偏转 5° ~ -5°\n" +
                 "（比如填 5，实际会在 -5° ~ +5° 之间均匀随机）\n\n" +
                 "★ 只影响**实际飞行方向**，不影响瞄准环 / 辅助线 ——\n" +
                 "  瞄准辅助线仍然画的是「完全不偏」的理想方向。")]
        [Min(0f)] public float bulletSpreadDegrees = 4f;

        [Tooltip("单发伤害。怪物血量见 PuzzleMonster.dungeonMaxHealth（默认 12）。\n★ 支持小数（保留三位）")]
        [Min(0.001f)] public float damage = 4f;

        [Tooltip("打死一只怪回复的蓝。★ 支持小数（保留三位）")]
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
        public Color bulletColor = new Color(0.35f, 0.65f, 1f, 1f);

        [Tooltip("拖尾长度（米）。只画波前身后这么长的一段。\n\n" +
                 "★ 太长（比如跟射程一样）时，每秒好几发会首尾相接，\n" +
                 "看起来像一根连续的光柱，会让人误以为“发射没有间隔”。")]
        [Min(0.2f)] public float bulletTrailLength = 2f;

        // ★ 原来的 bulletFadeTime 已移除：
        //   现在光带上每个发光点都是**生成后 0.5 秒**自然淡出，
        //   由 DungeonBullet.pointFadeTime 控制（默认 0.5）。

        [Header("怪物受击圆柱（实体只有 0.5，这里放大到能打到）")]
        [Tooltip("水平半径（米）")]
        [Min(0.1f)] public float monsterHurtRadius = 2f;

        [Tooltip("竖直总高（米）")]
        [Min(0.1f)] public float monsterHurtHeight = 4f;

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
        /// </summary>
        public float EffectiveManaCost
            => Round3(Mathf.Max(0f, manaCost * Mul(s => s.ManaCostMul)));

        /// <summary>
        /// 含 buff 后的单发伤害。
        ///
        /// ★★ 保留三位小数 ★★
        /// </summary>
        public float EffectiveDamage
            => Round3(Mathf.Max(0.001f, damage * Mul(s => s.DamageMul)));

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
            => Mathf.Max(0.02f, fireInterval / Mathf.Max(0.1f, Mul(s => s.FireRateMul)));

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

            // 强化三选一界面打开时世界是冻结的（timeScale = 0），武器也不该响应
            if (DungeonBuffSelectUI.Instance != null && DungeonBuffSelectUI.Instance.IsOpen)
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

            if (!IsAiming) return;

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
            if (!showCrosshair) return;
            if (!GameUI.IsDungeonMode) return;
            if (GameUI.Current != GameState.Playing) return;

            // ★ 只有**瞄准中**才显示准星 —— 退出瞄准就消失
            if (!IsAiming) return;

            if (m_CrossTex == null) m_CrossTex = MakePixel();

            // 按 1080 高度等比缩放，和 HUD 一个口径
            float scale = Screen.height / 1080f;
            if (scale <= 0.0001f) return;

            // ★ 注意 IMGUI 的 y 轴是向下的，而 Input 积分出来的是屏幕坐标（y 向上），
            //   所以要翻转一次。
            float cx = CrosshairScreen.x;
            float cy = Screen.height - CrosshairScreen.y;

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

            // 初始化指针位置为屏幕中心，避免第一帧指向奇怪的方向
            m_VirtualCursor = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);

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
        private void HandleAimMouse()
        {
            float mx = Input.GetAxisRaw("Mouse X");
            float my = Input.GetAxisRaw("Mouse Y");

            // 积分出虚拟指针（屏幕像素坐标）
            m_VirtualCursor += new Vector2(mx, my) * Mathf.Max(1f, virtualCursorPixelsPerUnit);

            // 夹在屏幕内，避免指针跑到画面外
            m_VirtualCursor.x = Mathf.Clamp(m_VirtualCursor.x, 0f, Screen.width);
            m_VirtualCursor.y = Mathf.Clamp(m_VirtualCursor.y, 0f, Screen.height);

            CrosshairScreen = m_VirtualCursor;

            if (m_Player == null) return;

            // 指针 -> 地面世界点
            if (!m_Player.ScreenToGround(m_VirtualCursor, out Vector3 groundPoint))
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

            // 3) 出膛：方向由 AimYaw 决定（和相机没关系），再叠加随机散布
            //
            // ★ 精准度：每次发射在 [-bulletSpreadDegrees, +bulletSpreadDegrees]
            //   之间均匀随机偏一个水平角。
            //   AimDirection() 本身**不改** —— 瞄准环和辅助线仍指向理想方向。
            Vector3 aimDir = AimDirection();
            Vector3 dir    = ApplyBulletSpread(aimDir);

            Vector3 muzzle = MuzzlePosition(dir);

            DungeonBullet.Spawn(muzzle, dir,
                                EffectiveBulletSpeed, bulletRange,
                                EffectiveBulletWidth,
                                dmg, killBack,
                                monsterHurtRadius, monsterHurtHeight,
                                m_BulletMat, bulletTrailLength);

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
