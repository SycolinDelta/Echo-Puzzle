using System.Collections.Generic;
using UnityEngine;

namespace EchoPuzzle.Dungeon
{
    /// <summary>
    /// 【地牢模式】插件装配界面（第十一周版）。
    ///
    /// ── 操作 ────────────────────────────────────────────────
    ///   · **R** 开 / 关本界面
    ///   · **E** 切换武器模板（**战斗中也能按**，不用先开界面）
    ///   · 点背包里的插件 → 选中；再点同色的空格 → 装上
    ///   · 点已占用的格子 → 拆下
    ///
    /// ── 版式 ────────────────────────────────────────────────
    ///   左边背包，右边两个模板；每个模板按颜色分行：
    ///     白 1/3   [■][ ][ ]
    ///     绿 0/3   [ ][ ][ ]
    ///     蓝 0/0
    ///     紫 1/1   [■]
    ///     橙 0/0
    ///     红 0/1   [ ]      ← 红槽必须填满，没填会标红提示
    /// </summary>
    [AddComponentMenu("Echo Puzzle/Dungeon/Dungeon Loadout UI")]
    public sealed class DungeonLoadoutUI : MonoBehaviour
    {
        public static DungeonLoadoutUI Instance { get; private set; }

        [Header("按键")]
        public KeyCode toggleKey = KeyCode.R;
        public KeyCode switchKey = KeyCode.E;

        [Header("版式（1080 高度基准的像素）")]
        public float panelWidth  = 1740f;
        public float panelHeight = 1040f;
        public float titleSize   = 46f;
        public float labelSize   = 26f;
        public float smallSize   = 20f;
        public float slotSize    = 58f;
        public float slotGap     = 6f;

        [Header("颜色")]
        public Color textColor      = Color.white;
        public Color dimColor       = new Color(1f, 1f, 1f, 0.5f);
        public Color activeColor    = new Color(0.4f, 0.85f, 1f, 1f);
        public Color backdropColor  = new Color(0f, 0f, 0f, 0.96f);
        public Color cardColor      = new Color(1f, 1f, 1f, 0.06f);
        public Color cardHoverColor = new Color(1f, 1f, 1f, 0.16f);
        public Color borderColor    = new Color(1f, 1f, 1f, 0.22f);
        public Color warnColor      = new Color(1f, 0.4f, 0.4f, 1f);

        private const float RefHeight = 1080f;

        public bool IsOpen { get; private set; }

        /// <summary>背包里红色插件显示用的"无限"计数标记。</summary>
        private const int RedVirtualCount = -1;

        private BuffInstance m_Selected;

        /// <summary>
        /// 这一帧的右键是否已经被某个槽位消费掉了（拆下插件）。
        ///
        /// ★ 用途：右键点**非空槽**是"拆下"，不应该退出编辑；
        ///   右键点**其它地方**才退出编辑。
        /// ★ 每帧在 OnGUI 开头清零。
        /// </summary>
        private bool m_RightClickHandled;
        private float        m_PrevTimeScale = 1f;

        private Texture2D m_Pixel;
        private Texture2D m_CardTex;
        private Texture2D m_CardHoverTex;

        private GUIStyle m_TitleStyle;
        private GUIStyle m_LabelStyle;
        private GUIStyle m_SmallStyle;
        private GUIStyle m_SlotStyle;
        private GUIStyle m_RowStyle;
        private GUIStyle m_ClickArea;

        private string m_Hint = "";
        private float  m_HintUntil;

        // ------------------------------------------------------------------ 生命周期

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }

            Instance = this;

            m_Pixel        = MakeSolid(Color.white);
            m_CardTex      = MakeSolid(cardColor);
            m_CardHoverTex = MakeSolid(cardHoverColor);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;

            if (m_Pixel != null) Destroy(m_Pixel);
            if (m_CardTex != null) Destroy(m_CardTex);
            if (m_CardHoverTex != null) Destroy(m_CardHoverTex);
        }

        private void Update()
        {
            if (!GameUI.IsDungeonMode) { if (IsOpen) Close(); return; }

            var st = DungeonBuffState.Instance;

            // ★ 任务书：战斗中也能按 E 切模板（不用先开 R）
            if (st != null && GameUI.Current == GameState.Playing && Input.GetKeyDown(switchKey))
            {
                st.SetActiveTemplate((st.ActiveTemplate + 1) % DungeonBuffState.TemplateCount);
                ShowHint($"已切换到模板 {(st.ActiveTemplate == 0 ? "A" : "B")}");
            }

            if (!IsOpen)
            {
                if (GameUI.Current == GameState.Playing && Input.GetKeyDown(toggleKey)) Open();
                return;
            }

            if (Input.GetKeyDown(toggleKey) || Input.GetKeyDown(KeyCode.Escape))
            {
                // ★★ 红槽必须填满才能退出 ★★
                //
                //   需求："必须将红色插件槽位填满才允许退出武器插件界面。"
                var stNow = DungeonBuffState.Instance;

                if (stNow != null && !stNow.RedSlotsFull(stNow.ActiveTemplate))
                {
                    ShowHint($"必须填满红色插件槽");
                }
                else
                {
                    Close();
                }
            }

            if (Cursor.lockState != CursorLockMode.None || Cursor.visible)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible   = false;
            }
        }

        private void Open()
        {
            if (IsOpen) return;

            IsOpen = true;
            m_Selected = null;

            m_PrevTimeScale = Time.timeScale;
            Time.timeScale  = 0f;

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible   = false;
        }

        public void Close()
        {
            if (!IsOpen) return;

            IsOpen = false;
            m_Selected = null;

            Time.timeScale = m_PrevTimeScale <= 0.0001f ? 1f : m_PrevTimeScale;

            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible   = false;
        }

        private void ShowHint(string text)
        {
            m_Hint      = text ?? "";
            m_HintUntil = Time.unscaledTime + 2.5f;
        }

        // ------------------------------------------------------------------ 绘制

        private void OnGUI()
        {
            if (!IsOpen) return;

            var st = DungeonBuffState.Instance;
            if (st == null) return;

            EnsureStyles();

            // ★ 每帧开头清零"右键已被消费"标记
            m_RightClickHandled = false;

            float scale = Screen.height / RefHeight;
            Matrix4x4 saved = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity,
                                       new Vector3(scale, scale, 1f));

            float w = Screen.width / scale;

            Color prev = GUI.color;
            GUI.color = backdropColor;
            GUI.DrawTexture(new Rect(0f, 0f, w, RefHeight * 1.6f), m_Pixel);
            GUI.color = prev;

            float px = (w - panelWidth) * 0.5f;
            float py = (RefHeight - panelHeight) * 0.5f;
            var panel = new Rect(px, py, panelWidth, panelHeight);
            DrawBorder(panel);

            GUI.Label(new Rect(panel.x, panel.y + 16f, panel.width, 60f), "插件装配", m_TitleStyle);

            GUI.Label(new Rect(panel.x + 24f, panel.y + 74f, panel.width - 48f, 30f),
                      $"模板等级：{SlotRules.NameOf(st.Tier)}级　·　商店剩余升级：{st.ShopUpgradesLeft}/2　" +
                      $"·　R 关闭　·　E 切换模板（战斗中也可以）", m_SmallStyle);

            // 左：背包
            float bagW = 400f;
            var bag = new Rect(panel.x + 24f, panel.y + 120f, bagW, panel.height - 180f);
            DrawBorder(bag);
            GUI.Label(new Rect(bag.x + 14f, bag.y + 8f, bag.width - 28f, 30f), "背包", m_LabelStyle);
            DrawBag(st, new Rect(bag.x + 12f, bag.y + 44f, bag.width - 24f, bag.height - 56f));

            // 右：两个模板**并排两列**（上下堆叠的话每个模板只剩半屏高，格子只能很小）
            float rightX = bag.xMax + 20f;
            float avail  = panel.xMax - 24f - rightX;
            float colGap = 20f;
            float colW   = (avail - colGap) * 0.5f;

            float colY = panel.y + 120f;
            float colH = panel.height - 190f;

            DrawTemplate(st, 0, new Rect(rightX, colY, colW, colH));
            DrawTemplate(st, 1, new Rect(rightX + colW + colGap, colY, colW, colH));

            // ★★ 底部提示：有临时提示就显示它，否则显示默认说明 ★★
            //
            //   需求："在武器模板界面，左下角没有其他提示需要显示时，
            //         它会默认显示「推进关卡以增加白绿槽位，升级模板以增加紫橙槽位，
            //         当前白、绿、紫、橙插件最多合计可安装 x 个」。
            //         x 为插件都不装时的白色槽位数。"
            //
            //   ★ x = WhiteGreenBase(当前层) —— 即"白绿共享的基础格数"，
            //     它只跟层数有关，跟装了什么插件无关。
            //     这正好等于"插件都不装时的白色槽位数"。
            //   ★ 白/绿共用这一池子格数；紫橙插件各会额外占掉 1 格白绿。
            string hintText;

            if (!string.IsNullOrEmpty(m_Hint) && Time.unscaledTime < m_HintUntil)
            {
                hintText = m_Hint;
            }
            else
            {
                int sharedBase = SlotRules.WhiteGreenBase(DungeonBuffState.StageIndex);

                hintText = "推进关卡以增加白绿槽位，升级模板以增加紫橙槽位，" +
                           $"当前白、绿、紫、橙插件最多合计可安装 {sharedBase} 个，攻速大于*8时会转化为伤害和蓝耗";
            }

            GUI.Label(new Rect(panel.x + 24f, panel.yMax - 40f, panel.width - 48f, 30f),
                      hintText, m_SmallStyle);

            // ★★ 右键退出编辑 ★★
            //
            //   需求 4："退出编辑的方式只有两种，
            //            一是在鼠标没有位于非空槽位上时按右键，
            //            二是将鼠标移动至背包栏插件上点击左键。"
            //
            //   ★ 右键点**非空槽**已经由 OnSlotClicked 处理成"拆下"了，
            //     那种情况 m_RightClickHandled = true，这里不退出。
            //   ★ 其余任何位置按右键 -> 退出编辑。
            if (m_Selected != null &&
                Event.current.type == EventType.MouseDown &&
                Event.current.button == 1 &&
                !m_RightClickHandled)
            {
                m_Selected = null;
                ShowHint("退出编辑");
                Event.current.Use();
            }

            GUI.matrix = saved;
        }

        private void DrawBag(DungeonBuffState st, Rect area)
        {
            // 按 def 分组（同色同名算一份一组），显示拥有数和装在哪
            var defs  = new List<DungeonBuffDef>();
            var counts = new List<int>();

            for (int i = 0; i < st.Owned.Count; i++)
            {
                BuffInstance inst = st.Owned[i];
                if (inst == null || inst.def == null) continue;

                // ★ 红色实例不进背包列表：红色是无限量的，
                //   会在下面统一补三条"虚拟无限"的条目。
                if (inst.def.color == PluginColor.Red) continue;

                int idx = defs.FindIndex(x => x != null && x.id == inst.def.id);
                if (idx >= 0) { counts[idx]++; continue; }

                defs.Add(inst.def);
                counts.Add(1);
            }

            // ★★ 红色插件：始终出现在背包里，数量为无限 ★★
            //
            //   需求："这三种红色插件应当始终出现在背包里，数量为无限。"
            //
            //   ★ 数量用 <see cref="RedVirtualCount"/> 这个特殊值显示成"∞"
            var reds = DungeonBuffState.RedDefs();

            for (int i = 0; i < reds.Count; i++)
            {
                if (reds[i] == null) continue;
                if (defs.FindIndex(x => x != null && x.id == reds[i].id) >= 0) continue;

                defs.Add(reds[i]);
                counts.Add(RedVirtualCount);
            }

            float y = area.y;

            if (defs.Count == 0)
            {
                GUI.Label(new Rect(area.x, y, area.width, 28f), "（还没有插件）", m_SmallStyle);
                return;
            }

            for (int i = 0; i < defs.Count; i++)
            {
                DungeonBuffDef d = defs[i];

                // 找到一份"还没装进当前模板"的实例用于选中
                BuffInstance pick = null;
                for (int k = 0; k < st.Owned.Count; k++)
                {
                    BuffInstance inst = st.Owned[k];
                    if (inst == null || inst.def == null || inst.def.id != d.id) continue;
                    if (st.IsEquipped(st.ActiveTemplate, inst)) continue;

                    pick = inst;
                    break;
                }

                // ★ 需求：背包只写插件名字和数量，不写它在 A / B 模板里。
                //   （所以这里不再统计 inA / inB）

                // ★ 需求："去掉禁用。" —— 所有插件都永远可点，不再灰化。
                const bool usable = true;

                bool selected = m_Selected != null && m_Selected.def != null && m_Selected.def.id == d.id;

                var row = new Rect(area.x, y, area.width, 44f);

                if (GUI.Button(row, GUIContent.none, m_ClickArea) && usable)
                {
                    // ★★ 背包点击 = 编辑状态的唯一开关 ★★
                    //
                    //   需求 4："二是将鼠标移动至背包栏插件上点击左键。
                    //            如果鼠标移动至背包栏目前正在编辑的插件，
                    //            点左键后会退出编辑；而如果移动至另一个插件，
                    //            点左键后仍然在编辑状态，但会将编辑对象改为另一个插件。"
                    //
                    //   ★ 点"当前正在编辑的" -> 退出编辑
                    //   ★ 点"另一个" -> 切换编辑对象（保持编辑状态）
                    if (selected)
                    {
                        m_Selected = null;
                        ShowHint("退出编辑");
                    }
                    else
                    {
                        // 红色没有实体实例：现场造一个"仅用于编辑"的临时实例
                        if (d.color == PluginColor.Red && pick == null)
                            m_Selected = new BuffInstance(d, 1);
                        else
                            m_Selected = pick != null ? pick : new BuffInstance(d, 1);

                        ShowHint($"点击右侧相应颜色的槽位来插入该插件");
                    }
                }

                DrawBorder(row);

                // 左侧颜色条
                Color prev = GUI.color;
                GUI.color = SlotRules.UiColor(d.color);
                GUI.DrawTexture(new Rect(row.x + 2f, row.y + 2f, 6f, row.height - 4f), m_Pixel);

                if (selected) GUI.color = activeColor;
                // ★ 不再有"不可用 -> 变灰"的分支

                // ★ RedVirtualCount(-1) 显示成无限符号
                string countText = counts[i] == RedVirtualCount ? "∞" : counts[i].ToString();

                GUI.Label(new Rect(row.x + 16f, row.y + 8f, row.width - 24f, 30f),
                          $"{d.name} ×{countText}", m_LabelStyle);

                GUI.color = prev;

                y += 50f;

                if (y > area.yMax - 44f) break;
            }
        }

        private void DrawTemplate(DungeonBuffState st, int template, Rect area)
        {
            bool active = st.ActiveTemplate == template;

            DrawBorder(area);

            Color prev = GUI.color;

            // ★★ 模板标题：去掉「白级」，颜色随模板等级变化 ★★
            //
            //   需求："模板这里有个标题「模板A 白级 （当前使用）」，把白级两个字去掉。
            //         这个标题的颜色会随着模板等级变化成对应的颜色。"
            //
            //   ★ 原来：模板 A　白级　（当前使用）
            //   ✔ 现在：模板 A　（当前使用）
            //     并且文字颜色 = 该模板等级的对应颜色（白/绿/蓝/紫/橙）。
            //   ★ "哪个模板在用"靠标题里的文字「（当前使用）」区分，不额外改色，
            //     否则会盖掉等级颜色。
            GUI.color = SlotRules.UiColor(st.Tier);

            GUI.Label(new Rect(area.x + 16f, area.y + 8f, area.width - 32f, 38f),
                      $"模板 {(template == 0 ? "A" : "B")}" +
                      (active ? "　（当前使用）" : "　（按 E 切换）"),
                      m_LabelStyle);

            GUI.color = prev;

            const float headerH = 58f;
            const float labelW  = 150f;
            const float padX    = 16f;

            float y      = area.y + headerH;
            float availH = area.height - headerH - 12f;

            float boxX0  = area.x + padX + labelW;
            float maxX   = area.xMax - 14f;
            float availW = maxX - boxX0;

            // ★ 找一个"能塞进这个区域的最大格子尺寸" —— 区域越大字就越大
            float slot = 40f;
            float gap  = 6f;

            for (float s = 132f; s >= 40f; s -= 2f)
            {
                float g   = Mathf.Max(4f, s * 0.10f);
                int   per = Mathf.Max(1, Mathf.FloorToInt((availW + g) / (s + g)));

                float total = 0f;

                for (int c = 0; c < SlotRules.ColorCount; c++)
                {
                    int cap   = st.CapOf(template, (PluginColor)c);
                    int lines = Mathf.Max(1, Mathf.CeilToInt(cap / (float)per));

                    total += lines * s + (lines - 1) * g + s * 0.22f;
                }

                if (total <= availH) { slot = s; gap = g; break; }
            }

            // 行标签和格子里的字都跟着格子尺寸走
            m_RowStyle.fontSize  = Mathf.RoundToInt(Mathf.Clamp(slot * 0.40f, 16f, 34f));
            m_SlotStyle.fontSize = Mathf.RoundToInt(slot * 0.50f);

            int perLine = Mathf.Max(1, Mathf.FloorToInt((availW + gap) / (slot + gap)));

            for (int c = 0; c < SlotRules.ColorCount; c++)
            {
                var color = (PluginColor)c;

                int cap   = st.CapOf(template, color);
                int used  = st.UsedOf(template, color);
                var list  = st.SlotsOf(template, color);

                // 红槽没填满 -> 标红提醒
                bool warn = color == PluginColor.Red && used < cap;

                int lines = Mathf.Max(1, Mathf.CeilToInt(cap / (float)perLine));

                Color cprev = GUI.color;
                GUI.color = warn ? warnColor : SlotRules.UiColor(color);

                // ★ 行标签的矩形必须和第一行格子**完全同高同起点**，
                //   否则文字会看起来比格子高/低半格，像是"对不上"。
                GUI.Label(new Rect(area.x + padX, y, labelW - 8f, slot),
                          $"{SlotRules.NameOf(color)} {used}/{cap}", m_RowStyle);

                GUI.color = cprev;

                float bx = boxX0;
                float by = y;

                for (int i = 0; i < cap; i++)
                {
                    if (i > 0 && (i % perLine) == 0)
                    {
                        bx  = boxX0;
                        by += slot + gap;
                    }

                    var box = new Rect(bx, by, slot, slot);

                    BuffInstance equipped = (i < list.Count) ? list[i] : null;

                    // ★★ 左键 / 右键分别处理 ★★
                    //
                    //   需求：
                    //     · 左键或右键点**已装插件**的槽 -> 拆下
                    //     · 左键点**空槽** -> 安装 / 替换（需要编辑状态）
                    //     · 右键点非空槽 -> 拆下（**不是**退出编辑）
                    //
                    //   ★ 用 Event.current 自己判，因为 GUI.Button 只认左键。
                    bool hovered = box.Contains(Event.current.mousePosition);

                    int btn = -1;
                    if (hovered && Event.current.type == EventType.MouseDown)
                    {
                        if (Event.current.button == 0)      btn = 0;
                        else if (Event.current.button == 1) btn = 1;
                    }

                    // 底色：装了 = 该颜色铺一层；空槽 = 很淡的灰
                    Color tint  = SlotRules.UiColor(color);
                    Color p2    = GUI.color;

                    GUI.color = equipped != null
                        ? new Color(tint.r, tint.g, tint.b, 0.26f)
                        : new Color(1f, 1f, 1f, 0.04f);
                    GUI.DrawTexture(box, m_Pixel);
                    GUI.color = p2;

                    // 边框：装了用纯色（字和格子一眼对上），空槽用很淡的白
                    DrawBorder(box, equipped != null ? tint : new Color(1f, 1f, 1f, 0.14f));

                    // 装了东西再套一圈内框，把字"围"在自己格子里
                    if (equipped != null)
                    {
                        float inset = Mathf.Max(3f, slot * 0.10f);
                        DrawBorder(new Rect(box.x + inset, box.y + inset,
                                            box.width - inset * 2f, box.height - inset * 2f),
                                   new Color(tint.r, tint.g, tint.b, 0.45f));
                    }

                    if (equipped != null)
                        GUI.Label(box, ShortTag(equipped), m_SlotStyle);

                    if (btn >= 0)
                    {
                        // ★★ 右键分两种，必须区别对待 ★★
                        //
                        //   需求 1：右键点**非空槽** -> 拆下（不退出编辑）
                        //   需求 4：鼠标不在非空槽上时右键 -> 退出编辑
                        //
                        //   ★ 以前这里无条件 Event.current.Use()，
                        //     把"右键点在空槽上"的事件也吃掉了，
                        //     导致 OnGUI 末尾的退出编辑逻辑收不到 → 空槽右键没反应。
                        //   ✔ 现在只有"右键点在非空槽上"才 Use()（那是拆下，要消费）；
                        //     空槽的右键**不消费**，原样交给 OnGUI 末尾去退出编辑。
                        if (btn == 1)
                        {
                            if (equipped != null)
                            {
                                m_RightClickHandled = true;
                                OnSlotClicked(st, template, color, i, equipped, btn);
                                Event.current.Use();
                            }
                            // 空槽：什么都不做，让事件继续往下走（由 OnGUI 退出编辑）
                        }
                        else
                        {
                            OnSlotClicked(st, template, color, i, equipped, btn);
                            Event.current.Use();
                        }
                    }

                    bx += slot + gap;
                }

                y += lines * slot + (lines - 1) * gap + slot * 0.22f;
            }
        }

        /// <summary>
        /// 槽位被点击时调用。
        /// </summary>
        /// <param name="btn">0 = 左键，1 = 右键</param>
        ///
        /// ★ 需求全文：
        ///   1) 用左键或右键点击一个槽位中的插件时会将其拆下。
        ///   2) 点击背包栏位中的插件后进入编辑状态，此时左键（必须是左键）
        ///      点击右侧空槽位会安装插件，点击非空槽位会替换原有插件，
        ///      安装或替换插件后**不会自动退出编辑状态**。
        ///   3) 不存在任何会禁用背包栏位中的插件的情况。
        ///   4) 退出编辑只有两种：鼠标不在非空槽位上时按右键；
        ///      或点背包里**当前正在编辑的**那个插件。
        ///   5) 一个模板上装的数量不能超过背包里的数量。
        private void OnSlotClicked(DungeonBuffState st, int template, PluginColor color,
                                   int index, BuffInstance equipped, int btn)
        {
            // ============================================================
            // 右键
            // ============================================================
            if (btn == 1)
            {
                // ---- 非空槽：拆下 ----
                //
                //   ★ 需求 1："用左键或右键点击一个槽位中的插件时会将其拆下。"
                //   ★ 这里拆完**不进/不退**编辑状态，编辑状态原样保留。
                if (equipped != null)
                {
                    st.Unequip(template, color, index);
                    ShowHint($"拆下 {equipped.Name}");
                    return;
                }

                // ---- 空槽：什么都不做 ----
                //
                //   ★ 需求 4 说"鼠标没有位于非空槽位上时按右键"才退出编辑。
                //     空槽算"不在非空槽上"，所以退出编辑 —— 但由 OnGUI 统一处理，
                //     这里直接返回即可（OnGUI 会看到 m_RightClickHandled = false 而退出）。
                return;
            }

            // ============================================================
            // 左键
            // ============================================================

            // ---- 非空槽：分两种情况 ----
            //
            //   ★ 需求 1：没在编辑状态时，左键点已装的插件 -> **拆下**
            //   ★ 需求 2：**在编辑状态**时，左键点非空槽 -> **替换**原有插件
            if (equipped != null)
            {
                if (m_Selected == null || m_Selected.def == null)
                {
                    // 不在编辑状态 -> 拆下
                    st.Unequip(template, color, index);
                    ShowHint($"拆下 {equipped.Name}");
                    return;
                }

                // 在编辑状态 -> 先拆掉旧的，再走下面的"安装"流程
                DungeonBuffDef replWant = m_Selected.def;

                if (replWant.color != color)
                {
                    ShowHint($"{replWant.name} 只能插进 {SlotRules.NameOf(replWant.color)}槽");
                    return;
                }

                st.Unequip(template, color, index);

                // 拆完继续往下走"安装"分支（这里不 return）
                // 提示稍后由安装分支统一给
            }

            // ---- 空槽（或刚被拆空）：必须处于编辑状态 ----
            if (m_Selected == null || m_Selected.def == null)
            {
                ShowHint("请点击左侧的插件并将它们插入模板");
                return;
            }

            DungeonBuffDef want = m_Selected.def;

            // 颜色必须对得上
            if (want.color != color)
            {
                ShowHint($"{want.name} 只能插进 {SlotRules.NameOf(want.color)}槽");
                return;
            }

            // ---- 需求 5：数量不能超过背包 ----
            //
            //   ★ 红色是无限量，直接放行。
            if (color != PluginColor.Red)
            {
                int ownedCount = CountOwned(st, want);
                int usedHere   = CountUsedOnTemplate(st, template, want);

                if (usedHere >= ownedCount)
                {
                    ShowHint($"插件数量不足");
                    return;
                }
            }

            // ---- 安装 ----
            if (color == PluginColor.Red)
            {
                // 红色没有实体实例：现场发一份
                if (st.EquipRedFresh(template, want, out string redReason))
                    ShowHint($"已安装 {want.name}");
                else
                    ShowHint(redReason ?? "安装失败");
            }
            else
            {
                if (st.TryEquip(template, m_Selected, out string reason))
                    ShowHint($"已安装 {want.name}");
                else
                    ShowHint(reason ?? "安装失败");
            }

            // ★★ 安装后：编辑状态保持在**同一个效果**上，但换成下一份未装备的实例 ★★
            //
            //   需求：“在玩家有多个相同白色插件时，编辑该插件时仍然没法将它们
            //         连续安装到模板上，必须先安装一个，然后退出并重新进入编辑状态，
            //         才能再安装一个。把它修复掉，看看其他颜色的插件是不是也有这个问题。”
            //
            //   ★ 根因：m_Selected 指向的是一个**具体实例**。
            //     装上之后那个实例已经在槽里了，下一次点空槽会走
            //     TryEquip -> CanEquip -> IsEquipped，直接被判它已经装在这个模板里了。
            //   ★ 这个 bug 对**所有非红色**颜色都成立（红色走 EquipRedFresh，
            //     不依赖 m_Selected 的实例，所以红色本来就能连续装）。
            //   ✔ 修复：装完立刻把 m_Selected 换成同一效果的**另一份未装备实例**。
            //     这样连着点空槽就能连续装；背包里那一行的高亮也不会跳掉
            //     （高亮是按 def.id 比的）。
            if (m_Selected != null && m_Selected.def != null)
            {
                m_Selected = PickUnequipped(st, m_Selected.def, template);
            }
        }

        /// <summary>
        /// 在背包里找一份同种插件、且还没装进指定模板的实例。
        ///
        /// ★ 用途：连续安装。装完一份之后用它换到下一份，
        ///   否则 m_Selected 还指着刚装进去的那份，下一次会被
        ///   CanEquip 的它已经装在这个模板里了 挡掉。
        ///
        /// ★ 如果所有份都装上了，返回一个**新建的占位实例** ——
        ///   这样编辑状态仍然停在这个效果上（背包高亮不跳），
        ///   下次点空槽会被数量检查拦下并提示插件数量不足。
        /// </summary>
        private static BuffInstance PickUnequipped(DungeonBuffState st,
                                                    DungeonBuffDef def, int template)
        {
            if (st == null || def == null) return null;

            for (int i = 0; i < st.Owned.Count; i++)
            {
                BuffInstance inst = st.Owned[i];
                if (inst == null || inst.def == null || inst.def.id != def.id) continue;
                if (st.IsEquipped(template, inst)) continue;

                return inst;
            }

            // 全装上了：给个占位，保持编辑对象不变
            return new BuffInstance(def, 1);
        }

        /// <summary>背包里某种插件总共有几份。</summary>
        private static int CountOwned(DungeonBuffState st, DungeonBuffDef def)
        {
            if (st == null || def == null) return 0;

            int n = 0;

            for (int i = 0; i < st.Owned.Count; i++)
            {
                BuffInstance inst = st.Owned[i];
                if (inst != null && inst.def != null && inst.def.id == def.id) n++;
            }

            return n;
        }

        /// <summary>某个模板上已经装了几份该插件。</summary>
        private static int CountUsedOnTemplate(DungeonBuffState st, int template, DungeonBuffDef def)
        {
            if (st == null || def == null) return 0;

            var list = st.SlotsOf(template, def.color);
            if (list == null) return 0;

            int n = 0;

            for (int i = 0; i < list.Count; i++)
            {
                BuffInstance inst = list[i];
                if (inst != null && inst.def != null && inst.def.id == def.id) n++;
            }

            return n;
        }

        private static string ShortTag(BuffInstance inst)
        {
            if (inst == null || inst.def == null) return "?";

            switch (inst.def.effectId)
            {
                case "dmg":     return "伤";
                case "rate":    return "速";
                case "mana":    return "耗";
                case "aoe":     return "爆";
                case "shotgun": return "霰";
                case "volley":  return "连";
                case "bounce":  return "跳";
                case "pierce":  return "穿";
                default:        return "?";
            }
        }

        private static string ShortName(BuffInstance inst)
        {
            if (inst == null || inst.def == null) return "";

            string n = inst.def.name;

            // "紫·爆炸弹头" -> 去掉颜色前缀，格子里只留效果名
            int dot = n.IndexOf('·');
            if (dot >= 0 && dot + 1 < n.Length) n = n.Substring(dot + 1);

            return n;
        }

        // ------------------------------------------------------------------ 样式

        private void DrawBorder(Rect r)
        {
            DrawBorder(r, borderColor);
        }

        /// <summary>用指定颜色画四边框（按插件颜色给格子描边）。</summary>
        private void DrawBorder(Rect r, Color color)
        {
            Color prev = GUI.color;
            GUI.color = color;

            GUI.DrawTexture(new Rect(r.x,        r.y,        r.width, 2f),       m_Pixel);
            GUI.DrawTexture(new Rect(r.x,        r.yMax - 2f, r.width, 2f),      m_Pixel);
            GUI.DrawTexture(new Rect(r.x,        r.y,        2f,      r.height), m_Pixel);
            GUI.DrawTexture(new Rect(r.xMax - 2f, r.y,       2f,      r.height), m_Pixel);

            GUI.color = prev;
        }

        private void EnsureStyles()
        {
            if (m_TitleStyle != null) return;

            Font f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (f == null) f = Resources.GetBuiltinResource<Font>("Arial.ttf");

            m_TitleStyle = new GUIStyle(GUI.skin.label)
            {
                font = f, fontSize = Mathf.RoundToInt(titleSize),
                alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold,
            };
            m_TitleStyle.normal.textColor = textColor;

            m_LabelStyle = new GUIStyle(GUI.skin.label)
            {
                font = f, fontSize = Mathf.RoundToInt(labelSize),
                alignment = TextAnchor.MiddleLeft,
            };
            m_LabelStyle.normal.textColor = textColor;

            m_SmallStyle = new GUIStyle(GUI.skin.label)
            {
                font = f, fontSize = Mathf.RoundToInt(smallSize),
                alignment = TextAnchor.MiddleLeft, wordWrap = true,
            };
            m_SmallStyle.normal.textColor = dimColor;

            // 插槽格子里的那个字：**严格居中**
            //
            // ★ padding / margin 必须清零：
            //   GUI.skin.label 默认带左内边距和行高边距，
            //   不清掉的话字会往右下偏，看起来就不像"这个格子里的东西"。
            m_SlotStyle = new GUIStyle(GUI.skin.label)
            {
                font = f, fontSize = 24,
                alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold,
                padding   = new RectOffset(0, 0, 0, 0),
                margin    = new RectOffset(0, 0, 0, 0),
                border    = new RectOffset(0, 0, 0, 0),
                clipping  = TextClipping.Overflow,
                wordWrap  = false,
            };
            m_SlotStyle.normal.textColor = textColor;

            // 颜色行标签（白 1/3）：字号每帧按格子大小重设
            //
            // ★ padding / margin 同样清零：skin 的默认内边距会把字推偏，
            //   行标签就会和它那一行的格子错开半个字。
            m_RowStyle = new GUIStyle(GUI.skin.label)
            {
                font = f, fontSize = 24,
                alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold,
                padding   = new RectOffset(0, 0, 0, 0),
                margin    = new RectOffset(0, 0, 0, 0),
                border    = new RectOffset(0, 0, 0, 0),
                clipping  = TextClipping.Overflow,
                wordWrap  = false,
            };
            m_RowStyle.normal.textColor = textColor;

            m_ClickArea = new GUIStyle(GUI.skin.button)
            {
                border  = new RectOffset(0, 0, 0, 0),
                margin  = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(0, 0, 0, 0),
            };
            m_ClickArea.normal.background = m_CardTex;
            m_ClickArea.hover.background  = m_CardHoverTex;
            m_ClickArea.active.background = m_CardHoverTex;
        }

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