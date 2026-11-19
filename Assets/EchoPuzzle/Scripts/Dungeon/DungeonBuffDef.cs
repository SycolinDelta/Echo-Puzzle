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
        public static readonly List<DungeonBuffDef> All = new List<DungeonBuffDef>
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

        /// <summary>
        /// 抽 <paramref name="count"/> 条**互不相同**的 buff。
        /// 已经叠满的（达到 maxStacks）不会再出现。
        /// </summary>
        public static List<DungeonBuffDef> Roll(int count, DungeonBuffState state)
        {
            var result = new List<DungeonBuffDef>(count);
            if (count <= 0) return result;

            // 候选：过滤掉叠满的
            var candidates = new List<DungeonBuffDef>(All.Count);
            for (int i = 0; i < All.Count; i++)
            {
                DungeonBuffDef d = All[i];
                if (d == null) continue;
                if (state != null && state.StacksOf(d) >= d.maxStacks) continue;
                candidates.Add(d);
            }

            for (int n = 0; n < count && candidates.Count > 0; n++)
            {
                // 加权随机挑一条，挑完移出候选，保证三张不重复
                float total = 0f;
                for (int i = 0; i < candidates.Count; i++) total += Mathf.Max(0.01f, candidates[i].weight);

                float pick = Random.value * total;
                int chosen = candidates.Count - 1;

                for (int i = 0; i < candidates.Count; i++)
                {
                    pick -= Mathf.Max(0.01f, candidates[i].weight);
                    if (pick <= 0f) { chosen = i; break; }
                }

                result.Add(candidates[chosen]);
                candidates.RemoveAt(chosen);
            }

            return result;
        }
    }
}