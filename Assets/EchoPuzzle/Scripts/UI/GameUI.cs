using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EchoPuzzle
{
    /// <summary>游戏所处的大状态。</summary>
    public enum GameState
    {
        ModeSelect, // 主页：选「探秘模式 / 地牢模式」（只在 Home 场景里用）
        Home,       // 关卡内的开局菜单
        DifficultyMenu, // 暂停菜单里点"降低难度"后的子界面
        Playing,    // 进行中
        Paused,     // 暂停
        Won,        // 通关
        Dead,       // 踩到陷阱死亡
    }

    /// <summary>
    /// 首页 / 暂停 / 通关三个界面，全部用 IMGUI 绘制。
    ///
    /// 用 IMGUI 而不是 uGUI 是刻意的：整套界面只有文字，
    /// IMGUI 的 GUIStyle 可以把按钮背景完全去掉，代码里直接拼出来，
    /// 不需要 Canvas / 字体资源 / Prefab 这些额外资产。
    ///
    /// 状态与光标的约定：
    ///   Home / Paused  -> 光标解锁、时间冻结，玩家不接受任何输入
    ///   Playing        -> 光标锁定、时间正常流逝、玩家可操作
    ///   Won / Dead     -> 前 endFadeDuration 秒是"淡出期"：
    ///                     光标仍锁定（可以用鼠标环顾）、时间正常流逝（光带继续跑），
    ///                     但**玩家不可操作**（不能走 / 发声 / 放标记）。
    ///                     淡出结束后光标解锁，让玩家点按钮。
    ///
    /// 其它脚本通过静态成员查询当前状态，不需要互相持有引用：
    ///   GameUI.CameraInputActive  —— 镜头是否可以转 / 缩放
    ///   GameUI.PlayerControllable —— 玩家角色是否可以操作
    ///
    /// ══════════════════════════════════════════════════════════════
    /// ★★ 交接说明（给接手地牢模式的人看）★★
    ///
    ///   这个脚本服务**两个模式**，但有很多地方是探秘模式专用的：
    ///
    ///   【可用】任何模式都适用的公共部分：
    ///     · GameState 状态机（ModeSelect / Home / Playing / Paused / Won / Dead）
    ///     · 输入门控静态属性：
    ///         GameUI.PlayerControllable
    ///         GameUI.CameraInputActive
    ///         GameUI.GameplayInputActive
    ///     · 光标锁定 / 世界显隐（SetWorldVisible）
    ///     · 胜负淡出（BeginEndFade / EnterWon / EnterDead）
    ///     · 按钮绘制（DrawButton / DrawTitle / DrawLabel）
    ///
    ///   【仅探秘模式】地牢模式里**不该出现**或需要改造的部分：
    ///     · 保存 / 加载（CanUseSave 已经在地牢模式返回 false）
    ///     · 路线图（DrawCompass）—— 地牢里没有指南针
    ///     · 提示框（DrawHintBox）/ 密码框（DrawPasswordBox）
    ///     · 受惊死亡（BeginFrightenedDeath）—— 地牢里没有"秒杀"
    ///     · 降低难度菜单（DrawDifficultyMenu）—— 是探秘模式的难度设置
    ///
    ///   【需要新建】地牢模式专用的 UI：
    ///     · 生命值 HUD   -> Scripts/Dungeon/DungeonHUD.cs
    ///       左上角去掉电池（BatterySystem）和石头（StoneSystem），
    ///       换成生命值。这两个 HUD 已经在地牢模式下自动不画了。
    ///     · 枪械准星     -> 跟 DungeonGun 一起做
    ///
    ///   【注意】PuzzleMonster / DaggerSystem / StoneThrowSystem
    ///     都已经在地牢模式里自动禁用，不需要额外处理。
    /// ══════════════════════════════════════════════════════════════
    /// </summary>
    [DefaultExecutionOrder(-200)]
    public sealed class GameUI : MonoBehaviour
    {
        // ------------------------------------------------------------------ 静态入口

        /// <summary>全局唯一实例。</summary>
        public static GameUI Instance { get; private set; }

        private static GameState s_State = GameState.Home;

        /// <summary>当前状态。</summary>
        public static GameState Current => s_State;

        // ------------------------------------------------------------------ 游戏模式

        /// <summary>当前场景名。</summary>
        public static string CurrentSceneName =>
            UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;

        /// <summary>是不是主页场景（Home）。</summary>
        public static bool IsHomeScene => CurrentSceneName == HomeSceneName;

        /// <summary>当前处于哪个游戏模式。</summary>
        public static bool IsDungeonMode => CurrentSceneName == DungeonSceneName;

        /// <summary>主页场景名。</summary>
        public const string HomeSceneName    = "Home";
        public const string PuzzleSceneName  = "PuzzleMode";
        public const string DungeonSceneName = "DungeonMode";

        /// <summary>
        /// 从主页选了模式后置 true：新场景里的 GameUI 会**跳过开局菜单**
        /// 直接进入 Playing。
        ///
        /// ★ 必须是静态的 —— LoadScene 之后 GameUI 会重新创建，
        ///   实例字段传不过去。
        /// </summary>
        private static bool s_AutoStartOnLoad;


        /// <summary>
        /// 当前模式能不能存档。
        ///
        /// ★ 地牢模式（DungeonMode）**不能存档** ——
        ///   存档按钮不显示，保存入口也会被拒绝。
        /// </summary>
        public static bool CanUseSave => !IsDungeonMode;

        /// <summary>
        /// 玩家是否应该接受输入。首页 / 暂停 / 死亡时为 false。
        /// 通关时为 true —— 世界继续跑，玩家仍可转动视角看残留的声波。
        ///
        /// ⚠ 注意它是"**玩家操作**"的开关，不是"整个 Update 是否运行"的开关。
        ///   胜负界面淡出那几秒里镜头仍要能动，所以 PlayerController 用的是
        ///   <see cref="CameraInputActive"/>，并且用
        ///   <see cref="PlayerControllable"/> 单独挡掉移动。
        /// </summary>
        public static bool GameplayInputActive =>
            Instance != null && (s_State == GameState.Playing || s_State == GameState.Won);

        // ------------------------------------------------------------------ 密码输入
        //
        // 当玩家站在"需要密码"的 TriggerVolume 里时：
        //   · 右下角显示「输入密码并回车：」
        //   · 数字键改为输入密码，不再放置标记（MarkerSystem 会检查 PasswordInputActive）

        /// <summary>当前正在等密码输入的触发区（没有就是 null）。</summary>
        public static TriggerVolume ActivePasswordVolume { get; private set; }

        /// <summary>是否正在等待玩家输入密码。</summary>
        public static bool PasswordInputActive =>
            ActivePasswordVolume != null && ActivePasswordVolume.WantsPasswordInput;

        /// <summary>
        /// 镜头是否接受输入（转视角 / 滚轮缩放）。
        /// 进行中、以及**胜负界面淡出期间**都为 true —— 那几秒里还能环顾四周。
        /// </summary>
        public static bool CameraInputActive =>
            Instance != null &&
            (s_State == GameState.Playing || Instance.m_EndFadeActive);

        /// <summary>
        /// 玩家角色是否可操作（移动 / 发声 / 放标记）。
        ///
        /// ★ 只有真正的"进行中"才是 true —— 胜负界面淡出期间世界继续跑，但人不能动。
        ///
        /// ★★ 地牢的全屏界面开着时也是 false ★★
        ///
        ///   需求："在选buff界面按下空格时能听到玩家发出声波的音效……
        ///         我希望在选buff界面无法操控玩家物体。"
        ///
        ///   buff 三选一 / 商店 / loadout 用的是 timeScale = 0 冻结世界，
        ///   但 s_State 仍然是 Playing，所以光看状态位玩家还是"可操作"的 ——
        ///   空格（发声波）、移动这些输入照样生效。
        ///   这里额外排除全屏界面打开的情况。
        /// </summary>
        public static bool PlayerControllable =>
            Instance != null &&
            s_State == GameState.Playing &&
            !DungeonFullScreenUiOpen();

        /// <summary>是否正在播放胜负界面的淡出。</summary>
        public static bool EndFadeInProgress =>
            Instance != null && Instance.m_EndFadeActive;

        /// <summary>通关时由 LevelGoal 调用。</summary>
        public static void NotifyGoalReached()
        {
            if (Instance != null) Instance.EnterWon();
        }

        /// <summary>踩到陷阱时由 ZoneTrigger 调用。</summary>
        public static void NotifyPlayerDied()
        {
            if (Instance != null) Instance.EnterDead();
        }

        // ------------------------------------------------------------------ 受惊死亡

        /// <summary>
        /// 怪物因「玩家收回匕首」进入受惊状态时调用。
        ///
        /// 效果（持续 delay 秒）：
        ///   · 玩家**只能以 slowSpeed 移动**（不能瞄准）
        ///   · 玩家物体的轮廓在整段倒计时内渐变成 RGB(255,0,0)
        ///   · 时间到**一定**触发死亡（不管期间发生了什么）
        /// </summary>
        public static void BeginFrightenedDeath(float delay)
        {
            if (Instance == null) return;
            Instance.StartFrightenedDeath(delay);
        }

        /// <summary>
        /// 登记"是这只怪物触发了受惊死亡"。
        /// 3 秒倒计时结束时由它播放击杀演出（大量光带 + 击杀音效）。
        /// </summary>
        public static void RegisterFrightenedKiller(PuzzleMonster killer)
        {
            if (Instance == null) return;
            Instance.m_FrightKiller = killer;
        }

        /// <summary>受惊死亡是否正在倒计时。</summary>
        public static bool FrightenedDeathActive =>
            Instance != null && Instance.m_FrightDeathActive;

        /// <summary>受惊期间玩家允许的最大移动速度（米/秒）。</summary>
        public static float FrightenedMoveSpeed =>
            Instance != null && Instance.m_FrightDeathActive ? Instance.frightenedMoveSpeed : float.MaxValue;

        [Header("受惊死亡")]
        [Tooltip("受惊后玩家能移动的最大速度（米/秒）")]
        public float frightenedMoveSpeed = 0.4f;

        [Tooltip("玩家被染成的颜色")]
        public Color frightenedTint = new Color(1f, 0f, 0f, 1f);

        private bool  m_FrightDeathActive;
        private float m_FrightDeathTimer;

        /// <summary>触发受惊死亡的怪物（死亡时由它播放光带 + 音效）。</summary>
        private PuzzleMonster m_FrightKiller;
        private float m_FrightDeathTotal = 1f;

        private static readonly int OutlineColorId = Shader.PropertyToID("_OutlineColor");

        private void StartFrightenedDeath(float delay)
        {
            if (m_FrightDeathActive) return;

            m_FrightDeathActive = true;
            m_FrightDeathTotal  = Mathf.Max(0.05f, delay);
            m_FrightDeathTimer  = m_FrightDeathTotal;

            // 记住原始轮廓色（用 m_PlayerLook 之外单独存，避免影响淡出逻辑）
            m_FrightOriginalColors.Clear();

            if (m_PlayerRenderers.Count == 0) CachePlayerRenderers();

            for (int i = 0; i < m_PlayerRenderers.Count; i++)
            {
                Renderer r = m_PlayerRenderers[i];
                if (r == null) { m_FrightOriginalColors.Add(Color.white); continue; }

                if (m_PlayerMpb == null) m_PlayerMpb = new MaterialPropertyBlock();
                r.GetPropertyBlock(m_PlayerMpb);

                m_FrightOriginalColors.Add(
                    m_PlayerMpb.HasColor(OutlineColorId) ? m_PlayerMpb.GetColor(OutlineColorId)
                                                          : Color.white);
            }
        }

        private readonly List<Color> m_FrightOriginalColors = new List<Color>(4);

        // ------------------------------------------------------------------ 密码输入

        /// <summary>
        /// 找出"当前正在等密码输入"的触发区。
        ///
        /// 只在游戏进行中、且玩家确实站在某个需要密码的触发区里时才非空。
        /// 场景里的触发区通常只有几个，每帧扫一遍代价可以忽略。
        ///
        /// ★ 这里只负责"发现新的触发区"，淡出由 DrawPasswordBox 的状态机负责 ——
        ///   所以清空时**不直接置 null**，而是交给状态机判断该不该淡出。
        /// </summary>
        private void UpdatePasswordVolume()
        {
            TriggerVolume found = null;

            if (s_State == GameState.Playing)
            {
                var all = FindObjectsOfType<TriggerVolume>();

                for (int i = 0; i < all.Length; i++)
                {
                    TriggerVolume v = all[i];
                    if (v == null) continue;
                    if (!v.WantsPasswordInput) continue;

                    found = v;
                    break;      // 一次只处理一个
                }
            }

            ActivePasswordVolume = found;
        }

        // 密码框的淡入淡出状态（和 HintBox 完全同一套做法）

        private TriggerVolume m_PwdShownVolume;   // 当前正在显示的那个触发区（淡出期间也保留）

        /// <summary>
        /// **冻结下来的文字内容**。
        ///
        /// ★ 这是关键：淡出期间不能再去读 m_PwdShownVolume.PasswordInput ——
        ///   密码输对的那一刻 TriggerVolume 会把 PasswordInput 清成空字符串，
        ///   于是"密码部分"会在淡出第一帧就消失，看起来只有提示文字在淡出。
        ///
        ///   所以这里把"最后要显示的那一行"记下来，淡出全程都用它。
        /// </summary>
        private string m_PwdShownLine = "";

        private bool  m_PwdShownWrong;

        private float m_PwdAlpha;
        private bool  m_PwdFadingOut;

        /// <summary>
        /// 右下角绘制密码提示。
        ///
        /// ★ 样式和 HintZone **完全一致**：
        ///   · 透明文本框（不画背景）
        ///   · 白色不透明文字
        ///   · 左上对齐、自动换行
        ///   · **同一个位置 / 同一个字体 / 同一个字号**
        ///   · **同一套淡入淡出**（hintFadeIn / hintFadeOut）
        ///
        /// 格式：输入密码并回车：&lt;玩家输入的内容&gt;
        ///   输入的内容**可见**，紧跟在提示文字后面。
        ///
        /// ★ 触发区触发后 hiding 变成 1，它就不再"需要输入"了，
        ///   但这里的淡出状态机让文字**正常淡出**，而不是瞬间消失 ——
        ///   整行（含密码部分）一起淡出。
        /// </summary>
        private void DrawPasswordBox(float w)
        {
            float dt = Time.unscaledDeltaTime;

            float fadeIn  = Mathf.Max(0.01f, hintFadeIn);
            float fadeOut = Mathf.Max(0.01f, hintFadeOut);

            // ---- 状态机（与 DrawHintBox 同构） ----

            if (m_PwdFadingOut)
            {
                // 正在淡出
                m_PwdAlpha = Mathf.MoveTowards(m_PwdAlpha, 0f, dt / fadeOut);

                if (m_PwdAlpha <= 0.001f)
                {
                    m_PwdAlpha       = 0f;
                    m_PwdShownVolume = null;
                    m_PwdShownLine   = "";
                    m_PwdFadingOut   = false;
                }
            }
            else if (m_PwdShownVolume == null)
            {
                // 还没有显示任何东西 -> 出现新触发区就开始淡入
                if (ActivePasswordVolume != null)
                {
                    m_PwdShownVolume = ActivePasswordVolume;
                    m_PwdShownLine   = BuildPasswordLine(m_PwdShownVolume);   // ★ 冻住文字
                    m_PwdShownWrong  = m_PwdShownVolume.PasswordWrong;
                    m_PwdAlpha       = Mathf.MoveTowards(m_PwdAlpha, 1f, dt / fadeIn);
                }
            }
            else if (ActivePasswordVolume == null)
            {
                // 触发区不再需要输入（输对了 / 触发后 hiding 变 1 / 玩家离开）
                // -> 开始淡出。**文字保持冻结**，整行一起淡出。
                m_PwdFadingOut = true;
            }
            else if (ActivePasswordVolume != m_PwdShownVolume)
            {
                // 换成了另一个触发区 -> 先淡出旧的
                m_PwdFadingOut = true;
            }
            else
            {
                // 内容不变，保持全亮；同时刷新文字（玩家还在输入）
                m_PwdShownLine  = BuildPasswordLine(m_PwdShownVolume);
                m_PwdShownWrong = m_PwdShownVolume.PasswordWrong;
                m_PwdAlpha      = Mathf.MoveTowards(m_PwdAlpha, 1f, dt / fadeIn);
            }

            if (m_PwdAlpha <= 0.001f) return;
            if (m_PwdShownVolume == null) return;

            // ---- 绘制（与 DrawHintBox 完全相同的布局参数） ----

            float bw = hintBoxWidth;
            float bh = hintBoxHeight;
            float bx = w - hintBoxMargin - bw;
            float by = RefHeight - hintBoxMargin - bh;

            var style = new GUIStyle(GUIStyle.none)
            {
                font      = m_BuiltFont,
                fontSize  = hintTextSize,          // ← 与 HintZone 同字号
                alignment = TextAnchor.UpperLeft,
                wordWrap  = true,
            };

            // 颜色：白色不透明（和 HintZone 一样），再乘淡入淡出的 alpha。
            // 输错时整行变淡红。
            Color c = hintTextColor;
            if (m_PwdShownWrong) c = new Color(1f, 0.4f, 0.4f, 1f);

            c.a *= m_PwdAlpha;

            style.normal.textColor = c;

            GUI.Label(new Rect(bx, by, bw, bh), m_PwdShownLine, style);
        }

        /// <summary>拼出要显示的那一行：提示文字 + 玩家输入（可见）。</summary>
        private static string BuildPasswordLine(TriggerVolume v)
        {
            return "输入密码并回车：" + v.PasswordInput;
        }

        /// <summary>受惊死亡的每帧推进。放在 Update 里调用。</summary>
        private void UpdateFrightenedDeath()
        {
            if (!m_FrightDeathActive) return;

            m_FrightDeathTimer -= Time.deltaTime;

            // ---- 渐变成红色 ----
            float k = 1f - Mathf.Clamp01(m_FrightDeathTimer / m_FrightDeathTotal);

            for (int i = 0; i < m_PlayerRenderers.Count; i++)
            {
                Renderer r = m_PlayerRenderers[i];
                if (r == null) continue;

                Color from = i < m_FrightOriginalColors.Count ? m_FrightOriginalColors[i] : Color.white;
                Color to   = Color.Lerp(from, frightenedTint, k);

                if (m_PlayerMpb == null) m_PlayerMpb = new MaterialPropertyBlock();
                r.GetPropertyBlock(m_PlayerMpb);
                m_PlayerMpb.SetColor(OutlineColorId, to);
                r.SetPropertyBlock(m_PlayerMpb);
            }

            // ---- 时间到：必定死亡 ----
            if (m_FrightDeathTimer <= 0f)
            {
                m_FrightDeathActive = false;
                m_FrightDeathTimer  = 0f;

                // ★ 先播"怪物击杀玩家"的那套演出：
                //   大量光带 + 击杀音效。由触发受惊的那只怪物来放。
                if (m_FrightKiller != null) m_FrightKiller.PlayKillEffects();
                m_FrightKiller = null;

                // ★ 死亡瞬间**保持红色** —— 这里不能 RestoreFrightColors()，
                //   否则玩家会"在死的一瞬间变回白色"。
                //   红色只在重玩 / 回首页时（ResetRunState）才恢复。
                ForceFrightColor();

                EnterDead();
            }
        }

        /// <summary>
        /// 把玩家轮廓色**强制设成受惊红**（死亡瞬间用，保证不会闪回白色）。
        /// </summary>
        private void ForceFrightColor()
        {
            for (int i = 0; i < m_PlayerRenderers.Count; i++)
            {
                Renderer r = m_PlayerRenderers[i];
                if (r == null) continue;

                if (m_PlayerMpb == null) m_PlayerMpb = new MaterialPropertyBlock();
                r.GetPropertyBlock(m_PlayerMpb);
                m_PlayerMpb.SetColor(OutlineColorId, frightenedTint);
                r.SetPropertyBlock(m_PlayerMpb);
            }
        }

        /// <summary>把玩家轮廓色恢复原样。</summary>
        private void RestoreFrightColors()
        {
            for (int i = 0; i < m_PlayerRenderers.Count; i++)
            {
                Renderer r = m_PlayerRenderers[i];
                if (r == null) continue;

                Color c = i < m_FrightOriginalColors.Count ? m_FrightOriginalColors[i] : Color.white;

                if (m_PlayerMpb == null) m_PlayerMpb = new MaterialPropertyBlock();
                r.GetPropertyBlock(m_PlayerMpb);
                m_PlayerMpb.SetColor(OutlineColorId, c);
                r.SetPropertyBlock(m_PlayerMpb);
            }
        }
        // ------------------------------------------------------------------ 配置

        [Header("字体")]
        [Tooltip("留空则用 Unity 内置字体。也可以在 Play 中把它换成任意字体资源，样式会自动重建")]
        public Font font;

        [Header("配色")]
        public Color textColor = Color.white;

        [Header("尺寸（以 1080 高度为基准，自动等比缩放）")]
        [Tooltip("标题字号（ECHO PUZZLE / PAUSE / YOU WIN!）")]
        public int titleSize  = 120;
        [Tooltip("按钮字号（Start / Resume / Restart / Exit）")]
        public int buttonSize = 48;
        [Tooltip("正文行字号（通关时间）")]
        public int labelSize  = 48;

        [Tooltip("按钮之间的垂直间距（首页 / 通关 / 降低难度等界面共用）")]
        public float buttonSpacing = 110f;

        [Header("暂停界面")]
        [Tooltip("【探秘模式】暂停菜单里按钮的竖直间距。\n\n" +
                 "★ 只影响暂停菜单，不影响首页 / 通关 / 降低难度界面。")]
        public float pauseButtonSpacing = 80f;

        [Tooltip("【地牢模式】暂停菜单里按钮的竖直间距。\n\n" +
                 "★ 需求：地牢模式的暂停界面按钮间距 = 110，\n" +
                 "  和探秘模式的 80 区分开。")]
        public float dungeonPauseButtonSpacing = 110f;

        [Tooltip("标题顶部距离屏幕顶端多少（1080 基准下的像素）")]
        public float titleTop = 300f;

        [Tooltip("标题与第一个按钮之间的距离")]
        public float titleToButtons = 180f;

        [Tooltip("通关时间与第一个按钮之间的距离")]
        public float labelToButtons = 100f;

        [Header("遮挡")]
        [Tooltip("首页 / 暂停 / 通关时把整个世界隐藏掉（相机 Culling Mask 清零），只留纯黑背景 + 文字")]
        public bool hideWorldOnOverlay = true;

        [Header("开局相机（每次 Start / Restart 时应用）")]
        [Tooltip("开局相机的初始俯仰角（度）。正值 = 向下看；89.9 = 垂直俯视地面")]
        public float startPitch = 60f;
        [Tooltip("开局相机的初始水平朝向（度）。设为 -1 则自动采用出生点朝向")]
        public float startYaw = 0f;

        [Header("指南针（右上角）")]
        public bool showCompass = true;

        [Tooltip("指南针半径（1080 基准下的像素）")]
        public float compassRadius = 74f;

        [Tooltip("指南针圆心距离屏幕右边和上边的距离")]
        public float compassMargin = 150f;

        [Tooltip("刻度与刻字的字号")]
        public int compassSize = 22;

        [Tooltip("指南针整体不透明度")]
        [Range(0f, 1f)] public float compassAlpha = 0.9f;

        [Header("提示文字框（右下角）")]
        public bool showHintBox = true;

        [Tooltip("文字框的宽高（1080 基准下的像素）。文字内容暂时留空，这里先把框留出来")]
        public float hintBoxWidth  = 620f;
        public float hintBoxHeight = 200f;

        [Tooltip("文字框距离屏幕右边和下边的距离")]
        public float hintBoxMargin = 70f;

        [Tooltip("提示文字的字号")]
        public int hintTextSize = 40;

        [Tooltip("提示文字的颜色。默认浅灰")]
        public Color hintTextColor = new Color(1f, 1f, 1f, 1f);   // 纯白、完全不透明

        [Tooltip("文字淡入耗时（秒）")]
        [Min(0.01f)] public float hintFadeIn  = 0.45f;

        [Tooltip("文字淡出耗时（秒）")]
        [Min(0.01f)] public float hintFadeOut = 0.35f;

        // ------------------------------------------------------------------ 运行时

        private const float RefHeight = 1080f;

        private PlayerController m_Player;
        private SonarSystem      m_Sonar;
        private LevelGoal        m_Goal;
        private MarkerSystem     m_Markers;
        private FootprintSystem  m_Footprints;
        private StoneThrowSystem m_Stones;

        private Camera m_Camera;
        private int    m_SavedCullingMask;
        private bool   m_MaskSaved;

        // 世界可见性：每个 Renderer 的"基准开关状态"只记录一次，之后永不覆盖。
        // 用 InstanceID 做键，避免下标错位；物体被销毁也不影响其它项。
        private readonly Dictionary<int, bool> m_BaseVisibility  = new Dictionary<int, bool>(512);
        private readonly List<Renderer>        m_HiddenRenderers = new List<Renderer>(512);

        private int m_LastWorldVisibleState = -1;   // -1 未知 / 0 隐藏 / 1 显示
        private int m_MaskRetryFrames;

        private Texture2D m_CompassDot;

        // ---- 胜负界面的淡入 ----

        [Header("胜负界面")]
        [Tooltip("进入「你赢了！」/「你死了」之后：\n" +
                 "  · 地图与场上光带渐渐淡出到黑\n" +
                 "  · 背景变色到目标色\n" +
                 "  · 文字逐渐显现\n" +
                 "三者共用这个时长（秒）")]
        [Min(0f)] public float endFadeDuration = 5f;

        [Tooltip("「你死了」的背景色")]
        public Color loseBackgroundColor = new Color(65f / 255f, 0f, 0f, 1f);

        [Tooltip("「你赢了！」的背景色")]
        public Color winBackgroundColor = Color.white;

        [Tooltip("「你赢了！」的文字颜色。背景是纯白，所以文字要用纯黑")]
        public Color winTextColor = Color.black;

        /// <summary>进入胜负界面后经过的时间（用**非缩放**时间，因为「你死了」会把 timeScale 设成 0）。</summary>
        private float m_EndFade;
        private bool  m_EndFadeActive;

        // ---- 玩家淡出 ----

        [Tooltip("「你死了」时，玩家物体淡出到不可见所用的时长（秒）")]
        [Min(0f)] public float playerFadeDuration = 1f;

        private bool m_PlayerFadeActive;
        private float m_PlayerFade;

        /// <summary>玩家身上的渲染器（开场时抓一次）。</summary>
        private readonly List<Renderer> m_PlayerRenderers = new List<Renderer>(4);

        /// <summary>
        /// 玩家轮廓的初始外观参数，逐个渲染器记下来 —— 淡出是从"原本的值"开始插值的。
        /// 用 MaterialPropertyBlock 写，不改材质资产、也不复制材质实例。
        ///
        /// 为什么要记三个而不是只记 _OutlineWidth：
        ///   玩家的可见性由两部分组成 ——
        ///     · Pass 1 的外扩张描边：只受 _OutlineWidth 控制
        ///     · Pass 2 的边缘光（rim）：`col = _BodyColor + _OutlineColor * fres * _RimStrength * _Glow`
        ///       它**完全不依赖 _OutlineWidth**，所以光把宽度降到 0，
        ///       身上还会留一圈亮边。必须把 _RimStrength 和 _Glow 也一起归零。
        /// </summary>
        private struct PlayerLook
        {
            public float outlineWidth;
            public float rimStrength;
            public float glow;
        }

        private readonly List<PlayerLook> m_PlayerLook = new List<PlayerLook>(4);

        private MaterialPropertyBlock m_PlayerMpb;

        private static readonly int OutlineWidthId  = Shader.PropertyToID("_OutlineWidth");
        private static readonly int RimStrengthId   = Shader.PropertyToID("_RimStrength");
        private static readonly int GlowId          = Shader.PropertyToID("_Glow");

        private Texture2D m_WhitePixel;

        private Vector3 m_StartPosition;
        private float   m_StartYaw;

        private float m_Elapsed;

        /// <summary>当前局已累计的通关计时（秒）。</summary>
        public float ElapsedTime => m_Elapsed;

        private GUIStyle m_TitleStyle;
        private GUIStyle m_LabelStyle;
        private GUIStyle m_ButtonStyle;
        private GUIStyle m_CompassStyle;
        private bool     m_StylesReady;

        // 记录样式是按哪组参数建出来的，用来判断要不要重建
        private Font  m_BuiltFont;
        private int   m_BuiltTitleSize;
        private int   m_BuiltButtonSize;
        private int   m_BuiltLabelSize;
        private int   m_BuiltCompassSize;
        private Color m_BuiltColor;

        // ------------------------------------------------------------------ 生命周期

        private void Awake()
        {
            Instance = this;

            m_Player = FindObjectOfType<PlayerController>();
            m_Sonar  = FindObjectOfType<SonarSystem>();
            m_Goal   = FindObjectOfType<LevelGoal>();
            m_Camera = Camera.main;

            if (m_Player != null)
            {
                m_StartPosition = m_Player.transform.position;
                // startYaw = -1 表示自动采用出生点朝向；否则用玩家在 Inspector 里指定的初始水平角
                m_StartYaw = startYaw < -0.5f ? m_Player.transform.eulerAngles.y : startYaw;
            }

            // 记下相机原本能看到哪些层，之后在"显示世界"和"全黑"之间来回切
            if (m_Camera != null)
            {
                m_SavedCullingMask = m_Camera.cullingMask;
                m_MaskSaved        = true;
            }

            // ★ 在 Home 场景里 -> 显示"选模式"界面；
            //   在关卡场景里（PuzzleMode / DungeonMode）-> 显示关卡内的开局菜单。
            //
            //   ★ 必须放在最后：EnterHome() 会把 s_State 设成 Home，
            //     先设 ModeSelect 的话会被它覆盖掉。
            if (IsHomeScene)
            {
                EnterModeSelect();
                return;
            }

            // ★ 从主页选了模式进来的：清掉标志，正常显示关卡内的开局菜单
            //   （"新游戏 / 加载存档"）。
            s_AutoStartOnLoad = false;

            EnterHome();
        }

        /// <summary>进入主页的模式选择界面（只在 Home 场景里）。</summary>
        private void EnterModeSelect()
        {
            s_State = GameState.ModeSelect;
            Time.timeScale = 0f;
            UnlockCursor();
            SetWorldVisible(false);
        }

        /// <summary>
        /// 关闭引擎前的最后一道保险。
        ///
        /// ★ 首页 / 暂停界面会设 Time.timeScale = 0。如果正好在那种状态下
        ///   关闭 Unity，编辑器域会留着 timeScale = 0 —— 编辑器自身的部分更新
        ///   也受它影响，可能表现为关闭时卡住、界面显示
        ///   "Waiting for Unity's code to finish executing"。
        ///
        ///   这里在引擎关闭流程最早的一步就把它恢复成 1。
        /// </summary>
        private void OnApplicationQuit()
        {
            Time.timeScale = 1f;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (m_CompassDot != null) Destroy(m_CompassDot);
            if (m_WhitePixel != null) Destroy(m_WhitePixel);
            // 离开时把时间恢复正常，避免编辑器里残留 timeScale = 0
            Time.timeScale = 1f;
        }

        private void Update()
        {
            // Esc：进行中 -> 暂停；暂停 -> 继续
            //
            // ★ 地牢强化三选一界面打开时**忽略 Esc** ——
            //   那个界面用 timeScale = 0 冻结世界，但它自己不是"暂停"，
            //   如果这里进了 EnterPaused()，三选一后面就会叠一层暂停 UI。
            if (Input.GetKeyDown(KeyCode.Escape) && !DungeonBuffUIOpen())
            {
                if (s_State == GameState.Playing) EnterPaused();
                else if (s_State == GameState.Paused) EnterPlaying();
            }

            if (s_State == GameState.Playing) m_Elapsed += Time.deltaTime;

            // 受惊死亡的倒计时（渐变 + 到点必死）
            UpdateFrightenedDeath();

            // 密码输入：找出"当前正在等输入"的触发区
            UpdatePasswordVolume();

            // 胜负界面的淡出推进。
            // 用 unscaledDeltaTime 而不是 deltaTime —— 虽然 EnterDead 现在也把
            // timeScale 设成 1 了，但淡出进度不该受任何时间缩放影响，用非缩放时间最稳。
            if (s_State == GameState.Won || s_State == GameState.Dead)
            {
                if (m_EndFadeActive)
                {
                    m_EndFade += Time.unscaledDeltaTime;

                    // 淡出走完 -> 锁光标 + 把光带真正清掉。
                    // 黑幕已经铺满，这一步看不出来，但能让场上不留残余。
                    // 判 EndFade01 而不是比较时间，这样 endFadeDuration = 0 时也能正确收尾。
                    if (EndFade01 >= 1f) EndEndFade();
                }

                // 「你死了」时玩家在 playerFadeDuration 秒内淡出到不可见。
                // 单独一条时间轴：它比场景黑幕快得多（1s vs 5s），
                // 所以人先"消失"，场景再慢慢沉下去。
                if (m_PlayerFadeActive)
                {
                    m_PlayerFade += Time.unscaledDeltaTime;
                    ApplyPlayerFade();
                }
            }
            else
            {
                m_EndFadeActive = false;
                m_EndFade       = 0f;

                m_PlayerFadeActive = false;
                m_PlayerFade       = 0f;
            }

            EnforceWorldVisibility();
        }

        /// <summary>
        /// 光标锁定要放到 LateUpdate 里做，而且锁上之后还要再巩固几帧。
        ///
        /// 原因：按 Esc 恢复时，Unity 自己（以及编辑器）会在 Update 之后处理光标 ——
        /// 在 Update 里设的 lockState 会被它覆盖掉，表现就是"用鼠标点按钮能隐藏，
        /// 但按 Esc 回来光标还在"。放到 LateUpdate 并连续几帧重申，就能盖住它。
        /// </summary>
        private void LateUpdate()
        {
            // ★ 任何"瞄准"状态下光标都必须保持解锁
            //   （极坐标要读 Input.mousePosition，锁住了就永远钉在屏幕中心）。
            //   松开时由各自的系统自己锁回，这里在瞄准时直接让路即可。
            bool aiming = AnySystemAiming();

            if (s_State == GameState.Playing && !aiming)
            {
                if (Cursor.lockState != CursorLockMode.Locked)
                {
                    Cursor.lockState = CursorLockMode.Locked;
                    Cursor.visible   = false;
                    m_CursorHoldFrames = CursorHoldFrames;   // 重新开始巩固
                }
                else if (m_CursorHoldFrames > 0)
                {
                    Cursor.visible = false;
                    m_CursorHoldFrames--;
                }
            }
        }

        /// <summary>锁上之后再连续重申这么多帧，压掉 Unity 自己的光标处理。</summary>
        private const int CursorHoldFrames = 5;

        private int m_CursorHoldFrames;

        /// <summary>
        /// 兜底：菜单状态下反复确认相机 Culling Mask 是 0、光标状态正确。
        ///
        /// 为什么需要这个：GameUI 是在各种系统生成完之后才创建的，
        /// 如果那一刻 Camera.main 还没就绪（或顺序被别处改动），
        /// 一次性的设置就会失效、菜单上还能看到地图。这里每帧自检。
        ///
        /// 光标也在这里统一强制 —— 之前遇到过"暂停再回来光标没隐藏"的问题，
        /// 与其追每一条调用路径，不如按状态每帧强制一次（代价可忽略）。
        /// </summary>
        private void EnforceWorldVisibility()
        {
            // ---- 光标 ----
            //   · 进行中：锁定隐藏
            //   · 胜负界面**淡出期间**：同样锁定隐藏 —— 那几秒还能用鼠标转视角，
            //     解锁了视角就没法转了
            //   · 其它（首页 / 暂停 / 淡出结束后的胜负界面）：解锁可见，好点按钮
            // ★ 任何瞄准状态（投石 / 匕首）都不锁 ——
            //   它们需要 Input.mousePosition 读极坐标，
            //   这里每帧强制锁定的话会把它们的解锁覆盖掉。
            bool aiming     = AnySystemAiming();
            bool wantLocked = (s_State == GameState.Playing || m_EndFadeActive) && !aiming;

            if (wantLocked)
            {
                if (Cursor.lockState != CursorLockMode.Locked)
                {
                    Cursor.lockState = CursorLockMode.Locked;
                    Cursor.visible   = false;
                }
            }
            else
            {
                // ★★ 地牢模式下"解锁但不可见" ★★
                //
                //   需求："指针始终不出现，看起来像是短剑就是指针"。
                //
                //   · 地牢 + 有全屏界面（三选一 / 商店 / loadout）：
                //        None + visible=false
                //        —— 鼠标坐标有效（短剑能跟、能点卡片），但看不到系统指针。
                //   · 其它情况（首页 / 暂停 / 胜负界面）：照旧 None + visible=true，
                //        那些界面本来就要用系统指针点按钮。
                bool dungeonUi = IsDungeonMode && aiming;

                if (Cursor.lockState != CursorLockMode.None)
                {
                    Cursor.lockState = CursorLockMode.None;
                    Cursor.visible   = !dungeonUi;
                }
                else if (Cursor.visible && dungeonUi)
                {
                    Cursor.visible = false;
                }
            }

            if (!hideWorldOnOverlay) return;

            // ★ 胜负界面的淡出期间，世界要**保持可见** ——
            //   它是"渐渐淡出"的，如果这里立刻把 cullingMask 清零，
            //   就变成硬切、看不到淡出过程了。
            //   等 EndFade01 走到 1（黑幕铺满、已经看不出来）之后再隐藏。
            bool wantVisible = s_State == GameState.Playing
                            || ((s_State == GameState.Won || s_State == GameState.Dead)
                                && EndFade01 < 1f);

            // ★★ 地牢：buff 界面开着 / 地图还在等 buff -> 强制隐藏世界 ★★
            //
            //   需求（用户给的时序）：
            //     "跳出buff界面后，立即新建地图，然后将玩家物体传送至出生点，
            //      但是此时不要显示地图和玩家物体，不要让玩家操控玩家物体。"
            //     "玩家选好buff后，退出buff界面，并显示地图和玩家物体。"
            //
            //   ★ 这时 s_State 已经是 Playing（EnterPlaying 在开局就调了），
            //     光看状态位世界会被判成"该显示" —— 必须在这里额外压下去，
            //     否则建好的新地图会立刻露出来。
            if (IsDungeonMode && DungeonSubStateBlocksWorld())
                wantVisible = false;

            if (wantVisible)
            {
                // 之前被隐藏过（比如从暂停界面过来的）就先恢复回来
                if (m_LastWorldVisibleState != 1)
                {
                    SetWorldVisible(true);
                    m_LastWorldVisibleState = 1;
                }

                m_MaskRetryFrames = 0;
                return;
            }

            // 状态没变过、而且已经确认过若干帧，就不必每帧折腾
            if (m_LastWorldVisibleState == 0 && m_MaskRetryFrames > 120) return;

            if (m_LastWorldVisibleState != 0)
            {
                m_LastWorldVisibleState = 0;
                m_MaskRetryFrames = 0;
            }

            if (m_Camera == null) m_Camera = Camera.main;
            if (m_Camera == null) m_Camera = FindObjectOfType<Camera>();

            if (m_Camera != null)
            {
                if (!m_MaskSaved)
                {
                    m_SavedCullingMask = m_Camera.cullingMask;
                    m_MaskSaved        = true;
                }

                if (m_Camera.cullingMask != 0) m_Camera.cullingMask = 0;
            }

            m_MaskRetryFrames++;
        }

        // ------------------------------------------------------------------ 状态切换

        private void EnterHome()
        {
            s_State = GameState.Home;
            Time.timeScale = 0f;
            UnlockCursor();
            SetWorldVisible(false);
        }

        private void EnterPlaying()
        {
            s_State = GameState.Playing;
            Time.timeScale = 1f;
            LockCursor();
            SetWorldVisible(true);
        }

        private void EnterPaused()
        {
            s_State = GameState.Paused;
            Time.timeScale = 0f;
            UnlockCursor();
            SetWorldVisible(false);
        }

        private void EnterWon()
        {
            if (s_State == GameState.Won) return;

            s_State = GameState.Won;
            Time.timeScale = 1f;

            // ★ 不解锁光标：淡出那几秒里还要用鼠标转视角。
            //   解锁挪到淡出结束（EndEndFade）再做。

            // ★ 不立刻隐藏世界。
            //   胜负界面是"地图与光带渐渐淡出"，立即隐藏会变成硬切。
            //   淡出由 Update 里的黑幕推进 + 这里的 EndFade01 合成完成。
            //   fadePlayer = true：玩家同样在 playerFadeDuration 秒内淡出到不可见。

            BeginEndFade(true);
        }

        private void EnterDead()
        {
            if (s_State == GameState.Dead) return;

            s_State = GameState.Dead;

            // ★ timeScale 保持 1：淡出那几秒里世界要继续跑 ——
            //   光带继续推进、继续淡出，镜头还能转。
            //   以前这里是 0，会画面直接冻住。淡出用的是 unscaledDeltaTime，
            //   所以这里改不改都不影响淡出本身的进度。
            Time.timeScale = 1f;

            // ★ 不立刻解锁光标：那几秒里还要用鼠标转视角。
            //   解锁挪到淡出结束（EndEndFade）和进入其它状态时再做。

            // ★ 不立刻隐藏世界 —— 见 EnterWon 的说明。
            //   fadePlayer = true：玩家在 playerFadeDuration 秒内淡出到不可见。
            BeginEndFade(true);
        }

        /// <summary>
        /// 开始胜负界面的淡出：黑幕压暗地图 + 背景变色 + 文字显现。
        /// <paramref name="fadePlayer"/> 为 true 时同时开始玩家物体的淡出。
        /// </summary>
        private void BeginEndFade(bool fadePlayer = false)
        {
            m_EndFade       = 0f;
            m_EndFadeActive = true;

            // 样式里的文字颜色是按状态决定的（赢了要纯黑），强制重建一次
            m_StylesReady = false;

            if (fadePlayer) BeginPlayerFade();
        }

        // ------------------------------------------------------------------ 玩家淡出

        /// <summary>
        /// 把玩家恢复成完全可见的状态：重新启用渲染器 + 把轮廓外观参数写回原值。
        ///
        /// ★ 必须在 SetWorldVisible(false) **之前**调用。
        ///
        ///   原因：SetWorldVisible 会记录每个渲染器的"基准可见性"，而且**只记一次**。
        ///   如果玩家淡出先把渲染器关掉了（enabled = false），世界隐藏时就会把
        ///   false 当成基准记进去 —— 之后无论怎么恢复世界，玩家都会保持隐藏。
        ///   这正是"触发 Lose/Win 之后再开新游戏，玩家不见了"的原因。
        ///
        ///   在关世界之前先把玩家恢复成 enabled = true，基准就记对了。
        /// </summary>
        private void RestorePlayerVisibility()
        {
            m_PlayerFadeActive = false;
            m_PlayerFade       = 0f;

            for (int i = 0; i < m_PlayerRenderers.Count; i++)
            {
                Renderer r = m_PlayerRenderers[i];
                if (r == null) continue;

                // 外观参数写回原值（清掉淡出时覆盖的 _OutlineWidth / _RimStrength / _Glow）
                PlayerLook look = i < m_PlayerLook.Count ? m_PlayerLook[i] : default;
                if (look.outlineWidth > 0f || look.rimStrength > 0f || look.glow > 0f)
                {
                    if (m_PlayerMpb == null) m_PlayerMpb = new MaterialPropertyBlock();

                    r.GetPropertyBlock(m_PlayerMpb);
                    m_PlayerMpb.SetFloat(OutlineWidthId, look.outlineWidth);
                    m_PlayerMpb.SetFloat(RimStrengthId,  look.rimStrength);
                    m_PlayerMpb.SetFloat(GlowId,         look.glow);
                    r.SetPropertyBlock(m_PlayerMpb);
                }

                r.enabled = true;
            }
        }

        /// <summary>抓一次玩家身上的渲染器，并记下各自的初始轮廓外观。</summary>
        private void CachePlayerRenderers()
        {
            m_PlayerRenderers.Clear();
            m_PlayerLook.Clear();

            if (m_Player == null) return;

            m_Player.GetComponentsInChildren(true, m_PlayerRenderers);

            for (int i = 0; i < m_PlayerRenderers.Count; i++)
            {
                Renderer r = m_PlayerRenderers[i];

                // 没有共享材质时给一套兜底值，插值仍然能正常跑
                var look = new PlayerLook
                {
                    outlineWidth = 0.022f,
                    rimStrength  = 1.1f,
                    glow         = 2f,
                };

                Material mat = r != null ? r.sharedMaterial : null;
                if (mat != null)
                {
                    if (mat.HasProperty(OutlineWidthId)) look.outlineWidth = mat.GetFloat(OutlineWidthId);
                    if (mat.HasProperty(RimStrengthId))  look.rimStrength  = mat.GetFloat(RimStrengthId);
                    if (mat.HasProperty(GlowId))         look.glow         = mat.GetFloat(GlowId);
                }

                m_PlayerLook.Add(look);
            }
        }

        /// <summary>开始玩家淡出（重新启用渲染器，从头插值）。</summary>
        private void BeginPlayerFade()
        {
            if (m_Player == null) return;

            CachePlayerRenderers();

            m_PlayerFade       = 0f;
            m_PlayerFadeActive = true;

            // 上一局如果把它关掉了，这里先放回来
            for (int i = 0; i < m_PlayerRenderers.Count; i++)
            {
                if (m_PlayerRenderers[i] != null) m_PlayerRenderers[i].enabled = true;
            }

            ApplyPlayerFade();
        }

        /// <summary>
        /// 把玩家轮廓按淡出进度缩到不可见。
        ///
        /// 三个参数一起插值到 0：
        ///   _OutlineWidth —— Pass 1 的外扩描边（宽度为 0 时外壳与本体重合，被本体盖住）
        ///   _RimStrength —— Pass 2 的边缘光强度
        ///   _Glow        —— Pass 2 边缘光的整体增益
        ///
        /// 只降 _OutlineWidth 是不够的（见 PlayerLook 的注释）。
        ///
        /// 用 MaterialPropertyBlock 而不是 material / sharedMaterial：
        ///   · sharedMaterial 会改到工程里的 .mat 资产
        ///   · .material 会复制一份材质实例，而且只作用于第一个渲染器
        /// </summary>
        private void ApplyPlayerFade()
        {
            float dur = Mathf.Max(0f, playerFadeDuration);
            float t   = dur <= 0.0001f ? 1f : Mathf.Clamp01(m_PlayerFade / dur);

            if (m_PlayerMpb == null) m_PlayerMpb = new MaterialPropertyBlock();

            for (int i = 0; i < m_PlayerRenderers.Count; i++)
            {
                Renderer r = m_PlayerRenderers[i];
                if (r == null) continue;

                PlayerLook look = i < m_PlayerLook.Count ? m_PlayerLook[i] : default;

                r.GetPropertyBlock(m_PlayerMpb);
                m_PlayerMpb.SetFloat(OutlineWidthId, Mathf.Lerp(look.outlineWidth, 0f, t));
                m_PlayerMpb.SetFloat(RimStrengthId,  Mathf.Lerp(look.rimStrength,  0f, t));
                m_PlayerMpb.SetFloat(GlowId,         Mathf.Lerp(look.glow,         0f, t));
                r.SetPropertyBlock(m_PlayerMpb);
            }

            // 已经完全看不见了，直接把渲染器关掉，省掉每帧画一份全黑几何
            if (t >= 1f)
            {
                m_PlayerFadeActive = false;

                for (int i = 0; i < m_PlayerRenderers.Count; i++)
                {
                    if (m_PlayerRenderers[i] != null) m_PlayerRenderers[i].enabled = false;
                }
            }
        }

        /// <summary>
        /// 淡出结束：把场上残留的光带清掉。
        ///
        /// 为什么不早清：胜负界面要求"光带渐渐淡出"，
        /// 所以整个 <see cref="endFadeDuration"/> 期间它们都得留在场上被黑幕压暗。
        /// 清掉之后世界也会被 <see cref="EnforceWorldVisibility"/> 隐藏，不会闪。
        /// </summary>
        private void EndEndFade()
        {
            m_EndFadeActive = false;
            m_EndFade       = endFadeDuration;      // 固定成满值，EndFade01 才稳定返回 1

            // 淡出结束 -> 这才把光标交还给玩家（胜负界面上的按钮要点）
            UnlockCursor();

            // ★ 在世界被隐藏之前，先把玩家恢复成可见。
            //
            //   SetWorldVisible(false) 会记录每个渲染器的"基准可见性"，而且只记一次。
            //   如果玩家的渲染器此刻还是淡出后的 enabled = false，
            //   那么 false 就会被当成基准记下来 —— 之后开新游戏玩家永远是隐藏的。
            //   先恢复成 true，基准才记对。
            RestorePlayerVisibility();

            if (m_Sonar == null) m_Sonar = FindObjectOfType<SonarSystem>();
            if (m_Sonar != null)
            {
                SonarTrailRenderer trail = m_Sonar.GetComponentInChildren<SonarTrailRenderer>(true);
                if (trail != null) trail.ClearTrails();
            }
        }

        /// <summary>淡入进度 0..1。duration <= 0 时直接返回 1（不淡入）。</summary>
        private float EndFade01
        {
            get
            {
                if (!m_EndFadeActive) return 1f;
                if (endFadeDuration <= 0.0001f) return 1f;
                return Mathf.Clamp01(m_EndFade / endFadeDuration);
            }
        }

        /// <summary>
        /// 显示 / 隐藏整个世界。
        ///
        /// 双保险：
        ///   1. 相机 Culling Mask 清零 —— 一次挡住所有几何
        ///   2. 逐个关掉 Renderer —— 万一相机没找到或 Mask 被别处改回来，这一层也兜底
        ///
        /// ★ 关键：每个 Renderer 的"基准可见性"**只在第一次隐藏时记录，之后永不覆盖**。
        ///
        ///   之前的写法是每次隐藏都重新快照一遍当前状态。看起来没问题，
        ///   但如果某次隐藏时物体本来就是关着的，快照就把 false 当成了基准 ——
        ///   之后恢复世界时全部还原成 false，画面一片全黑。
        ///   这正好解释了"Exit 回首页再 Start 就看不到世界和玩家"。
        ///
        ///   改成只记一次基准之后，无论状态机来回切多少次，还原结果都稳定。
        ///
        /// 另外：本组件用 IMGUI 绘制，自己没有任何 Renderer，所以关掉所有 Renderer 是安全的
        /// （CanvasRenderer 不是 Renderer 的子类，FindObjectsOfType&lt;Renderer&gt; 本来就取不到它）。
        /// </summary>
        private void SetWorldVisible(bool visible)
        {
            if (!hideWorldOnOverlay) return;

            // ---- 1) 相机 Culling Mask ----
            if (m_Camera == null) m_Camera = Camera.main;
            if (m_Camera == null) m_Camera = FindObjectOfType<Camera>();

            if (m_Camera != null)
            {
                if (!m_MaskSaved)
                {
                    m_SavedCullingMask = m_Camera.cullingMask;
                    m_MaskSaved        = true;
                }

                m_Camera.cullingMask = visible ? m_SavedCullingMask : 0;
            }

            // ---- 2) 逐个 Renderer ----
            if (!visible)
            {
                Renderer[] all = FindObjectsOfType<Renderer>(true);
                for (int i = 0; i < all.Length; i++)
                {
                    Renderer r = all[i];
                    if (r == null) continue;

                    int id = r.GetInstanceID();

                    // 只在第一次见到它时记录基准，之后不再覆盖
                    if (!m_BaseVisibility.ContainsKey(id))
                    {
                        m_BaseVisibility[id] = r.enabled;
                        m_HiddenRenderers.Add(r);
                    }

                    r.enabled = false;
                }
            }
            else
            {
                for (int i = 0; i < m_HiddenRenderers.Count; i++)
                {
                    Renderer r = m_HiddenRenderers[i];
                    if (r == null) continue;

                    m_BaseVisibility.TryGetValue(r.GetInstanceID(), out bool baseOn);
                    r.enabled = baseOn;
                }
            }
        }

        /// <summary>
        /// 兜底修复：把"基准可见性"里被误记成 false 的项改回 true。
        ///
        /// 正常情况下不会需要它 —— 只要在隐藏世界之前先把玩家恢复成可见就行。
        /// 但如果某个物体的渲染器在"被隐藏之前"就已经被别处关掉了（比如玩家淡出），
        /// 基准就会记成 false，从此再也显示不出来。
        /// 这个方法在开新一局时把所有基准强制成正，是一条便宜的保险。
        /// </summary>
        private void RepairBaseVisibility()
        {
            foreach (int id in new List<int>(m_BaseVisibility.Keys))
            {
                if (!m_BaseVisibility[id]) m_BaseVisibility[id] = true;
            }
        }

        /// <summary>
        /// 是否有系统需要**解锁光标**。
        ///
        /// ★ 现在**永远是 false** —— 没有任何系统需要解锁光标了。
        ///
        ///   投石和匕首都改成了"虚拟光标"：
        ///     光标保持 Locked（玩家看不到指针），
        ///     用 GetAxisRaw("Mouse X"/"Mouse Y") 的增量自己积分出位置，
        ///     再拿它算极坐标。效果和真实光标完全一致，但屏幕上没有指针。
        ///
        ///   如果这里放行（返回 true），GameUI 就会去解锁光标，
        ///   屏幕上立刻冒出指针 —— 这正是"按右键仍会出现指针"的原因。
        /// </summary>
        private static bool AnySystemAiming()
        {
            // 正常情况永远 false —— 投石 / 匕首都用"虚拟光标"，不需要解锁光标。

            // 但匕首的**调试模式**要显示真实指针来验证瞄准，
            // 这时必须让路，否则这里每帧锁回去，指针一闪就没了。
            var d = DaggerSystem.Instance;
            if (d != null && d.IsAiming && d.showCursorForDebug) return true;

            // ★★ 地牢的三个全屏界面：让 GameUI **让路**（不再锁光标）★★
            //
            //   这三个界面用的是 IMGUI 的 GUI.Button，必须靠 Input.mousePosition
            //   才能命中。而 Locked 状态下鼠标位置被钉死在屏幕中心 —— 点不到。
            //
            //   所以这里返回 true，让 LateUpdate / EnforceWorldVisibility
            //   不要再去锁光标。界面自己会把光标设成
            //       lockState = None, visible = false
            //   —— 解锁（鼠标坐标有效、能点）但**不画系统指针**，
            //      屏幕上的短剑图标就是唯一的指针。
            //
            //   ★ 关键是三处必须**口径一致**（都认 None + 隐藏），
            //     否则 GameUI 每帧锁回、界面每帧解锁，就会疯狂闪烁。

            return DungeonFullScreenUiOpen();
        }

        /// <summary>
        /// 地牢里是否有"全屏 IMGUI 界面"开着（buff 三选一 / 商店 / loadout）。
        ///
        /// ★ 这些界面打开时光标必须是 None + 隐藏（见 AnySystemAiming 的注释）。
        /// </summary>
        /// <summary>
        /// 地牢里"世界应该被遮住"的子状态：buff 界面开着，或地图正在等 buff。
        ///
        /// ★ 和 <see cref="DungeonFullScreenUiOpen"/> 的区别：
        ///   那个只管"光标是否让路"，这个管"世界是否可见"。
        ///   地图等 buff 期间界面可能还没弹出来（或刚好关掉一帧），
        ///   但世界一律不该露出来。
        /// </summary>
        private static bool DungeonSubStateBlocksWorld()
        {
            if (DungeonFullScreenUiOpen()) return true;

            var gen = Object.FindObjectOfType<EchoPuzzle.Dungeon.DungeonMapGenerator>();
            if (gen != null && gen.IsWaitingBuff) return true;

            return false;
        }

        private static bool DungeonFullScreenUiOpen()
        {
            var buff = EchoPuzzle.Dungeon.DungeonBuffSelectUI.Instance;
            if (buff != null && buff.IsOpen) return true;

            var shop = EchoPuzzle.Dungeon.DungeonShopUI.Instance;
            if (shop != null && shop.IsOpen) return true;

            var loadout = EchoPuzzle.Dungeon.DungeonLoadoutUI.Instance;
            return loadout != null && loadout.IsOpen;
        }

        /// <summary>地牢里"要用鼠标点"的界面是否开着（三选一 / 商店）。探秘模式下永远是 false。</summary>
        private static bool DungeonBuffUIOpen()
        {
            var ui = EchoPuzzle.Dungeon.DungeonBuffSelectUI.Instance;
            if (ui != null && ui.IsOpen) return true;

            // 商店界面（第十周任务 3）同样要真实指针点"购买"
            var shop = EchoPuzzle.Dungeon.DungeonShopUI.Instance;
            if (shop != null && shop.IsOpen) return true;

            // 插件装备界面（第十周任务 5）要拖/点插槽，也必须放开指针
            var loadout = EchoPuzzle.Dungeon.DungeonLoadoutUI.Instance;
            return loadout != null && loadout.IsOpen;
        }

        private static void LockCursor()
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible   = false;
        }

        private static void UnlockCursor()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible   = true;
        }

        // ------------------------------------------------------------------ 按钮动作

        private void StartGame()
        {
            try
            {
                StartGameInternal();
            }
            catch (System.Exception e)
            {
                // ★ 不重新抛出 —— 让游戏至少能进 Playing 状态，
                //   否则一直卡在 Home 菜单（表现为"无法操作"）。
                Debug.LogError("[诊断] StartGame 内部出错，但已忽略并继续：" + e);

                // 兜底：强行进入游戏中，把时间恢复正常
                EnterPlaying();
            }
        }

        private void StartGameInternal()
        {
            m_Elapsed = 0f;

            // ★「开始游戏」：给**所有物体的坐标 + hiding** 拍一份开局快照。
            //
            //   这份快照**只在这里保存一次**，之后整局游戏都不会再更新它。
            //   点「重新开始」时还原到它。
            WorldSnapshot.CaptureStart();

            // 开局把玩家与相机一起归位（位置、朝向、缩放距离都回到初始值），
            // 避免上一次 Play 里滚轮缩放过的相机距离残留到这一局。
            //
            // ★★ 地牢模式例外 ★★
            //
            //   m_StartPosition 是 GameUI.Awake() 时记下的**场景原始坐标**，
            //   而地牢模式的出生点是由 DungeonMapGenerator 按矩阵里的 8
            //   在 Start() 里把玩家瞬移过去的 —— 那比 Awake 晚，
            //   所以 m_StartPosition 是个**过时的错误位置**。
            //   这里如果照旧 Teleport 回去，玩家就会被拉出出生点。
            //
            //   地牢模式的起点由生成器负责 —— 改调它的出生点。
            if (IsDungeonMode)
                TeleportToDungeonSpawn();
            else if (m_Player != null)
                m_Player.Teleport(m_StartPosition, m_StartYaw, startPitch);

            // ★ 地牢模式：开局送一次"任意自选强化"（第十周任务 1）
            QueueDungeonStarterBuff();

            EnterPlaying();
        }

        /// <summary>
        /// 【地牢模式】排队一次「开局自选强化」。
        ///
        /// 探秘模式下 DungeonBuffSelectUI.Instance 为 null，这里什么都不做。
        /// 排的是"队"不是"立刻开"：界面那边会等 0.6 秒再弹，
        /// 让玩家先看清自己站在哪儿。
        /// </summary>
        private static void QueueDungeonStarterBuff()
        {
            var picker = Dungeon.DungeonBuffSelectUI.Instance;
            if (picker != null && !picker.StarterPending) picker.QueueStarterBuff();
        }

        private void RestartGame()
        {
            // ★「重新开始」：先做常规复位，再还原到**开局快照**。
            //
            //   顺序很重要：ResetRunState() 里会调用 TriggerVolume / Hide 相关逻辑，
            //   必须等它把该还原的都还原完，最后再按快照统一覆盖一次，
            //   这样才不会出现"某一步又把 hiding / 坐标改回去了"。
            ResetRunState();

            WorldSnapshot.RestoreStart();
            TriggerVolume.HideAllMarkedObjects();

            // ★ 快照恢复之后再复位一次提示区。
            //
            //   为什么要在这一步：
            //     · 勾了 triggerOnce 的 HintZone 在上一局结束时把自己 hiding 改成 1，
            //       于是失活、并且从 ZoneSystem 的注册列表里掉了出去。
            //     · ResetRunState() 里的那次复位发生在 hiding 还是 1 的时候；
            //       现在 hiding 已经恢复到 0、物体重新启用（OnEnable 会重新注册），
            //       再复位一次能保证 Consumed 一定被清掉。
            //
            //   ResetHint() 是幂等的，重复调用没有副作用。
            ZoneSystem.ResetAllHints();

            // ★ 地牢模式：重开一局同样送一次开局自选（ResetRunState 刚清空过 buff）
            QueueDungeonStarterBuff();

            EnterPlaying();
        }

        /// <summary>
        /// 把这一局的状态清干净：声波光带、标记、玩家位置、终点触发、机关。
        ///
        /// 「重玩」和「Exit 回首页」都要调它。
        /// 之前只有 RestartGame 调，所以从暂停 / 通关界面点 Exit 回首页再 Start 时，
        /// 上一局残留的光带和标记还挂在场上（玩家还会看到它们）。
        /// </summary>
        private void ResetRunState()
        {
            // ★ 先把玩家恢复成可见（并写回轮廓外观参数）。
            //   淡出可能还没走完玩家就被重开了（比如淡出期按了 Esc 之类），
            //   这里兜底一次，保证新一局玩家一定是看得见的。
            RestorePlayerVisibility();

            // 兜底：把被误记成"基准隐藏"的渲染器改回可见
            RepairBaseVisibility();

            // 清掉场上所有残留的声波
            if (m_Sonar == null) m_Sonar = FindObjectOfType<SonarSystem>();
            if (m_Sonar != null)
            {
                SonarTrailRenderer trail = m_Sonar.GetComponentInChildren<SonarTrailRenderer>(true);
                if (trail != null) trail.ClearTrails();
            }

            // 清掉放置的标记（回首页时它们可能已经被隐藏，所以要连未激活的一起找）
            if (m_Markers == null) m_Markers = FindObjectOfType<MarkerSystem>(true);
            if (m_Markers != null) m_Markers.ClearMarkers();

            // 清掉上一把留下的脚印。同样要连未激活的一起找。
            if (m_Footprints == null) m_Footprints = FindObjectOfType<FootprintSystem>(true);
            if (m_Footprints != null) m_Footprints.ClearFootprints();

            // 清掉场上飞着的石子和石子光带
            if (m_Stones == null) m_Stones = FindObjectOfType<StoneThrowSystem>(true);
            if (m_Stones != null) m_Stones.ClearStones();

            // 玩家与相机回到出生点
            //
            // ★★ 地牢模式例外 ★★
            //   m_StartPosition 是 GameUI.Awake() 时的场景原始坐标，对地牢模式
            //   来说是个过时的错误位置。地牢的起点由 DungeonMapGenerator 负责，
            //   这里改调生成器的出生点。
            if (IsDungeonMode)
                TeleportToDungeonSpawn();
            else if (m_Player != null)
                m_Player.Teleport(m_StartPosition, m_StartYaw, startPitch);

            // 重置终点触发
            if (m_Goal == null) m_Goal = FindObjectOfType<LevelGoal>(true);
            if (m_Goal != null) m_Goal.ResetCleared();

            // 恢复所有机关空间，并重新启用它们关掉的东西（机关门、陷阱空间）
            ZoneTrigger.ResetAllMechanisms();

            // ★ 复位所有 TriggerVolume。
            //
            //   这一步以前漏了 —— 于是"触发后出现"的物体重开时不会被重新隐藏。
            //   ResetTrigger() 会还原触发时被改动的物体，并**重新执行开局隐藏**
            //   （spawnObjects 在新开局必须重新藏起来）。
            ZoneSystem.ResetAllTriggers();


            // ★ 复位所有怪物：回到出生点、清掉击杀标记 / 移动目标 / 悬浮状态 / 光带。
            //
            //   之前漏了这一步，导致：
            //     · 怪物停在上一把追到的位置（不回到出生点）
            //     · m_Killed 还是 true -> Kill() 里直接 return，"击杀玩家"彻底失效
            foreach (PuzzleMonster mon in FindObjectsOfType<PuzzleMonster>(true))
            {
                if (mon != null) mon.ResetMonster();
            }

            // ★★ 最后再无条件跑一次"开局隐藏" ★★
            //
            //   为什么必须放在**所有复位动作之后**：
            //     · ResetAllTriggers() 会把触发器改动过的物体还原成"原来的状态"，
            //       其中就包括把它们 SetActive(true)
            //     · ResetMonster() 里也有一段 `wasInactive -> SetActive(true)`，
            //       用来把被匕首移除的怪物复活
            //   这两个动作都会把"开局本该删除"的物体重新打开。
            //
            //   所以最终的隐藏必须放在它们**后面**，否则刚藏好又被打开 ——
            //   表现就是"开局隐藏的怪物被启用后，点重新开始它没有消失"。
            //
            //   HideAllMarkedObjects() 是幂等的，重复调用没有副作用。
            TriggerVolume.HideAllMarkedObjects();

            // 注意：hiding 的快照恢复在 RestartGame() 里做 ——
            //       必须在 ResetRunState() **完全结束之后**再覆盖，
            //       否则这里的 HideAllMarkedObjects 又会被它改回去。

            // ★ 把玩家轮廓色恢复成原本的白色。
            //
            //   受惊死亡时我们**故意**把玩家染成红色并保留了它（ForceFrightColor），
            //   这里必须显式还原，否则重开一局玩家还是红的。
            m_FrightDeathActive = false;
            m_FrightDeathTimer  = 0f;
            m_FrightKiller      = null;
            RestoreFrightColors();

            // 电池回到开局数量
            if (BatterySystem.Instance != null) BatterySystem.Instance.ResetToStart();

            // 石头回到开局数量
            if (StoneSystem.Instance != null) StoneSystem.Instance.ResetToStart();

            // 音乐停止并复位（否则上一局的音乐会在一局里继续播）
            TriggerMusic.ResetAllMusic();

            // ★★ 地牢模式：复位武器 / 蓝量 / 强化 ★★
            //
            //   重新开始时玩家可能正按着右键瞄准 —— 如果不管，
            //   DungeonWeapon 的瞄准圆环会留在场上，
            //   而且这时 IsAiming 已经不成立、不再跟随玩家，
            //   看起来就是一个"卡在原地不动的圆环"。
            ResetDungeonSystems();

            m_Elapsed = 0f;
        }

        /// <summary>
        /// 复位地牢模式独有的武器 / 蓝量 / 强化系统（探秘模式下什么都不做）。
        /// </summary>
        private void ResetDungeonSystems()
        {
            if (!IsDungeonMode) return;

            // ---- 武器：强制退出瞄准，隐藏圆环 / 武器模型 / 辅助线 ----
            var gun = EchoPuzzle.Dungeon.DungeonWeapon.Instance;
            if (gun != null) gun.ForceLeaveAim();

            // ---- 蓝量：回到开局值 ----
            var mana = EchoPuzzle.Dungeon.DungeonMana.Instance;
            if (mana != null) mana.ResetMana();

            // ---- 金币：回到开局值（第十周任务 2）----
            var gold = EchoPuzzle.Dungeon.DungeonCurrency.Instance;
            if (gold != null) gold.ResetGold();

            // ---- 强化（buff）：清空 ----
            //
            //   ★ 按 R 重开一局 = 从第 1 层重新来过，强化自然也要清。
            //     如果以后想做成"跨局保留"，把这一行去掉即可。
            var buffs = EchoPuzzle.Dungeon.DungeonBuffState.Instance;
            if (buffs != null) buffs.Clear();

            // ---- 三选一界面：如果正开着，关掉并把 timeScale 还原 ----
            var picker = EchoPuzzle.Dungeon.DungeonBuffSelectUI.Instance;
            if (picker != null && picker.IsOpen) picker.ForceClose();

            // ---- 插件装备界面：同理（否则重开后画面被它挡着、时间还停着）----
            var loadout = EchoPuzzle.Dungeon.DungeonLoadoutUI.Instance;
            if (loadout != null && loadout.IsOpen) loadout.Close();

            // ---- 怪物潮：清掉残留怪物 ----
            var waves = FindObjectOfType<EchoPuzzle.Dungeon.MonsterWaveManager>();
            if (waves != null) waves.ForceClear();

            // ---- ★ 地图：回到第 1 层并重新随机生成 ----
            //
            //   需求："地牢模式每次重建地图的时机是在开始新游戏、
            //         进入下一层和点重新开始的时候，并且每次都会重新随机创建地图"
            //
            //   ★ 前两个时机已经在 DungeonMapGenerator 里处理了；
            //     这里是第三个：点"重新开始"。
            var gen = FindObjectOfType<EchoPuzzle.Dungeon.DungeonMapGenerator>();
            if (gen != null) gen.RestartDungeonMap();

            // ---- 玩家血量：回到开局值 ----
            //
            // ★ 之前漏了这一步，"重新开始"之后血量还是上一局剩下的，
            //   死过一次再开就会带着 0~1 血进去。
            var health = FindObjectOfType<EchoPuzzle.Dungeon.PlayerHealth>();
            if (health != null) health.ResetHealth();

            // ---- 怪物房状态：清掉"已经打过"的标记 ----
            //
            // ★ m_Consumed 记录哪些区域已经被清完。重开一局要全部作废，
            //   否则再走进怪物房时不会重新刷怪（会当成已经打过）。
            var zone = EchoPuzzle.Dungeon.MonsterZone.Instance;
            if (zone != null) zone.ResetProgress();
        }

        private void ExitToDesktop()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        // ------------------------------------------------------------------ 存档

        /// <summary>暂停界面上的 Save 按钮。</summary>
        private void SaveGame()
        {
            // ★ 地牢模式不能存档
            if (!CanUseSave)
            {
                ShowToast("地牢模式不能存档");
                return;
            }

            // ★ 有怪物处于 killRadiusMoving 时不许存档
            if (!SaveSystem.CanSave())
            {
                ShowToast("场上有怪物活动或受惊，无法保存");
                return;
            }

            if (SaveSystem.Save()) ShowToast("已保存");
            else                   ShowToast("保存失败");
        }

        // ------------------------------------------------------------------ 临时提示

        private string m_ToastText;
        private float  m_ToastUntil;

        /// <summary>在屏幕下方显示一条短提示（秒）。</summary>
        private void ShowToast(string text, float seconds = 2f)
        {
            m_ToastText  = text;
            m_ToastUntil = Time.unscaledTime + seconds;
        }

        /// <summary>画提示条。</summary>
        private void DrawToast(float w)
        {
            if (string.IsNullOrEmpty(m_ToastText)) return;
            if (Time.unscaledTime > m_ToastUntil) { m_ToastText = null; return; }

            var style = new GUIStyle(GUIStyle.none)
            {
                font      = m_BuiltFont,
                fontSize  = hintTextSize,
                alignment = TextAnchor.MiddleCenter,
            };

            Color c = hintTextColor;
            c.a = 1f;
            style.normal.textColor = c;

            GUI.Label(new Rect(0f, RefHeight * 0.5f + 120f, w, 60f), m_ToastText, style);
        }

        /// <summary>首页上的 Load game 按钮。</summary>
        private void LoadGame()
        {
            // ★ 地牢模式不能读档（存档只属于探秘模式）
            if (!CanUseSave) return;

            if (!SaveSystem.HasSave) return;

            // 先按"重玩"把这一局清干净（光带 / 标记 / 机关复位），
            // 再把存档盖上去 —— 否则上一次 Play 的残留会和存档混在一起
            ResetRunState();

            if (SaveSystem.Load())
            {
                // 读档后计时从存档时刻继续（ResetRunState 已把它清 0，这里恢复）
                m_Elapsed = SaveSystem.LoadedElapsed;
                EnterPlaying();
            }
            else
            {
                // 读档失败就留在首页
                EnterHome();
            }
        }

        // ------------------------------------------------------------------ 绘制

        private void EnsureStyles()
        {
            // 胜负界面会用不同的文字颜色（赢了是纯黑），所以把它算进"用于比对的颜色"里
            Color activeTextColor = s_State == GameState.Won ? winTextColor : textColor;

            // 检测到 Inspector 上的字体 / 字号 / 颜色变了就重建，这样运行中调参能立刻看到效果
            if (m_StylesReady &&
                m_BuiltFont == font &&
                m_BuiltTitleSize == titleSize &&
                m_BuiltButtonSize == buttonSize &&
                m_BuiltLabelSize == labelSize &&
                m_BuiltCompassSize == compassSize &&
                m_BuiltColor == activeTextColor)
                return;

            Font f = font != null ? font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (f == null) f = Resources.GetBuiltinResource<Font>("Arial.ttf");

            m_TitleStyle = new GUIStyle(GUIStyle.none)
            {
                font      = f,
                fontSize  = titleSize,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.UpperCenter,
            };
            m_TitleStyle.normal.textColor = activeTextColor;

            m_LabelStyle = new GUIStyle(GUIStyle.none)
            {
                font      = f,
                fontSize  = labelSize,
                alignment = TextAnchor.UpperCenter,
            };
            m_LabelStyle.normal.textColor = activeTextColor;

            // 指南针刻度：居中显示的小字
            m_CompassStyle = new GUIStyle(GUIStyle.none)
            {
                font      = f,
                fontSize  = compassSize,
                alignment = TextAnchor.MiddleCenter,
            };
            m_CompassStyle.normal.textColor = textColor;

            // 按钮：只有文字，没有可见轮廓 / 背景
            m_ButtonStyle = new GUIStyle(GUIStyle.none)
            {
                font      = f,
                fontSize  = buttonSize,
                alignment = TextAnchor.MiddleCenter,
                padding   = new RectOffset(0, 0, 0, 0),
                margin    = new RectOffset(0, 0, 0, 0),
            };
            m_ButtonStyle.normal.textColor   = activeTextColor;
            m_ButtonStyle.hover.textColor    = activeTextColor;
            m_ButtonStyle.active.textColor   = activeTextColor;
            m_ButtonStyle.focused.textColor  = activeTextColor;
            m_ButtonStyle.onNormal.textColor = activeTextColor;
            m_ButtonStyle.onHover.textColor  = activeTextColor;
            m_ButtonStyle.onActive.textColor = activeTextColor;

            // 六个状态全部换成空纹理，这样按钮在任何状态下都不会画出底框
            Texture2D none = null;
            m_ButtonStyle.normal.background   = none;
            m_ButtonStyle.hover.background    = none;
            m_ButtonStyle.active.background   = none;
            m_ButtonStyle.focused.background  = none;
            m_ButtonStyle.onNormal.background = none;
            m_ButtonStyle.onHover.background  = none;
            m_ButtonStyle.onActive.background = none;

            m_BuiltFont       = font;
            m_BuiltTitleSize  = titleSize;
            m_BuiltButtonSize = buttonSize;
            m_BuiltLabelSize  = labelSize;
            m_BuiltCompassSize = compassSize;
            m_BuiltColor      = activeTextColor;

            m_StylesReady = true;
        }

        private void OnGUI()
        {
            EnsureStyles();

            // 按 1080 高度等比缩放，任何分辨率下版式一致
            float scale = Screen.height / RefHeight;
            Matrix4x4 saved = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));

            float w  = Screen.width / scale;
            float cx = w * 0.5f;

            // ★ 胜负界面：先铺一层全屏底色，再画文字。
            //   底色从"当前画面的颜色"渐变成目标色 —— 也就是在原来的画面上叠一层
            //   alpha 由 0 到 1 的目标色，视觉上就是"逐渐变色"。
            DrawEndBackground(w);

            switch (s_State)
            {
                case GameState.ModeSelect:     DrawModeSelect(cx, w);     break;
                case GameState.DifficultyMenu: DrawDifficultyMenu(cx, w); break;
                case GameState.Home:   DrawHome(cx, w);         break;
                case GameState.Paused: DrawPause(cx, w);        break;
                case GameState.Won:    DrawWin(cx, w);          break;
                case GameState.Dead:   DrawLose(cx, w);         break;
            }

            // 指南针只在游戏进行中显示
            // ★ 指南针同时受"降低难度 → 显示指南针"控制（默认关）
            if (showCompass && DifficultySettings.ShowCompass && s_State == GameState.Playing)
                DrawCompass(w);

            // 提示空间的文字：只在进行中显示，位置在右下角
            if (s_State == GameState.Playing) DrawHintBox(w);

            // 密码输入框：画在最上层（必须马上处理）
            if (s_State == GameState.Playing) DrawPasswordBox(w);

            // 临时提示条（存档成功 / 存档被拒绝之类）
            DrawToast(w);

            GUI.matrix = saved;
        }

        /// <summary>
        /// 胜负界面的全屏底色 + 地图淡出。
        ///
        /// 分两层叠加，都在这里用同一张 1×1 白纹理画：
        ///   ① 黑色，透明度 0 → 1 —— 把地图、光带、脚印一起"沉到黑里"。
        ///      这就是"地图和场上光带渐渐淡出"：我们没法逐个改场景材质的透明度，
        ///      但用一层渐浓的黑幕盖上去，视觉上完全等价，而且代价是零。
        ///   ② 目标色（白 / 暗红），透明度同样 0 → 1 —— 从黑再渐变成目标色。
        ///
        /// 两者时长都是 <see cref="endFadeDuration"/>，所以看起来是
        /// "画面先暗下去，同时底色慢慢浮上来"。
        /// </summary>
        private void DrawEndBackground(float w)
        {
            if (s_State != GameState.Won && s_State != GameState.Dead) return;

            if (m_WhitePixel == null)
            {
                m_WhitePixel = new Texture2D(1, 1, TextureFormat.RGBA32, false)
                {
                    hideFlags = HideFlags.DontSave,
                };
                m_WhitePixel.SetPixel(0, 0, Color.white);
                m_WhitePixel.Apply();
            }

            float t = EndFade01;

            // GUI.matrix 已经按 RefHeight 缩放过，所以这里用参考高度即可
            var full = new Rect(0f, 0f, w, RefHeight);
            Color prev = GUI.color;

            // ① 黑幕：把地图与光带压暗
            GUI.color = new Color(0f, 0f, 0f, t);
            GUI.DrawTexture(full, m_WhitePixel);

            // ② 目标底色
            Color target = s_State == GameState.Won ? winBackgroundColor : loseBackgroundColor;
            target.a *= t;
            GUI.color = target;
            GUI.DrawTexture(full, m_WhitePixel);

            GUI.color = prev;
        }

        /// <summary>当前界面文字的透明度（胜负界面淡入用）。</summary>
        private float TextAlpha => EndFade01;

        // ------------------------------------------------------------------ 提示文字框

        // 淡入淡出用的运行时状态
        private float  m_HintAlpha;
        private string m_HintShownText = "";
        private bool   m_HintFadingOut;         // true = 正在淡出

        // ★ 显示这段文字要用的样式，在"文字被设定"的那一刻**冻结**下来。
        //
        //   为什么不每帧去读 ZoneTrigger.ActiveHintSize：
        //   淡出会持续零点几秒，而这段时间里状态随时可能变（区域被清空、
        //   另一个区域接管、复位等）。只要有一次读到 0，字号就会掉回默认值，
        //   视觉上就是"淡出时字突然变小"。
        //   冻结之后，这行字从出现到消失，字号和颜色在它自己这一轮里绝对不变。
        private int   m_HintShownSize;
        private Color m_HintShownColor;

        /// <summary>
        /// 右下角的提示文字框，**出现 / 切换 / 消失**都走淡入淡出。
        ///
        /// 文字来源是 <see cref="ZoneTrigger.ActiveHintText"/>（由 HintZone 设置）。
        ///
        /// 切换时不直接替换，而是先把旧文字淡出、淡完再淡入新文字 ——
        /// 否则新文字会在旧文字还亮着的时候突然出现，看着很硬。
        ///
        /// 注意：<see cref="Time.timeScale"/> 在首页 / 暂停时是 0，
        /// 所以这里用 <see cref="Time.unscaledDeltaTime"/> 推进淡入淡出。
        /// </summary>
        private void DrawHintBox(float w)
        {
            if (!showHintBox) return;

            string want = ZoneTrigger.ActiveHintText ?? "";
            float dt = Time.unscaledDeltaTime;

            float fadeIn  = Mathf.Max(0.01f, hintFadeIn);
            float fadeOut = Mathf.Max(0.01f, hintFadeOut);

            // ================= 状态机 =================
            //
            // 三种情况：
            //   A. 还没有文字，目标有    -> 直接开始淡入新文字
            //   B. 有文字，目标换了内容  -> 先把旧文字淡出，淡完再淡入新的
            //   C. 有文字，目标变空      -> 淡出，淡完清掉

            if (m_HintFadingOut)
            {
                // ---- 正在淡出：用的还是冻结下来的样式 ----
                m_HintAlpha = Mathf.MoveTowards(m_HintAlpha, 0f, dt / fadeOut);

                if (m_HintAlpha <= 0.001f)
                {
                    m_HintAlpha     = 0f;
                    m_HintShownText = "";
                    m_HintFadingOut = false;

                    // 不用在这里记目标文字：淡完清掉之后，
                    // 下一帧会走"还没有显示任何文字"的分支，自动开始淡入 want。
                }
            }
            else if (string.IsNullOrEmpty(m_HintShownText))
            {
                // ---- 情况 A：还没有显示任何文字 ----
                if (!string.IsNullOrEmpty(want))
                {
                    m_HintShownText = want;

                    // 冻住这一刻的样式
                    m_HintShownSize  = ZoneTrigger.ActiveHintSize;
                    m_HintShownColor = ZoneTrigger.ActiveHintColor;

                    m_HintAlpha = Mathf.MoveTowards(m_HintAlpha, 1f, dt / fadeIn);
                }
            }
            else if (string.IsNullOrEmpty(want))
            {
                // ---- 情况 C：目标消失，开始淡出 ----
                m_HintFadingOut = true;
            }
            else if (want != m_HintShownText)
            {
                // ---- 情况 B：内容换了，先淡出旧的 ----
                // 淡完之后下一帧会自动淡入 want，所以这里不用另存一份目标文字
                m_HintFadingOut = true;
            }
            else
            {
                // ---- 内容不变，保持全亮 ----
                m_HintAlpha = Mathf.MoveTowards(m_HintAlpha, 1f, dt / fadeIn);
            }

            if (m_HintAlpha <= 0.001f) return;
            if (string.IsNullOrEmpty(m_HintShownText)) return;

            float bw = hintBoxWidth;
            float bh = hintBoxHeight;

            // 文本框仍然固定在画面右下角
            float bx = w - hintBoxMargin - bw;
            float by = RefHeight - hintBoxMargin - bh;

            // 字号：用**冻结下来的**值，0 则用 GameUI 的默认值。
            // 不再每帧读 ZoneTrigger.ActiveHintSize —— 那样淡出中途状态一变
            // 字号就会跳，正是"淡出时字变小"的原因。
            int size = m_HintShownSize > 0 ? m_HintShownSize : hintTextSize;

            var style = new GUIStyle(GUIStyle.none)
            {
                font      = m_BuiltFont,
                fontSize  = size,

                // 文字靠左对齐，但文本框本身仍在右下角
                alignment = TextAnchor.UpperLeft,
                wordWrap  = true,
            };

            // 颜色：同样用冻结值。alpha > 0 才算指定过，否则用默认色。
            Color c = hintTextColor;
            if (m_HintShownColor.a > 0.001f) c = m_HintShownColor;
            c.a *= m_HintAlpha;

            style.normal.textColor = c;

            GUI.Label(new Rect(bx, by, bw, bh), m_HintShownText, style);
        }

        // ------------------------------------------------------------------ 指南针

        /// <summary>
        /// 右上角的指南针：把玩家朝向画成一圈刻度。
        /// 屏幕正上方永远是"玩家正前方"，所以罗盘会随视角旋转，跟现实里的罗盘一样。
        /// 外圈另外画一个指向终点的小箭头。
        /// </summary>
        private void DrawCompass(float w)
        {
            if (m_Player == null || m_Camera == null) return;

            float r    = Mathf.Max(20f, compassRadius);
            float m    = Mathf.Max(r + 10f, compassMargin);
            float cxp  = w - m;
            float cyp  = m;

            Color prev = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(compassAlpha));

            if (m_CompassDot == null) m_CompassDot = MakeDot();

            // 相机朝向：把玩家的前方向投影到水平面
            Vector3 fwd = m_Camera.transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
            fwd.Normalize();
            float yaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;   // 0 = +Z, 90 = +X

            // 八个方位的刻度。bearing 是世界方位角，屏幕角 = bearing - yaw
            string[] labels = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
            for (int i = 0; i < 8; i++)
            {
                float bearing = i * 45f;
                // 屏幕坐标：向上是"前方"。atan2(x,z) 的量角方向和屏幕相反，所以取负
                float a  = (bearing - yaw) * Mathf.Deg2Rad;
                float sx = cxp + Mathf.Sin(a) * r;
                float sy = cyp - Mathf.Cos(a) * r;
                DrawCentered(labels[i], sx, sy);
            }

            // 圆心一个小点，标出"你在哪"
            float ds = 6f;
            GUI.DrawTexture(new Rect(cxp - ds * 0.5f, cyp - ds * 0.5f, ds, ds), m_CompassDot);

            GUI.color = prev;
        }

        private void DrawCentered(string text, float sx, float sy)
        {
            const float bw = 90f;
            const float bh = 44f;
            GUI.Label(new Rect(sx - bw * 0.5f, sy - bh * 0.5f, bw, bh), text, m_CompassStyle);
        }

        private static Texture2D MakeDot()
        {
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontSave };
            tex.SetPixel(0, 0, Color.white);
            tex.Apply();
            return tex;
        }

        /// <summary>
        /// 主页（Home 场景）的模式选择界面。
        ///
        /// ★ 样式完全沿用 <see cref="DrawHome"/> 那一套：
        ///   同一个标题样式、同一个按钮样式、同一套纵向排版。
        ///   —— 这样从主页进关卡后，视觉上是连贯的。
        /// </summary>
        private void DrawModeSelect(float cx, float w)
        {
            float y = titleTop;
            DrawTitle("ECHO PUZZLE", cx, w, y);
            y += titleToButtons;

            if (DrawButton("探秘模式", cx, w, y)) EnterSceneMode(PuzzleSceneName);
            y += buttonSpacing;

            if (DrawButton("地牢模式", cx, w, y)) EnterSceneMode(DungeonSceneName);
            y += buttonSpacing;

            if (DrawButton("退出", cx, w, y)) ExitToDesktop();
        }

        /// <summary>
        /// 从主页加载某个模式的场景。
        ///
        /// ★ 加载前必须把 timeScale 恢复成 1 ——
        ///   主页期间它是 0，带到新场景里游戏就动不了。
        /// </summary>
        private void EnterSceneMode(string sceneName)
        {
            if (!Application.CanStreamedLevelBeLoaded(sceneName))
            {
                Debug.LogError($"[EchoPuzzle] 场景「{sceneName}」不在 Build Settings 里，无法加载。\n" +
                               "请打开 File → Build Settings，把场景拖进去。");
                return;
            }

            Time.timeScale = 1f;

            // ★★ 进地牢模式默认是「普通闯关」★★
            //
            //   上一次可能是 boss 挑战（BossChallenge 是静态字段，跨场景保留），
            //   从主页重新进来时必须复位，否则会莫名其妙还是 Z-B-Y 那张图。
            //   之后点「boss挑战」按钮会再把它设回 true。
            if (sceneName == DungeonSceneName)
                EchoPuzzle.Dungeon.DungeonMapGenerator.BossChallenge = false;

            // ★ 新场景里的 GameUI 会读到这个标志，直接进 Playing
            s_AutoStartOnLoad = true;


            UnityEngine.SceneManagement.SceneManager.LoadScene(
                sceneName, UnityEngine.SceneManagement.LoadSceneMode.Single);
        }

        private void DrawHome(float cx, float w)
        {
            float y = titleTop;
            DrawTitle("ECHO PUZZLE", cx, w, y);
            y += titleToButtons;

            // ★★ 地牢模式：开局菜单是「普通闯关 / boss 挑战」两个按钮 ★★
            //
            //   需求："将 DungeonMode 场景的'新游戏'按钮去掉，改成
            //         '普通闯关''boss挑战'两个按钮。"
            //   · 普通闯关：照常触发原本点新游戏后触发的游戏内容（随机布局）
            //   · boss挑战：同样走 DungeonMapGenerator 建图，但分块矩阵固定
            //               a11=Z / a21=B / a31=Y
            if (IsDungeonMode)
            {
                if (DrawButton("普通闯关", cx, w, y)) BeginDungeonRun(false);
                y += buttonSpacing;

                if (DrawButton("boss挑战", cx, w, y)) BeginDungeonRun(true);
                y += buttonSpacing;
            }
            else
            {
                if (DrawButton("新游戏", cx, w, y)) StartGame();
                y += buttonSpacing;
            }

            // 有存档时才显示"加载存档"。
            // ★ 地牢模式不能存档，也就不显示它 ——
            //   否则会读进探秘模式的进度，模式和场景对不上。
            if (CanUseSave && SaveSystem.HasSave)
            {
                if (DrawButton("加载存档", cx, w, y)) LoadGame();
                y += buttonSpacing;
            }

            if (DrawButton("返回主页", cx, w, y)) ReturnHome();
        }

        /// <summary>
        /// 【地牢模式】从开局菜单选挑战模式并开始。
        ///
        /// ★ 先让地图生成器切到对应布局，再走原来的 StartGame 流程
        ///   （开局自选强化、进入 Playing 等一行都不改）。
        /// </summary>
        private void BeginDungeonRun(bool bossChallenge)
        {
            var gen = FindObjectOfType<EchoPuzzle.Dungeon.DungeonMapGenerator>();
            if (gen != null) gen.BeginRun(bossChallenge);

            StartGame();
        }

        private void DrawPause(float cx, float w)
        {
            // ★ 暂停菜单用**独立的**间距字段，这样调它不会影响首页 / 通关界面。
            //   ★ 地牢模式和探秘模式各用各的：
            //       探秘 = pauseButtonSpacing          (默认 80)
            //       地牢 = dungeonPauseButtonSpacing   (默认 110)
            float sp = IsDungeonMode ? dungeonPauseButtonSpacing : pauseButtonSpacing;

            float y = titleTop;
            DrawTitle("暂停", cx, w, y);
            y += titleToButtons;

            if (DrawButton("继续",    cx, w, y)) EnterPlaying();
            y += sp;

            // ★ 地牢模式不能存档 -> 连"保存"按钮都不显示
            if (CanUseSave)
            {
                if (DrawButton("保存", cx, w, y)) SaveGame();
                y += sp;

                // ★「降低难度」：只有探秘模式有（地牢模式不能存档，一并跳过）
                if (DrawButton("降低难度", cx, w, y)) EnterDifficultyMenu();
                y += sp;
            }

            if (CanUseSave && SaveSystem.HasSave)
            {
                if (DrawButton("加载游戏", cx, w, y)) LoadGame();
                y += sp;
            }

            if (DrawButton("重新开始",   cx, w, y)) RestartGame();
            y += sp;
            if (DrawButton("退出",      cx, w, y)) ReturnHome();
        }

        // ------------------------------------------------------------------ 降低难度

        /// <summary>进入"降低难度"子界面。</summary>
        private void EnterDifficultyMenu()
        {
            s_State = GameState.DifficultyMenu;
        }

        /// <summary>
        /// 「降低难度」界面：三个可勾选的开关。
        ///
        /// ★ 排版 / 字号 / 颜色全部沿用 DrawButton 那一套，只是每个按钮
        ///   右边多一个方框 —— 有勾就画一个 ✓。
        /// </summary>
        private void DrawDifficultyMenu(float cx, float w)
        {
            float y = titleTop;
            DrawTitle("降低难度", cx, w, y);
            y += titleToButtons;

            if (DrawCheckButton("提高标记上限", cx, w, y, DifficultySettings.RaiseMarkerLimit))
                DifficultySettings.RaiseMarkerLimit = !DifficultySettings.RaiseMarkerLimit;
            y += buttonSpacing;

            if (DrawCheckButton("显示指南针", cx, w, y, DifficultySettings.ShowCompass))
                DifficultySettings.ShowCompass = !DifficultySettings.ShowCompass;
            y += buttonSpacing;

            if (DrawCheckButton("显示脚印", cx, w, y, DifficultySettings.ShowFootprints))
                DifficultySettings.ShowFootprints = !DifficultySettings.ShowFootprints;
            y += buttonSpacing;

            if (DrawButton("返回", cx, w, y)) EnterPaused();
        }

        /// <summary>
        /// 带勾选框的按钮：文字按钮 + 右侧一个方框，勾选时画 ✓。
        ///
        /// ★ 文字部分完全复用 <see cref="DrawButton"/> 的样式（同一个 m_ButtonStyle、
        ///   同一个宽度 460、同一个高度 68），所以字体格式和别的按钮一模一样。
        /// </summary>
        private bool DrawCheckButton(string text, float cx, float w, float y, bool isChecked)
        {
            // 文字按钮本体（和普通按钮同尺寸同位置）
            bool clicked = DrawButton(text, cx, w, y);

            // ---- 右侧的方框 ----
            const float boxSize   = 40f;
            const float boxGap    = 24f;

            // 1x1 白色纹理：可能还没被别处创建过，这里兜底一次
            if (m_WhitePixel == null)
            {
                m_WhitePixel = new Texture2D(1, 1, TextureFormat.RGBA32, false)
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                m_WhitePixel.SetPixel(0, 0, Color.white);
                m_WhitePixel.Apply();
            }

            // 按钮右边缘 = cx + 460/2，方框贴在它右边
            float buttonRight = cx + 230f;
            float boxX = buttonRight + boxGap;
            float boxY = y + (68f - boxSize) * 0.5f;   // 和按钮垂直居中对齐

            Color prev = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, TextAlpha);

            // 方框：四条边（用白色 1x1 纹理画线）
            var boxRect = new Rect(boxX, boxY, boxSize, boxSize);
            DrawRectOutline(boxRect, m_WhitePixel, 2f);

            // 勾：两道斜线
            if (isChecked)
            {
                float t = 4f;
                // 短的一笔：从左中往下
                DrawLine(boxX + boxSize * 0.20f, boxY + boxSize * 0.52f,
                         boxX + boxSize * 0.42f, boxY + boxSize * 0.74f, t, m_WhitePixel);
                // 长的一笔：从底往上右
                DrawLine(boxX + boxSize * 0.42f, boxY + boxSize * 0.74f,
                         boxX + boxSize * 0.80f, boxY + boxSize * 0.26f, t, m_WhitePixel);
            }

            GUI.color = prev;

            return clicked;
        }

        /// <summary>画一个空心矩形框。</summary>
        private void DrawRectOutline(Rect r, Texture2D tex, float thickness)
        {
            if (tex == null) return;

            GUI.DrawTexture(new Rect(r.x, r.y, r.width, thickness), tex);
            GUI.DrawTexture(new Rect(r.x, r.yMax - thickness, r.width, thickness), tex);
            GUI.DrawTexture(new Rect(r.x, r.y, thickness, r.height), tex);
            GUI.DrawTexture(new Rect(r.xMax - thickness, r.y, thickness, r.height), tex);
        }

        /// <summary>用若干小方块拼一条有宽度的斜线。</summary>
        private void DrawLine(float x0, float y0, float x1, float y1,
                              float thickness, Texture2D tex)
        {
            if (tex == null) return;

            float dx = x1 - x0;
            float dy = y1 - y0;
            float len = Mathf.Sqrt(dx * dx + dy * dy);
            if (len < 0.001f) return;

            int steps = Mathf.Max(2, Mathf.CeilToInt(len));

            for (int i = 0; i <= steps; i++)
            {
                float t = (float)i / steps;
                float px = x0 + dx * t;
                float py = y0 + dy * t;

                GUI.DrawTexture(new Rect(px - thickness * 0.5f,
                                         py - thickness * 0.5f,
                                         thickness, thickness), tex);
            }
        }

        private void DrawWin(float cx, float w)
        {
            float y = titleTop;
            DrawTitle("你赢了！", cx, w, y);
            y += titleToButtons * 0.9f;

            int min = Mathf.FloorToInt(m_Elapsed / 60f);
            float rem = m_Elapsed - min * 60f;
            DrawLabel($"耗时 {min:D2}min{rem:00.0}s", cx, w, y);
            y += labelToButtons;

            if (DrawButton("重新开始", cx, w, y)) RestartGame();
            y += buttonSpacing;
            if (DrawButton("退出",    cx, w, y)) ReturnHome();
        }

        private void DrawLose(float cx, float w)
        {
            float y = titleTop;
            DrawTitle("你死了", cx, w, y);
            y += titleToButtons;

            // ★ 地牢模式**不显示**「返回存档位置」——
            //   地牢是肉鸽玩法，没有存档点可回，死了就只能重开。
            if (!IsDungeonMode && SaveSystem.HasSave)
            {
                if (DrawButton("返回存档位置", cx, w, y)) LoadGame();
                y += buttonSpacing;
            }
            if (DrawButton("重新开始", cx, w, y)) RestartGame();
            y += buttonSpacing;
            if (DrawButton("返回主页",    cx, w, y)) ReturnHome();
        }

        /// <summary>
        /// 把玩家瞬移到地牢模式当前层的出生点（矩阵里数字 8 的位置）。
        ///
        /// ★ 地牢的起点由 DungeonMapGenerator 决定，
        ///   GameUI 的 m_StartPosition 对它来说是过时的。
        /// </summary>
        private void TeleportToDungeonSpawn()
        {
            var gen = FindObjectOfType<EchoPuzzle.Dungeon.DungeonMapGenerator>();

            if (gen != null)
            {
                gen.TeleportPlayerToSpawn();
                return;
            }

            // 找不到生成器（理论上不该发生）时保持原位，不要乱传
            Debug.LogWarning("[地牢] 没找到 DungeonMapGenerator，玩家位置未重置。");
        }

        /// <summary>
        /// 回到主页场景（不是退出程序）。
        ///
        /// ★ 主页现在是一个**独立场景**（Home），所以这里 LoadScene 回去，
        ///   而不是在本场景里切到 GameState.Home。
        /// </summary>
        private void ReturnHome()
        {
            // 先把这一局清干净，否则下次进来时光带和标记还挂在场上
            ResetRunState();

            // 加载前把时间恢复正常，否则 Home 场景里 timeScale 还是 0
            Time.timeScale = 1f;

            const string HomeScene = "Home";

            if (!Application.CanStreamedLevelBeLoaded(HomeScene))
            {
                Debug.LogError($"[EchoPuzzle] 场景「{HomeScene}」不在 Build Settings 里，无法回到主页。");

                // 退化成"在本场景内回到首页界面"（旧行为），至少不会卡死
                EnterHome();
                return;
            }

            UnityEngine.SceneManagement.SceneManager.LoadScene(HomeScene,
                UnityEngine.SceneManagement.LoadSceneMode.Single);
        }

        private void DrawTitle(string text, float cx, float w, float y)
        {
            var rect = new Rect(0f, y, w, titleSize * 1.4f);

            // 胜负界面淡入时，文字也跟着逐渐显现
            Color prev = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, TextAlpha);
            GUI.Label(rect, text, m_TitleStyle);
            GUI.color = prev;
        }

        private void DrawLabel(string text, float cx, float w, float y)
        {
            var rect = new Rect(0f, y, w, labelSize * 1.6f);

            Color prev = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, TextAlpha);
            GUI.Label(rect, text, m_LabelStyle);
            GUI.color = prev;
        }

        private bool DrawButton(string text, float cx, float w, float y)
        {
            const float bw = 460f;
            const float bh = 68f;

            var rect = new Rect(cx - bw * 0.5f, y, bw, bh);

            Color prev = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, TextAlpha);
            bool clicked = GUI.Button(rect, text, m_ButtonStyle);
            GUI.color = prev;

            // 点击音效：所有按钮都走这里，所以这一处就够了
            if (clicked) AudioSystem.Play(AudioSystem.Cue.Click);

            return clicked;
        }
    }
}
