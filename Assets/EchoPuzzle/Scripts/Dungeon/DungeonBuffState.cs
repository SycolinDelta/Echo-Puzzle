using System.Collections.Generic;
using UnityEngine;

namespace EchoPuzzle.Dungeon
{
    /// <summary>
    /// 【地牢模式】玩家拥有的插件（buff）与两个武器模板的插槽。
    ///
    /// ── 模型（第十周任务 5）──────────────────────────────────
    ///   · 每种 buff 在背包里**只有一个实例**，重复获得 = **升级**（最高 maxStacks 级）
    ///   · 两个武器模板各有一套插槽，按 E 切换；一个实例可以同时装在两个模板上
    ///   · **只有插进当前模板槽里的插件才生效**（没插就是背包里的摆件）
    ///   · 每推进 2 层，每个模板 +1 个插槽（初始 3 个）
    ///
    /// ── 等级怎么作用到数值 ──────────────────────────────────
    ///   乘区：pow(值, 等级)。例：蓝耗 ×0.75 的 Lv2 = 0.5625
    ///   加法：值 × 等级。  例：爆炸半径 +3.5 的 Lv2 = +7
    /// </summary>
    [AddComponentMenu("Echo Puzzle/Dungeon/Dungeon Buff State")]
    public sealed class DungeonBuffState : MonoBehaviour
    {
        public static DungeonBuffState Instance { get; private set; }

        /// <summary>数值 / 插槽变化时触发。</summary>
        public event System.Action Changed;

        // ------------------------------------------------------------------ 背包

        /// <summary>拥有的插件（每种 buff 最多一个实例）。</summary>
        private readonly List<BuffInstance> m_Instances = new List<BuffInstance>(16);

        /// <summary>拥有的插件（按获得顺序）。</summary>
        public IReadOnlyList<BuffInstance> Owned => m_Instances;

        /// <summary>拥有几种插件。</summary>
        public int Count => m_Instances.Count;

        // ------------------------------------------------------------------ 插槽

        public const int BaseSlots = 3;
        public const int FloorsPerExtraSlot = 2;
        public const int TemplateCount = 2;

        /// <summary>每个模板当前的插槽数：初始 3 个，每推进 2 层 +1。</summary>
        public static int SlotCount
            => BaseSlots + Mathf.Max(0, StageIndex - 1) / FloorsPerExtraSlot;

        /// <summary>当前使用的模板（0 / 1），按 E 切换。</summary>
        public int ActiveTemplate { get; private set; }

        private readonly List<BuffInstance>[] m_Slots =
        {
            new List<BuffInstance>(8),
            new List<BuffInstance>(8),
        };

        // ------------------------------------------------------------------ 合成后的数值

        public float DamageMul      { get; private set; } = 1f;
        public float FireRateMul    { get; private set; } = 1f;
        public float ManaCostMul    { get; private set; } = 1f;
        public float KillManaMul    { get; private set; } = 1f;
        public float BulletSpeedMul { get; private set; } = 1f;
        public float BulletWidthMul { get; private set; } = 1f;
        public float BulletSpreadMul { get; private set; } = 1f;

        public int   MaxManaAdd { get; private set; }
        public int   ExtraPellets { get; private set; }
        public int   ExtraVolleys { get; private set; }
        public int   BounceCount { get; private set; }
        public int   PierceCount { get; private set; }
        public float ManaCostAdd { get; private set; }

        private int m_AppliedMaxBonus;

        // ------------------------------------------------------------------ 生命周期

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }

            Instance = this;
            Recompute();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ------------------------------------------------------------------ 背包 API

        /// <summary>某种 buff 的等级（没拥有 = 0）。</summary>
        public int LevelOf(DungeonBuffDef def)
        {
            if (def == null) return 0;

            for (int i = 0; i < m_Instances.Count; i++)
                if (m_Instances[i] != null && m_Instances[i].def != null && m_Instances[i].def.id == def.id)
                    return m_Instances[i].level;

            return 0;
        }

        /// <summary>兼容旧调用：等级（没拥有 = 0）。</summary>
        public int StacksOf(DungeonBuffDef def) => LevelOf(def);

        /// <summary>取某种 buff 的实例（没有返回 null）。</summary>
        public BuffInstance Find(DungeonBuffDef def)
        {
            if (def == null) return null;

            for (int i = 0; i < m_Instances.Count; i++)
                if (m_Instances[i] != null && m_Instances[i].def != null && m_Instances[i].def.id == def.id)
                    return m_Instances[i];

            return null;
        }

        /// <summary>
        /// 获得一条 buff：没有就新建 Lv1，已有就**升一级**（封顶 maxStacks）。
        /// </summary>
        public void Add(DungeonBuffDef def)
        {
            if (def == null) return;

            BuffInstance inst = Find(def);

            if (inst == null)
            {
                inst = new BuffInstance(def, 1);
                m_Instances.Add(inst);

                // 新插件自动装进当前模板的第一个空槽
                int free = FirstFreeSlot(ActiveTemplate);
                if (free >= 0) SetSlotInternal(ActiveTemplate, free, inst);
            }
            else if (!inst.IsMax)
            {
                inst.level++;
            }

            Recompute();
        }

        /// <summary>清空背包与插槽（重开一局用）。</summary>
        public void Clear()
        {
            if (m_Instances.Count == 0 && UsedSlots(0) == 0 && UsedSlots(1) == 0) return;

            m_Instances.Clear();

            for (int t = 0; t < m_Slots.Length; t++) m_Slots[t].Clear();

            ActiveTemplate = 0;
            Recompute();
        }

        // ------------------------------------------------------------------ 插槽 API

        /// <summary>取某个模板某个槽里的插件（越界 / 空槽返回 null）。</summary>
        public BuffInstance SlotAt(int template, int index)
        {
            List<BuffInstance> list = SlotsOf(template);
            if (list == null || index < 0 || index >= list.Count) return null;
            return list[index];
        }

        public int UsedSlots(int template)
        {
            List<BuffInstance> list = SlotsOf(template);
            if (list == null) return 0;

            int n = 0;
            int limit = Mathf.Min(list.Count, SlotCount);
            for (int i = 0; i < limit; i++) if (list[i] != null) n++;
            return n;
        }

        public int FirstFreeSlot(int template)
        {
            List<BuffInstance> list = SlotsOf(template);
            if (list == null) return -1;

            for (int i = 0; i < SlotCount; i++)
            {
                if (i >= list.Count) return i;
                if (list[i] == null) return i;
            }

            return -1;
        }

        /// <summary>
        /// 把插件放进某个槽。
        /// <returns>放不下返回 false（同一模板里已经装过这一个插件实例了）</returns>
        /// </summary>
        public bool SetSlot(int template, int index, BuffInstance inst)
        {
            if (index < 0 || index >= SlotCount) return false;
            if (SlotAt(template, index) == inst) return true;

            // ★ 一个模板里，同一个插件实例只能占一个槽（不会出现"一份插件插三格"）
            if (inst != null && CountInTemplate(template, inst) > 0) return false;

            SetSlotInternal(template, index, inst);
            Recompute();
            return true;
        }

        public void ClearSlot(int template, int index) => SetSlot(template, index, null);

        /// <summary>这个模板里装了几个该实例（只可能是 0 或 1）。</summary>
        public int CountInTemplate(int template, BuffInstance inst)
        {
            if (inst == null) return 0;

            List<BuffInstance> list = SlotsOf(template);
            if (list == null) return 0;

            for (int i = 0; i < list.Count; i++)
                if (list[i] == inst) return 1;

            return 0;
        }

        /// <summary>切换当前使用的模板（按 E）。</summary>
        public void SetActiveTemplate(int template)
        {
            template = Mathf.Clamp(template, 0, TemplateCount - 1);
            if (template == ActiveTemplate) return;

            ActiveTemplate = template;
            Recompute();
        }

        private List<BuffInstance> SlotsOf(int template)
        {
            if (m_Slots == null || m_Slots.Length == 0) return null;
            return m_Slots[Mathf.Clamp(template, 0, m_Slots.Length - 1)];
        }

        private void SetSlotInternal(int template, int index, BuffInstance inst)
        {
            List<BuffInstance> list = SlotsOf(template);
            if (list == null || index < 0) return;

            while (list.Count <= index) list.Add(null);

            list[index] = inst;
        }

        // ------------------------------------------------------------------ 计算

        /// <summary>只累加**当前模板已插进槽里**的插件。</summary>
        public void Recompute()
        {
            float dmg = 1f, rate = 1f, mana = 1f, kill = 1f;
            float speed = 1f, width = 1f, spread = 1f;
            int   maxAdd = 0, pellets = 0, volleys = 0, bounces = 0, pierce = 0;
            float manaAdd = 0f;

            List<BuffInstance> active = SlotsOf(ActiveTemplate);

            for (int i = 0; active != null && i < active.Count; i++)
            {
                BuffInstance inst = active[i];
                if (inst == null || inst.def == null) continue;

                DungeonBuffDef b = inst.def;
                int lv = Mathf.Max(1, inst.level);

                // 乘区：按等级取幂
                dmg    *= Mathf.Pow(b.damageMul, lv);
                rate   *= Mathf.Pow(b.fireRateMul, lv);
                mana   *= Mathf.Pow(b.manaCostMul, lv);
                kill   *= Mathf.Pow(b.killManaMul, lv);
                speed  *= Mathf.Pow(b.bulletSpeedMul, lv);
                width  *= Mathf.Pow(b.bulletWidthMul, lv);
                spread *= Mathf.Pow(b.bulletSpreadMul, lv);

                // 加法：按等级乘
                maxAdd  += b.maxManaAdd * lv;
                pellets += b.extraPellets * lv;
                // ★ aoeRadiusAdd 已删除（爆炸半径改由 aoeRadiusByLevel 等级表决定）
                volleys += b.extraVolleys * lv;
                bounces += b.bounceAdd * lv;
                pierce  += b.pierceAdd * lv;
                manaAdd += b.manaCostAdd * lv;
            }

            DamageMul      = Round3(dmg);
            FireRateMul    = Round3(rate);
            ManaCostMul    = Round3(mana);
            KillManaMul    = Round3(kill);
            BulletSpeedMul = Round3(speed);
            BulletWidthMul = Round3(width);
            BulletSpreadMul = Round3(spread);

            MaxManaAdd   = maxAdd;
            ExtraPellets = Mathf.Max(0, pellets);
            // ★ AoeRadius 已删除（爆炸半径改由 aoeRadiusByLevel 决定）
            ExtraVolleys = Mathf.Max(0, volleys);
            BounceCount  = Mathf.Max(0, bounces);
            PierceCount  = Mathf.Max(0, pierce);
            ManaCostAdd  = Round3(Mathf.Max(0f, manaAdd));

            ApplyMaxMana(maxAdd);

            Changed?.Invoke();
        }

        // ------------------------------------------------------------------ 层数缩放

        public const float StageScalePerFloor = 1.25f;

        public static float StageScale => s_StageScale;

        private static float s_StageScale = 1f;
        private static int   s_StageIndex = 1;

        public static int StageIndex => s_StageIndex;

        /// <summary>设置当前层号（1 起），并重算 StageScale 与插槽上限。</summary>
        public static void SetStage(int stageIndex)
        {
            s_StageIndex = Mathf.Max(1, stageIndex);

            float s = 1f;
            for (int i = 1; i < s_StageIndex; i++) s *= StageScalePerFloor;

            s_StageScale = Round3(s);

            DungeonBuffState inst = Instance;
            if (inst != null) inst.RaiseChanged();
        }

        private void RaiseChanged()
        {
            Changed?.Invoke();
        }

        /// <summary>保留三位小数。</summary>
        public static float Round3(float v) => Mathf.Round(v * 1000f) / 1000f;

        /// <summary>把「蓝上限加成」落实到 DungeonMana 上。</summary>
        private void ApplyMaxMana(int add)
        {
            DungeonMana mana = DungeonMana.Instance;
            if (mana == null) return;

            int delta = add - m_AppliedMaxBonus;
            if (delta == 0) return;

            m_AppliedMaxBonus = add;

            mana.SetMax(mana.BaseMax + add);

            if (delta > 0) mana.Restore(delta);
        }
    }
}