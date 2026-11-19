using UnityEngine;

namespace EchoPuzzle.Dungeon
{
    /// <summary>
    /// 【地牢模式】右下角的技能冷却显示。
    ///
    /// 显示两格：
    ///   · 闪避（左 Alt）
    ///   · 声波（空格）
    ///
    /// 每一个格子：
    ///   · 一个圆角方框 + 技能名
    ///   · 冷却中：框内从下往上"填充"表示进度，中间显示剩余秒数
    ///   · 就绪：框边高亮，显示「就绪」
    ///
    /// ── 数据来源 ────────────────────────────────────────────
    ///   全部来自 <see cref="PlayerController"/> 的公开属性：
    ///     闪避（充能制）：DodgeEnergy / DodgeEnergyMax / DodgeEnergy01 / DodgeReady
    ///     声波（冷却制）：ShoutCooldownRemaining / ShoutCooldownTotal
    ///   这里只负责画，不存任何状态。
    ///
    /// ── 和探秘模式的关系 ────────────────────────────────────
    ///   ★ 只在 <see cref="GameUI.IsDungeonMode"/> 为真时绘制。
    /// </summary>
    [AddComponentMenu("Echo Puzzle/Dungeon/Dungeon Cooldown HUD")]
    public sealed class DungeonCooldownHUD : MonoBehaviour
    {
        // ---------------------------------------------------------------- 版式

        [Header("位置（1080 高度基准的像素，从右下角量）")]
        [Tooltip("距屏幕右边多少像素")]
        public float marginRight = 72f;

        [Tooltip("距屏幕下边多少像素")]
        public float marginBottom = 84f;

        [Tooltip("每个格子的尺寸")]
        public Vector2 slotSize = new Vector2(144f, 144f);

        [Tooltip("两个格子之间的间距")]
        public float slotGap = 21f;

        // ---------------------------------------------------------------- 颜色

        [Header("颜色")]
        [Tooltip("框底色")]
        public Color backColor = new Color(0f, 0f, 0f, 0.55f);

        [Tooltip("冷却中的填充色")]
        public Color cooldownColor = new Color(0.35f, 0.45f, 0.62f, 0.85f);

        [Tooltip("就绪时的边框色")]
        public Color readyColor = new Color(0.55f, 0.95f, 0.65f, 0.95f);

        [Tooltip("冷却中的边框色")]
        public Color busyColor = new Color(1f, 1f, 1f, 0.28f);

        [Tooltip("充能已够用时的填充色（闪避格用）")]
        public Color chargeColor = new Color(0.40f, 0.72f, 0.95f, 0.90f);

        public Color textColor = new Color(1f, 1f, 1f, 0.92f);

        [Header("字号")]
        [Min(8)] public int nameSize = 23;
        [Min(8)] public int valueSize = 33;

        [Header("边框")]
        [Min(0f)] public float borderThickness = 3f;

        private const float RefHeight = 1080f;

        // ---------------------------------------------------------------- 运行时

        private PlayerController m_Player;
        private Texture2D m_Pixel;

        private GUIStyle m_NameStyle;
        private GUIStyle m_ValueStyle;

        private void Awake()
        {
            m_Pixel = MakePixel();
        }

        private void OnDestroy()
        {
            if (m_Pixel != null) Destroy(m_Pixel);
        }

        private void Update()
        {
            if (m_Player == null) m_Player = FindObjectOfType<PlayerController>();
        }

        private void OnGUI()
        {
            if (DungeonModalUi.AnyOpen) return;      // 装配 / 商店 / 三选一打开时让路

            if (!GameUI.IsDungeonMode) return;
            if (GameUI.Current != GameState.Playing) return;
            if (m_Player == null) return;

            if (m_Pixel == null) m_Pixel = MakePixel();

            EnsureStyles();

            float scale = Screen.height / RefHeight;
            if (scale <= 0.0001f) return;

            Matrix4x4 saved = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity,
                                       new Vector3(scale, scale, 1f));

            // 用"基准坐标系"里的屏幕尺寸（因为已经乘过 scale 了）
            float w = Screen.width / scale;

            Vector2 size = slotSize;

            // 从右往左排：声波在右，闪避在它左边
            float baseY = RefHeight - marginBottom - size.y;

            float shoutX = w - marginRight - size.x;
            float dodgeX = shoutX - slotGap - size.x;

            // ★★ 闪避改成充能制：填充高度 = 能量比例 ★★
            DrawChargeSlot(new Rect(dodgeX, baseY, size.x, size.y),
                           "闪避", "Alt",
                           m_Player.DodgeEnergy01,
                           m_Player.DodgeEnergy,
                           m_Player.DodgeEnergyMax,
                           m_Player.DodgeReady,
                           m_Player.IsDodging);

            DrawSlot(new Rect(shoutX, baseY, size.x, size.y),
                     "声波", "空格",
                     m_Player.ShoutCooldownRemaining,
                     m_Player.ShoutCooldownTotal,
                     false);

            GUI.matrix = saved;
        }

        /// <summary>
        /// 画一个**充能型**技能格（闪避用）。
        ///
        /// ★ 和冷却型 <see cref="DrawSlot"/> 的区别：
        ///     · 填充高度 = 当前能量 / 满能量（**从下往上长**）
        ///     · 中间显示当前能量秒数（如 "1.4"）而不是剩余冷却
        ///     · 能量够按下限 -> 边框高亮
        /// </summary>
        private void DrawChargeSlot(Rect r, string name, string key,
                                    float ratio01, float energy, float energyMax,
                                    bool ready, bool active)
        {
            Color prev = GUI.color;

            // ---- 底框 ----
            GUI.color = backColor;
            GUI.DrawTexture(r, m_Pixel);

            // ---- 能量填充：从下往上 ----
            float t = Mathf.Clamp01(ratio01);

            if (t > 0.001f)
            {
                GUI.color = ready ? chargeColor : cooldownColor;
                GUI.DrawTexture(new Rect(r.x, r.y + r.height * (1f - t), r.width, r.height * t),
                                m_Pixel);
            }

            // ---- 正在闪避：整格淡亮 ----
            if (active)
            {
                GUI.color = new Color(readyColor.r, readyColor.g, readyColor.b, 0.25f);
                GUI.DrawTexture(r, m_Pixel);
            }

            // ---- 边框 ----
            if (borderThickness > 0f)
            {
                GUI.color = ready ? readyColor : busyColor;
                DrawBorder(r, Mathf.Max(1f, borderThickness));
            }

            GUI.color = prev;

            // ---- 技能名（顶部）----
            GUI.Label(new Rect(r.x, r.y + 6f, r.width, 20f), name, m_NameStyle);

            // ---- 按键提示（底部）----
            GUI.Label(new Rect(r.x, r.yMax - 22f, r.width, 18f), key, m_NameStyle);

            // ---- 中间：能量秒数 ----
            string value = ready ? energy.ToString("0.0") : "不足";
            GUI.Label(new Rect(r.x, r.y + r.height * 0.5f - 16f, r.width, 32f), value, m_ValueStyle);
        }

        /// <summary>画一个技能格。</summary>
        private void DrawSlot(Rect r, string name, string key,
                              float remaining, float total, bool active)
        {
            Color prev = GUI.color;

            bool ready = remaining <= 0.001f;

            // ---- 底框 ----
            GUI.color = backColor;
            GUI.DrawTexture(r, m_Pixel);

            // ---- 冷却进度：从下往上填 ----
            if (!ready)
            {
                float t = Mathf.Clamp01(remaining / Mathf.Max(0.01f, total));

                GUI.color = cooldownColor;
                GUI.DrawTexture(new Rect(r.x, r.y + r.height * (1f - t), r.width, r.height * t),
                                m_Pixel);
            }
            else if (active)
            {
                // 正在闪避：整格淡亮一下
                GUI.color = new Color(readyColor.r, readyColor.g, readyColor.b, 0.25f);
                GUI.DrawTexture(r, m_Pixel);
            }

            // ---- 边框 ----
            if (borderThickness > 0f)
            {
                GUI.color = ready ? readyColor : busyColor;
                DrawBorder(r, Mathf.Max(1f, borderThickness));
            }

            GUI.color = prev;

            // ---- 技能名（顶部）----
            GUI.Label(new Rect(r.x, r.y + 6f, r.width, 20f), name, m_NameStyle);

            // ---- 按键提示（底部）----
            GUI.Label(new Rect(r.x, r.yMax - 22f, r.width, 18f), key, m_NameStyle);

            // ---- 中间：剩余秒数 / 就绪 ----
            string value = ready ? "就绪" : remaining.ToString("0.0");
            GUI.Label(new Rect(r.x, r.y + r.height * 0.5f - 16f, r.width, 32f), value, m_ValueStyle);
        }

        private void DrawBorder(Rect r, float t)
        {
            GUI.DrawTexture(new Rect(r.x,          r.y,          r.width, t),        m_Pixel);
            GUI.DrawTexture(new Rect(r.x,          r.yMax - t,   r.width, t),        m_Pixel);
            GUI.DrawTexture(new Rect(r.x,          r.y,          t,       r.height), m_Pixel);
            GUI.DrawTexture(new Rect(r.xMax - t,   r.y,          t,       r.height), m_Pixel);
        }

        private void EnsureStyles()
        {
            if (m_NameStyle != null) return;

            Font f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (f == null) f = Resources.GetBuiltinResource<Font>("Arial.ttf");

            m_NameStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize  = nameSize,
                alignment = TextAnchor.MiddleCenter,
                font      = f,
            };
            m_NameStyle.normal.textColor = textColor;

            m_ValueStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize  = valueSize,
                alignment = TextAnchor.MiddleCenter,
                font      = f,
                fontStyle = FontStyle.Bold,
            };
            m_ValueStyle.normal.textColor = textColor;
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
    }
}
