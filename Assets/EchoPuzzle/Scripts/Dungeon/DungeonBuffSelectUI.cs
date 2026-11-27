using System.Collections.Generic;
using UnityEngine;

namespace EchoPuzzle.Dungeon
{
    /// <summary>
    /// 【地牢模式】每通关一层弹出的「三选一」强化界面。
    ///
    /// ── 触发时机 ────────────────────────────────────────────
    ///   监听 <see cref="TriggerVolume.OnNextStage"/>（玩家走进下一层传送点），
    ///   等 <see cref="openDelay"/> 秒（让 DungeonMapGenerator 把新一层生成完、
    ///   淡入结束）再弹出。**不改齐放的地图代码**，只订阅它已有的静态事件。
    ///
    /// ── 版式（3 列大卡片）──────────────────────────────────
    ///   三张等宽卡片横排，几乎铺满整个画面：
    ///     · 左右各留 sideMargin，卡片之间留 columnGap
    ///     · 顶部留出标题和上下边距，其余高度全部给卡片
    ///   卡片只有一层极淡的底色 + 细边框（悬停时底色会亮一点），
    ///   文字纯白居中 —— 和 GameUI 那套"没有底框、只有文字"的风格一致。
    ///
    /// ── 交互 ────────────────────────────────────────────────
    ///   · 打开时 Time.timeScale = 0（世界冻结）、光标解锁
    ///   · 点卡片，或按 1 / 2 / 3 选一项
    ///   · 选完立刻恢复 timeScale、锁定光标
    ///
    /// ── 和探秘模式的关系 ────────────────────────────────────
    ///   ★ 只在 <see cref="GameUI.IsDungeonMode"/> 时工作。
    /// </summary>
    [AddComponentMenu("Echo Puzzle/Dungeon/Dungeon Buff Select UI")]
    public sealed class DungeonBuffSelectUI : MonoBehaviour
    {
        public static DungeonBuffSelectUI Instance { get; private set; }

        // ------------------------------------------------------------------ 参数

        [Header("触发")]
        [Tooltip("走进下一层后等多久再弹（等地图生成 + 淡入）。秒")]
        [Min(0f)] public float openDelay = 1.2f;

        [Tooltip("一次给几个选项（上限 3，填大于 3 会被夹到 3）")]
        [Min(1)] public int optionCount = 3;

        /// <summary>一次最多只能给 3 张卡（需求：每次只在 3 个里选）。</summary>
        private const int MaxOptionsPerPick = 3;

        /// <summary>这一次实际抽几张。</summary>
        private int PickCount => Mathf.Clamp(optionCount, 1, MaxOptionsPerPick);

        [Header("刷新（花金币）")]
        [Tooltip("刷新一次三选一花多少金币。0 = 免费（但开局自选永远不能刷新）")]
        [Min(0)] public int rerollCost = 10;

        [Tooltip("每次选卡最多能刷新几次。-1 = 不限")]
        public int rerollLimit = -1;

        [Tooltip("刷新的快捷键（点击底部按钮也可以）")]
        public KeyCode rerollKey = KeyCode.F;

        // ------------------------------------------------------------------ 版式

        [Header("版式（1080 高度基准的像素）")]
        [Tooltip("左右边距")]
        public float sideMargin = 72f;

        [Tooltip("卡片之间的间距")]
        public float columnGap = 28f;

        [Tooltip("卡片顶部起始位置（标题下面）")]
        public float cardsTop = 196f;

        [Tooltip("卡片底部距屏幕底边的距离（留给底部提示）")]
        public float cardsBottom = 132f;

        [Tooltip("标题距顶部的距离")]
        public float titleTop = 96f;

        [Header("字号")]
        public float titleSize  = 40f;
        public float nameSize   = 36f;
        public float descSize   = 21f;
        public float footerSize = 19f;

        [Header("卡片外观")]
        [Tooltip("卡片底色（很淡，只用来划分三列）")]
        public Color cardColor = new Color(1f, 1f, 1f, 0.045f);

        [Tooltip("鼠标悬停时的底色")]
        public Color cardHoverColor = new Color(1f, 1f, 1f, 0.13f);

        [Tooltip("边框颜色")]
        public Color cardBorderColor = new Color(1f, 1f, 1f, 0.22f);

        [Tooltip("边框粗细")]
        [Min(1f)] public float borderThickness = 2f;

        [Header("文字颜色")]
        public Color textColor     = Color.white;
        public Color dimColor      = new Color(1f, 1f, 1f, 0.55f);
        [Tooltip("选择界面的全屏背景色。\n\n" +
                 "★ 需求：背景改成**纯黑**（不透明，完全遮住后面的游戏画面）。")]
        public Color backdropColor = new Color(0f, 0f, 0f, 1f);

        private const float RefHeight = 1080f;

        // ------------------------------------------------------------------ 运行时

        /// <summary>界面是否正在显示。</summary>
        public bool IsOpen { get; private set; }

        private readonly List<DungeonBuffDef> m_Options = new List<DungeonBuffDef>(3);

        /// <summary>&gt;= 0 表示正在倒计时，到 0 就弹。</summary>
        private float m_PendingTimer = -1f;

        /// <summary>开局自选的倒计时（和通关那条完全独立）。</summary>
        private float m_StarterTimer = -1f;

        /// <summary>开局自选还在排队。</summary>
        private bool m_StarterPending;

        /// <summary>当前这次界面是"开局自选"（关掉时不去通知地图）。</summary>
        private bool m_IsStarterOpen;

        /// <summary>当前这一轮是"蓝插件三选一"（模板刚升到紫色）。</summary>
        private bool m_IsBluePick;

        /// <summary>本次界面已经刷新过几次。</summary>
        private int m_RerollsUsed;

        private float     m_PrevTimeScale = 1f;

        private Texture2D m_Pixel;
        private Texture2D m_CardTex;
        private Texture2D m_CardHoverTex;

        private GUIStyle m_TitleStyle;
        private GUIStyle m_NameStyle;
        private GUIStyle m_DescStyle;
        private GUIStyle m_IndexStyle;
        private GUIStyle m_FooterStyle;
        private GUIStyle m_ButtonStyle;
        private GUIStyle m_GoldStyle;
        private GUIStyle m_ClickArea;

        // ------------------------------------------------------------------ 生命周期

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }

            Instance = this;

            m_Pixel        = MakeSolid(Color.white);
            m_CardTex      = MakeSolid(cardColor);
            m_CardHoverTex = MakeSolid(cardHoverColor);
        }

        private void OnEnable()  => TriggerVolume.OnNextStage += OnNextStage;
        private void OnDisable() => TriggerVolume.OnNextStage -= OnNextStage;

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;

            DestroyTex(m_Pixel);
            DestroyTex(m_CardTex);
            DestroyTex(m_CardHoverTex);
        }

        private static void DestroyTex(Texture2D t)
        {
            if (t != null) Destroy(t);
        }

        // ------------------------------------------------------------------ 触发

        private void OnNextStage()
        {
            // 只在地牢模式里给 buff
            if (!GameUI.IsDungeonMode) return;

            m_PendingTimer = Mathf.Max(0f, openDelay);
        }

        /// <summary>
        /// 告诉 DungeonMapGenerator："buff 流程结束了，可以建下一层了"。
        ///
        /// ★ 选完 buff（或界面被强制关掉）时调用，
        ///   地图收到后才从"等 buff"状态继续往下走。
        /// </summary>
        private void NotifyMapBuffDone()
        {
            var gen = FindObjectOfType<DungeonMapGenerator>();
            if (gen != null) gen.NotifyBuffSelected();
        }

        // ------------------------------------------------------------------ 开局自选（第十周任务 1）

        /// <summary>
        /// 开局排队一次"自选强化"。
        ///
        /// 和通关时的那次三选一**不是一回事**：
        ///   · 它不参与地图的"等 buff"时序（地图没在等），所以关掉时不去通知生成器
        ///   · 它展示的是池子里**所有**还没叠满的 buff（"任意自选"），不是随机三张
        /// </summary>
        /// <param name="delay">
        /// 延迟多少秒再弹。**开局 / 重新开始传 0**（立刻弹），
        /// 因为那时地图还没建，没有任何东西需要等。
        /// </param>
        public void QueueStarterBuff(float delay = 0f)
        {
            m_StarterTimer   = Mathf.Max(0f, delay);
            m_StarterPending = true;
        }

        /// <summary>开局那次是否还排着队（给 GameUI 判断用，避免重复排队）。</summary>
        public bool StarterPending => m_StarterPending;

        private void Update()
        {
            if (!GameUI.IsDungeonMode)
            {
                if (IsOpen) Close(false);

                m_StarterPending = false;
                return;
            }

            // ---- 开局自选 ----
            //
            //   需求（第二条）："点击新游戏或重新开始时不要有这个延迟，
            //                   但玩家每次进入下一层时会有这个延迟。
            //                   并且所有情况下都是先出现 buff 界面后再新建地图。"
            //
            //   ★ starterDelay 默认 0 -> 开局同一帧就弹，不等。
            //     需要延迟的话把 QueueStarterBuff 的参数调大即可。
            //   ★ 地图这时还没建（DungeonMapGenerator.StarterStageRoutine 在等），
            //     所以天然满足"先 buff 后地图"。
            if (m_StarterPending && !IsOpen && GameUI.Current == GameState.Playing)
            {
                if (m_StarterTimer > 0f)
                {
                    m_StarterTimer -= Time.unscaledDeltaTime;

                    if (m_StarterTimer > 0f) { /* 还在等 */ }
                    else
                    {
                        m_StarterPending = false;
                        m_StarterTimer   = -1f;
                        OpenStarter();
                    }
                }
                else
                {
                    m_StarterPending = false;
                    m_StarterTimer   = -1f;
                    OpenStarter();
                }
            }

            // ---- 蓝插件三选一（**已废弃**）----
            //
            //   ★ 需求："我希望玩家获得蓝色插件时会一口气获得所有三个蓝插件，而不是三选一。"
            //   ✔ 现在模板升到紫色时，DungeonBuffState.GrantAllBluePlugins()
            //     会直接把三个蓝插件全部发进背包，PendingBluePick **永远是 false**，
            //     所以下面这个分支不会再触发，OpenBluePick() 成了死路径（保留不动）。
            if (!IsOpen && !m_StarterPending)
            {
                var st = DungeonBuffState.Instance;

                if (st != null && st.PendingBluePick && GameUI.Current == GameState.Playing)
                {
                    OpenBluePick();
                }
            }

            // ---- 倒计时：等地图淡出完成后弹 ----
            //
            // ★★ 新时序：淡出全黑 -> 弹 buff -> 选完 -> 建地图 -> 淡入 ★★
            //
            //   DungeonMapGenerator 淡出完会置 IsWaitingBuff = true，
            //   我们等这个信号再弹界面。
            if (!IsOpen && m_PendingTimer >= 0f)
            {
                var gen = FindObjectOfType<DungeonMapGenerator>();

                // 地图还没进入"等 buff"状态 -> 说明淡出还没完，继续等
                if (gen != null && !gen.IsWaitingBuff && !IsOpen)
                {
                    // 还在等淡出，倒计时不动
                }
                else
                {
                    m_PendingTimer -= Time.unscaledDeltaTime;

                    if (m_PendingTimer <= 0f)
                    {
                        m_PendingTimer = -1f;
                        Open();
                    }
                }
            }

            if (!IsOpen) return;

            // ★ 兜底：切窗口 / 别的系统可能把光标解锁成可见。
            //   ★ 选 buff 界面也不能出现系统指针 —— 屏幕上的短剑图标就是指针
            //     （需求："指针始终不出现，看起来像是短剑就是指针"）。
            // ★ 解锁（None）但保持**不可见** ★
            //
            //   ★ 必须解锁：Locked 时 Input.mousePosition 被钉在屏幕中心，
            //     短剑动不了，GUI.Button 也点不到。
            //   ★ visible = false：屏幕上看不到系统指针，
            //     玩家眼里那个短剑图标就是指针。
            if (Cursor.lockState != CursorLockMode.None || Cursor.visible)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible   = false;
            }

            // ---- 数字键快速选择 ----
            if (Input.GetKeyDown(KeyCode.Alpha1)) Pick(0);
            if (Input.GetKeyDown(KeyCode.Alpha2)) Pick(1);
            if (Input.GetKeyDown(KeyCode.Alpha3)) Pick(2);

            // ---- 花金币刷新三张卡（第十周任务 2）----
            if (Input.GetKeyDown(rerollKey)) Reroll();
        }

        // ------------------------------------------------------------------ 开关

        /// <summary>
        /// 开局自选：和每层通关那次**完全一样** —— 随机抽 3 张互不相同的卡。
        ///
        /// ★ 之前这里是"把池子里所有 buff 都列出来"，池子涨到 5 条之后就变成五选一了。
        ///   需求明确要求：每次只能从 **3 个**里选，而且每次随机。
        /// </summary>
        private void OpenStarter()
        {
            if (IsOpen) return;
            if (GameUI.Current != GameState.Playing) return;

            m_Options.Clear();
            m_Options.AddRange(DungeonBuffPool.Roll(PickCount, DungeonBuffState.Instance));

            if (m_Options.Count == 0) return;

            IsOpen = true;
            m_IsStarterOpen = true;
            m_RerollsUsed   = 0;

            m_PrevTimeScale = Time.timeScale;
            Time.timeScale  = 0f;

            // ★★ 解锁光标（鼠标坐标才有效），但**不画系统指针** ★★
            //
            //   ★ 必须 None：Locked 时 Input.mousePosition 被钉在屏幕中心，
            //     短剑动不了、GUI.Button 也点不到。
            //   ★ visible = false：屏幕上看不到系统指针，
            //     玩家眼里那个短剑图标就是唯一的指针。
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible   = false;
        }

        /// <summary>
        /// 蓝插件三选一：模板刚升到紫色时弹一次。
        ///
        /// ★★ 已废弃 ★★
        ///   需求改成"一口气获得所有三个蓝插件"，所以
        ///   DungeonBuffState.GrantAllBluePlugins() 已经直接把三个全发了。
        ///   这里保留只为兼容，PendingBluePick 永远是 false，本方法不会再被调用。
        /// </summary>
        private void OpenBluePick()
        {
            if (IsOpen) return;
            if (GameUI.Current != GameState.Playing) return;

            m_Options.Clear();
            m_Options.AddRange(DungeonBuffPool.ByColor(PluginColor.Blue));

            // 已经拿满蓝插件（理论上不会）就不再弹
            var st = DungeonBuffState.Instance;
            if (st != null) m_Options.RemoveAll(d => d == null || st.StacksOf(d) >= d.maxStacks);

            if (m_Options.Count == 0)
            {
                if (st != null) st.ConsumeBluePick();
                return;
            }

            IsOpen        = true;
            m_IsStarterOpen = false;
            m_IsBluePick    = true;
            m_RerollsUsed   = 0;

            m_PrevTimeScale = Time.timeScale;
            Time.timeScale  = 0f;

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible   = false;
        }

        private void Open()
        {
            if (IsOpen) return;

            // 正在结算 / 暂停时不弹（等回到游戏中再说）
            if (GameUI.Current != GameState.Playing) return;

            m_Options.Clear();
            m_Options.AddRange(DungeonBuffPool.Roll(PickCount, DungeonBuffState.Instance));

            // 池子里的 buff 全叠满了 -> 这一层就没有强化可选
            if (m_Options.Count == 0)
            {
                return;
            }

            IsOpen = true;
            m_RerollsUsed = 0;

            m_PrevTimeScale = Time.timeScale;
            Time.timeScale  = 0f;

            // ★★ 解锁光标（鼠标坐标才有效），但**不画系统指针** ★★
            //
            //   ★ 必须 None：Locked 时 Input.mousePosition 被钉在屏幕中心，
            //     短剑动不了、GUI.Button 也点不到。
            //   ★ visible = false：屏幕上看不到系统指针，
            //     玩家眼里那个短剑图标就是唯一的指针。
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible   = false;
        }

        /// <summary>
        /// 花金币刷新三张卡（第十周任务 2）。
        ///
        /// ★ 开局自选不能刷新：它列的是**全部**可选 buff，刷新没有意义。
        /// </summary>
        private void Reroll()
        {
            if (!IsOpen || m_IsBluePick) return;

            if (rerollLimit >= 0 && m_RerollsUsed >= rerollLimit) return;

            DungeonCurrency cur = DungeonCurrency.Instance;
            if (cur == null) return;

            int cost = Mathf.Max(0, rerollCost);

            // 金币不够 -> 什么都不发生（不扣钱、也不换卡）
            if (!cur.TrySpend(cost)) return;

            m_RerollsUsed++;

            m_Options.Clear();
            m_Options.AddRange(DungeonBuffPool.Roll(PickCount, DungeonBuffState.Instance));

            // 池子抽空了（全都叠满）就关掉，避免界面卡在一堆空卡片上
            if (m_Options.Count == 0) Close();
        }

        /// <param name="restoreCursor">是否把光标锁回去（换场景 / 退出时不需要）</param>
        private void Close(bool restoreCursor = true)
        {
            // ★★ 界面关了 = buff 流程结束 -> 通知地图可以建下一层了 ★★
            //
            //   放在 Close 里而不是 Pick 里：
            //   Close 是所有关闭路径的唯一出口（选完 / ForceClose / 切场景），
            //   保证地图不会因为某个路径漏了通知而永远卡在黑屏。
            //
            //   ★ 开局自选**也要**通知 ——
            //     开局现在是"界面出来 -> 立刻建图 -> 等选完 -> 显示世界"，
            //     地图正卡在 while (m_WaitingBuff) 上等这个信号。
            //     不通知的话，选完 buff 后地图永远不显示（卡黑屏）。
            m_IsStarterOpen = false;

            // ★ 蓝插件三选一：不管选没选，关掉就算处理过了（否则会一直重开）
            //   ★ 它是"额外奖励"，不是每层通关那个 buff 流程，
            //     所以**不能**去通知地图（地图这时并没有在等 buff）。
            bool wasBluePick = m_IsBluePick;

            if (wasBluePick)
            {
                m_IsBluePick = false;

                if (DungeonBuffState.Instance != null)
                    DungeonBuffState.Instance.ConsumeBluePick();
            }

            if (!wasBluePick) NotifyMapBuffDone();
            if (!IsOpen) return;

            IsOpen = false;
            m_Options.Clear();

            // 之前如果是暂停状态（0），别把它当成「恢复成 0」再冻住
            Time.timeScale = m_PrevTimeScale <= 0.0001f ? 1f : m_PrevTimeScale;

            if (restoreCursor)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible   = false;
            }
        }

        /// <summary>
        /// 强制关闭（不管当前状态），并把 timeScale 与光标还原。
        ///
        /// ★ 给「重新开始」用：那一局已经作废，界面必须立刻消失，
        ///   否则会挡在新一局前面，而且 timeScale 可能还停在 0。
        /// </summary>
        public void ForceClose()
        {
            m_PendingTimer = -1f;
            m_StarterPending = false;
            m_StarterTimer   = -1f;
            m_IsStarterOpen  = false;

            if (!IsOpen)
            {
                // 界面没开，但世界可能因为它冻结过 —— 兜底把时间恢复
                if (Time.timeScale <= 0.0001f) Time.timeScale = 1f;
                return;
            }

            IsOpen = false;
            m_Options.Clear();

            // 不在这里锁光标 —— 由 GameUI 的状态机统一管
            Time.timeScale = m_PrevTimeScale <= 0.0001f ? 1f : m_PrevTimeScale;
        }

        private void Pick(int index)
        {
            if (!IsOpen) return;
            if (index < 0 || index >= m_Options.Count) return;

            DungeonBuffDef def = m_Options[index];

            if (DungeonBuffState.Instance != null)
                DungeonBuffState.Instance.Add(def);


            Close();
        }

        // ------------------------------------------------------------------ 绘制

        private void OnGUI()
        {
            if (!IsOpen) return;

            EnsureStyles();

            float scale = Screen.height / RefHeight;
            Matrix4x4 saved = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity,
                                       new Vector3(scale, scale, 1f));

            float w = Screen.width / scale;

            // 全屏黑底（多画高一点，防高分屏下有缝）
            Color prev = GUI.color;
            GUI.color = backdropColor;
            GUI.DrawTexture(new Rect(0f, 0f, w, RefHeight * 1.5f), m_Pixel);
            GUI.color = prev;

            // 标题
            GUI.Label(new Rect(0f, titleTop, w, 64f), "选择一项强化", m_TitleStyle);

            // 金币：界面背景是不透明的，会把游戏内的 HUD 盖住，所以这里再画一次
            if (DungeonCurrency.Instance != null)
            {
                GUI.Label(new Rect(w - 340f, titleTop + 6f, 292f, 44f),
                          "金币 " + DungeonCurrency.Instance.Gold, m_GoldStyle);
            }

            // ---- 三列等宽卡片 ----
            int   n       = m_Options.Count;
            float usable  = w - sideMargin * 2f - columnGap * (n - 1);
            float cardW   = usable / Mathf.Max(1, n);
            float cardH   = RefHeight - cardsTop - cardsBottom;
            float cardY   = cardsTop;

            // ★ 先把选项拷一份 —— Pick() 会 Close() 并清空 m_Options，
            //   如果继续用 m_Options[i] 遍历就会 IndexOutOfRange。
            var options = m_Options.ToArray();

            for (int i = 0; i < options.Length; i++)
            {
                DungeonBuffDef d = options[i];

                if (d == null) continue;

                float cardX = sideMargin + i * (cardW + columnGap);
                var   card  = new Rect(cardX, cardY, cardW, cardH);

                // 1) 整张卡片就是点击区（背景 + 悬停高亮都由这个样式画）
                bool clicked = GUI.Button(card, GUIContent.none, m_ClickArea);

                float pad = Mathf.Max(16f, cardW * 0.07f);

                // ★ 第十一周：卡片按插件颜色上色（白 / 绿 / 紫）
                //   底色铺一层很淡的同色，边框和右上角的颜色名用纯色 ——
                //   不抢文字的对比度，但一眼能看出这是哪种插件。
                Color tint = SlotRules.UiColor(d.color);

                Color pcFill = GUI.color;
                GUI.color = new Color(tint.r, tint.g, tint.b, 0.12f);
                GUI.DrawTexture(card, m_Pixel);
                GUI.color = pcFill;

                // 2) 细边框（用插件颜色）
                DrawBorder(card, tint);

                Color pcTag = GUI.color;
                GUI.color = tint;
                GUI.Label(new Rect(cardX + cardW - pad - 130f, cardY + 20f, 130f, 40f),
                          SlotRules.NameOf(d.color) + "插件", m_IndexStyle);
                GUI.color = pcTag;

                // 3) 文字（画在按钮之上）

                //    左上角的编号，提示"按数字键也能选"
                GUI.Label(new Rect(cardX + pad, cardY + 20f, 60f, 40f), (i + 1).ToString(), m_IndexStyle);

                //    名称（居中，卡片上部）
                GUI.Label(new Rect(cardX + pad, cardY + cardH * 0.22f, cardW - pad * 2f, 58f),
                          d.name, m_NameStyle);

                //    名称下面一条分隔线
                Color pc = GUI.color;
                GUI.color = cardBorderColor;
                GUI.DrawTexture(new Rect(cardX + pad, cardY + cardH * 0.22f + 66f, cardW - pad * 2f, 1f), m_Pixel);
                GUI.color = pc;

                //    描述（自动换行）
                GUI.Label(new Rect(cardX + pad, cardY + cardH * 0.22f + 92f, cardW - pad * 2f, cardH * 0.36f),
                          d.desc, m_DescStyle);

                //    已拥有层数（卡片底部）
                int stacks = DungeonBuffState.Instance != null
                           ? DungeonBuffState.Instance.StacksOf(d)
                           : 0;

                string stackText = stacks > 0 ? $"已拥有 Lv{stacks}（上限 Lv{d.maxStacks}）"
                                              : $"还未拥有（上限 Lv{d.maxStacks}）";

                GUI.Label(new Rect(cardX + pad, cardY + cardH - 72f, cardW - pad * 2f, 40f),
                          stackText, m_FooterStyle);

                if (clicked)
                {
                    Pick(i);
                    break;      // ★ 已经关闭界面，不要继续画后面的卡片
                }
            }

            // ---- 底部：刷新按钮 + 提示 ----
            //
            // ★ 开局三选一**也**给刷新按钮。
            //
            //   以前不给是因为"开局列的是全部可选 buff"，刷新没意义；
            //   现在开局也是随机抽 3 张，玩家同样会想重抽，所以放开。
            //   不会被白嫖：开局金币是 0（DungeonCurrency.startGold 默认 0），
            //   按钮显示为灰、按 F 也不扣钱不换卡。
            if (DungeonCurrency.Instance != null)
            {
                float btnW = 320f;
                float btnH = 46f;
                var   btn  = new Rect((w - btnW) * 0.5f, RefHeight - 124f, btnW, btnH);

                bool canReroll = DungeonCurrency.Instance.CanAfford(Mathf.Max(0, rerollCost))
                              && (rerollLimit < 0 || m_RerollsUsed < rerollLimit);

                bool clickedRefresh = GUI.Button(btn, GUIContent.none, m_ClickArea);
                DrawBorder(btn);

                Color pc2 = GUI.color;
                if (!canReroll) GUI.color = new Color(1f, 1f, 1f, 0.35f);

                GUI.Label(new Rect(btn.x, btn.y, btn.width, btn.height),
                          $"{rerollKey}　刷新（{rerollCost} 金币）", m_ButtonStyle);

                GUI.color = pc2;

                if (clickedRefresh) Reroll();
            }

            // 提示
            GUI.Label(new Rect(0f, RefHeight - 70f, w, 40f),
                      "点击卡片，或按 1 / 2 / 3", m_FooterStyle);

            GUI.matrix = saved;
        }

        /// <summary>画一圈细边框（四条 1×1 拉伸的矩形）。</summary>
        private void DrawBorder(Rect r)
        {
            DrawBorder(r, cardBorderColor);
        }

        /// <summary>用指定颜色画四边框（第十一周：按插件颜色给卡片上色）。</summary>
        private void DrawBorder(Rect r, Color color)
        {
            Color prev = GUI.color;
            GUI.color = color;

            float t = Mathf.Max(1f, borderThickness);

            GUI.DrawTexture(new Rect(r.x,           r.y,            r.width, t),          m_Pixel); // 上
            GUI.DrawTexture(new Rect(r.x,           r.yMax - t,     r.width, t),          m_Pixel); // 下
            GUI.DrawTexture(new Rect(r.x,           r.y,            t,       r.height),   m_Pixel); // 左
            GUI.DrawTexture(new Rect(r.xMax - t,    r.y,            t,       r.height),   m_Pixel); // 右

            GUI.color = prev;
        }

        private void EnsureStyles()
        {
            if (m_TitleStyle != null) return;

            Font f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (f == null) f = Resources.GetBuiltinResource<Font>("Arial.ttf");

            m_TitleStyle = new GUIStyle(GUI.skin.label)
            {
                font      = f,
                fontSize  = Mathf.RoundToInt(titleSize),
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
            };
            m_TitleStyle.normal.textColor = textColor;

            m_NameStyle = new GUIStyle(GUI.skin.label)
            {
                font      = f,
                fontSize  = Mathf.RoundToInt(nameSize),
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
            };
            m_NameStyle.normal.textColor = textColor;

            m_DescStyle = new GUIStyle(GUI.skin.label)
            {
                font      = f,
                fontSize  = Mathf.RoundToInt(descSize),
                alignment = TextAnchor.UpperCenter,
                wordWrap  = true,
                richText  = false,
            };
            m_DescStyle.normal.textColor = new Color(1f, 1f, 1f, 0.85f);

            m_IndexStyle = new GUIStyle(GUI.skin.label)
            {
                font      = f,
                fontSize  = Mathf.RoundToInt(footerSize),
                alignment = TextAnchor.UpperLeft,
            };
            m_IndexStyle.normal.textColor = dimColor;

            m_FooterStyle = new GUIStyle(GUI.skin.label)
            {
                font      = f,
                fontSize  = Mathf.RoundToInt(footerSize),
                alignment = TextAnchor.MiddleCenter,
                wordWrap  = true,
            };
            m_FooterStyle.normal.textColor = dimColor;

            // 刷新按钮上的字
            m_ButtonStyle = new GUIStyle(GUI.skin.label)
            {
                font      = f,
                fontSize  = Mathf.RoundToInt(footerSize),
                alignment = TextAnchor.MiddleCenter,
            };
            m_ButtonStyle.normal.textColor = textColor;

            // 右上角的金币
            m_GoldStyle = new GUIStyle(GUI.skin.label)
            {
                font      = f,
                fontSize  = Mathf.RoundToInt(footerSize),
                alignment = TextAnchor.MiddleRight,
                fontStyle = FontStyle.Bold,
            };
            m_GoldStyle.normal.textColor = new Color(1f, 0.85f, 0.35f, 1f);

            // 卡片点击区：只负责底色和悬停，不画字
            m_ClickArea = new GUIStyle(GUI.skin.button)
            {
                border  = new RectOffset(0, 0, 0, 0),
                margin  = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(0, 0, 0, 0),
            };

            m_ClickArea.normal.background   = m_CardTex;
            m_ClickArea.hover.background    = m_CardHoverTex;
            m_ClickArea.active.background   = m_CardHoverTex;
            m_ClickArea.focused.background  = m_CardTex;
            m_ClickArea.onNormal.background = m_CardTex;
            m_ClickArea.onHover.background  = m_CardHoverTex;
            m_ClickArea.onActive.background = m_CardHoverTex;
        }

        /// <summary>生成一张 1×1 纯色纹理（带 alpha），不依赖任何贴图资源。</summary>
        private static Texture2D MakeSolid(Color c)
        {
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave
            };

            tex.SetPixel(0, 0, c);
            tex.Apply();
            return tex;
        }
    }
}