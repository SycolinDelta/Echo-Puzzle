using System.Collections.Generic;
using UnityEngine;

namespace EchoPuzzle.Dungeon
{
    /// <summary>
    /// 【地牢模式】玩家已获得的 buff，以及它们叠出来的最终数值。
    ///
    /// ── 分工 ────────────────────────────────────────────────
    ///   · 这里只负责**累加**：把 N 条 buff 的乘区乘起来、加法加起来
    ///   · <see cref="DungeonWeapon"/> 每开一枪读一次这些值，不自己存
    ///   · 谁想加 buff 就 <see cref="Add"/>，谁想清空就 <see cref="Clear"/>
    ///
    /// ── 叠加规则 ────────────────────────────────────────────
    ///   乘区相乘：两个「蓝耗 -30%」= 0.7 × 0.7 = 0.49
    ///   加法相加：两个「蓝上限 +50」= +100
    ///   同一 buff 能叠几层由它自己的 maxStacks 决定（抽卡时会过滤）。
    /// </summary>
    [AddComponentMenu("Echo Puzzle/Dungeon/Dungeon Buff State")]
    public sealed class DungeonBuffState : MonoBehaviour
    {
        public static DungeonBuffState Instance { get; private set; }

        /// <summary>buff 列表或数值变化时触发（UI 想刷新就挂这个）。</summary>
        public event System.Action Changed;

        private readonly List<DungeonBuffDef> m_Owned = new List<DungeonBuffDef>(16);

        /// <summary>已获得的 buff（按获得顺序，允许重复 id）。</summary>
        public IReadOnlyList<DungeonBuffDef> Owned => m_Owned;

        /// <summary>一共拿了几个。</summary>
        public int Count => m_Owned.Count;

        // ---- 叠完之后的最终乘区（1 = 无效果）----

        public float DamageMul      { get; private set; } = 1f;
        public float FireRateMul    { get; private set; } = 1f;
        public float ManaCostMul    { get; private set; } = 1f;
        public float KillManaMul    { get; private set; } = 1f;
        public float BulletSpeedMul { get; private set; } = 1f;
        public float BulletWidthMul { get; private set; } = 1f;

        /// <summary>子弹最大偏转角倍率（&gt;1 = 更飘，&lt;1 = 更准）。</summary>
        public float BulletSpreadMul { get; private set; } = 1f;

        /// <summary>蓝上限加成（加法）。</summary>
        public int MaxManaAdd { get; private set; }

        /// <summary>上一次已经应用过的上限加成，用来算「这次又涨了多少」。</summary>
        private int m_AppliedMaxBonus;

        // ------------------------------------------------------------------ 生命周期

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }

            Instance = this;
            Recompute();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ------------------------------------------------------------------ 对外

        /// <summary>某个 buff 已经叠了几层。</summary>
        public int StacksOf(DungeonBuffDef def)
        {
            if (def == null) return 0;

            int n = 0;
            for (int i = 0; i < m_Owned.Count; i++)
                if (m_Owned[i] != null && m_Owned[i].id == def.id) n++;

            return n;
        }

        /// <summary>获得一条 buff。</summary>
        public void Add(DungeonBuffDef def)
        {
            if (def == null) return;

            m_Owned.Add(def);
            Recompute();
        }

        /// <summary>清空（重开一局 / 换层不保留时用）。</summary>
        public void Clear()
        {
            if (m_Owned.Count == 0) return;

            m_Owned.Clear();
            Recompute();
        }

        /// <summary>把已获得 buff 的乘区重新算一遍。</summary>
        public void Recompute()
        {
            float dmg = 1f, rate = 1f, mana = 1f, kill = 1f;
            float speed = 1f, width = 1f, spread = 1f;
            int   maxAdd = 0;

            for (int i = 0; i < m_Owned.Count; i++)
            {
                DungeonBuffDef b = m_Owned[i];
                if (b == null) continue;

                dmg   *= b.damageMul;
                rate  *= b.fireRateMul;
                mana  *= b.manaCostMul;
                kill  *= b.killManaMul;
                speed  *= b.bulletSpeedMul;
                width  *= b.bulletWidthMul;
                spread *= b.bulletSpreadMul;

                maxAdd += b.maxManaAdd;
            }

            // ★ 乘算结果保留**三位小数**（需求："所有这些会因为乘算带来小数的数字保留三位小数"）
            //
            //   例：0.75 × 0.75 × 1.5 = 0.84375 -> 0.844
            //       2 × 2 × 1.5 = 6              -> 6（整数不动）
            DamageMul      = Round3(dmg);
            FireRateMul    = Round3(rate);
            ManaCostMul    = Round3(mana);
            KillManaMul    = Round3(kill);
            BulletSpeedMul   = Round3(speed);
            BulletWidthMul   = Round3(width);
            BulletSpreadMul  = Round3(spread);

            MaxManaAdd = maxAdd;

            ApplyMaxMana(maxAdd);

            Changed?.Invoke();
        }

        // ------------------------------------------------------------------ 层数缩放

        /// <summary>
        /// 每过一层，怪物血量和击杀回蓝乘以这个系数。
        ///
        /// 需求："每进入新的一层，怪物的血量就会 *1.25，
        ///       击杀怪物获得的蓝量也会 *1.25。"
        /// </summary>
        public const float StageScalePerFloor = 1.25f;

        /// <summary>
        /// 当前层对应的缩放系数（第 1 层 = 1，第 2 层 = 1.25，第 3 层 = 1.562…）。
        ///
        /// ★ 由 <see cref="DungeonMapGenerator"/> 在每次换层时调用
        ///   <see cref="SetStage"/> 更新。
        /// ★ 保留三位小数。
        /// </summary>
        public static float StageScale => s_StageScale;

        /// <summary>
        /// 层数缩放的**静态后备**。
        ///
        /// ★ 为什么不用实例字段：DungeonMapGenerator.Start() 可能比
        ///   DungeonWeaponBootstrap 更早跑到（换层时也一样），
        ///   那时 Instance 可能还是 null。用静态字段就没有时序问题。
        /// </summary>
        private static float s_StageScale = 1f;

        /// <summary>层号也用静态后备。</summary>
        private static int s_StageIndex = 1;

        /// <summary>当前层号（从 1 开始，方便显示）。</summary>
        public static int StageIndex => s_StageIndex;

        /// <summary>
        /// 设置当前层号（1 起），并重算 <see cref="StageScale"/>。
        ///
        /// ★ 幂等：传同一个层号不会重复累乘。
        /// </summary>
        public static void SetStage(int stageIndex)
        {
            s_StageIndex = Mathf.Max(1, stageIndex);

            float s = 1f;

            for (int i = 1; i < s_StageIndex; i++)
                s *= StageScalePerFloor;

            s_StageScale = Round3(s);

            // ★ 事件不能在类外用 ?. 触发，所以交给实例方法做
            DungeonBuffState inst = Instance;
            if (inst != null) inst.RaiseChanged();
        }

        /// <summary>手动触发一次 Changed（给静态 SetStage 用）。</summary>
        private void RaiseChanged()
        {
            Changed?.Invoke();
        }

        /// <summary>
        /// 保留三位小数。
        ///
        /// ★ 乘算会引入浮点尾数（0.75 × 0.75 = 0.5625，再多乘几次就是 0.421875…），
        ///   这里统一收到三位，UI 显示和实际数值都一致。
        ///
        /// ★ 顺便消掉 float 的表示误差（比如 0.30000001 -> 0.3）。
        /// </summary>
        public static float Round3(float v)
        {
            return Mathf.Round(v * 1000f) / 1000f;
        }

        /// <summary>
        /// 把「蓝上限加成」落实到 <see cref="DungeonMana"/> 上。
        ///
        /// ★ 上限涨了要把**涨出来的这部分补成实蓝**，
        ///   否则拿了「扩容电池」只是条变长、当前蓝没变，玩家会觉得没生效。
        /// </summary>
        private void ApplyMaxMana(int add)
        {
            DungeonMana mana = DungeonMana.Instance;
            if (mana == null) return;

            int delta = add - m_AppliedMaxBonus;
            if (delta == 0) return;

            m_AppliedMaxBonus = add;

            // ★ BaseMax 现在是 float，加 int 后仍是 float
            mana.SetMax(mana.BaseMax + add);

            if (delta > 0) mana.Restore(delta);
        }
    }
}
