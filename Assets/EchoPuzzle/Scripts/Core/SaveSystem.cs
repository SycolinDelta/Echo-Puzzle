using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace EchoPuzzle
{
    /// <summary>
    /// 存档 / 读档。**只有一个存档槽**。
    ///
    /// 记录的内容：
    ///   · 玩家位置与朝向
    ///   · 5 个标记（哪些槽位有、各自位置）
    ///   · 相机（俯仰角 / 缩放距离 / 朝向）
    ///   · 场景里所有物体的启用状态 —— 这一条同时覆盖了
    ///     TriggerVolume（它触发后会把自己收起来）、spawnObjects 放出来的物体、
    ///     HintZone、以及机关门之类的被影响物
    ///
    /// **不记录**声波光带 —— 读档后场上应当是干净的。
    ///
    /// 物体的识别方式：场景路径（`父/子/孙`）。因为存档只在这一个场景里用，
    /// 路径足够稳定，也避免了给每个物体加脚本、改 GUID 之类的麻烦。
    /// </summary>
    public static class SaveSystem
    {
        private const string FileName = "echopuzzle_save.json";

        /// <summary>存档文件路径（只读用途，方便调试时查看）。</summary>
        public static string SavePath => Path.Combine(Application.persistentDataPath, FileName);

        /// <summary>有没有存档。</summary>
        public static bool HasSave => File.Exists(SavePath);

        /// <summary>最近一次成功读档恢复的计时（秒）。旧存档（无此字段）为 0。</summary>
        public static float LoadedElapsed { get; private set; }

        // ------------------------------------------------------------------ 数据结构

        [System.Serializable]
        private sealed class SaveData
        {
            public int     version = 2;
            public float   elapsed;        // 通关计时（秒），读档后从这一刻继续

            // 玩家
            public float   playerX, playerY, playerZ;
            public float   playerYaw;       // 玩家物体的水平朝向

            // 相机
            public float   cameraYaw;       // 相机的水平朝向
            public float   pitch;           // 相机的俯仰角
            public float   cameraDistance;  // 相机的缩放距离

            // 标记（固定 5 个槽位）
            public List<float> markerX = new List<float>();
            public List<float> markerY = new List<float>();
            public List<float> markerZ = new List<float>();
            public List<bool>  markerUsed = new List<bool>();

            // ★ 世界快照：所有物体的坐标 + hiding，以及怪物的 captured
            public WorldSnapshot.Snapshot world = new WorldSnapshot.Snapshot();
        }

        // ------------------------------------------------------------------ 保存

        /// <summary>
        /// 现在能存档吗？
        ///
        /// ★ 两条禁止条件（任何一条成立就不许存档）：
        ///   1) 有怪物的击杀范围处于 killRadiusMoving（移动中）
        ///      —— 那种时刻存下来，读档会直接落在被追杀的位置。
        ///      特殊动作 / 停下后 3 秒余韵也算"移动中"。
        ///   2) 有怪物处于**受惊**状态
        ///      —— 受惊时击杀范围虽然按 Idle 算，但怪物仍在追查，
        ///         存下来会让读档后的状态很别扭。
        ///
        /// ★ 注意受惊要单独判断：受惊时 IsKillRangeMoving 会返回 false，
        ///   只靠第 1 条拦不住。
        /// </summary>
        public static bool CanSave()
        {
            PuzzleMonster[] all = Object.FindObjectsOfType<PuzzleMonster>(true);

            for (int i = 0; i < all.Length; i++)
            {
                PuzzleMonster m = all[i];
                if (m == null) continue;
                if (!m.isActiveAndEnabled) continue;

                // 1) 移动中（击杀范围按 killRadiusMoving）
                if (m.IsKillRangeMoving) return false;

                // 2) 受惊中
                if (m.IsFrightened) return false;
            }

            return true;
        }

        /// <summary>把当前进度写到存档。返回是否成功。</summary>
        public static bool Save()
        {
            // ★ 有怪物处于 killRadiusMoving -> 不许存档
            if (!CanSave())
            {
                Debug.LogWarning("[EchoPuzzle] 存档失败：场上有怪物正在活动或受惊，等它们平静下来再存。");
                return false;
            }

            var data = new SaveData();

            // ---- 玩家 ----
            var player = Object.FindObjectOfType<PlayerController>();
            if (player != null)
            {
                Vector3 p = player.transform.position;
                data.playerX   = p.x;
                data.playerY   = p.y;
                data.playerZ   = p.z;
                data.playerYaw = player.transform.eulerAngles.y;

                // 相机状态：水平角 / 俯仰角 / 缩放距离，三项都存
                data.cameraYaw      = player.Yaw;
                data.pitch          = player.Pitch;
                data.cameraDistance = player.Distance;
            }

            // ---- 标记 ----
            var markers = Object.FindObjectOfType<MarkerSystem>(true);
            if (markers != null)
            {
                var pos  = new List<Vector3>();
                var used = new List<bool>();
                markers.ExportSlots(pos, used);

                for (int i = 0; i < pos.Count; i++)
                {
                    data.markerX.Add(pos[i].x);
                    data.markerY.Add(pos[i].y);
                    data.markerZ.Add(pos[i].z);
                    data.markerUsed.Add(used[i]);
                }
            }

            // ---- 通关计时 ----
            var ui = GameUI.Instance;
            if (ui != null) data.elapsed = ui.ElapsedTime;

            // ---- 世界快照（所有物体的坐标 + hiding，怪物 captured） ----
            //
            // ★ 同时写进内存里的「存档快照」，读档时直接用这一份，
            //   不必再从磁盘反序列化一遍。
            WorldSnapshot.CaptureSave();
            data.world = WorldSnapshot.SaveSnapshot;

            // ---- 写盘 ----
            try
            {
                File.WriteAllText(SavePath, JsonUtility.ToJson(data, true));
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[EchoPuzzle] 保存失败：{e.Message}");
                return false;
            }
        }

        // ------------------------------------------------------------------ 读取

        /// <summary>读取存档并应用。返回是否成功。</summary>
        public static bool Load()
        {
            LoadedElapsed = 0f;

            if (!HasSave)
            {
                Debug.LogWarning("[EchoPuzzle] 没有找到存档。");
                return false;
            }

            SaveData data;

            try
            {
                data = JsonUtility.FromJson<SaveData>(File.ReadAllText(SavePath));
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[EchoPuzzle] 读取存档失败：{e.Message}");
                return false;
            }

            if (data == null)
            {
                Debug.LogError("[EchoPuzzle] 存档内容无法解析。");
                return false;
            }

            // ---- 先恢复世界快照（所有物体的坐标 + hiding） ----
            //   要在玩家 / 相机之前做：万一读档时玩家正好站在某个触发区里，
            //   先恢复好物体状态更稳。
            //
            //   ★ Restore 内部会**跳过玩家物体** —— 玩家必须走 Teleport，
            //     否则相机枢轴不同步（见 WorldSnapshot 里的说明）。
            WorldSnapshot.SetSaveSnapshot(data.world);
            WorldSnapshot.RestoreSave();

            // ---- 玩家 + 相机 ----
            var player = Object.FindObjectOfType<PlayerController>();
            if (player != null)
            {
                var p = new Vector3(data.playerX, data.playerY, data.playerZ);

                // 玩家位置 + 自身朝向。
                // ★ Teleport 会把相机枢轴一起瞬移过去（枢轴是平滑跟随的，
                //   只改 transform.position 会让镜头留在旧位置）。
                player.Teleport(p, data.playerYaw, data.pitch);

                // 相机的三项（水平角 / 俯仰角 / 缩放距离）单独恢复，
                // 因为相机的水平角可能和玩家朝向不同（玩家转过头之后镜头还留在原方向）。
                //
                // ★ 必须放在 Teleport **之后**：它会调 SnapCameraToPivot，
                //   按最终的朝向 / 距离把枢轴和相机重新摆正。
                player.ResetCameraImmediate(data.pitch, data.cameraYaw, data.cameraDistance);
            }

            // ---- 标记 ----
            var markers = Object.FindObjectOfType<MarkerSystem>(true);
            if (markers != null)
            {
                var pos  = new List<Vector3>(data.markerX.Count);
                for (int i = 0; i < data.markerX.Count; i++)
                    pos.Add(new Vector3(data.markerX[i], data.markerY[i], data.markerZ[i]));

                markers.ImportSlots(pos, data.markerUsed);
            }

            // ---- 声波光带按需求不记录，读档时清掉 ----
            var sonar = Object.FindObjectOfType<SonarSystem>();
            if (sonar != null)
            {
                var trail = sonar.GetComponentInChildren<SonarTrailRenderer>(true);
                if (trail != null) trail.ClearTrails();
            }

            // 计时从存档时刻恢复
            LoadedElapsed = data.elapsed;

            return true;
        }

        /// <summary>删掉存档。</summary>
        public static void DeleteSave()
        {
            if (!HasSave) return;

            try { File.Delete(SavePath); }
            catch (System.Exception e) { Debug.LogError($"[EchoPuzzle] 删除存档失败：{e.Message}"); }
        }

        // ------------------------------------------------------------------ 物体状态
    }
}
