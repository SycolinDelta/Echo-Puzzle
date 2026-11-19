using UnityEngine;

namespace EchoPuzzle.Dungeon
{
    /// <summary>
    /// 【地牢模式】金币。
    ///
    /// ── 来源与用途（第十周任务 2）────────────────────────────
    ///   · 来源：击杀一只怪物 +<see cref="goldPerKill"/>（默认 3）
    ///   · 用途：① 选强化时刷新三张卡（DungeonBuffSelectUI 的刷新按钮）
    ///           ② 商店买药剂（第十周任务 3，届时接 <see cref="TrySpend"/>）
    ///
    /// ── 和探秘模式的关系 ────────────────────────────────────
    ///   ★ 只在 <see cref="GameUI.IsDungeonMode"/> 时由
    ///     <see cref="DungeonWeaponBootstrap"/> 创建，探秘模式没有金币概念。
    /// </summary>
    [AddComponentMenu("Echo Puzzle/Dungeon/Dungeon Currency")]
    public sealed class DungeonCurrency : MonoBehaviour
    {
        public static DungeonCurrency Instance { get; private set; }

        [Header("收入")]
        [Tooltip("击杀一只怪物获得多少金币")]
        [Min(0)] public int goldPerKill = 3;

        [Header("开局")]
        [Tooltip("开局携带的金币。重开一局也回到这个值")]
        [Min(0)] public int startGold;

        /// <summary>当前金币。</summary>
        public int Gold { get; private set; }

        /// <summary>金币变化（当前值）。</summary>
        public event System.Action<int> Changed;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }

            Instance = this;

            Gold = Mathf.Max(0, startGold);
            Changed?.Invoke(Gold);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>加钱（击杀、关卡奖励）。</summary>
        public void Add(int amount)
        {
            if (amount <= 0) return;

            Gold += amount;
            Changed?.Invoke(Gold);
        }

        /// <summary>钱够不够。</summary>
        public bool CanAfford(int cost) => Gold >= Mathf.Max(0, cost);

        /// <summary>够就扣，返回是否真的扣了。</summary>
        public bool TrySpend(int cost)
        {
            cost = Mathf.Max(0, cost);
            if (Gold < cost) return false;

            Gold -= cost;
            Changed?.Invoke(Gold);
            return true;
        }

        /// <summary>
        /// 击杀一只怪物的结算入口。
        ///
        /// ★ 单独开这个方法而不是让调用方直接 Add(goldPerKill)：
        ///   以后要加成（比如"贪婪"buff 让金币 ×2）只需要改这一处。
        /// </summary>
        public void OnMonsterKilled()
        {
            Add(Mathf.Max(0, goldPerKill));
        }

        /// <summary>重开一局 / 换新档时回到开局金币。</summary>
        public void ResetGold()
        {
            Gold = Mathf.Max(0, startGold);
            Changed?.Invoke(Gold);
        }
    }
}