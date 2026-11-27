using System.Collections.Generic;
using UnityEngine;

namespace EchoPuzzle.Dungeon
{
    /// <summary>
    /// 【地牢模式】一条**插件实例** = 一个 buff 定义 + 它的等级（第十周任务 5）。
    ///
    /// ★ 关键设定：每种 buff 在背包里**只有一个实例**，重复获得只提升等级。
    ///   所以不会出现"两份爆炸弹头"，只有"爆炸弹头 Lv3"。
    ///   等级上限就是 <see cref="DungeonBuffDef.maxStacks"/>。
    /// </summary>
    public sealed class BuffInstance
    {
        public readonly DungeonBuffDef def;

        /// <summary>等级（1 起）。</summary>
        public int level;

        public BuffInstance(DungeonBuffDef def, int level = 1)
        {
            this.def   = def;
            this.level = Mathf.Max(1, level);
        }

        public string Name     => def != null ? def.name : "?";
        public int    MaxLevel => def != null ? Mathf.Max(1, def.maxStacks) : 1;
        public bool   IsMax    => level >= MaxLevel;
    }

    /// <summary>
    /// 【地牢模式】一条 buff 的定义（纯数据，不是 ScriptableObject）。
    ///
    /// ── 为什么不用 ScriptableObject ──────────────────────────
    ///   文档要求"不要往场景 / 工程里加东西，一切代码生成"，而且现在只是
    ///   搭框架阶段。放在代码里改一个字就能加一条 buff，不用建资产、不用管 GUID。
    ///   以后要交给策划配表，再把这个类换成 SO 即可，字段完全一致。
    ///
    /// ── 数值怎么表达 ────────────────────────────────────────
    ///   **乘区**：1 = 不变。0.7 = 减 30%，1.5 = 加 50%。
    ///   可叠加的 buff 就是多次乘上去（0.7 × 0.7 = 0.49）。
    ///
    ///   ★ 文档要求"越强的 buff 蓝耗越大" —— 实现方式就是：
    ///     强效果的那条同时把 <see cref="manaCostMul"/> 设成 &gt; 1。
    /// </summary>
    public sealed class DungeonBuffDef
    {
        public string id   = "buff";
        public string name = "未命名";
        public string desc = "";

        /// <summary>抽卡权重（越大越常见）。</summary>
        [Range(0.01f, 100f)] public float weight = 1f;

        /// <summary>最多能叠几层。</summary>
        [Min(1)] public int maxStacks = 5;

        // ---- 第十一周任务：颜色体系 ----

        [Tooltip("插件颜色。必须插进同颜色的槽位才生效")]
        public PluginColor color = PluginColor.White;

        [Tooltip("效果标识：跨颜色共用。紫爆炸弹头 + 橙爆炸弹头 -> 同一个效果、等级相加")]
        public string effectId = "";

        [Tooltip("效果名（**不含颜色前缀**）。\n\n" +
                 "★ 给 HUD 汇总用：紫爆炸弹头 + 橙爆炸弹头 -> 显示「爆炸弹头 lv2」。\n" +
                 "★ 和 name 的区别：name 是「紫·爆炸弹头（蓝耗*1.5）」，带颜色和数值。")]
        public string effectName = "";

        // ---- 效果（乘区）----

        /// <summary>伤害倍率。</summary>
        public float damageMul = 1f;

        /// <summary>射速倍率（&gt;1 = 更快，内部是 fireInterval ÷ 本值）。</summary>
        public float fireRateMul = 1f;

        /// <summary>蓝耗倍率（&lt;1 = 更省，&gt;1 = 更费）。</summary>
        public float manaCostMul = 1f;

        /// <summary>击杀回蓝倍率。</summary>
        public float killManaMul = 1f;

        /// <summary>子弹速度倍率。</summary>
        public float bulletSpeedMul = 1f;

        /// <summary>拖尾宽度倍率（纯视觉，但粗一点更容易看清弹道）。</summary>
        public float bulletWidthMul = 1f;

        /// <summary>
        /// 子弹**最大偏转角**的倍率（精准度）。
        ///
        /// ★ &gt;1 = 更飘（散布更大），&lt;1 = 更准（散布更小）。
        /// ★ 作用对象是 DungeonWeapon.bulletSpreadDegrees。
        /// </summary>
        public float bulletSpreadMul = 1f;

        /// <summary>蓝上限加成（加法，每次加这么多）。</summary>
        public int maxManaAdd;

        // ---- 效果（加法 / 计数，第十周任务 4）----

        /// <summary>
        /// 额外弹丸数（0 = 单发）。霰弹枪 = +2；
        /// 继续叠加会变成 5 发、7 发……
        /// </summary>
        [Min(0)] public int extraPellets;

        // ★ aoeRadiusAdd 已删除：
        //   爆炸半径现在由 DungeonWeapon.aoeRadiusByLevel（等级表）决定，
        //   这个加法字段不再参与任何计算。

        // ---- 第十周任务 6：连发 / 弹跳 / 穿透 ----

        /// <summary>
        /// 每次**单击**额外开火几次（0 = 只打一轮）。
        /// 每叠一层 +1 轮，同时 <see cref="manaCostAdd"/> 累加。
        /// </summary>
        [Min(0)] public int extraVolleys;

        /// <summary>子弹撞墙后还能反弹几次（0 = 撞墙就停）。</summary>
        [Min(0)] public int bounceAdd;

        /// <summary>子弹能穿过几只怪物继续飞（0 = 命中即停）。</summary>
        [Min(0)] public int pierceAdd;

        /// <summary>
        /// 单发蓝耗的**加法**加成（不是乘区）。0.5 = 每发多花 0.5 蓝。
        /// 「连发」这类强力 buff 用它作为代价。
        /// </summary>
        [Min(0f)] public float manaCostAdd;

        public bool HasEffect =>
            !Mathf.Approximately(damageMul, 1f) ||
            !Mathf.Approximately(fireRateMul, 1f) ||
            !Mathf.Approximately(manaCostMul, 1f) ||
            !Mathf.Approximately(killManaMul, 1f) ||
            !Mathf.Approximately(bulletSpeedMul, 1f) ||
            !Mathf.Approximately(bulletWidthMul, 1f) ||
            !Mathf.Approximately(bulletSpreadMul, 1f) ||
            maxManaAdd != 0 ||
            extraPellets != 0;
    }

    /// <summary>
    /// 【地牢模式】buff 池 + 抽卡。
    ///
    /// ★ 现在**只放三条**，是刻意的：
    ///   一次三选一正好把三条都发出去，能完整验证「池子 → 抽卡 → 三列卡片 → 生效」
    ///   这条链路。效果类型也刻意选成三种不同量纲（伤害 / 射速 / 蓝耗），
    ///   方便一眼看出哪条生效了。
    ///
    ///   加新 buff：往 <see cref="All"/> 里加一行即可，字段见 <see cref="DungeonBuffDef"/>。
    ///   之前做过的 8 条在 git 历史里（commit 56af1b1），
    ///   需要时把对应几行粘回来，并把抽卡逻辑原样保留。
    /// </summary>
    public static class DungeonBuffPool
    {
        // ⚠️ 第十一周起停用：这套无颜色的旧池子由下面的彩色插件池取代。
        //   先留着（里面的中文描述和数值是历史记录），确认新池子跑通后再整块删掉。
        private static readonly List<DungeonBuffDef> LegacyUnused = new List<DungeonBuffDef>
        {
            // ══════════════════════════════════════════════════════
            //   第十周任务书里点名的效果，必须全部存在：
            //     · 范围伤害（越叠越大）        -> "aoe"
            //     · 霰弹（越多子弹）            -> "shotgun"
            //     · 连发（单击多打一轮，+0.5 蓝耗）-> "volley"
            //     · 反弹（撞墙弹更多次）        -> "bounce"
            //     · 穿透（穿过更多敌人）        -> "pierce"
            //   原来的「高爆弹头 / 快扳机 / 省电模式」三条已按要求删掉，
            //   只保留一条「省电模式」当作缓解蓝耗压力的选项。
            // ══════════════════════════════════════════════════════

            new DungeonBuffDef
            {
                id = "aoe", name = "爆炸弹头",
                desc = "子弹命中怪物或墙壁时产生爆炸：\n半径 2.5 米内的其它怪物受到 80% 伤害。\n" +
                       "再获得一次，爆炸半径继续增加。",
                weight = 10f, maxStacks = 3,
                manaCostMul  = 1.15f,
            },

            new DungeonBuffDef
            {
                id = "shotgun", name = "霰弹改造",
                desc = "开火时额外发射 2 颗子弹，\n在 16° 扇形里铺开。\n" +
                       "再获得一次，继续 +2 颗。",
                weight = 10f, maxStacks = 3,
                extraPellets    = 2,
                manaCostMul     = 1.2f,
                bulletSpreadMul = 1.15f,
            },

            new DungeonBuffDef
            {
                id = "volley", name = "连发装置",
                desc = "每次单击额外开火 1 轮，\n但单发基础蓝耗 +0.5。\n" +
                       "再获得一次，多打一轮、多 +0.5 蓝耗。",
                weight = 10f, maxStacks = 3,
                extraVolleys = 1,
                manaCostAdd  = 0.5f,
            },

            new DungeonBuffDef
            {
                id = "bounce", name = "跳弹",
                desc = "子弹撞墙后会反弹，\n每层多弹一次。\n" +
                       "再获得一次，反弹次数 +1。",
                weight = 10f, maxStacks = 3,
                bounceAdd   = 1,
                manaCostMul = 1.1f,
            },

            new DungeonBuffDef
            {
                id = "pierce", name = "穿甲弹",
                desc = "子弹第一次命中敌人时会穿过它继续飞，\n每层多穿一只。\n" +
                       "再获得一次，穿透数 +1。",
                weight = 10f, maxStacks = 3,
                pierceAdd   = 1,
                manaCostMul = 1.1f,
            },

            // 唯一保留的"减负"选项：上面的 buff 都在加蓝耗，总得有一条能压回来
            new DungeonBuffDef
            {
                id = "eco", name = "省电模式",
                desc = "蓝耗 ×0.75。\n叠得越多，省得越多。",
                weight = 10f, maxStacks = 3,
                manaCostMul = 0.75f,
            },

            // ── 下面两条是后来加回来的（编号见需求 4）────────────────
            new DungeonBuffDef
            {
                id = "dmg", name = "高爆弹头",
                desc = "伤害 ×2，射速 ×0.75，蓝耗 ×1.75。\n" +
                       "打得慢、也更费蓝，但一发顶两发。",
                weight = 10f, maxStacks = 3,
                damageMul   = 2f,
                fireRateMul = 0.75f,
                manaCostMul = 1.75f,
            },

            new DungeonBuffDef
            {
                id = "rate", name = "快扳机",
                desc = "射速 ×2，蓝耗 ×1.25。\n" +
                       "打得快，稍微费一点蓝。",
                weight = 10f, maxStacks = 3,
                fireRateMul = 2f,
                manaCostMul = 1.25f,
            },
        };

        // ══════════════════════════════════════════════════════════════
        //   第十一周：六色插件池
        // ══════════════════════════════════════════════════════════════
        //
        //   白 / 绿 / 蓝 / 红 = 纯数值插件（伤害 / 射速 / 蓝耗 各一条）
        //   紫 / 橙         = 五个机制插件（爆炸弹头 / 霰弹 / 连发 / 跳弹 / 穿甲）
        //
        //   ★ 紫插件额外 ×1.5 蓝耗，橙插件没有额外蓝耗 —— 这是"想要机制就得付出"。
        //   ★ 同效果跨颜色的等级合并靠 effectId（"aoe" / "shotgun" / "volley" / "bounce" / "pierce"），
        //     所以不管紫的还是橙的爆炸弹头，装两个就是 Lv2。
        //   ★ maxStacks = 该颜色"同效果最多能拿几个"：
        //       白 3、绿 2、蓝 1、紫 1、橙 2、红 无限（用 99 表示）
        public static readonly List<DungeonBuffDef> All = BuildColoredPool();

        private static List<DungeonBuffDef> BuildColoredPool()
        {
            var list = new List<DungeonBuffDef>(32);

            // ---- 纯数值：白 / 绿 / 蓝 / 红 ----
            AddStatTrio(list, PluginColor.White,  "白", 1.5f,  1.8f,  0.6f,  3);
            AddStatTrio(list, PluginColor.Green,  "绿", 1.75f, 2.25f, 0.45f, 2);
            AddStatTrio(list, PluginColor.Blue,   "蓝", 2.25f, 3.24f, 0.36f, 1);
            AddStatTrio(list, PluginColor.Red,    "红", 0.9f,  0.8f,  1.5f,  99);

            // ---- 机制：紫（蓝耗 ×1.5）/ 橙（不额外加蓝耗）----
            AddMechanicSet(list, PluginColor.Purple, "紫", 1.5f, 1);
            AddMechanicSet(list, PluginColor.Orange, "橙", 1f,   2);

            return list;
        }

        /// <summary>
        /// 一条颜色的「伤害 / 射速 / 蓝耗」三件套。
        ///
        /// ★ 名字里带上数值，例：「白·伤害*1.5」「红·射速*0.8」。
        ///   （在原来的 prefix + "·XXX" 后面直接再拼一段，不改原结构）
        /// </summary>
        private static void AddStatTrio(List<DungeonBuffDef> list, PluginColor color, string prefix,
                                        float dmg, float rate, float mana, int max)
        {
            string tag = $"{(int)color}";

            list.Add(new DungeonBuffDef
            {
                id = tag + "-dmg", effectId = "dmg", color = color,
                effectName = "伤害",
                name = prefix + "·伤害*" + dmg.ToString("0.##"),
                desc = $"伤害 ×{dmg:0.##}\n（{SlotRules.NameOf(color)}插件）",
                weight = 10f, maxStacks = max,
                damageMul = dmg,
            });

            list.Add(new DungeonBuffDef
            {
                id = tag + "-rate", effectId = "rate", color = color,
                effectName = "射速",
                name = prefix + "·射速*" + rate.ToString("0.##"),
                desc = $"射速 ×{rate:0.##}\n（{SlotRules.NameOf(color)}插件）",
                weight = 10f, maxStacks = max,
                fireRateMul = rate,
            });

            list.Add(new DungeonBuffDef
            {
                id = tag + "-mana", effectId = "mana", color = color,
                effectName = "蓝耗",
                name = prefix + "·蓝耗*" + mana.ToString("0.##"),
                desc = $"蓝耗 ×{mana:0.##}\n（{SlotRules.NameOf(color)}插件）",
                weight = 10f, maxStacks = max,
                manaCostMul = mana,
            });
        }

        /// <summary>
        /// 一条颜色的五个机制插件。
        ///
        /// ★ 名字后面带上蓝耗倍率，例：「紫·爆炸弹头（蓝耗*1.5）」。
        ///   橙色没有额外蓝耗（manaMul = 1），所以**不加**这个后缀。
        ///   （在原来的 prefix + "·XXX" 后面直接再拼一段，不改原结构）
        /// </summary>
        private static void AddMechanicSet(List<DungeonBuffDef> list, PluginColor color, string prefix,
                                           float manaMul, int max)
        {
            string tag  = $"{(int)color}";
            string cost = manaMul > 1.0001f ? $"\n蓝耗 ×{manaMul:0.##}" : "";
            string kind = SlotRules.NameOf(color);

            // ★ 名字后缀：蓝耗 > 1 才加，格式「（蓝耗*1.5）」
            string nameSuffix = manaMul > 1.0001f ? $"（蓝耗*{manaMul:0.##}）" : "";

            list.Add(new DungeonBuffDef
            {
                id = tag + "-aoe", effectId = "aoe", color = color,
                effectName = "爆炸弹头",
                name = prefix + "·爆炸弹头" + nameSuffix,
                desc = "子弹命中怪物或墙壁时爆炸，\n范围内的其它怪物受到伤害。" + cost,
                weight = 10f, maxStacks = max,
                manaCostMul = manaMul,
            });

            list.Add(new DungeonBuffDef
            {
                id = tag + "-shotgun", effectId = "shotgun", color = color,
                effectName = "霰弹改造",
                name = prefix + "·霰弹改造" + nameSuffix,
                desc = "开火时额外发射 2 颗子弹，呈扇形铺开。\n再装一份，扇形更宽。" + cost,
                weight = 10f, maxStacks = max,
                extraPellets = 2,
                manaCostMul  = manaMul,
                bulletSpreadMul = 1.15f,
            });

            list.Add(new DungeonBuffDef
            {
                id = tag + "-volley", effectId = "volley", color = color,
                effectName = "连发装置",
                name = prefix + "·连发装置" + nameSuffix,
                desc = "每次单击额外开火一轮。" + cost,
                weight = 10f, maxStacks = max,
                extraVolleys = 1,
                manaCostMul  = manaMul,
            });

            list.Add(new DungeonBuffDef
            {
                id = tag + "-bounce", effectId = "bounce", color = color,
                effectName = "跳弹",
                name = prefix + "·跳弹" + nameSuffix,
                desc = "子弹撞墙后反弹，每份多弹一次。" + cost,
                weight = 10f, maxStacks = max,
                bounceAdd   = 1,
                manaCostMul = manaMul,
            });

            list.Add(new DungeonBuffDef
            {
                id = tag + "-pierce", effectId = "pierce", color = color,
                effectName = "穿甲弹",
                name = prefix + "·穿甲弹" + nameSuffix,
                desc = "子弹命中敌人时穿过它继续飞，每份多穿一只。" + cost,
                weight = 10f, maxStacks = max,
                pierceAdd   = 1,
                manaCostMul = manaMul,
            });

            _ = kind;
        }

        /// <summary>
        /// 抽 <paramref name="count"/> 条**互不相同**的 buff。
        /// 已经叠满的（达到 maxStacks）不会再出现。
        /// </summary>
        public static List<DungeonBuffDef> Roll(int count, DungeonBuffState state)
        {
            var result = new List<DungeonBuffDef>(count);
            if (count <= 0) return result;

            // ★ 第十一周：三选一只出白 / 绿 / 紫，颜色概率 6 : 2 : 1
            //   蓝插件是"模板升到紫色时三选一"，橙插件只从商店出，红色也只能商店买
            var byColor = new List<DungeonBuffDef>[3];
            for (int c = 0; c < 3; c++) byColor[c] = new List<DungeonBuffDef>();

            for (int i = 0; i < All.Count; i++)
            {
                DungeonBuffDef d = All[i];
                if (d == null) continue;

                int ci = ColorIndexForPick(d.color);
                if (ci < 0) continue;

                // 达到"这个颜色同效果最多几个"就不再出现
                if (state != null && state.StacksOf(d) >= d.maxStacks) continue;

                byColor[ci].Add(d);
            }

            for (int n = 0; n < count; n++)
            {
                float total = 0f;
                for (int c = 0; c < 3; c++)
                    if (byColor[c].Count > 0) total += PickWeights[c];

                if (total <= 0f) break;      // 三色都抽干了

                float pick = Random.value * total;
                int chosenColor = -1;

                for (int c = 0; c < 3; c++)
                {
                    if (byColor[c].Count == 0) continue;

                    pick -= PickWeights[c];
                    if (pick <= 0f) { chosenColor = c; break; }
                }

                // 浮点误差兜底
                if (chosenColor < 0)
                    for (int c = 2; c >= 0; c--)
                        if (byColor[c].Count > 0) { chosenColor = c; break; }

                var list = byColor[chosenColor];

                int idx = WeightedIndex(list);
                result.Add(list[idx]);
                list.RemoveAt(idx);          // 保证三张不重复
            }

            return result;
        }

        /// <summary>三选一的颜色权重：白 6、绿 2、紫 1。</summary>
        public static readonly float[] PickWeights = { 6f, 2f, 1f };

        private static int ColorIndexForPick(PluginColor c)
        {
            switch (c)
            {
                case PluginColor.White:  return 0;
                case PluginColor.Green:  return 1;
                case PluginColor.Purple: return 2;
                default:                 return -1;      // 蓝 / 橙 / 红 不进三选一
            }
        }

        private static int WeightedIndex(List<DungeonBuffDef> list)
        {
            float total = 0f;
            for (int i = 0; i < list.Count; i++) total += Mathf.Max(0.01f, list[i].weight);

            float pick = Random.value * total;

            for (int i = 0; i < list.Count; i++)
            {
                pick -= Mathf.Max(0.01f, list[i].weight);
                if (pick <= 0f) return i;
            }

            return list.Count - 1;
        }

        /// <summary>取某个颜色的全部定义（蓝插件三选一、商店橙插件用）。</summary>
        public static List<DungeonBuffDef> ByColor(PluginColor color)
        {
            var list = new List<DungeonBuffDef>(8);

            for (int i = 0; i < All.Count; i++)
                if (All[i] != null && All[i].color == color) list.Add(All[i]);

            return list;
        }
    }
}