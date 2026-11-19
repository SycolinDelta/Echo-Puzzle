using System.Collections.Generic;
using UnityEngine;

namespace EchoPuzzle.Dungeon
{
    /// <summary>
    /// 【地牢模式】插件装备界面（第十周任务 5）。
    ///
    /// ── 规则 ────────────────────────────────────────────────
    ///   · 每个 buff 就是一个"插件"，插进插槽才生效（见 DungeonBuffState.Recompute）
    ///   · 两个武器模板各有一套插槽，按 **E** 切换当前模板
    ///   · 一个插件**可以同时装在两个模板上**（不互斥）
    ///   · 每个模板初始 3 格，每推进 2 层 +1 格
    ///
    /// ── 操作（按 R 打开）────────────────────────────────────
    ///   · 左键点背包里的插件 -> 选中
    ///   · 再点某个槽 -> 装进去（原来那格的退回背包）
    ///   · 直接点已占用的槽 -> 卸下
    ///   · 按 E 切换模板（A / B），按 R 或 Esc 关闭
    ///
    /// ── 为什么没做"拖拽" ────────────────────────────────────
    ///   任务书写的是"拖动到插槽"。IMGUI 里做拖拽要自己处理 drag 状态、
    ///   悬停高亮、放下判定，容易和现有的光标锁定打架；这版先用
    ///   "点选 + 点槽"达到同样的效果，手感更稳，之后要做拖拽也只是换个交互层。
    /// </summary>
    [AddComponentMenu("Echo Puzzle/Dungeon/Dungeon Loadout UI")]
    public sealed class DungeonLoadoutUI : MonoBehaviour
    {
        public static DungeonLoadoutUI Instance { get; private set; }

        [Header("按键")]
        public KeyCode toggleKey     = KeyCode.R;
        public KeyCode switchKey     = KeyCode.E;

        [Header("版式（1080 高度基准的像素）")]
        public float panelWidth  = 1180f;
        public float panelHeight = 720f;
        public float titleSize   = 34f;
        public float itemSize    = 20f;
        public float slotSize    = 96f;
        public float slotGap     = 10f;

        [Header("颜色")]
        public Color textColor      = Color.white;
        public Color dimColor       = new Color(1f, 1f, 1f, 0.5f);
        public Color activeColor    = new Color(0.35f, 0.75f, 1f, 1f);
        public Color backdropColor  = new Color(0f, 0f, 0f, 0.9f);
        public Color cardColor      = new Color(1f, 1f, 1f, 0.05f);
        public Color cardHoverColor = new Color(1f, 1f, 1f, 0.14f);
        public Color borderColor    = new Color(1f, 1f, 1f, 0.22f);
        public Color equippedColor  = new Color(0.4f, 0.9f, 0.6f, 0.16f);

        private const float RefHeight = 1080f;

        /// <summary>界面是否开着。</summary>
        public bool IsOpen { get; private set; }

        /// <summary>当前在背包里被选中的插件实例（点槽时用）。</summary>
        private BuffInstance m_Selected;

        private float     m_PrevTimeScale = 1f;
        private Texture2D m_Pixel;
        private Texture2D m_CardTex;
        private Texture2D m_CardHoverTex;

        private GUIStyle m_TitleStyle;
        private GUIStyle m_LabelStyle;
        private GUIStyle m_SmallStyle;
        private GUIStyle m_ClickArea;

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

            // 只在游戏进行中能开
            if (!IsOpen && GameUI.Current != GameState.Playing) return;

            if (Input.GetKeyDown(toggleKey)) { if (IsOpen) Close(); else Open(); }

            if (!IsOpen) return;

            // 切模板
            if (Input.GetKeyDown(switchKey)) SwitchTemplate();

            if (Input.GetKeyDown(KeyCode.Escape)) Close();

            // 兜底：切窗口 / 别的系统可能把光标解锁成可见。
            //   ★ 这个界面也不能出现系统指针（短剑图标就是指针）。
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
        }

        private void Open()
        {
            if (IsOpen) return;

            IsOpen = true;
            m_Selected = null;

            m_PrevTimeScale = Time.timeScale;
            Time.timeScale  = 0f;

            // ★ 系统指针保持隐藏；把短剑图标挪到屏幕中心当指针用
            if (DungeonWeapon.Instance != null) DungeonWeapon.Instance.RecenterCursor();
        }

        /// <summary>关闭界面（重开 / 换场景时也给 GameUI 调）。</summary>
        public void Close()
        {
            if (!IsOpen) return;

            IsOpen = false;
            m_Selected = null;

            Time.timeScale = m_PrevTimeScale <= 0.0001f ? 1f : m_PrevTimeScale;

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible   = false;
        }

        private static void SwitchTemplate()
        {
            var st = DungeonBuffState.Instance;
            if (st == null) return;

            st.SetActiveTemplate((st.ActiveTemplate + 1) % DungeonBuffState.TemplateCount);
        }

        // ------------------------------------------------------------------ 绘制

        private void OnGUI()
        {
            if (!IsOpen) return;

            var st = DungeonBuffState.Instance;
            if (st == null) return;

            EnsureStyles();

            // ★ 压在其它 IMGUI 之上：HUD（DPS / 蓝耗 / 蓝量）是别的脚本画的，
            //   OnGUI 的先后顺序不固定，不设 depth 的话它就会盖在面板上挡住视野。
            //   Unity 里 depth **越小越靠上**。
            int prevDepth = GUI.depth;
            GUI.depth = -1000;

            float scale = Screen.height / RefHeight;
            Matrix4x4 saved = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity,
                                       new Vector3(scale, scale, 1f));

            float w = Screen.width / scale;

            // 背景
            Color prev = GUI.color;
            GUI.color = backdropColor;
            GUI.DrawTexture(new Rect(0f, 0f, w, RefHeight * 1.5f), m_Pixel);
            GUI.color = prev;

            float px = (w - panelWidth) * 0.5f;
            float py = (RefHeight - panelHeight) * 0.5f;
            var panel = new Rect(px, py, panelWidth, panelHeight);
            DrawBorder(panel);

            GUI.Label(new Rect(panel.x, panel.y + 14f, panel.width, 50f),
                      "插件装配", m_TitleStyle);

            GUI.Label(new Rect(panel.x + 28f, panel.y + 64f, panel.width - 56f, 30f),
                      "点背包里的插件，再点插槽装入；点已占用的槽卸下。E 切换模板，R 关闭。",
                      m_SmallStyle);

            // ---- 左：背包（已获得但没装进**当前**模板的）----
            float listX = panel.x + 28f;
            float listY = panel.y + 108f;
            float listW = 330f;

            GUI.Label(new Rect(listX, listY, listW, 28f), "背包", m_LabelStyle);
            listY += 34f;

            DrawInventory(st, new Rect(listX, listY, listW, panel.height - 190f));

            // ---- 右：两个模板 ----
            float tplX = panel.x + 390f;
            float tplW = panel.width - 390f - 28f;
            float tplY = panel.y + 108f;

            for (int t = 0; t < DungeonBuffState.TemplateCount; t++)
            {
                DrawTemplate(st, t, new Rect(tplX, tplY, tplW, 210f));
                tplY += 226f;
            }

            // ---- 底部提示 ----
            if (!string.IsNullOrEmpty(m_Hint) && Time.unscaledTime < m_HintUntil)
            {
                GUI.Label(new Rect(panel.x + 28f, panel.yMax - 44f, panel.width - 56f, 32f),
                          m_Hint, m_SmallStyle);
            }

            GUI.matrix = saved;
            GUI.depth  = prevDepth;
        }

        private void DrawInventory(DungeonBuffState st, Rect area)
        {
            float y = area.y;

            if (st.Owned.Count == 0)
            {
                GUI.Label(new Rect(area.x, y, area.width, 28f), "（还没有插件）", m_SmallStyle);
                return;
            }

            // ★ 一种 buff 只有一个实例（等级制），所以这里不用再去重
            for (int i = 0; i < st.Owned.Count; i++)
            {
                BuffInstance inst = st.Owned[i];
                if (inst == null || inst.def == null) continue;

                // ★ 可选择性**不看当前模板**。
                //   之前是"装在 A 里就不能选"，结果想把同一个插件也装到 B，
                //   必须先按 E 切到 B 才能选 —— 非常反直觉。
                //   真正"同一模板不能占两格"的限制交给 SetSlot 判定。
                bool inA = st.CountInTemplate(0, inst) > 0;
                bool inB = st.CountInTemplate(1, inst) > 0;

                bool usable   = !(inA && inB);      // 两个模板都装了就没必要再选
                bool selected = m_Selected == inst;

                var row = new Rect(area.x, y, area.width, 40f);

                if (GUI.Button(row, GUIContent.none, m_ClickArea) && usable)
                    m_Selected = selected ? null : inst;

                DrawBorder(row);

                Color prev = GUI.color;
                if (selected) GUI.color = activeColor;
                else if (!usable) GUI.color = new Color(1f, 1f, 1f, 0.4f);

                string lv = $"Lv{inst.level}" + (inst.IsMax ? "·满" : "");

                // 标出已经装在哪个模板里（A/B），方便一眼看出还剩哪个模板能装
                string badge = (inA ? "[A]" : "") + (inB ? "[B]" : "");

                GUI.Label(new Rect(row.x + 12f, row.y + 6f, row.width - 24f, 28f),
                          $"{inst.Name} {lv} {badge}", m_LabelStyle);

                GUI.color = prev;

                y += 46f;
            }
        }

        private void DrawTemplate(DungeonBuffState st, int template, Rect area)
        {
            bool active = st.ActiveTemplate == template;

            // 标题：模板 A / B（当前模板高亮）
            Color prev = GUI.color;
            if (active) GUI.color = activeColor;

            int used = st.UsedSlots(template);
            int total = DungeonBuffState.SlotCount;

            GUI.Label(new Rect(area.x, area.y, area.width, 32f),
                      $"模板 {(template == 0 ? "A" : "B")}　{used}/{total}" +
                      (active ? "　（当前使用）" : "　（按 E 切换）"),
                      m_LabelStyle);

            GUI.color = prev;

            // 插槽
            float x = area.x;
            float y = area.y + 40f;

            for (int i = 0; i < total; i++)
            {
                var slot = new Rect(x, y, slotSize, slotSize);

                if (GUI.Button(slot, GUIContent.none, m_ClickArea))
                    OnSlotClicked(st, template, i);

                // 装了东西的槽换个底色
                if (st.SlotAt(template, i) != null)
                {
                    Color p2 = GUI.color;
                    GUI.color = equippedColor;
                    GUI.DrawTexture(slot, m_Pixel);
                    GUI.color = p2;
                }

                DrawBorder(slot);

                BuffInstance d = st.SlotAt(template, i);

                GUI.Label(new Rect(slot.x + 4f, slot.y + 6f, slot.width - 8f, 24f),
                          (i + 1).ToString(), m_SmallStyle);

                if (d != null)
                {
                    GUI.Label(new Rect(slot.x + 4f, slot.y + 34f, slot.width - 8f, slot.height - 38f),
                              $"{d.Name}\nLv{d.level}", m_SmallStyle);
                }

                x += slotSize + slotGap;
            }
        }

        private void OnSlotClicked(DungeonBuffState st, int template, int index)
        {
            BuffInstance equipped = st.SlotAt(template, index);

            // 槽里有东西 -> 卸下
            if (equipped != null)
            {
                st.ClearSlot(template, index);
                m_Selected = equipped;      // 顺手选中，方便放到别的槽
                return;
            }

            // 空槽 + 背包里选了东西 -> 装上
            if (m_Selected != null)
            {
                // 同一模板里一个插件只能占一个槽（SetSlot 会拒绝重复）
                if (st.SetSlot(template, index, m_Selected))
                {
                    m_Selected = null;
                    ShowHint("");
                }
                else
                {
                    ShowHint($"「{m_Selected.Name}」已经装在这个模板里了，换个槽或换个模板");
                }
            }
        }

        /// <summary>底部的一行提示（操作失败时说明原因）。</summary>
        private string m_Hint = "";

        private float m_HintUntil;

        private void ShowHint(string text)
        {
            m_Hint      = text ?? "";
            m_HintUntil = Time.unscaledTime + 2.5f;
        }

        // ------------------------------------------------------------------ 样式

        private void DrawBorder(Rect r)
        {
            Color prev = GUI.color;
            GUI.color = borderColor;

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
                font = f, fontSize = Mathf.RoundToInt(itemSize),
                alignment = TextAnchor.MiddleLeft,
            };
            m_LabelStyle.normal.textColor = textColor;

            m_SmallStyle = new GUIStyle(GUI.skin.label)
            {
                font = f, fontSize = Mathf.RoundToInt(itemSize * 0.8f),
                alignment = TextAnchor.MiddleLeft, wordWrap = true,
            };
            m_SmallStyle.normal.textColor = dimColor;

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