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

        // ---------------------------------------------------------------- 被挤出的插件


        /// <summary>
        /// ★★ 因为槽位变少而被挤出、但**仍然属于玩家**的插件。
        ///
        ///   需求："由于插件槽位数量经常会变化，当槽位变少时，
        ///         我希望系统能记住原来存放在槽位中，但因为槽位减少而被挤出的那些插件。
        ///         当槽位再次增加时，如果该插件的数量还有剩余，它们会被还原回去。"
        ///
        /// ★ 按模板分开记：[模板] -> 被挤出的实例列表
        /// ★ 被挤出的实例仍然在 m_Instances 里（玩家的背包），
        ///   只是不在 m_Slots 里 —— 所以"数量还有剩余"天然成立。
        /// ★ 还原时按**原来装入的先后顺序**放回去（列表顺序就是挤出顺序）。
        /// </summary>
        private readonly List<BuffInstance>[] m_Evicted =
            new List<BuffInstance>[TemplateCount];

        /// <summary>武器模板等级（两个模板共享）。</summary>
        public TemplateTier Tier { get; private set; } = TemplateTier.White;

        /// <summary>商店里还能升几次（初始 2 次，任务书：剩下两次升级只能在商店刷出）。</summary>
        public int ShopUpgradesLeft { get; private set; } = 2;

        /// <summary>
        /// 模板刚升到紫色，等着弹「蓝插件三选一」。
        ///
        /// ★★ 已废弃：现在蓝插件是升级时**全部直接到手**，
        ///   不再弹三选一，所以这个标记**永远是 false**。
        ///   保留字段 + <see cref="ConsumeBluePick"/> 是为了不改 DungeonBuffSelectUI 的引用，
        ///   让它那段代码自然失效。
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
            {
                m_Evicted[t] = new List<BuffInstance>(8);
                m_LastRedCap[t] = -1;   // -1 = 还没记录过

                for (int c = 0; c < SlotRules.ColorCount; c++)
                    m_Slots[t, c] = new List<BuffInstance>(8);
            }

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
            {
                m_Evicted[t].Clear();

                for (int c = 0; c < SlotRules.ColorCount; c++)
                    m_Slots[t, c].Clear();
            }

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

            // ★★ 升到紫色时：三个蓝插件**全部直接到手**，不再弹三选一 ★★
            //
            //   需求：“我希望玩家获得蓝色插件时会一口气获得所有三个蓝插件，而不是三选一。”
            //
            //   ★ 以前是 PendingBluePick = true -> UI 弹三选一，选一个丢两个。
            //   ✔ 现在直接调 Add() 全发。蓝槽只有 1 个，
            //     所以能自动装上的装一个，其余留在背包里，
            //     玩家自己按 R 在模板界面换。
            if (Tier == TemplateTier.Purple) GrantAllBluePlugins();

            Recompute();
            return true;
        }

        /// <summary>
        /// 把**三个蓝色插件一次性全部**发给玩家。
        ///
        /// ★ 需求：“我希望玩家获得蓝色插件时会一口气获得所有三个蓝插件，而不是三选一。”
        ///
        /// ★ 蓝插件共 3 个：蓝·伤害 / 蓝·射速 / 蓝·蓝耗（maxStacks 都是 1）。
        /// ★ 已经拿满的跳过，避免重复升级时多发。
        /// </summary>
        private void GrantAllBluePlugins()
        {
            var blues = DungeonBuffPool.ByColor(PluginColor.Blue);

            for (int i = 0; i < blues.Count; i++)
            {
                DungeonBuffDef d = blues[i];
                if (d == null) continue;

                // 已经拿满就不再重复发
                if (StacksOf(d) >= Mathf.Max(1, d.maxStacks)) continue;

                Add(d);
            }
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

        /// <summary>
        /// 从某颜色槽里拆下第 index 个。
        ///
        /// ★ 拆下**不会**减少背包：插件仍然留在 m_Instances 里，
        ///   玩家可以再把它装回去（这是正确行为，不要改成"拆下即丢弃"）。
        ///
        /// ★ 唯一要清理的是 m_Evicted（被挤出待还原列表）：
        ///   如果这个实例正躺在那里等待还原，从槽位拆下时就该把它移出去，
        ///   否则它会一直挂在待还原列表里越攒越多。
        /// </summary>
        public void Unequip(int template, PluginColor color, int index)
        {
            template = Mathf.Clamp(template, 0, TemplateCount - 1);

            var list = m_Slots[template, (int)color];
            if (index < 0 || index >= list.Count) return;

            BuffInstance removed = list[index];

            list.RemoveAt(index);

            // ★ 只清"待还原"记录，**不动背包**
            if (removed != null)
            {
                for (int t = 0; t < TemplateCount; t++)
                {
                    if (m_Evicted[t] != null)
                        m_Evicted[t].Remove(removed);
                }
            }

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
            => EquippedEffectCount(ActiveTemplate, effectId);

        /// <summary>
        /// 某个**效果键**在指定模板里装了几个（跨颜色相加）。
        ///
        /// ★ 效果键的取值：优先用 def.effectId（"aoe" / "dmg" / ...）；
        ///   没有 effectId 时退回 def.id，保证任何插件都能被区分。
        /// </summary>
        public int EquippedEffectCount(int template, string effectKey)
        {
            if (string.IsNullOrEmpty(effectKey)) return 0;

            template = Mathf.Clamp(template, 0, TemplateCount - 1);

            int lv = 0;

            for (int c = 0; c < SlotRules.ColorCount; c++)
            {
                var list = m_Slots[template, c];

                for (int i = 0; i < list.Count; i++)
                {
                    BuffInstance inst = list[i];
                    if (inst == null || inst.def == null) continue;
                    if (EffectKeyOf(inst.def) != effectKey) continue;

                    lv += Mathf.Max(1, inst.level);
                }
            }

            return lv;
        }

        /// <summary>
        /// 取某个插件的「效果键」：跨颜色同一效果用同一个键。
        ///
        /// ★ 优先 effectId（紫爆炸弹头 / 橙爆炸弹头都是 "aoe"）；
        ///   没有 effectId 时退回 id（至少保证不串味）。
        /// </summary>
        public static string EffectKeyOf(DungeonBuffDef def)
        {
            if (def == null) return null;

            if (!string.IsNullOrEmpty(def.effectId)) return def.effectId;

            return def.id;
        }

        // ------------------------------------------------------------------ 计算

        /// <summary>刷新容量 / 占用 / 数值。</summary>
        public void Recompute()
        {
            // ---- 0) 被挤出的插件：先尝试还原 ----
            RestoreEvicted();

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

                // ★ 容量变小 -> 把超出的从末尾挤出去（后装的先出）
                EvictOverflow(t, caps);

                // 挤出之后同步一次占用数
                for (int c = 0; c < SlotRules.ColorCount; c++)
                    m_Used[t, c] = m_Slots[t, c].Count;

                // ★★ 红槽扩容 -> 自动用「红·伤害」填满**新增的**槽位 ★★
                //
                //   需求："当红色插件槽位增加时，如果没有正在等待还原的红插件，
                //         就会自动在新增的槽位里填充红伤害插件。"
                //
                //   ★ 只填"扩容出来的新槽"，玩家手动拆出来的空位**不补**。
                //     靠记录"上次的红槽容量"来判断这次是不是真的扩容了。
                //   ★ RestoreEvicted 已经在这之前跑过：
                //     有待还原的插件会先被装回去，剩下的空位才用红伤害补。
                FillNewRedSlots(t, caps);
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

        // ------------------------------------------------------------------ 红色插件（无限量）

        /// <summary>
        /// 取某个红色插件的定义（伤害 / 射速 / 蓝耗 三条）。
        /// </summary>
        public static List<DungeonBuffDef> RedDefs()
        {
            var list = new List<DungeonBuffDef>(4);

            // ★ All 是 DungeonBuffPool 的静态字段（不是本类的）
            var all = DungeonBuffPool.All;
            if (all == null) return list;

            for (int i = 0; i < all.Count; i++)
            {
                DungeonBuffDef d = all[i];
                if (d != null && d.color == PluginColor.Red) list.Add(d);
            }

            return list;
        }

        /// <summary>
        /// ★★ 红色插件是**无限量**的 ★★
        ///
        ///   需求："这三种红色插件应当始终出现在背包里，数量为无限。"
        ///
        /// ★ 做法：不让红色实例躺在 m_Instances 里（否则背包会列出几百条），
        ///   而是「需要的时候现场发一个」。
        ///   · 背包界面用 <see cref="RedDefs"/> 把三条固定列出来
        ///   · 点红色空槽时用 <see cref="EquipRedFresh"/> 现场发一份并装上
        /// ★ 这样红色插件永远不会"用完"，也不会污染背包计数。
        /// </summary>
        public bool EquipRedFresh(int template, DungeonBuffDef redDef, out string reason)
        {
            reason = null;

            if (redDef == null || redDef.color != PluginColor.Red)
            {
                reason = "不是红色插件";
                return false;
            }

            template = Mathf.Clamp(template, 0, TemplateCount - 1);

            // ---- 先检查槽位够不够 ----
            int[] counts = new int[SlotRules.ColorCount];
            for (int c = 0; c < SlotRules.ColorCount; c++)
                counts[c] = m_Slots[template, c].Count;

            counts[(int)PluginColor.Red]++;

            int[] caps = new int[SlotRules.ColorCount];
            SlotRules.ComputeCaps(Tier, StageIndex, counts, caps);

            for (int c = 0; c < SlotRules.ColorCount; c++)
            {
                if (counts[c] <= caps[c]) continue;

                var color = (PluginColor)c;
                reason = color == PluginColor.Red
                    ? $"红槽已满（{caps[c]}/{caps[c]}）"
                    : $"{SlotRules.NameOf(color)}槽会被挤爆";
                return false;
            }

            // ---- 发一份新的实例（不进背包列表，只进槽位）----
            var inst = new BuffInstance(redDef, 1);

            // ★ 红色是无限的：让它仍然登记在 m_Instances 里，
            //   这样 Recompute 的数值合成能读到它；
            //   背包界面会跳过红色，所以不会刷屏。
            m_Instances.Add(inst);

            m_Slots[template, (int)PluginColor.Red].Add(inst);

            Recompute();
            return true;
        }

        /// <summary>
        /// 红色槽位是否已经填满（用于"必须填满才能退出"的判断）。
        /// </summary>
        public bool RedSlotsFull(int template)
            => UsedOf(template, PluginColor.Red) >= CapOf(template, PluginColor.Red);

        // ------------------------------------------------------------------ 红槽扩容填充

        /// <summary>
        /// 各模板上一次记录到的红槽容量（用来判断"这次是不是扩容了"）。
        /// -1 = 还没记录过。
        /// </summary>
        private readonly int[] m_LastRedCap = new int[TemplateCount];


        /// <summary>
        /// 红槽**扩容**时：
        ///   ① 先还原待还原的红插件
        ///   ② 再看当前**是不是在武器模板界面**：
        ///      · 不在 -> 用「红·伤害」填满剩余空位
        ///      · 在   -> **不填**（玩家正在手动整理，别抢他的操作）
        ///
        /// ★ 需求："红槽位扩充时先检查是否有待还原的插件，如果有就还原。
        ///         做完这一步后（不管刚刚有没有还原插件），先检查一下当前是不是在
        ///         武器模板界面！如果不是，再检查是否还有空位，如果还有就用红伤害
        ///         填满所有空位。如果是在模板界面，就不需要用红伤害填满空位了！"
        ///
        /// ── 为什么先判断"是不是真的扩容了" ────────────────
        ///   ★ 玩家手动拆掉一个红插件后红槽会空一格，但那不是扩容，
        ///     这时**不该**补。
        ///   ★ 所以记住"上次的容量"，只有 cap > 上次 才算扩容。
        /// </summary>
        private void FillNewRedSlots(int template, int[] caps)
        {
            int cap  = caps[(int)PluginColor.Red];
            int prev = m_LastRedCap[template];

            // ★ 第一次见到这个模板：基准当成 0
            if (prev < 0)
                prev = 0;

            // 没扩容 -> 什么都不做
            if (cap <= prev)
            {
                m_LastRedCap[template] = cap;
                return;
            }

            var list = m_Slots[template, (int)PluginColor.Red];

            // ============================================================
            // ① 先还原待还原的红插件（不管在不在界面里，都要还原）
            // ============================================================
            var pending = m_Evicted[template];
            if (pending != null && pending.Count > 0)
            {
                for (int i = pending.Count - 1; i >= 0 && list.Count < cap; i--)
                {
                    BuffInstance inst = pending[i];
                    if (inst == null || inst.def == null) { pending.RemoveAt(i); continue; }
                    if (inst.def.color != PluginColor.Red) continue;   // 只管红的

                    list.Add(inst);
                    pending.RemoveAt(i);
                }
            }

            // ============================================================
            // ② 检查是不是在武器模板界面
            // ============================================================
            //
            // ★ 在界面里 -> 不填。玩家正在手动整理槽位，
            //   这时自动塞进去会和他抢操作。
            bool inLoadoutUi = DungeonLoadoutUI.Instance != null
                            && DungeonLoadoutUI.Instance.IsOpen;

            if (inLoadoutUi)
            {
                m_Used[template, (int)PluginColor.Red] = list.Count;
                m_LastRedCap[template] = cap;
                return;
            }

            // ============================================================
            // ③ 不在界面里 -> 还有空位就填满
            // ============================================================
            int need = cap - list.Count;

            if (need > 0)
            {
                // ---- 取「红·伤害」 ----
                DungeonBuffDef redDmg = null;
                var reds = RedDefs();

                for (int i = 0; i < reds.Count; i++)
                {
                    if (reds[i] != null && reds[i].effectId == "dmg")
                    {
                        redDmg = reds[i];
                        break;
                    }
                }

                if (redDmg == null && reds.Count > 0) redDmg = reds[0];

                if (redDmg != null)
                {
                    for (int i = 0; i < need; i++)
                    {
                        var inst = new BuffInstance(redDmg, 1);
                        m_Instances.Add(inst);
                        list.Add(inst);
                    }
                }
            }

            m_Used[template, (int)PluginColor.Red] = list.Count;
            m_LastRedCap[template] = cap;
        }

        // ------------------------------------------------------------------ 挤出 / 还原

        /// <summary>
        /// 把某模板某颜色的超出容量部分挤出到 m_Evicted。
        ///
        /// ★ 从列表末尾开始挤 —— 也就是后装的先出。
        ///   这样玩家手动先装的那几个会优先保留。
        /// </summary>
        private void EvictOverflow(int template, int[] caps)
        {
            for (int c = 0; c < SlotRules.ColorCount; c++)
            {
                var list = m_Slots[template, c];

                int cap = caps[c];

                while (list.Count > cap)
                {
                    int last = list.Count - 1;
                    BuffInstance inst = list[last];

                    list.RemoveAt(last);

                    // 记下来，等槽位回来再装回去
                    if (inst != null && !m_Evicted[template].Contains(inst))
                        m_Evicted[template].Add(inst);
                }
            }
        }

        /// <summary>
        /// 尝试把被挤出的插件装回去。
        ///
        /// ★ 需求：当槽位再次增加时，如果该插件的数量还有剩余，它们会被还原回去。
        ///
        /// ★ 判数量还有剩余：实例仍然在 m_Instances（背包）里。
        ///   如果玩家在别处把它用掉了 / 被清空了，就丢弃这条记录。
        /// ★ 按挤出顺序依次尝试；装不下就留在 m_Evicted 里等下次。
        /// </summary>
        private void RestoreEvicted()
        {
            for (int t = 0; t < TemplateCount; t++)
            {
                var pending = m_Evicted[t];
                if (pending.Count == 0) continue;

                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    BuffInstance inst = pending[i];

                    // ---- 实例已经不存在了（被 Clear / 用掉了）-> 丢弃记录 ----
                    if (inst == null || inst.def == null || !m_Instances.Contains(inst))
                    {
                        pending.RemoveAt(i);
                        continue;
                    }

                    // ---- 已经在槽里了（防御性处理）----
                    if (m_Slots[t, (int)inst.def.color].Contains(inst))
                    {
                        pending.RemoveAt(i);
                        continue;
                    }

                    // ---- 试试能不能装回去 ----
                    int[] counts = new int[SlotRules.ColorCount];
                    for (int c = 0; c < SlotRules.ColorCount; c++)
                        counts[c] = m_Slots[t, c].Count;

                    counts[(int)inst.def.color]++;

                    int[] caps = new int[SlotRules.ColorCount];
                    SlotRules.ComputeCaps(Tier, StageIndex, counts, caps);

                    bool fits = true;
                    for (int c = 0; c < SlotRules.ColorCount; c++)
                    {
                        if (counts[c] > caps[c]) { fits = false; break; }
                    }

                    if (!fits) continue;   // 装不下，留在待还原列表里

                    m_Slots[t, (int)inst.def.color].Add(inst);
                    pending.RemoveAt(i);
                }
            }
        }

        /// <summary>某模板被挤出、等待还原的插件数（给界面显示用）。</summary>
        public int EvictedCount(int template)
        {
            template = Mathf.Clamp(template, 0, TemplateCount - 1);
            return m_Evicted[template] != null ? m_Evicted[template].Count : 0;
        }

        /// <summary>某模板被挤出的插件列表（只读，给界面显示用）。</summary>
        public IReadOnlyList<BuffInstance> EvictedOf(int template)
        {
            template = Mathf.Clamp(template, 0, TemplateCount - 1);
            return m_Evicted[template];
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