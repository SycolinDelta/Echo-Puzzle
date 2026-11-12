using UnityEngine;

namespace EchoPuzzle.Dungeon
{
    /// <summary>
    /// 【地牢模式】怪物子弹的**发射入口**。
    ///
    /// ══════════════════════════════════════════════════════════════
    /// ★★ 它不再是 MonoBehaviour，也不会创建任何 GameObject ★★
    ///
    ///   以前每发子弹都是 `GameObject.CreatePrimitive(Sphere)`
    ///   + MeshRenderer + SphereCollider + 这个组件。
    ///   现在子弹的全部数据与逻辑都住在
    ///   <see cref="MonsterBulletRenderer"/> 里（全局单例），
    ///   这里只剩一个静态入口，负责把参数转交过去。
    ///
    /// ══════════════════════════════════════════════════════════════
    /// 淡入 / 完全显示 / 淡出 的时长
    ///   ★ 这些是**发射时**从下面这几个静态默认值带过去的，
    ///     想改就改这里的默认值（或者在 MonsterBulletRenderer 的 Body 上改）。
    ///
    ///   完整时间轴：
    ///     平时完全透明
    ///       -> 声波光带波前进入 sonarDetectRadius
    ///       -> 淡入 fadeInSeconds
    ///       -> 完全显示 holdSeconds
    ///       -> 淡出 fadeOutSeconds
    ///       -> 回到完全透明
    /// </summary>
    public static class DungeonMonsterBullet
    {
        /// <summary>波前水平距离小于该值（米）判定"光带来了"。</summary>
        public static float sonarDetectRadius = 1.5f;

        /// <summary>★ 淡入时长（秒）。</summary>
        public static float fadeInSeconds = 0.1f;

        /// <summary>★ 完全显示（不透明度 = 1）时长（秒）。</summary>
        public static float holdSeconds = 1f;

        /// <summary>★ 淡出时长（秒）。</summary>
        public static float fadeOutSeconds = 0.5f;

        /// <summary>★ 爆炸（放大 + 淡出）时长（秒）。</summary>
        public static float explodeSeconds = 0.5f;

        /// <summary>爆炸时放大到多少倍。</summary>
        public static float explodeScaleMultiplier = 2.5f;

        /// <summary>子弹最长存活时间（秒），超时自爆。</summary>
        public static float maxLifetime = 6f;

        /// <summary>
        /// 发射一发怪物子弹。
        /// </summary>
        /// <param name="pos">生成位置（世界坐标）</param>
        /// <param name="dir">飞行方向（内部会归一化）</param>
        /// <param name="owner">发射者（拷贝速度 / 伤害 / 颜色等参数，可为 null）</param>
        public static void Spawn(Vector3 pos, Vector3 dir, DungeonEnemy owner)
        {
            if (MonsterBulletRenderer.Instance == null)
            {
                // 相机没初始化 / 场景刚切 —— 补建一个
                var go = new GameObject("MonsterBulletRenderer");
                go.AddComponent<MonsterBulletRenderer>();
            }

            var r = MonsterBulletRenderer.Instance;
            if (r == null) return;

            MonsterBulletRenderer.Body b = r.Fire(pos, dir, owner);
            if (b == null) return;

            // 淡入淡出参数
            b.sonarDetectRadius = owner != null ? owner.bulletSonarDetectRadius : sonarDetectRadius;
            b.fadeInSeconds     = fadeInSeconds;
            b.holdSeconds       = holdSeconds;
            b.fadeOutSeconds    = fadeOutSeconds;

            // 爆炸参数
            b.explodeSeconds         = explodeSeconds;
            b.explodeScaleMultiplier = explodeScaleMultiplier;

            // 存活上限
            b.maxLifetime = maxLifetime;
        }
    }
}
