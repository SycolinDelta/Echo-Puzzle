using System.Collections.Generic;
using UnityEngine;

namespace EchoPuzzle.Dungeon
{
    /// <summary>
    /// 【地牢模式】商店界面（第十一周版）。
    ///
    /// ── 卖什么 ──────────────────────────────────────────────
    ///   · 生命药水 40 金币：+1 生命（满血不能买）
    ///   · 能量药水 40 金币：+40 蓝（满蓝不能买）
    ///   · **橙色插件** 150 金币：随机陈列 3 个，只能买其中一个；花 40 可以刷新陈列
    ///   · **模板升级** 300 金币：模板升一级，每个商店最多升一次，全局只有 2 次
    ///
    /// ── 出现规则（任务书）───────────────────────────────────
    ///   · 0.5 概率刷出橙色插件
    ///   · 0.3 概率刷出模板升级
    ///   · 两者**互斥**：一次商店只会出现其中一种（剩下 0.2 只有药水）
    ///
    /// ── 交互 ────────────────────────────────────────────────
    ///   走进矩阵里数字 5 的格子自动打开；Esc 或"离开商店"关闭；走出去也会关。
    /// </summary>
    [AddComponentMenu("Echo Puzzle/Dungeon/Dungeon Shop UI")]
    public sealed class DungeonShopUI : MonoBehaviour
    {
        public static DungeonShopUI Instance { get; private set; }

        public enum OfferKind { None, OrangePlugin, TemplateUpgrade }

        [Header("价格（第十一周）")]
        [Tooltip("生命药水价格")]
        [Min(0)] public int hpPrice = 40;

        [Tooltip("能量药水价格")]
        [Min(0)] public int manaPrice = 40;

        [Tooltip("橙色插件价格")]
        [Min(0)] public int orangePrice = 150;

        [Tooltip("刷新橙色插件陈列的价格")]
        [Min(0)] public int orangeRefreshPrice = 40;

        [Tooltip("模板升级价格")]
        [Min(0)] public int upgradePrice = 300;

        [Header("出现概率")]
        [Range(0f, 1f)] public float orangeChance = 0.5f;
        [Range(0f, 1f)] public float upgradeChance = 0.3f;

        [Header("效果")]
        [Min(1)] public int hpRestore = 1;
        [Min(1)] public int manaRestore = 40;

        [Header("版式（1080 高度基准的像素）")]
        public float panelWidth  = 1020f;
        public float panelHeight = 820f;
        public float titleSize   = 46f;
        public float itemSize    = 26f;
        public float descSize    = 20f;
        public float priceSize   = 26f;

        [Header("颜色")]
        public Color textColor      = Color.white;
        public Color dimColor       = new Color(1f, 1f, 1f, 0.55f);
        public Color goldColor      = new Color(1f, 0.85f, 0.35f, 1f);
        public Color backdropColor  = new Color(0f, 0f, 0f, 0.96f);
        public Color cardColor      = new Color(1f, 1f, 1f, 0.05f);
        public Color cardHoverColor = new Color(1f, 1f, 1f, 0.14f);
        public Color borderColor    = new Color(1f, 1f, 1f, 0.22f);

        private const float RefHeight = 1080f;

        public bool IsOpen { get; private set; }

        // ---- 本次商店的陈列 ----
        public OfferKind Offer { get; private set; } = OfferKind.None;

        private readonly List<DungeonBuffDef> m_OrangeOptions = new List<DungeonBuffDef>(3);

        private bool m_OrangeBought;
        private bool m_UpgradeBought;

        /// <summary>已经掷过陈列的商店格实例 id（0 = 还没掷过）。</summary>
        private int m_OfferZoneId;

        private DungeonShopZone m_Owner;
        private float           m_PrevTimeScale = 1f;

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

        // ------------------------------------------------------------------ 开关

        public static void OpenFor(DungeonShopZone zone)
        {
            DungeonShopUI ui = Ensure();
            if (ui == null) return;

            ui.OpenInternal(zone);
        }

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

            // ★★ 同一个商店格**只掷一次**陈列 ★★
            //
            //   之前是每次打开都 RollOffer()，于是玩家走出去再走进来就能
            //   免费换一批陈列 —— 等于无限刷新。现在按"商店格实例"记忆：
            //   同一格永远给同一批货，"已购买"状态也一起保留。
            //   换层 / 重开会生成新的商店格（实例 id 不同），自然会重新掷。
            if (m_OfferZoneId != zone.GetInstanceID())
            {
                m_OfferZoneId = zone.GetInstanceID();
                RollOffer();
            }

            m_PrevTimeScale = Time.timeScale;
            Time.timeScale  = 0f;

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible   = true;
        }

        private void Close()
        {
            if (!IsOpen) return;

            IsOpen  = false;
            m_Owner = null;

            Time.timeScale = m_PrevTimeScale <= 0.0001f ? 1f : m_PrevTimeScale;

            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible   = false;
        }

        private void Update()
        {
            if (!IsOpen) return;

            if (Cursor.lockState != CursorLockMode.None || !Cursor.visible)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible   = true;
            }

            if (Input.GetKeyDown(KeyCode.Escape)) Close();
        }

        // ------------------------------------------------------------------ 陈列

        /// <summary>每次进商店掷一次：橙插件 / 模板升级 / 都没有（互斥）。</summary>
        private void RollOffer()
        {
            m_OrangeBought  = false;
            m_UpgradeBought = false;

            Offer = OfferKind.None;

            float r = Random.value;

            if (r < orangeChance)
            {
                Offer = OfferKind.OrangePlugin;
                RollOrangeOptions();
            }
            else if (r < orangeChance + upgradeChance)
            {
                Offer = OfferKind.TemplateUpgrade;
            }
        }

        /// <summary>随机陈列 3 个不同的橙插件（已经拿满的会被排除）。</summary>
        private void RollOrangeOptions()
        {
            m_OrangeOptions.Clear();

            var pool = DungeonBuffPool.ByColor(PluginColor.Orange);
            var st   = DungeonBuffState.Instance;

            if (pool == null || pool.Count == 0) { Offer = OfferKind.None; return; }

            // 洗牌，保证三个不重复
            for (int i = 0; i < pool.Count; i++)
            {
                int j = Random.Range(i, pool.Count);
                (pool[i], pool[j]) = (pool[j], pool[i]);
            }

            for (int i = 0; i < pool.Count && m_OrangeOptions.Count < 3; i++)
            {
                DungeonBuffDef d = pool[i];
                if (d == null) continue;

                // 达到橙色"同效果最多 2 个"就不再卖
                if (st != null && st.StacksOf(d) >= d.maxStacks) continue;

                m_OrangeOptions.Add(d);
            }

            if (m_OrangeOptions.Count == 0) Offer = OfferKind.None;
        }

        // ------------------------------------------------------------------ 购买

        private void BuyHealth()
        {
            var h = FindObjectOfType<PlayerHealth>();
            if (h == null) return;
            if (h.Current >= h.maxHealth) return;

            var gold = DungeonCurrency.Instance;
            if (gold == null || !gold.TrySpend(hpPrice)) return;

            h.Heal(Mathf.Max(1, hpRestore));
        }

        private void BuyMana()
        {
            var mana = DungeonMana.Instance;
            if (mana == null) return;
            if (mana.Current >= mana.Max) return;

            var gold = DungeonCurrency.Instance;
            if (gold == null || !gold.TrySpend(manaPrice)) return;

            mana.Restore(Mathf.Max(1, manaRestore));
        }

        private void BuyOrange(DungeonBuffDef def)
        {
            if (def == null || m_OrangeBought) return;

            var gold = DungeonCurrency.Instance;
            if (gold == null || !gold.TrySpend(orangePrice)) return;

            var st = DungeonBuffState.Instance;
            if (st != null) st.Add(def);

            m_OrangeBought = true;
        }

        private void RefreshOrange()
        {
            if (m_OrangeBought) return;

            var gold = DungeonCurrency.Instance;
            if (gold == null || !gold.TrySpend(orangeRefreshPrice)) return;

            RollOrangeOptions();
        }

        private void BuyUpgrade()
        {
            if (m_UpgradeBought) return;

            var st = DungeonBuffState.Instance;
            if (st == null || st.ShopUpgradesLeft <= 0) return;

            var gold = DungeonCurrency.Instance;
            if (gold == null || !gold.TrySpend(upgradePrice)) return;

            if (st.UpgradeTierFromShop()) m_UpgradeBought = true;
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

            Color prev = GUI.color;
            GUI.color = backdropColor;
            GUI.DrawTexture(new Rect(0f, 0f, w, RefHeight * 1.6f), m_Pixel);
            GUI.color = prev;

            float px = (w - panelWidth) * 0.5f;
            float py = (RefHeight - panelHeight) * 0.5f;
            var panel = new Rect(px, py, panelWidth, panelHeight);
            DrawBorder(panel);

            GUI.Label(new Rect(panel.x, panel.y + 16f, panel.width, 60f), "商店", m_TitleStyle);

            int goldNow = DungeonCurrency.Instance != null ? DungeonCurrency.Instance.Gold : 0;
            GUI.Label(new Rect(panel.x, panel.y + 20f, panel.width - 30f, 40f),
                      "金币 " + goldNow, m_PriceStyle);

            float x = panel.x + 24f;
            float y = panel.y + 96f;
            float rowW = panel.width - 48f;
            float rowH = 84f;

            var health  = FindObjectOfType<PlayerHealth>();
            var manaSys = DungeonMana.Instance;

            // ---- 药水 ----
            DrawItem(new Rect(x, y, rowW, rowH), "生命药水",
                     health != null ? $"回复 {hpRestore} 点生命（当前 {health.Current}/{health.maxHealth}）"
                                    : $"回复 {hpRestore} 点生命",
                     hpPrice,
                     health != null && health.Current < health.maxHealth,
                     BuyHealth);

            y += rowH + 12f;

            DrawItem(new Rect(x, y, rowW, rowH), "能量药水",
                     manaSys != null ? $"回复 {manaRestore} 点蓝量（当前 {manaSys.Current:F0}/{manaSys.Max:F0}）"
                                     : $"回复 {manaRestore} 点蓝量",
                     manaPrice,
                     manaSys != null && manaSys.Current < manaSys.Max,
                     BuyMana);

            y += rowH + 22f;

            // ---- 特供 ----
            switch (Offer)
            {
                case OfferKind.OrangePlugin:   DrawOrangeOffer(x, y, rowW, rowH); break;
                case OfferKind.TemplateUpgrade: DrawUpgradeOffer(x, y, rowW, rowH); break;
                default:
                    GUI.Label(new Rect(x, y + 10f, rowW, 30f), "今天没有特供商品。", m_DescStyle);
                    break;
            }

            // ---- 离开 ----
            var closeBtn = new Rect(panel.x + (panel.width - 260f) * 0.5f,
                                    panel.yMax - 84f, 260f, 54f);

            if (GUI.Button(closeBtn, GUIContent.none, m_ClickArea)) Close();
            DrawBorder(closeBtn);
            GUI.Label(closeBtn, "离开商店（Esc）", m_ItemStyle);

            GUI.matrix = saved;
        }

        private void DrawOrangeOffer(float x, float y, float rowW, float rowH)
        {
            GUI.Label(new Rect(x, y, rowW, 30f),
                      m_OrangeBought ? "橙色插件（本次已购买）" : "橙色插件（只能买一个）", m_DescStyle);

            y += 32f;

            for (int i = 0; i < m_OrangeOptions.Count; i++)
            {
                DungeonBuffDef d = m_OrangeOptions[i];
                if (d == null) continue;

                bool canBuy = !m_OrangeBought;

                DrawItem(new Rect(x, y, rowW, rowH),
                         "橙·" + d.name,
                         d.desc.Replace("\n", " "),
                         orangePrice, canBuy,
                         () => BuyOrange(d),
                         "本次已购买");

                y += rowH + 10f;
            }

            var refresh = new Rect(x, y + 4f, 260f, 48f);
            bool canRefresh = !m_OrangeBought;

            if (GUI.Button(refresh, GUIContent.none, m_ClickArea) && canRefresh) RefreshOrange();
            DrawBorder(refresh);

            Color prev = GUI.color;
            if (!canRefresh) GUI.color = new Color(1f, 1f, 1f, 0.4f);
            GUI.Label(refresh, $"刷新陈列（{orangeRefreshPrice}）", m_DescStyle);
            GUI.color = prev;
        }

        private void DrawUpgradeOffer(float x, float y, float rowW, float rowH)
        {
            var st = DungeonBuffState.Instance;

            string tierNow = st != null ? SlotRules.NameOf(st.Tier) : "?";
            int    left    = st != null ? st.ShopUpgradesLeft : 0;

            bool canBuy = !m_UpgradeBought && left > 0;

            DrawItem(new Rect(x, y, rowW, rowH),
                     "模板升级",
                     $"{tierNow}级 → 下一级　（全局限 2 次，剩余 {left} 次）",
                     upgradePrice, canBuy, BuyUpgrade,
                     m_UpgradeBought ? "本店已升级" : "升级次数已用完");
        }

        /// <summary>
        /// 画一行商品。
        ///
        /// ★ 价格**永远显示**（任务书要求明码标价）。
        ///   买不了的原因（满血 / 满蓝 / 已购买 / 买不起）写在描述后面，
        ///   不能因为"现在不能买"就把价格藏成 "—"，那样玩家会以为没定价。
        /// </summary>
        private void DrawItem(Rect r, string name, string desc, int price, bool canBuy, System.Action buy,
                              string unavailableText = "已满")
        {
            bool clicked = GUI.Button(r, GUIContent.none, m_ClickArea);
            DrawBorder(r);

            int  goldNow    = DungeonCurrency.Instance != null ? DungeonCurrency.Instance.Gold : 0;
            bool affordable = goldNow >= price;
            bool canClick   = canBuy && affordable;

            Color prev = GUI.color;

            // 买不起 -> 整行变暗（价格也跟着暗，但依然看得见）
            if (!affordable) GUI.color = new Color(1f, 1f, 1f, 0.45f);

            GUI.Label(new Rect(r.x + 20f, r.y + 10f, r.width - 260f, 34f), name, m_ItemStyle);

            string descText = canBuy ? desc : desc + "　（" + unavailableText + "）";
            GUI.Label(new Rect(r.x + 20f, r.y + 44f, r.width - 260f, 30f), descText, m_DescStyle);

            // 价格：始终显示
            GUI.Label(new Rect(r.xMax - 232f, r.y + 24f, 200f, 40f),
                      $"{price} 金币", m_PriceStyle);

            GUI.color = prev;

            if (clicked && canClick) buy?.Invoke();
        }

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