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
        [Tooltip("第一行文字的位置。在蓝条（y = 228，高 20）下面")]
        // ★ 需求：盾条移到血条下方后，这里也跟着下移一行（216 -> 278）
        public Vector2 origin = new Vector2(73f, 265f);

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

        /// <summary>
        /// 取插件的「效果显示名」（不含颜色前缀、不含数值）。
        ///
        /// ★ 用 def.effectName 拼出来，这样紫/橙同一个效果会显示成同一个名字，
        ///   汇总成「爆炸弹头 lv2」而不是「紫·爆炸弹头」+「橙·爆炸弹头」两行。
        /// </summary>
        private static string EffectDisplayName(DungeonBuffDef def)
        {
            if (def == null) return "?";

            if (!string.IsNullOrEmpty(def.effectName)) return def.effectName;

            // 兜底：没有 effectName 就用 effectId
            return string.IsNullOrEmpty(def.effectId) ? def.name : def.effectId;
        }

        /// <summary>把「id -> 层数」整理成一行行文本，并算出当前实际数值。</summary>
        private void Rebuild()
        {
            m_Lines.Clear();

            if (m_State != null)
            {
                // ★★ 只列「当前模板已装备的紫色 + 橙色插件」，按效果汇总、不看颜色 ★★
                //
                //   需求："左侧 buff 列表 hud 里不会再把所有 buff 都列出来，
                //         只会列出当前模板已经装备的紫色和橙色插件
                //         （注意模板是可以按 E 切换的），而且只看总数不看颜色，
                //         格式为「xxxx lvx」。
                //         比如：一个紫色爆炸弹头 + 一个橙色爆炸弹头 -> 「爆炸弹头 lv2」
                //         其他什么都不会写。"
                //
                //   ★ 按 effectId 汇总（跨颜色合并），层数 = 当前模板里该效果的个数。
                //   ★ 模板由 DungeonBuffState.ActiveTemplate 决定，按 E 切换后
                //     Recompute() 会触发 Changed，本 HUD 会自动重算。
                var seenEffects = new List<string>();

                int tmpl = m_State.ActiveTemplate;

                // 遍历当前模板的六个颜色槽，只关心紫 / 橙
                for (int c = 0; c < SlotRules.ColorCount; c++)
                {
                    var color = (PluginColor)c;

                    if (color != PluginColor.Purple && color != PluginColor.Orange) continue;

                    var list = m_State.SlotsOf(tmpl, color);
                    if (list == null) continue;

                    for (int i = 0; i < list.Count; i++)
                    {
                        BuffInstance inst = list[i];
                        if (inst == null || inst.def == null) continue;

                        // ★ EffectKeyOf 是静态方法，必须用类名调（不能走实例）
                        string key = DungeonBuffState.EffectKeyOf(inst.def);
                        if (string.IsNullOrEmpty(key)) continue;
                        if (seenEffects.Contains(key)) continue;

                        seenEffects.Add(key);

                        // ★ 等级 = 当前模板里这个效果的总个数（跨颜色相加）
                        int lv = m_State.EquippedEffectCount(tmpl, key);
                        if (lv <= 0) continue;

                        m_Lines.Add($"{EffectDisplayName(inst.def)} lv{lv}");
                    }
                }
            }

            // ---- 当前实际数值 ----
            DungeonWeapon w = DungeonWeapon.Instance;

            if (w != null)
            {
                // ★ 「蓝量」已从这行去掉：蓝条本身就在 HUD 上，重复显示没意义。
                //   所以这里也不再需要 DungeonMana 的引用。

                // ★ 射速同时显示「倍率」和「每秒几发」：
                //   只看发/秒会误以为没封顶（封顶是倍率 ×8 = 16 发/秒）。
                //   倍率达到 8 时标「封顶」，并把溢出的伤害/蓝耗也标出来。
                float rawRate = w.RawFireRateMul;
                bool  capped  = rawRate > DungeonWeapon.MaxFireRateMul + 0.001f;

                string rateText = capped
                    ? $"×{w.EffectiveFireRateMul}(封顶) {w.EffectiveShotsPerSecond:F1}/秒"
                    : $"×{rawRate:F2} {w.EffectiveShotsPerSecond:F1}/秒";

                string overflowText = capped
                    ? $"溢出 伤害×{w.OverflowDamageMul} 蓝耗×{w.OverflowManaCostMul}　"
                    : "";

                m_StatsLine = $"伤害 {w.EffectiveDamage}　射速 {rateText}　" +
                              $"蓝耗 {w.EffectiveManaCost}　回蓝 {w.EffectiveKillMana}　{overflowText}";
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