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

        private BuffInstance m_Selected;
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

            if (Input.GetKeyDown(toggleKey) || Input.GetKeyDown(KeyCode.Escape)) Close();

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

            // 底部提示
            if (!string.IsNullOrEmpty(m_Hint) && Time.unscaledTime < m_HintUntil)
            {
                GUI.Label(new Rect(panel.x + 24f, panel.yMax - 40f, panel.width - 48f, 30f),
                          m_Hint, m_SmallStyle);
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

                int idx = defs.FindIndex(x => x != null && x.id == inst.def.id);
                if (idx >= 0) { counts[idx]++; continue; }

                defs.Add(inst.def);
                counts.Add(1);
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

                int inA = 0, inB = 0;
                for (int k = 0; k < st.Owned.Count; k++)
                {
                    BuffInstance inst = st.Owned[k];
                    if (inst == null || inst.def == null || inst.def.id != d.id) continue;

                    if (st.IsEquipped(0, inst)) inA++;
                    if (st.IsEquipped(1, inst)) inB++;
                }

                bool usable   = pick != null;
                bool selected = m_Selected != null && m_Selected.def != null && m_Selected.def.id == d.id;

                var row = new Rect(area.x, y, area.width, 44f);

                if (GUI.Button(row, GUIContent.none, m_ClickArea) && usable)
                {
                    m_Selected = selected ? null : pick;
                }

                DrawBorder(row);

                // 左侧颜色条
                Color prev = GUI.color;
                GUI.color = SlotRules.UiColor(d.color);
                GUI.DrawTexture(new Rect(row.x + 2f, row.y + 2f, 6f, row.height - 4f), m_Pixel);

                if (selected) GUI.color = activeColor;
                else if (!usable) GUI.color = new Color(1f, 1f, 1f, 0.45f);

                string badge = (inA > 0 ? "[A]" : "") + (inB > 0 ? "[B]" : "");
                GUI.Label(new Rect(row.x + 16f, row.y + 8f, row.width - 24f, 30f),
                          $"{d.name} ×{counts[i]} {badge}", m_LabelStyle);

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
            if (active) GUI.color = activeColor;

            GUI.Label(new Rect(area.x + 16f, area.y + 8f, area.width - 32f, 38f),
                      $"模板 {(template == 0 ? "A" : "B")}　{SlotRules.NameOf(st.Tier)}级" +
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
            m_SlotStyle.fontSize = Mathf.RoundToInt(slot * 0.55f);

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

                GUI.Label(new Rect(area.x + padX, y + slot * 0.5f - m_RowStyle.fontSize * 0.5f,
                                   labelW - 8f, slot * 0.8f),
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

                    bool clicked = GUI.Button(box, GUIContent.none, m_ClickArea);

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

                    if (clicked) OnSlotClicked(st, template, color, i, equipped);

                    bx += slot + gap;
                }

                y += lines * slot + (lines - 1) * gap + slot * 0.22f;
            }
        }

        private void OnSlotClicked(DungeonBuffState st, int template, PluginColor color, int index, BuffInstance equipped)
        {
            // 有东西 -> 拆下
            if (equipped != null)
            {
                st.Unequip(template, color, index);
                ShowHint($"拆下 {equipped.Name}");
                return;
            }

            // 空格 -> 把选中的插件装进来
            if (m_Selected == null)
            {
                ShowHint("先在左边点一个插件");
                return;
            }

            if (m_Selected.def.color != color)
            {
                ShowHint($"{m_Selected.Name} 只能插进 {SlotRules.NameOf(m_Selected.def.color)}槽");
                return;
            }

            if (st.TryEquip(template, m_Selected, out string reason))
            {
                m_Selected = null;
                ShowHint("");
            }
            else
            {
                ShowHint(reason ?? "装不下");
            }
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
            m_RowStyle = new GUIStyle(GUI.skin.label)
            {
                font = f, fontSize = 24,
                alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold,
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