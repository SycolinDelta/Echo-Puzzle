using UnityEngine;

namespace EchoPuzzle.Dungeon
{
    /// <summary>
    /// 【地牢模式】商店界面（第十周任务 3）。
    ///
    /// ── 卖什么 ──────────────────────────────────────────────
    ///   · 生命药水：回复 1 点生命（满血时不能买）
    ///   · 能量药水：回复 40 点蓝量（满蓝时不能买）
    ///   价格 = 基础价 + 每层加价 × (当前层 - 1)，层数越高越贵。
    ///
    /// ── 交互 ────────────────────────────────────────────────
    ///   · 走进矩阵里数字 5 的格子自动打开（DungeonShopZone 调 <see cref="OpenFor"/>）
    ///   · 打开时冻结世界（timeScale = 0）、解锁光标
    ///   · 点按钮购买，点「离开商店」或按 Esc 关闭
    ///   · 走出格子也会自动关闭
    ///
    /// ── 和探秘模式的关系 ────────────────────────────────────
    ///   ★ 只在地牢模式里存在；探秘模式没有商店格。
    /// </summary>
    [AddComponentMenu("Echo Puzzle/Dungeon/Dungeon Shop UI")]
    public sealed class DungeonShopUI : MonoBehaviour
    {
        public static DungeonShopUI Instance { get; private set; }

        [Header("价格")]
        [Tooltip("生命药水的基础价")]
        [Min(0)] public int hpPriceBase = 6;

        [Tooltip("每推进一层，生命药水加价多少")]
        [Min(0)] public int hpPricePerFloor = 2;

        [Tooltip("能量药水的基础价")]
        [Min(0)] public int manaPriceBase = 4;

        [Tooltip("每推进一层，能量药水加价多少")]
        [Min(0)] public int manaPricePerFloor = 1;

        [Header("effects")]
        [Tooltip("生命药水回复多少点生命")]
        [Min(1)] public int hpRestore = 1;

        [Tooltip("能量药水回复多少点蓝量")]
        [Min(1)] public int manaRestore = 40;

        [Header("版式（1080 高度基准的像素）")]
        public float panelWidth = 760f;
        public float panelHeight = 420f;
        public float titleSize  = 40f;
        public float itemSize   = 26f;
        public float descSize   = 19f;
        public float priceSize  = 24f;

        [Header("颜色")]
        public Color textColor     = Color.white;
        public Color dimColor      = new Color(1f, 1f, 1f, 0.55f);
        public Color goldColor     = new Color(1f, 0.85f, 0.35f, 1f);
        public Color backdropColor = new Color(0f, 0f, 0f, 0.86f);
        public Color cardColor     = new Color(1f, 1f, 1f, 0.05f);
        public Color cardHoverColor = new Color(1f, 1f, 1f, 0.14f);
        public Color borderColor   = new Color(1f, 1f, 1f, 0.22f);

        private const float RefHeight = 1080f;

        /// <summary>界面是否开着。</summary>
        public bool IsOpen { get; private set; }

        private DungeonShopZone m_Owner;

        private float     m_PrevTimeScale = 1f;
        private Texture2D m_Pixel;
        private Texture2D m_CardTex;
        private Texture2D m_CardHoverTex;

        private GUIStyle m_TitleStyle;
        private GUIStyle m_ItemStyle;
        private GUIStyle m_DescStyle;
        private GUIStyle m_PriceStyle;
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

        // ------------------------------------------------------------------ 开关

        /// <summary>进入商店格时调用。</summary>
        public static void OpenFor(DungeonShopZone zone)
        {
            DungeonShopUI ui = Ensure();
            if (ui == null) return;

            ui.OpenInternal(zone);
        }

        /// <summary>离开商店格时调用（只有当前这间店开着才关）。</summary>
        public static void CloseIfOwner(DungeonShopZone zone)
        {
            if (Instance == null || !Instance.IsOpen) return;
            if (Instance.m_Owner != zone) return;

            Instance.Close();
        }

        private static DungeonShopUI Ensure()
        {
            if (Instance != null) return Instance;

            var go = new GameObject("EchoPuzzle_DungeonShopUI");
            return go.AddComponent<DungeonShopUI>();
        }

        private void OpenInternal(DungeonShopZone zone)
        {
            if (IsOpen) return;
            if (GameUI.Current != GameState.Playing) return;

            m_Owner = zone;
            IsOpen  = true;

            m_PrevTimeScale = Time.timeScale;
            Time.timeScale  = 0f;

            // ★★ 光标保持"锁定 + 隐藏" ★★
            //
            //   需求："指针的位置始终在短剑处，并且始终不出现，
            //         看起来像是短剑就是指针一样。"
            //
            //   ★ 以前这里设成 None + 可见，于是商店里会出现一个真实的系统指针，
            //     而短剑图标还停留在原来的位置 —— 两个"指针"分开跑，
            //     看起来非常怪。
            //   ★ 现在系统指针全程不出现，屏幕上的短剑就是唯一指针。
            //
            //   ⚠ IMGUI 的 GUI.Button 在 Locked 模式下把鼠标位置当作屏幕中心，
            //     所以打开商店时把虚拟指针也挪到中心，
            //     短剑正好落在"实际可点击的位置"上。
            if (DungeonWeapon.Instance != null) DungeonWeapon.Instance.RecenterCursor();
        }

        private void Close()
        {
            if (!IsOpen) return;

            IsOpen  = false;
            m_Owner = null;

            Time.timeScale = m_PrevTimeScale <= 0.0001f ? 1f : m_PrevTimeScale;

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible   = false;
        }

        private void Update()
        {
            if (!IsOpen) return;

            // 兜底：切窗口 / 别的系统可能把光标解锁成可见 —— 这里每帧强制压回去。
            //   ★ 商店里也不能出现系统指针（短剑就是指针）。
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

            if (Input.GetKeyDown(KeyCode.Escape)) Close();
        }

        // ------------------------------------------------------------------ 价格

        private int CurrentStage
        {
            get
            {
                var gen = FindObjectOfType<DungeonMapGenerator>();
                return gen != null ? Mathf.Max(1, gen.CurrentStage) : 1;
            }
        }

        private int HpPrice    => Mathf.Max(0, hpPriceBase   + hpPricePerFloor   * (CurrentStage - 1));
        private int ManaPrice  => Mathf.Max(0, manaPriceBase + manaPricePerFloor * (CurrentStage - 1));

        // ------------------------------------------------------------------ 购买

        private void BuyHealth()
        {
            var h = FindObjectOfType<PlayerHealth>();
            if (h == null) return;
            if (h.Current >= h.maxHealth) return;          // 满血：不给买

            var gold = DungeonCurrency.Instance;
            if (gold == null || !gold.TrySpend(HpPrice)) return;

            h.Heal(Mathf.Max(1, hpRestore));
        }

        private void BuyMana()
        {
            var mana = DungeonMana.Instance;
            if (mana == null) return;
            if (mana.Current >= mana.Max) return;          // 满蓝：不给买

            var gold = DungeonCurrency.Instance;
            if (gold == null || !gold.TrySpend(ManaPrice)) return;

            mana.Restore(Mathf.Max(1, manaRestore));
        }

        // ------------------------------------------------------------------ 绘制

        private void OnGUI()
        {
            if (!IsOpen) return;

            EnsureStyles();

            // ★ 压在 HUD 之上（depth 越小越靠上），否则左上角的属性文字会盖在面板上
            int prevDepth = GUI.depth;
            GUI.depth = -1000;

            float scale = Screen.height / RefHeight;
            Matrix4x4 saved = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity,
                                       new Vector3(scale, scale, 1f));

            float w = Screen.width / scale;

            // 全屏黑底
            Color prev = GUI.color;
            GUI.color = backdropColor;
            GUI.DrawTexture(new Rect(0f, 0f, w, RefHeight * 1.5f), m_Pixel);
            GUI.color = prev;

            float px = (w - panelWidth) * 0.5f;
            float py = (RefHeight - panelHeight) * 0.5f;

            var panel = new Rect(px, py, panelWidth, panelHeight);
            DrawBorder(panel);

            GUI.Label(new Rect(panel.x, panel.y + 18f, panel.width, 56f), "商店", m_TitleStyle);

            // 金币
            int goldNow = DungeonCurrency.Instance != null ? DungeonCurrency.Instance.Gold : 0;
            GUI.Label(new Rect(panel.x, panel.y + 20f, panel.width - 28f, 40f),
                      "金币 " + goldNow, m_PriceStyle);

            var health = FindObjectOfType<PlayerHealth>();
            var manaSys = DungeonMana.Instance;

            float itemH = 92f;
            float y = panel.y + 84f;

            // ---- 生命药水 ----
            DrawItem(new Rect(panel.x + 24f, y, panel.width - 48f, itemH),
                     "生命药水",
                     health != null ? $"回复 {hpRestore} 点生命（当前 {health.Current}/{health.maxHealth}）"
                                    : $"回复 {hpRestore} 点生命",
                     HpPrice,
                     health != null && health.Current < health.maxHealth,
                     BuyHealth);

            y += itemH + 14f;

            // ---- 能量药水 ----
            DrawItem(new Rect(panel.x + 24f, y, panel.width - 48f, itemH),
                     "能量药水",
                     manaSys != null ? $"回复 {manaRestore} 点蓝量（当前 {manaSys.Current:F0}/{manaSys.Max}）"
                                     : $"回复 {manaRestore} 点蓝量",
                     ManaPrice,
                     manaSys != null && manaSys.Current < manaSys.Max,
                     BuyMana);

            // ---- 离开 ----
            var closeBtn = new Rect(panel.x + (panel.width - 220f) * 0.5f,
                                    panel.y + panel.height - 74f, 220f, 48f);

            if (GUI.Button(closeBtn, GUIContent.none, m_ClickArea)) Close();
            DrawBorder(closeBtn);
            GUI.Label(closeBtn, "离开商店（Esc）", m_ItemStyle);

            GUI.matrix = saved;
            GUI.depth  = prevDepth;
        }

        private void DrawItem(Rect r, string name, string desc, int price, bool canBuy, System.Action buy)
        {
            bool clicked = GUI.Button(r, GUIContent.none, m_ClickArea);
            DrawBorder(r);

            // 买不起：整行变暗
            int goldNow = DungeonCurrency.Instance != null ? DungeonCurrency.Instance.Gold : 0;
            bool affordable = goldNow >= price && canBuy;

            Color prev = GUI.color;
            if (!affordable) GUI.color = new Color(1f, 1f, 1f, 0.4f);

            GUI.Label(new Rect(r.x + 20f, r.y + 12f, r.width - 260f, 34f), name, m_ItemStyle);
            GUI.Label(new Rect(r.x + 20f, r.y + 48f, r.width - 260f, 30f), desc, m_DescStyle);

            string priceText = canBuy ? $"{price} 金币" : "已满";
            GUI.Label(new Rect(r.xMax - 232f, r.y + 26f, 200f, 40f), priceText,
                      canBuy ? m_PriceStyle : m_DescStyle);

            GUI.color = prev;

            if (clicked && affordable) buy?.Invoke();
        }

        private void DrawBorder(Rect r)
        {
            Color prev = GUI.color;
            GUI.color = borderColor;

            GUI.DrawTexture(new Rect(r.x,        r.y,        r.width, 2f),         m_Pixel);
            GUI.DrawTexture(new Rect(r.x,        r.yMax - 2f, r.width, 2f),        m_Pixel);
            GUI.DrawTexture(new Rect(r.x,        r.y,        2f,      r.height),   m_Pixel);
            GUI.DrawTexture(new Rect(r.xMax - 2f, r.y,       2f,      r.height),   m_Pixel);

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

            m_ItemStyle = new GUIStyle(GUI.skin.label)
            {
                font = f, fontSize = Mathf.RoundToInt(itemSize),
                alignment = TextAnchor.MiddleLeft,
            };
            m_ItemStyle.normal.textColor = textColor;

            m_DescStyle = new GUIStyle(GUI.skin.label)
            {
                font = f, fontSize = Mathf.RoundToInt(descSize),
                alignment = TextAnchor.MiddleLeft,
            };
            m_DescStyle.normal.textColor = dimColor;

            m_PriceStyle = new GUIStyle(GUI.skin.label)
            {
                font = f, fontSize = Mathf.RoundToInt(priceSize),
                alignment = TextAnchor.MiddleRight, fontStyle = FontStyle.Bold,
            };
            m_PriceStyle.normal.textColor = goldColor;

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