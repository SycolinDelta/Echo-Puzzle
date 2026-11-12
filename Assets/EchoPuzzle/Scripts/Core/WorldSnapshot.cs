using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EchoPuzzle
{
    /// <summary>
    /// 世界快照：记录场景里**所有物体的坐标和 hiding**（外加怪物的 captured）。
    ///
    /// ── 两种快照 ────────────────────────────────────────────
    ///   · <b>开局快照</b>（临时）：点「开始游戏」时拍一次，**只保存一次**，
    ///     之后整局都不更新。点「重新开始」时还原到它。
    ///     ★ <b>只在内存里</b>，不写磁盘 —— 关掉游戏就没了。
    ///   · <b>存档快照</b>：点「保存」时拍，**每次覆盖更新**。
    ///     ★ 同时留在内存（读档用）并写进存档文件（见 <see cref="SaveSystem"/>）。
    ///       点「读取游戏」时还原到它。
    ///
    /// ── 存档文件在哪 ────────────────────────────────────────
    ///   <see cref="SaveSystem.SavePath"/> =
    ///   <see cref="Application.persistentDataPath"/> / echopuzzle_save.json
    ///
    ///   ★ 这是 Unity 官方规定的可写目录（Windows 上在
    ///     C:\Users\&lt;用户名&gt;\AppData\LocalLow\&lt;公司&gt;\&lt;游戏&gt;\ 下），
    ///     所以打包成 exe 后存档**不会跟着 exe 走**。
    /// </summary>
    public static class WorldSnapshot
    {
        // ------------------------------------------------------------------ 数据结构

        /// <summary>一个物体的状态。</summary>
        [System.Serializable]
        public sealed class ObjectState
        {
            public string path;       // 场景路径（"父/子/孙"）
            public float  x, y, z;    // 世界坐标（**只对根物体有意义**）
            public int    hiding;     // Hide.hiding；没有 Hide 组件时为 0
            public bool   hasHide;    // 有没有 Hide 组件

            /// <summary>
            /// 这个物体有没有父物体。
            ///
            /// ★ 只记录 / 还原**根物体**的坐标。
            ///
            ///   有父物体的东西（比如 Player/Body）坐标是由父物体决定的：
            ///   Body 的 localPosition 必须永远是 (0, 0.9, 0)。
            ///   如果按世界坐标去还原它，Unity 会把它换算回 localPosition ——
            ///   只要父物体动过，换算结果就会偏离 (0, 0.9, 0)，
            ///   表现为"离出生点越远，Body 的局部坐标偏得越厉害"。
            /// </summary>
            public bool hasParent;
        }

        /// <summary>一个怪物的状态。</summary>
        [System.Serializable]
        public sealed class MonsterState
        {
            public string path;
            public int    captured;
        }

        [System.Serializable]
        public sealed class Snapshot
        {
            public List<ObjectState>  objects  = new List<ObjectState>();
            public List<MonsterState> monsters = new List<MonsterState>();
        }

        // ------------------------------------------------------------------ 两份快照

        /// <summary>
        /// 开局快照：点「开始游戏」时保存，之后**不再更新**。
        /// 点「重新开始」时还原到它。
        /// </summary>
        public static Snapshot StartSnapshot { get; private set; }

        /// <summary>
        /// 存档快照：点「保存」时记录并覆盖更新。
        /// 点「读取游戏」时还原到它。
        /// </summary>
        public static Snapshot SaveSnapshot { get; private set; }

        /// <summary>
        /// 用存档文件里反序列化出来的数据设置存档快照（只给 <see cref="SaveSystem"/> 用）。
        /// </summary>
        public static void SetSaveSnapshot(Snapshot snap)
        {
            SaveSnapshot = snap;
        }

        /// <summary>有没有开局快照。</summary>
        public static bool HasStartSnapshot => StartSnapshot != null && StartSnapshot.objects.Count > 0;

        /// <summary>有没有存档快照。</summary>
        public static bool HasSaveSnapshot => SaveSnapshot != null && SaveSnapshot.objects.Count > 0;

        // ------------------------------------------------------------------ 拍快照

        /// <summary>
        /// 记录当前世界状态，返回一份快照。
        ///
        /// ★ 记录的是**世界坐标**（transform.position），
        ///   还原时也直接写世界坐标 —— 不受父子关系影响。
        /// </summary>
        public static Snapshot Capture()
        {
            var snap = new Snapshot();

            GameObject[] all = Object.FindObjectsOfType<GameObject>(true);

            Scene scene = SceneManager.GetActiveScene();

            for (int i = 0; i < all.Length; i++)
            {
                GameObject go = all[i];
                if (go == null) continue;
                if (go.scene != scene) continue;
                if ((go.hideFlags & HideFlags.DontSave) != 0) continue;

                string path = ScenePath(go);
                if (string.IsNullOrEmpty(path)) continue;

                // 这类运行时自动生成的辅助物体不记录
                if (IsRuntimeOnly(go)) continue;

                bool isChild = go.transform.parent != null;

                Hide h = go.GetComponent<Hide>();
                Vector3 p = go.transform.position;

                // ★ 子物体不用记 hiding —— 它的显隐完全由父物体 + 各自系统的
                //   状态机决定，还原时也会被跳过（见 Restore 里的说明）。
                //   这里统一记成"可见"，避免存一堆没用的 false。
                int hidingToRecord = isChild ? 0 : (h != null ? h.hiding : 0);

                snap.objects.Add(new ObjectState
                {
                    path      = path,
                    x         = p.x,
                    y         = p.y,
                    z         = p.z,
                    hiding    = hidingToRecord,
                    hasHide   = h != null,

                    // ★ 只有根物体的世界坐标才是"它的位置"；
                    //   子物体（Player/Body 之类）的坐标由父物体决定，不能单独还原。
                    hasParent = isChild,
                });
            }

            // 怪物 captured
            PuzzleMonster[] mons = Object.FindObjectsOfType<PuzzleMonster>(true);

            for (int i = 0; i < mons.Length; i++)
            {
                PuzzleMonster m = mons[i];
                if (m == null) continue;
                if (m.gameObject.scene != scene) continue;

                snap.monsters.Add(new MonsterState
                {
                    path     = ScenePath(m.gameObject),
                    captured = m.Captured,
                });
            }


            return snap;
        }

        /// <summary>把当前世界状态记为「开局快照」。</summary>
        public static void CaptureStart() => StartSnapshot = Capture();

        /// <summary>把当前世界状态记为「存档快照」。</summary>
        public static void CaptureSave() => SaveSnapshot = Capture();

        // ------------------------------------------------------------------ 还原

        /// <summary>
        /// 把世界还原成 <paramref name="snap"/> 记录的样子。
        ///
        /// 还原顺序：
        ///   1. 先恢复所有 hiding（这决定了物体会不会被 SetActive(false)）
        ///   2. 再恢复坐标
        ///   3. 最后恢复怪物 captured
        ///
        /// ★ 先 hiding 再坐标：hiding = 1 的物体被 SetActive(false) 之后
        ///   仍然可以改 transform.position（Unity 允许对未激活物体改坐标），
        ///   所以顺序不影响结果，但先 hiding 语义更清楚。
        /// </summary>
        /// <returns>实际恢复了几个物体。</returns>
        public static int Restore(Snapshot snap, string label)
        {
            if (snap == null || snap.objects.Count == 0)
            {
                Debug.LogWarning($"[EchoPuzzle] {label}：没有可用的快照。");
                return 0;
            }

            Scene scene = SceneManager.GetActiveScene();

            // 先建路径 -> 物体 的索引（一次遍历，避免每个条目都全场景找）
            var byPath = new Dictionary<string, GameObject>(snap.objects.Count);

            GameObject[] all = Object.FindObjectsOfType<GameObject>(true);

            for (int i = 0; i < all.Length; i++)
            {
                GameObject go = all[i];
                if (go == null) continue;
                if (go.scene != scene) continue;
                if ((go.hideFlags & HideFlags.DontSave) != 0) continue;

                string path = ScenePath(go);
                if (string.IsNullOrEmpty(path)) continue;

                byPath[path] = go;
            }

            int applied = 0;

            // ---- 1) hiding（同时会同步 activeSelf） ----
            for (int i = 0; i < snap.objects.Count; i++)
            {
                ObjectState st = snap.objects[i];
                if (st == null) continue;
                if (!byPath.TryGetValue(st.path, out GameObject go)) continue;

                // ★★ 子物体的显隐**跳过** ★★
                //
                //   子物体的 activeSelf 是由父物体 + 各自系统的状态机决定的
                //   （比如 DungeonWeaponModel/Barrel 的开关完全跟着父物体的
                //    SetActive 走）。如果这里给子物体单独 SetActive(true)，
                //   就会把它从父物体的隐藏里"救"出来，表现为：
                //     · 父物体明明关着，零件却单独显示
                //     · 而且不再跟随父物体移动（父物体的 Transform 一动不动）
                //   —— 这正是"重新开始后武器建模一直显示且不跟随"的原因。
                //
                //   根物体的显隐照常还原（那是真的游戏状态）。
                if (st.hasParent) continue;

                Hide h = go.GetComponent<Hide>();

                if (h != null)
                {
                    h.SetHiding(st.hiding);
                }
                else if (go.activeSelf != (st.hiding == 0))
                {
                    // 没有 Hide 的物体：按 hiding 的语义直接设 activeSelf
                    go.SetActive(st.hiding == 0);
                }
            }

            // ---- 2) 坐标 ----
            for (int i = 0; i < snap.objects.Count; i++)
            {
                ObjectState st = snap.objects[i];
                if (st == null) continue;
                if (!byPath.TryGetValue(st.path, out GameObject go)) continue;

                // ★★ 跳过玩家物体 ★★
                //
                //   玩家不能靠"直接改 transform.position"来还原 ——
                //   PlayerController 的相机枢轴是 LateUpdate 里平滑跟随的，
                //   直接改坐标会让枢轴停在旧位置，表现为：
                //     · 玩家不在画面正中，偏在一边
                //     · 转身时绕着画面中心转
                //     · 脚步光带出现在画面中心
                //   玩家必须走 Teleport()，那里会**同时**把枢轴和相机瞬移过去。
                //   调用方（SaveSystem.Load / GameUI）会负责这件事。
                if (go.GetComponent<PlayerController>() != null) continue;

                // ★★ 只还原根物体的坐标 ★★
                //
                //   子物体的世界坐标完全由父物体决定（Player/Body 的
                //   localPosition 必须固定是 (0, 0.9, 0)）。
                //   如果给子物体写世界坐标，Unity 会把它换算回 localPosition，
                //   父物体一动就会偏离原值 —— 这正是"Body 局部坐标变了、
                //   而且离出生点越远偏得越厉害"的原因。
                if (st.hasParent) continue;
                if (go.transform.parent != null) continue;

                go.transform.position = new Vector3(st.x, st.y, st.z);
                applied++;
            }

            // ---- 3) 怪物 captured ----
            int monApplied = 0;

            if (snap.monsters != null && snap.monsters.Count > 0)
            {
                var capByPath = new Dictionary<string, int>(snap.monsters.Count);

                for (int i = 0; i < snap.monsters.Count; i++)
                {
                    MonsterState ms = snap.monsters[i];
                    if (ms == null) continue;
                    capByPath[ms.path] = ms.captured;
                }

                PuzzleMonster[] mons = Object.FindObjectsOfType<PuzzleMonster>(true);

                for (int i = 0; i < mons.Length; i++)
                {
                    PuzzleMonster m = mons[i];
                    if (m == null) continue;
                    if (m.gameObject.scene != scene) continue;

                    if (!capByPath.TryGetValue(ScenePath(m.gameObject), out int want)) continue;

                    m.SetCaptured(want);
                    monApplied++;
                }
            }


            return applied;
        }

        /// <summary>还原到开局快照（点「重新开始」）。</summary>
        public static int RestoreStart() => Restore(StartSnapshot, "重新开始（还原开局快照）");

        /// <summary>
        /// 丢掉开局快照（临时数据，不需要长期持有）。
        ///
        /// ★ 由 <see cref="EchoRuntimeBootstrap"/> 在退出时调用，
        ///   保证它只是**本次运行期间**的临时数据。
        /// </summary>
        public static void DropStartSnapshot()
        {
            StartSnapshot = null;
        }

        /// <summary>还原到存档快照（点「读取游戏」）。</summary>
        public static int RestoreSave() => Restore(SaveSnapshot, "读取游戏（还原存档快照）");

        // ------------------------------------------------------------------ 工具

        /// <summary>场景路径：从根到自己的名字，用 / 连接。</summary>
        private static string ScenePath(GameObject go)
        {
            if (go == null) return null;

            var parts = new List<string>(8);

            Transform t = go.transform;

            while (t != null)
            {
                parts.Add(t.name);
                t = t.parent;
            }

            parts.Reverse();

            return string.Join("/", parts);
        }

        /// <summary>
        /// 运行时自动生成的辅助物体（不该进快照）。
        /// 这些是各系统用 HideFlags.HideAndDontSave 或固定名字创建的。
        /// </summary>
        private static bool IsRuntimeOnly(GameObject go)
        {
            string n = go.name;

            return n.StartsWith("[EchoPuzzle]");
        }
    }
}
