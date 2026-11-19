using UnityEngine;

namespace EchoPuzzle.Dungeon
{
    /// <summary>
    /// 【地牢模式】玩家子弹的**发射入口**。
    ///
    /// ══════════════════════════════════════════════════════════════
    /// ★★ 它不再是 MonoBehaviour，也不会创建任何 GameObject ★★
    ///
    ///   以前每发子弹都是：
    ///     · new GameObject("DungeonBullet") + DungeonBullet 组件
    ///     · new GameObject("BulletTrailMesh") + MeshFilter + MeshRenderer
    ///
    ///   现在子弹的全部数据与逻辑都住在
    ///   <see cref="BulletTrailRenderer"/> 里（全局单例），
    ///   这里只剩一个静态入口。
    ///
    /// ══════════════════════════════════════════════════════════════
    /// </summary>
    public static class DungeonBullet
    {
        /// <summary>
        /// 发射一颗子弹。
        /// </summary>
        /// <param name="material">光带材质的颜色来源（材质本身由武器持有）</param>
        /// <param name="trailLength">保留参数（光带长度由 pointFadeTime × 速度决定）</param>
        /// <param name="aoeRadius">范围伤害半径（米）。0 = 无（第十周任务 4）</param>
        /// <param name="bounces">撞墙后能反弹几次（第十周任务 6）</param>
        /// <param name="pierce">能穿过几只怪（第十周任务 6）</param>
        public static void Spawn(Vector3 origin, Vector3 dir,
                                 float speed, float range, float width,
                                 float damage, float manaRestore,
                                 float hurtRadius, float hurtHeight,
                                 Material material, float trailLength,
                                 float aoeRadius = 0f, int bounces = 0, int pierce = 0)
        {
            if (BulletTrailRenderer.Instance == null)
            {
                var go = new GameObject("BulletTrailRenderer");
                go.AddComponent<BulletTrailRenderer>();
            }

            var r = BulletTrailRenderer.Instance;
            if (r == null) return;

            Color c = Color.white;

            if (material != null && material.HasProperty("_Color"))
                c = material.GetColor("_Color");

            r.Fire(origin, dir, speed, range, width,
                   damage, manaRestore, hurtRadius, hurtHeight, c, aoeRadius, bounces, pierce);
        }
    }
}
