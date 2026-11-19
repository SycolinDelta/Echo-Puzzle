using UnityEngine;

namespace EchoPuzzle.Dungeon
{
    /// <summary>
    /// 【地牢模式】右上角的金币显示。
    ///
    /// 放右上角而不是左上角：左上角已经被「爱心 / 蓝条 / 强化列表」占满了，
    /// 而且射击游戏里货币放右上角也更符合习惯。
    ///
    /// 只在 <see cref="GameUI.IsDungeonMode"/> 且真正在游玩时显示，
    /// 缩放口径和 GameUI / DungeonHUD / DungeonManaHUD 完全一致（按 1080 高度等比）。
    /// </summary>
    [AddComponentMenu("Echo Puzzle/Dungeon/Dungeon Gold HUD")]
    public sealed class DungeonGoldHUD : MonoBehaviour
    {
        [Header("位置（1080 高度基准的像素）")]
        [Tooltip("距离屏幕右边的边距")]
        public float rightMargin = 48f;

        [Tooltip("距离屏幕顶部的边距")]
        public float topMargin = 48f;

        [Tooltip("文本区域宽度（右对齐，宽一点没关系）")]
        public float width = 260f;

        [Header("外观")]
        [Min(8)] public int fontSize = 22;
        public Color textColor = new Color(1f, 0.85f, 0.35f, 1f);

        [Tooltip("前缀文字")]
        public string prefix = "金币 ";

        private const float RefHeight = 1080f;

        private DungeonCurrency m_Currency;
        private GUIStyle m_Style;

        private void Update()
        {
            if (m_Currency == null) m_Currency = DungeonCurrency.Instance;
        }

        private void OnGUI()
        {
            if (DungeonModalUi.AnyOpen) return;      // 装配 / 商店 / 三选一打开时让路

            if (!GameUI.IsDungeonMode) return;
            if (!GameUI.GameplayInputActive && GameUI.Current != GameState.Playing) return;

            if (m_Currency == null) return;

            EnsureStyle();

            float scale = Screen.height / RefHeight;
            Matrix4x4 saved = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity,
                                       new Vector3(scale, scale, 1f));

            float w = Screen.width / scale;

            var rect = new Rect(w - rightMargin - width, topMargin, width, fontSize + 14f);

            GUI.Label(rect, prefix + m_Currency.Gold, m_Style);

            GUI.matrix = saved;
        }

        private void EnsureStyle()
        {
            if (m_Style != null) return;

            Font f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (f == null) f = Resources.GetBuiltinResource<Font>("Arial.ttf");

            m_Style = new GUIStyle(GUI.skin.label)
            {
                font      = f,
                fontSize  = Mathf.Max(8, fontSize),
                alignment = TextAnchor.MiddleRight,
                fontStyle = FontStyle.Bold,
            };
            m_Style.normal.textColor = textColor;
        }
    }
}