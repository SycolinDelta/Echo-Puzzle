using System.Collections.Generic;
using UnityEngine;

namespace EchoPuzzle.Dungeon
{
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

        public bool HasEffect =>
            !Mathf.Approximately(damageMul, 1f) ||
            !Mathf.Approximately(fireRateMul, 1f) ||
            !Mathf.Approximately(manaCostMul, 1f) ||
            !Mathf.Approximately(killManaMul, 1f) ||
            !Mathf.Approximately(bulletSpeedMul, 1f) ||
            !Mathf.Approximately(bulletWidthMul, 1f) ||
            !Mathf.Approximately(bulletSpreadMul, 1f) ||
            maxManaAdd != 0;
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
            // ★★ 三条 buff 全部**乘算**（多层叠加时连乘）★★
            //
            //   需求原文：
            //     · 蓝耗 *0.75
            //     · 子弹伤害 *2，且蓝耗 *2
            //     · 射速 *2，且蓝耗 *1.25
            //
            //   ★ 带小数的乘积一律保留三位小数（见 DungeonBuffState 里的 Round3）。

            new DungeonBuffDef
            {
                id = "dmg", name = "高爆弹头",
                desc = "子弹伤害 ×2，但蓝耗 ×1.75，射速 ×0.75。\n" +
                       "子弹散布 ×0.75（打得更准）。\n每层可叠，伤害和蓝耗一起翻倍。",
                weight = 10f, maxStacks = 3,
                damageMul       = 2f,
                manaCostMul     = 1.75f,
                fireRateMul     = 0.75f,
                bulletSpreadMul = 0.75f,
            },

            new DungeonBuffDef
            {
                id = "rate", name = "快扳机",
                desc = "射速 ×2，但蓝耗 ×1.5。\n" +
                       "子弹散布 ×1.25（打得更飘）。\n打得快，也更费蓝。",
                weight = 10f, maxStacks = 3,
                fireRateMul     = 2f,
                manaCostMul     = 1.5f,
                bulletSpreadMul = 1.25f,
            },

            new DungeonBuffDef
            {
                id = "eco", name = "省电模式",
                desc = "蓝耗 ×0.75。\n叠得越多，省得越多。",
                weight = 10f, maxStacks = 3,
                manaCostMul = 0.75f,
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