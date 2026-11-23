using System.Collections.Generic;
using UnityEngine;

namespace EchoPuzzle.Dungeon
{
    /// <summary>
    /// 【地牢模式】已获得强化的显示：蓝条下面一小块文字。
    ///
    /// ── 显示什么 ────────────────────────────────────────────
    ///   · 每一条已获得的 buff 和它的层数，例如「高爆弹头 ×2」
    ///   · 最后一行是**当前实际数值**（含所有 buff 叠加后的结果）：
    ///       伤害 / 射速 / 蓝耗 / 击杀回蓝
    ///     数值全部来自 <see cref="DungeonWeapon"/> 的 Effective* 属性，
    ///     和真正开火时用的是同一套计算，所以**看到的就是打出来的**。
    ///
    /// ── 为什么单独做一个组件 ────────────────────────────────
    ///   文档要求地牢模式的东西不要塞进探秘模式的脚本里。
    ///   DungeonManaHUD 只管蓝条，这里只管强化，互不干扰。
    ///
    /// ── 刷新策略 ────────────────────────────────────────────
    ///   只在强化列表变化（<see cref="DungeonBuffState.Changed"/>）时重算一次文本，
    ///   OnGUI 里只画缓存的字符串，不每帧拼字符串。
    /// </summary>
    [AddComponentMenu("Echo Puzzle/Dungeon/Dungeon Buff HUD")]
    public sealed class DungeonBuffHUD : MonoBehaviour
    {
        [Header("位置（1080 高度基准的像素）")]
        [Tooltip("第一行文字的位置。默认在蓝条（y = 96，高 10）下面")]
        public Vector2 origin = new Vector2(73f, 216f);

        [Tooltip("行间距")]
        [Min(10f)] public float lineHeight = 47f;

        [Header("字号")]
        [Min(8)] public int titleSize = 31;
        [Min(8)] public int lineSize  = 30;
        [Min(8)] public int statsSize = 29;

        [Header("颜色")]
        public Color titleColor = new Color(1f, 1f, 1f, 0.55f);
        public Color lineColor  = new Color(1f, 1f, 1f, 0.92f);
        public Color statsColor = new Color(0.72f, 0.86f, 1f, 0.85f);
        public Color emptyColor = new Color(1f, 1f, 1f, 0.35f);

        private const float RefHeight = 1080f;

        private DungeonBuffState m_State;
        private bool m_Dirty = true;

        private readonly List<string> m_Lines = new List<string>(8);
        private string m_StatsLine = "";

        private GUIStyle m_TitleStyle;
        private GUIStyle m_LineStyle;
        private GUIStyle m_StatsStyle;

        private void MarkDirty() => m_Dirty = true;

        private void OnDestroy()
        {
            if (m_State != null) m_State.Changed -= MarkDirty;
        }

        private void Update()
        {
            // 状态对象可能比 HUD 晚建出来，懒绑定一次
            if (m_State == null)
            {
                m_State = DungeonBuffState.Instance;
                if (m_State != null) m_State.Changed += MarkDirty;
                m_Dirty = true;
            }

            if (m_Dirty)
            {
                Rebuild();
                m_Dirty = false;
            }
        }

        /// <summary>把「id -> 层数」整理成一行行文本，并算出当前实际数值。</summary>
        private void Rebuild()
        {
            m_Lines.Clear();

            if (m_State != null)
            {
                // ★ 第十一周：一份插件一个实例，所以按"种类"汇总成 "紫·爆炸弹头 ×2"
                var seenIds = new List<string>();

                for (int i = 0; i < m_State.Owned.Count; i++)
                {
                    BuffInstance inst = m_State.Owned[i];
                    if (inst == null || inst.def == null) continue;
                    if (seenIds.Contains(inst.def.id)) continue;

                    seenIds.Add(inst.def.id);

                    int total = 0, equipped = 0;

                    for (int k = 0; k < m_State.Owned.Count; k++)
                    {
                        BuffInstance o = m_State.Owned[k];
                        if (o == null || o.def == null || o.def.id != inst.def.id) continue;

                        total++;
                        if (m_State.IsEquipped(m_State.ActiveTemplate, o)) equipped++;
                    }

                    string suffix = equipped > 0 ? "" : "（未装备）";
                    m_Lines.Add($"{inst.def.name} ×{total}{suffix}");
                }
            }

            // ---- 当前实际数值 ----
            DungeonWeapon w = DungeonWeapon.Instance;
            DungeonMana   m = DungeonMana.Instance;

            if (w != null)
            {
                string manaPart = m != null ? $"{m.Current}/{m.Max}" : "?";
                m_StatsLine = $"伤害 {w.EffectiveDamage}　射速 {w.EffectiveShotsPerSecond:F1}/秒　" +
                              $"蓝耗 {w.EffectiveManaCost}　回蓝 {w.EffectiveKillMana}　蓝量 {manaPart}";
            }
            else
            {
                m_StatsLine = "";
            }
        }

        private void OnGUI()
        {
            if (DungeonModalUi.AnyOpen) return;      // 装配 / 商店 / 三选一打开时让路

            if (!GameUI.IsDungeonMode) return;
            if (!GameUI.GameplayInputActive && GameUI.Current != GameState.Playing) return;

            EnsureStyles();

            float scale = Screen.height / RefHeight;
            Matrix4x4 saved = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity,
                                       new Vector3(scale, scale, 1f));

            float x = origin.x;
            float y = origin.y;

            GUI.Label(new Rect(x, y, 600f, lineHeight), "强化", m_TitleStyle);
            y += lineHeight;

            if (m_Lines.Count == 0)
            {
                GUI.Label(new Rect(x, y, 600f, lineHeight), "（还没有强化）", m_StatsStyle);
                y += lineHeight;
            }
            else
            {
                for (int i = 0; i < m_Lines.Count; i++)
                {
                    GUI.Label(new Rect(x, y, 600f, lineHeight), m_Lines[i], m_LineStyle);
                    y += lineHeight;
                }
            }

            if (!string.IsNullOrEmpty(m_StatsLine))
            {
                y += 4f;
                GUI.Label(new Rect(x, y, 900f, lineHeight), m_StatsLine, m_StatsStyle);
            }

            GUI.matrix = saved;
        }

        private void EnsureStyles()
        {
            if (m_TitleStyle != null) return;

            Font f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (f == null) f = Resources.GetBuiltinResource<Font>("Arial.ttf");

            m_TitleStyle = new GUIStyle(GUI.skin.label)
            {
                font     = f,
                fontSize = Mathf.Max(8, titleSize),
            };
            m_TitleStyle.normal.textColor = titleColor;

            m_LineStyle = new GUIStyle(GUI.skin.label)
            {
                font     = f,
                fontSize = Mathf.Max(8, lineSize),
            };
            m_LineStyle.normal.textColor = lineColor;

            m_StatsStyle = new GUIStyle(GUI.skin.label)
            {
                font     = f,
                fontSize = Mathf.Max(8, statsSize),
            };
            m_StatsStyle.normal.textColor = statsColor;
        }
    }
}