using UnityEngine;

namespace EchoPuzzle.Dungeon
{
    /// <summary>
    /// 【地牢模式】蓝量池。
    ///
    /// ── 职责 ────────────────────────────────────────────────
    ///   · 存一个 0..max 的整数蓝量
    ///   · 给武器提供 <see cref="TrySpend"/>，给击杀回复提供 <see cref="Restore"/>
    ///   · 变化时发 <see cref="Changed"/>，HUD 只负责画，不存数据
    ///
    /// ── 和探秘模式的关系 ────────────────────────────────────
    ///   ★ 与 <see cref="BatterySystem"/>（探秘模式的电池）**没有任何关系**。
    ///     那个是"捡到才有光"的道具，这个是地牢模式的技能资源；
    ///     两者不会在同一个模式里同时出现，也不共享数值。
    /// </summary>
    [AddComponentMenu("Echo Puzzle/Dungeon/Dungeon Mana")]
    public sealed class DungeonMana : MonoBehaviour
    {
        public static DungeonMana Instance { get; private set; }

        [Header("容量")]
        [Tooltip("蓝量上限。★ 支持小数（保留三位）")]
        [Min(1f)] public float maxMana = 200f;

        [Tooltip("开局蓝量。重开一局（ResetMana）也会回到这个值。★ 支持小数（保留三位）")]
        [Min(0f)] public float startMana = 200f;

        /// <summary>
        /// 当前蓝量。
        ///
        /// ★★ 是 **float**，保留三位小数 ★★
        ///
        ///   需求："蓝量虽然以整数显示，但实际上仍然是有小数的，
        ///         并且计算蓝量时应当保留到小数点后三位，而不是保留到整数。"
        ///
        ///   所以内部一律用 float 计算，显示时才四舍五入成整数。
        /// </summary>
        public float Current { get; private set; }

        /// <summary>上限（至少 1，避免除零）。</summary>
        public float Max => Mathf.Max(1f, maxMana);

        /// <summary>当前蓝量取整（给 HUD 显示用）。</summary>
        public int CurrentDisplay => Mathf.RoundToInt(Current);

        /// <summary>上限取整（给 HUD 显示用）。</summary>
        public int MaxDisplay => Mathf.RoundToInt(Max);

        /// <summary>
        /// 代码里配置的**原始**上限（buff 加成的基准）。
        /// <see cref="SetMax"/> 只改 <see cref="maxMana"/>，不会动它，
        /// 这样 buff 叠加时永远是在原始值上加，不会越加越多。
        /// </summary>
        public float BaseMax { get; private set; }

        /// <summary>0~1，给 HUD 画条用。</summary>
        public float Ratio01 => Mathf.Clamp01(Current / Max);

        /// <summary>蓝量变化（当前, 最大）。</summary>
        public event System.Action<float, float> Changed;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                // 场景里已经有了一份（比如手滑挂了两个）—— 多余的自己消失
                Destroy(this);
                return;
            }

            Instance = this;

            BaseMax = Max;

            Current = Mathf.Clamp(startMana, 0, Max);
            Changed?.Invoke(Current, Max);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ------------------------------------------------------------------ 被动回复

        [Header("被动回复")]
        [Tooltip("每秒自动回复多少蓝。0 = 关闭。\n\n" +
                 "存在的意义是**兜底**：怪多的时候如果打空蓝又被围住，\n" +
                 "没有这一个回复就会卡死（打不出子弹、怪也打不死你）。\n" +
                 "它明显慢于射速，所以不影响「打得准才有盈余」这个经济。")]
        [Min(0f)] public float regenPerSecond = 0f;

        private void Update()
        {
            if (regenPerSecond <= 0f) return;

            // 只在地牢模式、真正进行中时回复（菜单 / 暂停 / 结算都不回）
            if (!GameUI.IsDungeonMode) return;
            if (GameUI.Current != GameState.Playing) return;

            if (Current >= Max) return;

            // ★ 蓝现在是 float，直接按小数累加即可
            Set(Current + regenPerSecond * Time.deltaTime);
        }

        /// <summary>蓝够不够。</summary>
        public bool CanAfford(float cost) => Current >= Mathf.Max(0f, cost);

        /// <summary>够就扣，返回是否真的扣了。</summary>
        public bool TrySpend(float cost)
        {
            cost = Mathf.Max(0f, cost);
            if (Current < cost) return false;

            Set(Current - cost);
            return true;
        }

        /// <summary>回蓝（击杀怪物 / 通关奖励）。</summary>
        public void Restore(float amount)
        {
            if (amount <= 0f) return;
            Set(Current + amount);
        }

        /// <summary>回到开局蓝量（重开 / 换层用）。</summary>
        public void ResetMana()
        {
            Set(Mathf.Clamp(startMana, 0f, Max));
        }

        /// <summary>改上限（buff 用）。当前值会被夹到新上限内。</summary>
        public void SetMax(float newMax)
        {
            maxMana = Mathf.Max(1f, newMax);
            Set(Current);
        }

        /// <summary>
        /// 设置当前蓝量。
        ///
        /// ★★ 保留三位小数 ★★
        ///   需求："计算蓝量时应当保留到小数点后三位。"
        /// </summary>
        private void Set(float value)
        {
            value = Round3(Mathf.Clamp(value, 0f, Max));

            if (Mathf.Approximately(value, Current)) return;

            Current = value;
            Changed?.Invoke(Current, Max);
        }

        /// <summary>保留三位小数（顺便消掉 float 的表示误差）。</summary>
        public static float Round3(float v)
        {
            return Mathf.Round(v * 1000f) / 1000f;
        }
    }
}