using UnityEngine;

namespace EchoPuzzle.Dungeon
{
    /// <summary>
    /// 【地牢模式】左上角蓝条，画在 DungeonHUD 的六颗爱心下面。
    ///
    /// ── 表现 ────────────────────────────────────────────────
    ///   · 一条 220×10 的横条 + 右侧 "80 / 100"
    ///   · 蓝量低于 lowRatio 时填充色换成 lowColor（提示快没蓝了）
    ///   · 默认位置 (48, 96)：爱心在 y≈56、半径 14，所以正好落在爱心下沿之下
    ///
    /// ── 和探秘模式的关系 ────────────────────────────────────
    ///   ★ 只在 <see cref="GameUI.IsDungeonMode"/> 为真时绘制，探秘模式不受影响。
    ///   · 缩放口径和 DungeonHUD / GameUI 一致：按 1080 高度等比缩放。
    /// </summary>
    [AddComponentMenu("Echo Puzzle/Dungeon/Dungeon Mana HUD")]
    public sealed class DungeonManaHUD : MonoBehaviour
    {
        [Header("位置（1080 高度基准的像素）")]
        public Vector2 origin = new Vector2(73f, 166f);

        [Tooltip("条宽")]
        [Min(20f)] public float width = 390f;

        [Tooltip("条高")]
        [Min(2f)] public float height = 20f;

        [Header("颜色")]
        [Tooltip("底槽颜色")]
        public Color backColor = new Color(0f, 0f, 0f, 0.55f);

        [Tooltip("正常填充色")]
        public Color fillColor = new Color(0.35f, 0.62f, 1f, 1f);

        [Tooltip("低于 lowRatio 时用的填充色")]
        public Color lowColor = new Color(1f, 0.35f, 0.35f, 1f);

        [Tooltip("低于这个比例换成 lowColor")]
        [Range(0f, 1f)] public float lowRatio = 0.2f;

        [Header("数字")]
        public bool showText = true;
        public Color textColor = new Color(1f, 1f, 1f, 0.85f);

        [Min(6)] public int textSize = 26;

        /// <summary>参考高度：所有尺寸都按它等比缩放。</summary>
        private const float RefHeight = 1080f;

        private DungeonMana m_Mana;
        private Texture2D   m_Pixel;
        private GUIStyle    m_TextStyle;

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
            // 蓝量对象可能比 HUD 晚建出来，缓存在这里找一次就够
            if (m_Mana == null) m_Mana = DungeonMana.Instance;
        }

        private void OnGUI()
        {
            // ★ 只有地牢模式才画
            if (!GameUI.IsDungeonMode) return;

            // ★★ 选 buff 界面开着时隐藏蓝条 ★★
            //
            //   需求："在选buff界面应当隐藏蓝条。"
            //   那时候整个屏幕是纯黑的，蓝条会突兀地浮在上面。
            if (DungeonBuffSelectUI.Instance != null && DungeonBuffSelectUI.Instance.IsOpen)
                return;

            // 和 DungeonHUD 一个口径：只在游戏中 / 胜利时显示
            if (!GameUI.GameplayInputActive && GameUI.Current != GameState.Playing) return;

            if (m_Mana == null) return;

            // 和 GameUI / DungeonHUD 一样：按 1080 高度等比缩放
            float scale = Screen.height / RefHeight;
            Matrix4x4 saved = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity,
                                       new Vector3(scale, scale, 1f));

            float ratio = m_Mana.Ratio01;

            Color prev = GUI.color;

            // 底槽
            GUI.color = backColor;
            GUI.DrawTexture(new Rect(origin.x, origin.y, width, height), m_Pixel);

            // 填充
            GUI.color = ratio <= lowRatio ? lowColor : fillColor;
            GUI.DrawTexture(new Rect(origin.x, origin.y, width * ratio, height), m_Pixel);

            GUI.color = prev;

            if (showText)
            {
                if (m_TextStyle == null)
                {
                    m_TextStyle = new GUIStyle(GUI.skin.label)
                    {
                        fontSize  = textSize,
                        alignment = TextAnchor.MiddleLeft,
                        padding   = new RectOffset(0, 0, 0, 0),
                        margin    = new RectOffset(0, 0, 0, 0),
                    };
                    m_TextStyle.normal.textColor = textColor;
                }

                // ★★ 显示用整数，内部仍保留小数 ★★
                //
                //   需求："蓝量虽然以整数显示，但实际上仍然是有小数的。"
                //   直接拼 float 会显示成 "187.23743 / 200"，
                //   所以这里用 CurrentDisplay / MaxDisplay（已四舍五入）。
                GUI.Label(new Rect(origin.x + width + 10f, origin.y - 5f, 140f, height + 10f),
                          m_Mana.CurrentDisplay + " / " + m_Mana.MaxDisplay, m_TextStyle);
            }

            GUI.matrix = saved;
        }

        /// <summary>1×1 白色纹理（用 GUI.color 染色），和 DungeonHUD 一样不依赖任何贴图资源。</summary>
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