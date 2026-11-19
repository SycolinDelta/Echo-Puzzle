using UnityEngine;

namespace EchoPuzzle.Dungeon
{
    /// <summary>
    /// 【地牢模式】商店格（地图矩阵里的数字 5）。
    ///
    /// ── 组成 ────────────────────────────────────────────────
    ///   由 <see cref="DungeonMapGenerator"/> 在生成地图时创建：
    ///     · 一个 1×1×6 的 Trigger 盒子（无实体、无碰撞体积）
    ///     · 一个 TriggerVolume，把射进来的光带改成蓝色
    ///     · 这个组件：玩家走进来就打开商店界面
    ///
    /// ── 为什么用一个 Trigger 而不是 SpecialZone ────────────
    ///   TriggerVolume 本来就有"光带进入这个区域时改颜色"的能力
    ///   （overrideColor + waveColor），终点格就是这么做的，
    ///   这里直接复用，不用动 Core。
    /// </summary>
    [AddComponentMenu("Echo Puzzle/Dungeon/Dungeon Shop Zone")]
    public sealed class DungeonShopZone : MonoBehaviour
    {
        [Tooltip("玩家离开后是否自动关闭商店界面")]
        public bool closeOnExit = true;

        /// <summary>本格在世界里的位置（给界面显示用）。</summary>
        public Vector3 CellCenter => transform.position;

        // ---------------------------------------------------------------- 全局触发锁
        //
        // ★★ 需求 ★★
        //   "如果有多个商店单元格连在一起，玩家在进入这片区域时只会触发一次商店界面，
        //     离开该区域后再次进入才会再次触发商店。"
        //
        // ── 为什么需要这个锁 ──────────────────────────────────
        //   每个商店格都是**独立**的 TriggerVolume + DungeonShopZone。
        //   玩家走进一片 3 格宽的商店区时，会连续触发 3 个 OnTriggerEnter，
        //   于是 OpenFor 被调 3 次（后面两次是无效的，但会刷日志、抢 owner）。
        //   离开时同理，会连续触发 3 个 OnTriggerExit。
        //
        // ── 做法 ────────────────────────────────────────────
        //   用**静态计数**统计"玩家现在待在几个商店格里"：
        //     · 计数从 0 变 1  -> 第一次进入这片区域，开门
        //     · 计数从 1 变 0  -> 完全离开这片区域，关门
        //   中间的 1->2、2->1 什么都不做，自然就满足"只触发一次"。

        /// <summary>玩家当前一共站在几个商店格的触发器里。</summary>
        private static int s_PlayerInsideCount;

        /// <summary>这一片区域是由哪个 zone 开的界面（关门时用）。</summary>
        private static DungeonShopZone s_Owner;

        public static void ResetForNewMap()
        {
            s_PlayerInsideCount = 0;
            s_Owner = null;
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!GameUI.IsDungeonMode) return;
            if (other == null) return;
            if (other.GetComponentInParent<PlayerController>() == null) return;

            s_PlayerInsideCount++;

            // 只有"从区域外进入区域"的那一次才开门
            if (s_PlayerInsideCount != 1) return;

            s_Owner = this;
            DungeonShopUI.OpenFor(this);
        }

        private void OnTriggerExit(Collider other)
        {
            if (!GameUI.IsDungeonMode) return;
            if (other == null) return;
            if (other.GetComponentInParent<PlayerController>() == null) return;

            s_PlayerInsideCount = Mathf.Max(0, s_PlayerInsideCount - 1);

            // 只有"完全离开这片区域"才关门
            if (s_PlayerInsideCount != 0) return;

            var owner = s_Owner;
            s_Owner = null;

            if (!closeOnExit) return;

            DungeonShopUI.CloseIfOwner(owner);
        }
    }
}