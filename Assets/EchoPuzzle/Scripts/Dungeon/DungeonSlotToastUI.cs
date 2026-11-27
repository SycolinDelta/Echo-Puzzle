using UnityEngine;

namespace EchoPuzzle.Dungeon
{
    /// <summary>
    /// 【地牢模式】槽位增加的文字提示：屏幕中间偏上淡入 → 停留 3 秒 → 淡出。
    ///
    /// ── 需求 ────────────────────────────────────────────────
    ///   "我希望在蓝、紫、橙色槽位增加时，屏幕中间偏上的地方会淡入并出现文字提示
    ///    「X色槽位增加！」，3 秒后淡出。在白、绿色槽位增加时（注意，这两个是一起增加的），
    ///    文字提示为「白、绿色槽位增加！」。这些文字的轮廓为黑色（不是矩形文本框的边框，
    ///    是每个字符的轮廓线），填充为白色。槽位增加的时机有可能是在进入新一层的时候，
    ///    在这种情况下，请确保这些文字提示会在新一层的地图显示出来时再显示。"
    ///
    /// ── 怎么判断"槽位增加了" ─────────────────────────────────
    ///   ★ 槽位数量其实是 (层数, 模板等级) 的纯函数：
    ///       · 白 / 绿  <- SlotRules.WhiteGreenBase(层数)      （每 2 层 +1）
    ///       · 蓝/紫/橙 <- 模板等级                             （升级时变化）
    ///   ★ 所以这里不监听"某个槽位变量被改了"，而是**每帧比较**上一帧和这一帧
    ///     的"基础容量"，变大了就说明槽位增加。
    ///   ★ "基础容量" = 把已装插件全当 0 算出来的容量（SlotRules.ComputeCaps 传全 0），
    ///     这样玩家手动装卸插件导致的容量变化**不会**误触发提示。
    ///
    /// ── 文字描边怎么画 ──────────────────────────────────────
    ///   ★ IMGUI 没有内置描边，标准做法是同一段文字画 9 遍：
    ///     先在 8 个偏移位置各画一遍黑色，最后在正中画一遍白色。
    ///     黑色那几遍会被白色盖住中间部分，只在边缘露出来 —— 就是"字符轮廓线"。
    ///   ★ 用 GUI.color 着色（它会乘以 GUIStyle 的白色 textColor），
    ///     淡入淡出直接改 alpha 即可，不用建多套样式。
    /// </summary>
    [AddComponentMenu("Echo Puzzle/Dungeon/Slot Toast UI")]
    [DefaultExecutionOrder(-120)]
    public sealed class DungeonSlotToastUI : MonoBehaviour
    {
        // ---------------------------------------------------------------- 显示参数

        [Header("位置（1080 高度基准的像素）")]
        [Tooltip("文字中心在屏幕高度的百分之几处。0.28 ≈ 中间偏上")]
        [Range(0f, 1f)] public float screenYPercent = 0.28f;

        [Tooltip("提示文字区域的宽度（居中排版用）")]
        public float boxWidth = 1200f;

        [Header("外观")]
        [Tooltip("字号")]
        [Min(8)] public int fontSize = 56;

        [Tooltip("字符轮廓（描边）的粗细，单位是 1080 基准的像素")]
        [Range(1f, 6f)] public float outlineWidth = 2.5f;

        [Tooltip("填充色（需求：白色）")]
        public Color fillColor = Color.white;

        [Tooltip("轮廓色（需求：黑色）")]
        public Color outlineColor = Color.black;

        // ---------------------------------------------------------------- 时间轴

        [Header("时间（秒）")]
        [Tooltip("淡入时长")]
        [Min(0.01f)] public float fadeInSeconds = 0.4f;

        [Tooltip("完全显示后停留多久")]
        [Min(0f)] public float holdSeconds = 3f;

        [Tooltip("淡出时长")]
        [Min(0.01f)] public float fadeOutSeconds = 0.6f;

        // ---------------------------------------------------------------- 运行时

        /// <summary>参考高度：和 DungeonHUD 等一致，按 1080 等比缩放。</summary>
        private const float RefHeight = 1080f;

        /// <summary>8 个描边偏移方向（加上中心点就是 9 遍）。</summary>
        private static readonly Vector2[] s_OutlineDirs =
        {
            new Vector2(-1f, -1f), new Vector2(0f, -1f), new Vector2(1f, -1f),
            new Vector2(-1f,  0f),                        new Vector2(1f,  0f),
            new Vector2(-1f,  1f), new Vector2(0f,  1f), new Vector2(1f,  1f),
        };

        private GUIStyle m_Style;
        private Texture2D m_Pixel;

        /// <summary>上一帧看到的基础容量（6 色）。-1 = 还没初始化。</summary>
        private readonly int[] m_LastCaps = new int[SlotRules.ColorCount];

        /// <summary>上一帧看到的层数（用来判断“是不是推进了新一层”）。-1 = 还没初始化。</summary>
        private int m_LastStage = -1;

        private bool m_Inited;

        // ---- 当前播报里包含哪些颜色（合并用）----
        //   ★ 用 bool 而不是直接存字符串，是为了合并时能按固定顺序重新拼。
        private bool m_ShowWG;
        private bool m_ShowBlue;
        private bool m_ShowPurple;
        private bool m_ShowOrange;

        /// <summary>当前展示的文字（由上面四个 bool 拼出来）。null = 没在显示。</summary>
        private string m_Showing;

        /// <summary>是否正在展示中（包括待显示、等地图显示的阶段）。</summary>
        private bool m_Active;

        /// <summary>播报的四个阶段。</summary>
        private enum ToastPhase
        {
            Idle,      // 没在播
            Waiting,   // 已经有内容了，但还在等地图显示出来
            FadeIn,    // 正在线性淡入 0 -> 1
            Hold,      // 满不透明度停留
            FadeOut,   // 正在线性淡出 1 -> 0
        }

        private ToastPhase m_Phase = ToastPhase.Idle;

        /// <summary>
        /// 当前不透明度（0~1）。
        ///
        /// ★ 这是“状态”而不是“用时间算出来的”，所以合并时能从任意不透明度接着走。
        /// </summary>
        private float m_Alpha;

        /// <summary>当前阶段已经走了多久（用 unscaledTime 累加）。</summary>
        private float m_PhaseElapsed;

        // ---------------------------------------------------------------- 生命周期

        private void Awake()
        {
            for (int i = 0; i < m_LastCaps.Length; i++) m_LastCaps[i] = -1;
        }

        private void OnDestroy()
        {
            if (m_Pixel != null) Destroy(m_Pixel);
        }

        // ---------------------------------------------------------------- 检测

        private void Update()
        {
            DetectChanges();

            StepPhase();
        }

        /// <summary>
        /// 推进播报状态。
        ///
        /// ★ 不透明度改成“自己累加”而不是“用时间算”，这样：
        ///   · 淡入 / 淡出都是**恒定速率**（每秒变化 1/fadeInSeconds、1/fadeOutSeconds）
        ///   · 合并时能从任意不透明度接着走（见 ApplyNewColors）
        ///
        /// 状态机：Waiting -> FadeIn -> Hold -> FadeOut -> Idle
        /// </summary>
        private void StepPhase()
        {
            if (m_Phase == ToastPhase.Idle) return;

            float dt = Time.unscaledDeltaTime;

            // ---- 1) 等待地图显示出来 ----
            if (m_Phase == ToastPhase.Waiting)
            {
                if (!CanShowNow()) return;

                // 开始播：从当前不透明度往 1 走
                //   （正常情况下 m_Alpha = 0；合并时可能是中间值）
                m_Phase = m_Alpha >= 0.999f ? ToastPhase.Hold : ToastPhase.FadeIn;
                m_PhaseElapsed = 0f;
                return;
            }

            m_PhaseElapsed += dt;

            // ---- 2) 线性淡入 ----
            if (m_Phase == ToastPhase.FadeIn)
            {
                float speed = 1f / Mathf.Max(0.01f, fadeInSeconds);

                m_Alpha = Mathf.MoveTowards(m_Alpha, 1f, speed * dt);

                if (m_Alpha >= 0.999f)
                {
                    m_Alpha = 1f;
                    m_Phase = ToastPhase.Hold;
                    m_PhaseElapsed = 0f;
                }
                return;
            }

            // ---- 3) 满不透明度停留 ----
            if (m_Phase == ToastPhase.Hold)
            {
                m_Alpha = 1f;

                if (m_PhaseElapsed >= Mathf.Max(0f, holdSeconds))
                {
                    m_Phase = ToastPhase.FadeOut;
                    m_PhaseElapsed = 0f;
                }
                return;
            }

            // ---- 4) 线性淡出 ----
            if (m_Phase == ToastPhase.FadeOut)
            {
                float speed = 1f / Mathf.Max(0.01f, fadeOutSeconds);

                m_Alpha = Mathf.MoveTowards(m_Alpha, 0f, speed * dt);

                if (m_Alpha <= 0.001f)
                    ClearShow();
            }
        }

        /// <summary>清掉当前播报状态。</summary>
        private void ClearShow()
        {
            m_Active = false;
            m_Phase  = ToastPhase.Idle;
            m_Showing = null;

            m_Alpha = 0f;
            m_PhaseElapsed = 0f;

            m_ShowWG = m_ShowBlue = m_ShowPurple = m_ShowOrange = false;
        }

        /// <summary>
        /// 现在能不能开始显示提示。
        ///
        /// ★ 需求："槽位增加的时机有可能是在进入新一层的时候，在这种情况下，
        ///   请确保这些文字提示会在新一层的地图显示出来时再显示。"
        ///
        /// ★ 两个拦路条件：
        ///   1) 有模态界面开着（选 buff / 商店 / 装配）—— 那时屏幕是黑的或全遮住
        ///   2) 地图还在淡入（新一层刚建好，屏幕正从黑变亮）
        /// </summary>
        private static bool CanShowNow()
        {
            // 不在游戏中（首页 / 暂停 / 结算）就别显示 —— 否则会盖在菜单上
            if (GameUI.Current != GameState.Playing) return false;

            if (DungeonModalUi.AnyOpen) return false;

            DungeonMapGenerator gen = DungeonMapGenerator.Instance;
            if (gen != null && !gen.IsMapVisible) return false;

            return true;
        }

        /// <summary>
        /// 每帧检测槽位变化，把变化并入当前播报。
        ///
        /// ★★ 白绿只在“推进层数”时播报 ★★
        ///
        ///   需求：“白绿色槽位的增加时机有两种，一是每推进两层增加一次，
        ///         二是因为拆卸了紫橙插件而给白绿槽位留出位置。
        ///         我希望只有前者会触发播报。”
        ///
        ///   ★ 白绿容量 = WhiteGreenBase(层数) - 绿已装 - 紫橙已装
        ///     所以拆掉紫橙插件也会让它变大 —— 这种情况要排除。
        ///   ✔ 做法：白绿有没有增加，只看 **层数是不是变了**，不看 caps。
        ///      （层数变了 -> 白绿可能增加；层数没变 -> 白绿增加一定是拆装带来的）
        ///
        /// ★★ 合并规则 ★★
        ///
        ///   需求：“当一个颜色增加时跳出播报，在播报尚未消失时
        ///         如果跳出其他颜色（相同颜色就不用管）的播报，
        ///         就会合并成一条，并重新计算时间。合并时默认按白绿蓝紫橙的顺序，
        ///         就算先触发橙色后触发蓝色，也仍旧会显示「蓝、橙色槽位增加！」。”
        ///
        ///   ★ 所以不存“排队”也不存“新的打断旧的”，而是**标记位合并**：
        ///     只要播报还没结束，新颜色就加进标记，然后按固定顺序重拼文字
        ///     并把时间重置到最初。
        /// </summary>
        private void DetectChanges()
        {
            if (!GameUI.IsDungeonMode) return;

            int stage = DungeonBuffState.StageIndex;
            var tier  = DungeonBuffState.Instance != null
                      ? DungeonBuffState.Instance.Tier
                      : TemplateTier.White;

            // ---- 算"基础容量"：已装插件全当 0，排除手动装卸的干扰 ----
            int[] zero = new int[SlotRules.ColorCount];
            int[] caps = new int[SlotRules.ColorCount];

            SlotRules.ComputeCaps(tier, stage, zero, caps);

            // ---- 首帧只记录，不提示 ----
            if (!m_Inited)
            {
                for (int i = 0; i < caps.Length; i++) m_LastCaps[i] = caps[i];
                m_LastStage = stage;
                m_Inited = true;
                return;
            }

            // ---- 蓝 / 紫 / 橙：看容量变化（只能靠升模板变大）----
            bool blueUp   = caps[(int)PluginColor.Blue]   > m_LastCaps[(int)PluginColor.Blue];
            bool purpleUp = caps[(int)PluginColor.Purple] > m_LastCaps[(int)PluginColor.Purple];
            bool orangeUp = caps[(int)PluginColor.Orange] > m_LastCaps[(int)PluginColor.Orange];

            // ---- 白绿：**只看层数是否推进** ----
            //
            //   ★ 拆掉紫橙插件会让白绿容量变大，但那不是"推进层数"，
            //     层数没变就不播报。
            bool stageAdvanced = stage != m_LastStage;
            bool wgUp = false;

            if (stageAdvanced)
            {
                wgUp = caps[(int)PluginColor.White] > m_LastCaps[(int)PluginColor.White]
                    || caps[(int)PluginColor.Green] > m_LastCaps[(int)PluginColor.Green];
            }

            // ---- 记录新值 ----
            for (int i = 0; i < caps.Length; i++) m_LastCaps[i] = caps[i];
            m_LastStage = stage;

            if (!wgUp && !blueUp && !purpleUp && !orangeUp) return;

            // ---- 合并到当前播报 ----
            //
            //   ★★ 按当前不透明度决定怎么接着走 ★★
            //
            //   需求："如果在播报中途有新播报合并进来，
            //         如果当前不透明度已经最大，就把停留时间重新计 3 秒；
            //         如果不是最大（这是播报已经淡出了一半的情况），
            //         就按照淡入速度重新达到最大，然后计 3 秒，
            //         由于播报淡出进度不固定，因此重新完全淡入所需的时间也是不固定的。"
            //
            //   ★ 真正难的是"淡出中途"：不能重开一次淡入（会从 0 开始、出现闪一下），
            //     而是**从当前不透明度继续往上走**。
            //     所以底层的 m_Alpha 是状态（不是用时间算），淡入淡出都用
            //     MoveTowards 按恒定速率推。
            //
            //   ★ 三种情况：
            //     · 上一轮已结束          -> 从 0 开始（正常淡入）
            //     · 当前已是满不透明度     -> 直接重新计 3 秒停留
            //     · 当前是中间值           -> 保持 m_Alpha，切到 FadeIn 往上走

            bool wasActive = m_Active;

            if (!wasActive)
            {
                // 上一次已经播完（或从来没播过）-> 从头开始
                ClearShow();
                m_Alpha = 0f;
                m_PhaseElapsed = 0f;
            }

            // ---- 1) 标记新颜色 ----
            if (wgUp)     m_ShowWG = true;
            if (blueUp)   m_ShowBlue = true;
            if (purpleUp) m_ShowPurple = true;
            if (orangeUp) m_ShowOrange = true;

            // ---- 2) 按固定顺序重拼文字（不管先后顺序）----
            m_Showing = BuildMessage(m_ShowWG, m_ShowBlue, m_ShowPurple, m_ShowOrange);

            m_Active = true;

            // ---- 3) 按当前不透明度安排后续状态 ----
            if (m_Phase == ToastPhase.Waiting)
            {
                // 已经排着队等画面：文字虽然更新了，但继续等，不透明度不动
                m_PhaseElapsed = 0f;
            }
            else if (!wasActive && !CanShowNow())
            {
                // ★★ 新的提示，但此刻画面还不能看（黑幕中 / 地图淡入中 / 模态界面开着）★★
                //
                //   需求："槽位增加的时机有可能是在进入新一层的时候，在这种情况下，
                //         请确保这些文字提示会在新一层的地图显示出来时再显示。"
                //
                //   ★ 这里必须先进入 Waiting 挂起。否则提示会在黑幕（或者选 buff 的
                //     全遮面板）后面把"淡入 + 3 秒停留 + 淡出"整套走完，
                //     等地图真正亮出来时它早就没了 —— 看起来就跟没做一样。
                m_Alpha = 0f;
                m_Phase = ToastPhase.Waiting;
                m_PhaseElapsed = 0f;
            }
            else if (wasActive && m_Alpha >= 0.999f)
            {
                // 已经最大 -> 停留时间重新计 3 秒
                m_Alpha = 1f;
                m_Phase = ToastPhase.Hold;
                m_PhaseElapsed = 0f;
            }
            else
            {
                // 不是最大（正在淡入 / 淡出中途，或刚开始）
                //   -> 从当前不透明度按**淡入速度**重新达到最大，
                //      到达后 StepPhase 会自然进入 Hold 并计 3 秒。
                //   ★ 不重置 m_Alpha，这就是"从淡出中途接着淡入"。
                m_Phase = ToastPhase.FadeIn;
                m_PhaseElapsed = 0f;
            }
        }

        /// <summary>
        /// 拼提示文字。
        ///
        /// ★ 白绿合并成「白、绿」（需求：这两个是一起增加的）。
        /// ★ 蓝/紫/橙按 enum 顺序（蓝 -> 紫 -> 橙）列出来。
        /// ★ 多个同时增加时用「、」连接，例：蓝+橙 -> 「蓝、橙色槽位增加！」
        /// </summary>
        private static string BuildMessage(bool wgUp, bool blueUp, bool purpleUp, bool orangeUp)
        {
            var parts = new System.Collections.Generic.List<string>(4);

            if (wgUp)     parts.Add("白、绿");
            if (blueUp)   parts.Add("蓝");
            if (purpleUp) parts.Add("紫");
            if (orangeUp) parts.Add("橙");

            return string.Join("、", parts) + "色槽位增加！";
        }

        // ---------------------------------------------------------------- 绘制

        private void OnGUI()
        {
            if (m_Showing == null) return;
            if (!GameUI.IsDungeonMode) return;
            if (DungeonModalUi.AnyOpen) return;

            float alpha = ComputeAlpha();
            if (alpha <= 0.001f) return;

            EnsureStyle();

            // 和 DungeonHUD 等一致：按 1080 高度等比缩放
            float scale = Screen.height / RefHeight;
            Matrix4x4 saved = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity,
                                       new Vector3(scale, scale, 1f));

            float w = Screen.width / scale;
            float h = RefHeight;

            var rect = new Rect((w - boxWidth) * 0.5f,
                                h * screenYPercent - fontSize,
                                boxWidth,
                                fontSize * 2.2f);

            Color prev = GUI.color;

            // ---- 1) 黑色轮廓：8 个偏移位置各画一遍 ----
            float ow = Mathf.Max(1f, outlineWidth);

            GUI.color = new Color(outlineColor.r, outlineColor.g, outlineColor.b, alpha);

            for (int i = 0; i < s_OutlineDirs.Length; i++)
            {
                Vector2 d = s_OutlineDirs[i] * ow;

                GUI.Label(new Rect(rect.x + d.x, rect.y + d.y, rect.width, rect.height),
                          m_Showing, m_Style);
            }

            // ---- 2) 白色填充：正中间画一遍，盖住轮廓内部 ----
            GUI.color = new Color(fillColor.r, fillColor.g, fillColor.b, alpha);
            GUI.Label(rect, m_Showing, m_Style);

            GUI.color = prev;
            GUI.matrix = saved;
        }
        /// <summary>
        /// 当前不透明度。
        ///
        /// ★ 直接返回状态里累加出来的 m_Alpha —— 不再用「当前时间 - 起点」反推。
        /// ★ 这样合并时才能从中间不透明度接着往上走。
        /// </summary>
        private float ComputeAlpha() => Mathf.Clamp01(m_Alpha);




        // ---------------------------------------------------------------- 样式

        private void EnsureStyle()
        {
            if (m_Pixel == null)
            {
                m_Pixel = new Texture2D(1, 1, TextureFormat.RGBA32, false)
                {
                    hideFlags = HideFlags.DontSave,
                };
                m_Pixel.SetPixel(0, 0, Color.white);
                m_Pixel.Apply();
            }

            if (m_Style != null) return;

            // ★ textColor 固定白色：实际的"黑/白"靠 GUI.color 相乘来控制，
            //   这样描边和填充共用一套样式，代码更短。
            m_Style = new GUIStyle(GUI.skin.label)
            {
                fontSize  = Mathf.Max(8, fontSize),
                alignment = TextAnchor.MiddleCenter,
                wordWrap  = false,
                richText  = false,
                padding   = new RectOffset(0, 0, 0, 0),
                margin    = new RectOffset(0, 0, 0, 0),
                clipping  = TextClipping.Overflow,
            };

            m_Style.normal.textColor = Color.white;

            // ★ 字体的取法和其它地牢 HUD 保持一致（LegacyRuntime 优先，旧版回退 Arial），
            //   这样打包版里中文也不会变成方块。
            Font f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (f == null) f = Resources.GetBuiltinResource<Font>("Arial.ttf");
            if (f != null) m_Style.font = f;
        }
    }
}