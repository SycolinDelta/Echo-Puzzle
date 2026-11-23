using System.Collections.Generic;
using UnityEngine;

namespace EchoPuzzle.Dungeon
{
    /// <summary>
    /// 【地牢模式】玩家拥有的插件、两个武器模板的六色插槽、以及合成后的最终数值。
    ///
    /// ── 模型（第十一周）────────────────────────────────────
    ///   · **一份插件 = 一个实例**。拿到 3 个白色伤害插件就是 3 个实例、占 3 个白槽。
    ///   · 槽位有颜色，插件必须插进同色槽（<see cref="SlotRules"/> 算容量）。
    ///   · 两个模板各有一套槽；一个实例可以同时装在两个模板上。
    ///   · 只有插进**当前模板**槽里的插件才生效。
    ///   · 武器模板等级（白→橙）由两个模板共享，通关第 5/10 层各升一次，商店还能升 2 次。
    ///
    /// ── 数值怎么合成 ────────────────────────────────────────
    ///   乘区：每个实例按 pow(值, 等级) 连乘（等级现在恒为 1，保留字段方便以后扩展）
    ///   加法：每个实例按 值 × 等级 累加
    ///   机制等级（爆炸弹头 / 霰弹）按 effectId 跨颜色汇总，见 <see cref="MechanicLevel"/>
    /// </summary>
    [AddComponentMenu("Echo Puzzle/Dungeon/Dungeon Buff State")]
    public sealed class DungeonBuffState : MonoBehaviour
    {
        public static DungeonBuffState Instance { get; private set; }

        /// <summary>数值 / 插槽 / 模板等级变化时触发。</summary>
        public event System.Action Changed;

        public const int TemplateCount = 2;

        // ------------------------------------------------------------------ 背包

        private readonly List<BuffInstance> m_Instances = new List<BuffInstance>(24);

        /// <summary>拥有的插件实例（按获得顺序，同名会有多个）。</summary>
        public IReadOnlyList<BuffInstance> Owned => m_Instances;

        public int Count => m_Instances.Count;

        // ------------------------------------------------------------------ 插槽

        /// <summary>每个模板的六色已装列表：[模板][颜色] -> 实例列表（不含空位）。</summary>
        private readonly List<BuffInstance>[,] m_Slots =
            new List<BuffInstance>[TemplateCount, SlotRules.ColorCount];

        /// <summary>当前使用的模板（0 / 1），按 E 切换。</summary>
        public int ActiveTemplate { get; private set; }

        /// <summary>武器模板等级（两个模板共享）。</summary>
        public TemplateTier Tier { get; private set; } = TemplateTier.White;

        /// <summary>商店里还能升几次（初始 2 次，任务书：剩下两次升级只能在商店刷出）。</summary>
        public int ShopUpgradesLeft { get; private set; } = 2;

        /// <summary>
        /// 模板刚升到紫色，等着弹「蓝插件三选一」。
        /// 由 <see cref="DungeonBuffSelectUI"/> 读取并调 <see cref="ConsumeBluePick"/> 清掉。
        /// </summary>
        public bool PendingBluePick { get; private set; }

        /// <summary>消费掉蓝插件三选一（选完 / 关掉都算）。</summary>
        public void ConsumeBluePick() => PendingBluePick = false;

        // 缓存的容量 / 占用（每次装备变化时重算，避免 UI 每帧重算）
        private readonly int[,] m_Caps = new int[TemplateCount, SlotRules.ColorCount];
        private readonly int[,] m_Used = new int[TemplateCount, SlotRules.ColorCount];

        // ------------------------------------------------------------------ 合成后的数值

        public float DamageMul      { get; private set; } = 1f;
        public float FireRateMul    { get; private set; } = 1f;
        public float ManaCostMul    { get; private set; } = 1f;
        public float KillManaMul    { get; private set; } = 1f;
        public float BulletSpeedMul { get; private set; } = 1f;
        public float BulletWidthMul { get; private set; } = 1f;
        public float BulletSpreadMul { get; private set; } = 1f;

        public int   MaxManaAdd   { get; private set; }
        public int   ExtraPellets { get; private set; }
        public float AoeRadius    { get; private set; }
        public int   ExtraVolleys { get; private set; }
        public int   BounceCount  { get; private set; }
        public int   PierceCount  { get; private set; }
        public float ManaCostAdd  { get; private set; }

        private int m_AppliedMaxBonus;

        // ------------------------------------------------------------------ 生命周期

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }

            Instance = this;

            for (int t = 0; t < TemplateCount; t++)
                for (int c = 0; c < SlotRules.ColorCount; c++)
                    m_Slots[t, c] = new List<BuffInstance>(8);

            Recompute();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ------------------------------------------------------------------ 背包 API

        /// <summary>某种插件拥有几份（同名同色）。</summary>
        public int StacksOf(DungeonBuffDef def)
        {
            if (def == null) return 0;

            int n = 0;
            for (int i = 0; i < m_Instances.Count; i++)
                if (m_Instances[i] != null && m_Instances[i].def != null && m_Instances[i].def.id == def.id)
                    n++;

            return n;
        }

        /// <summary>兼容旧名：拥有份数。</summary>
        public int LevelOf(DungeonBuffDef def) => StacksOf(def);

        /// <summary>取第一份该插件的实例（可能已经被装上了）。</summary>
        public BuffInstance Find(DungeonBuffDef def)
        {
            if (def == null) return null;

            for (int i = 0; i < m_Instances.Count; i++)
                if (m_Instances[i] != null && m_Instances[i].def != null && m_Instances[i].def.id == def.id)
                    return m_Instances[i];

            return null;
        }

        /// <summary>获得**一份**新插件；能装就自动装进当前模板，装不下就留在背包。</summary>
        public void Add(DungeonBuffDef def)
        {
            if (def == null) return;

            var inst = new BuffInstance(def, 1);
            m_Instances.Add(inst);

            // 自动入槽（放不下就算了，玩家按 R 自己整理）
            TryEquip(ActiveTemplate, inst);

            Recompute();
        }

        /// <summary>清空背包、插槽、模板等级（重开一局）。</summary>
        public void Clear()
        {
            m_Instances.Clear();

            for (int t = 0; t < TemplateCount; t++)
                for (int c = 0; c < SlotRules.ColorCount; c++)
                    m_Slots[t, c].Clear();

            ActiveTemplate   = 0;
            Tier             = TemplateTier.White;
            ShopUpgradesLeft = 2;

            Recompute();
        }

        // ------------------------------------------------------------------ 模板等级

        /// <summary>模板升一级（白→绿→蓝→紫→橙），两个模板一起升。</summary>
        public bool UpgradeTier()
        {
            if (Tier >= TemplateTier.Orange) return false;

            Tier = (TemplateTier)((int)Tier + 1);

            // ★ 任务书：升到紫色时把三个蓝插件一起发，蓝槽只有一个 -> 弹三选一
            if (Tier == TemplateTier.Purple) PendingBluePick = true;

            Recompute();
            return true;
        }

        /// <summary>商店里花钱升级（次数有限）。</summary>
        public bool UpgradeTierFromShop()
        {
            if (ShopUpgradesLeft <= 0) return false;
            if (!UpgradeTier()) return false;

            ShopUpgradesLeft--;
            return true;
        }

        // ------------------------------------------------------------------ 插槽查询

        /// <summary>某模板某颜色的容量（已扣掉挤占）。</summary>
        public int CapOf(int template, PluginColor color)
            => m_Caps[Mathf.Clamp(template, 0, TemplateCount - 1), (int)color];

        /// <summary>某模板某颜色已装几个。</summary>
        public int UsedOf(int template, PluginColor color)
            => m_Used[Mathf.Clamp(template, 0, TemplateCount - 1), (int)color];

        /// <summary>某模板某颜色已装列表（只读，按装入顺序）。</summary>
        public IReadOnlyList<BuffInstance> SlotsOf(int template, PluginColor color)
            => m_Slots[Mathf.Clamp(template, 0, TemplateCount - 1), (int)color];

        /// <summary>这个实例是否装在该模板里。</summary>
        public bool IsEquipped(int template, BuffInstance inst)
        {
            if (inst == null) return false;

            var list = m_Slots[Mathf.Clamp(template, 0, TemplateCount - 1), (int)inst.def.color];
            return list.Contains(inst);
        }

        /// <summary>
        /// 能不能把这个实例装进该模板。
        /// <paramref name="reason"/> 会带回失败原因（给界面显示）。
        /// </summary>
        public bool CanEquip(int template, BuffInstance inst, out string reason)
        {
            reason = null;

            if (inst == null || inst.def == null) { reason = "插件无效"; return false; }
            if (IsEquipped(template, inst)) { reason = "它已经装在这个模板里了"; return false; }

            template = Mathf.Clamp(template, 0, TemplateCount - 1);

            // 假设装上之后的颜色分布，再算一遍容量
            int[] counts = new int[SlotRules.ColorCount];
            for (int c = 0; c < SlotRules.ColorCount; c++)
                counts[c] = m_Slots[template, c].Count;

            counts[(int)inst.def.color]++;

            int[] caps = new int[SlotRules.ColorCount];
            SlotRules.ComputeCaps(Tier, StageIndex, counts, caps);

            for (int c = 0; c < SlotRules.ColorCount; c++)
            {
                if (counts[c] <= caps[c]) continue;

                var color = (PluginColor)c;

                // 说清楚为什么装不下
                if (color == inst.def.color)
                    reason = $"{SlotRules.NameOf(color)}槽已满（{caps[c]}/{caps[c]}）";
                else
                    reason = $"{SlotRules.NameOf(color)}槽会被挤爆，先拆掉一些{SlotRules.NameOf(color)}插件";

                return false;
            }

            return true;
        }

        /// <summary>尝试装上；装不下返回 false，原因见 <paramref name="reason"/>。</summary>
        public bool TryEquip(int template, BuffInstance inst) => TryEquip(template, inst, out _);

        public bool TryEquip(int template, BuffInstance inst, out string reason)
        {
            if (!CanEquip(template, inst, out reason)) return false;

            template = Mathf.Clamp(template, 0, TemplateCount - 1);
            m_Slots[template, (int)inst.def.color].Add(inst);

            Recompute();
            return true;
        }

        /// <summary>从某颜色槽里拆下第 index 个。</summary>
        public void Unequip(int template, PluginColor color, int index)
        {
            template = Mathf.Clamp(template, 0, TemplateCount - 1);

            var list = m_Slots[template, (int)color];
            if (index < 0 || index >= list.Count) return;

            list.RemoveAt(index);
            Recompute();
        }

        /// <summary>切换当前使用的模板（按 E）。</summary>
        public void SetActiveTemplate(int template)
        {
            template = Mathf.Clamp(template, 0, TemplateCount - 1);
            if (template == ActiveTemplate) return;

            ActiveTemplate = template;
            Recompute();
        }

        // ------------------------------------------------------------------ 机制等级

        /// <summary>
        /// 某个**机制效果**在当前模板里的等级（跨颜色合并）。
        /// 例：紫爆炸弹头 + 橙爆炸弹头 = "aoe" Lv2。
        /// </summary>
        public int MechanicLevel(string effectId)
        {
            if (string.IsNullOrEmpty(effectId)) return 0;

            int lv = 0;

            var all = m_Slots[ActiveTemplate, 0];
            for (int c = 0; c < SlotRules.ColorCount; c++)
            {
                var list = m_Slots[ActiveTemplate, c];

                for (int i = 0; i < list.Count; i++)
                {
                    BuffInstance inst = list[i];
                    if (inst == null || inst.def == null) continue;
                    if (inst.def.effectId != effectId) continue;

                    lv += Mathf.Max(1, inst.level);
                }
            }

            _ = all;
            return lv;
        }

        // ------------------------------------------------------------------ 计算

        /// <summary>刷新容量 / 占用 / 数值。</summary>
        public void Recompute()
        {
            // ---- 1) 容量与占用 ----
            for (int t = 0; t < TemplateCount; t++)
            {
                int[] counts = new int[SlotRules.ColorCount];
                for (int c = 0; c < SlotRules.ColorCount; c++)
                {
                    counts[c]  = m_Slots[t, c].Count;
                    m_Used[t, c] = counts[c];
                }

                int[] caps = new int[SlotRules.ColorCount];
                SlotRules.ComputeCaps(Tier, StageIndex, counts, caps);

                for (int c = 0; c < SlotRules.ColorCount; c++) m_Caps[t, c] = caps[c];
            }

            // ---- 2) 数值合成（只算当前模板）----
            float dmg = 1f, rate = 1f, mana = 1f, kill = 1f;
            float speed = 1f, width = 1f, spread = 1f;
            int   maxAdd = 0, pellets = 0, volleys = 0, bounces = 0, pierce = 0;
            float aoe = 0f, manaAdd = 0f;

            for (int c = 0; c < SlotRules.ColorCount; c++)
            {
                var list = m_Slots[ActiveTemplate, c];

                for (int i = 0; i < list.Count; i++)
                {
                    BuffInstance inst = list[i];
                    if (inst == null || inst.def == null) continue;

                    DungeonBuffDef b = inst.def;
                    int lv = Mathf.Max(1, inst.level);

                    dmg    *= Mathf.Pow(b.damageMul, lv);
                    rate   *= Mathf.Pow(b.fireRateMul, lv);
                    mana   *= Mathf.Pow(b.manaCostMul, lv);
                    kill   *= Mathf.Pow(b.killManaMul, lv);
                    speed  *= Mathf.Pow(b.bulletSpeedMul, lv);
                    width  *= Mathf.Pow(b.bulletWidthMul, lv);
                    spread *= Mathf.Pow(b.bulletSpreadMul, lv);

                    maxAdd  += b.maxManaAdd * lv;
                    pellets += b.extraPellets * lv;
                    volleys += b.extraVolleys * lv;
                    bounces += b.bounceAdd * lv;
                    pierce  += b.pierceAdd * lv;
                    manaAdd += b.manaCostAdd * lv;
                }
            }

            DamageMul       = Round3(dmg);
            FireRateMul     = Round3(rate);
            ManaCostMul     = Round3(mana);
            KillManaMul     = Round3(kill);
            BulletSpeedMul  = Round3(speed);
            BulletWidthMul  = Round3(width);
            BulletSpreadMul = Round3(spread);

            MaxManaAdd   = maxAdd;
            ExtraPellets = Mathf.Max(0, pellets);
            ExtraVolleys = Mathf.Max(0, volleys);
            BounceCount  = Mathf.Max(0, bounces);
            PierceCount  = Mathf.Max(0, pierce);
            ManaCostAdd  = Round3(Mathf.Max(0f, manaAdd));

            // ★ 爆炸半径不再由"每层 +N"累加：武器按机制等级查表（aoeRadiusByLevel）
            AoeRadius = 0f;
            _ = aoe;

            ApplyMaxMana(maxAdd);

            Changed?.Invoke();
        }

        // ------------------------------------------------------------------ 层数缩放

        public const float StageScalePerFloor = 1.25f;

        public static float StageScale => s_StageScale;

        private static float s_StageScale = 1f;
        private static int   s_StageIndex = 1;

        public static int StageIndex => s_StageIndex;

        /// <summary>设置当前层号（1 起）。第 5/10 层会顺带升级模板。</summary>
        public static void SetStage(int stageIndex)
        {
            int prev = s_StageIndex;

            s_StageIndex = Mathf.Max(1, stageIndex);

            float s = 1f;
            for (int i = 1; i < s_StageIndex; i++) s *= StageScalePerFloor;

            s_StageScale = Round3(s);

            DungeonBuffState inst = Instance;
            if (inst == null) return;

            // ★ 任务书：通关第 5、10 层时模板升一级（两个模板一起升）
            if (prev != s_StageIndex)
            {
                if (s_StageIndex == 5 || s_StageIndex == 10) inst.UpgradeTier();
            }

            inst.Recompute();
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