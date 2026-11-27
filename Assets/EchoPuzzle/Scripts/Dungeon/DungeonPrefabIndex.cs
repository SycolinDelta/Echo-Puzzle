using UnityEngine;

namespace EchoPuzzle.Dungeon
{
    /// <summary>
    /// 地牢模式 prefab 索引。
    ///
    /// ★ 为什么需要它：
    ///   怪物 / boss 的 prefab 都放在 Assets/EchoPuzzle/Prefabs 下（方便找、方便改），
    ///   但那个目录不在 Resources 里，打包后 Resources.Load 是找不到的。
    ///   所以这里放一个 Resources 下的索引资产，**只持有引用**；
    ///   引用到的 prefab 会被一起打进包，而且指的就是 Prefabs 里那一份，
    ///   改 Prefabs/BossMandala.prefab 打包后生成的 boss 会跟着变。
    /// </summary>
    [CreateAssetMenu(fileName = "DungeonPrefabIndex",
                     menuName = "EchoPuzzle/地牢 Prefab 索引")]
    public sealed class DungeonPrefabIndex : ScriptableObject
    {
        /// <summary>Resources 下的相对路径（不带扩展名）。</summary>
        public const string ResourcePath = "EchoRuntime/DungeonPrefabIndex";

        [Tooltip("普通怪物：Assets/EchoPuzzle/Prefabs/PuzzleMonster.prefab")]
        public GameObject puzzleMonster;

        [Tooltip("boss：Assets/EchoPuzzle/Prefabs/BossMandala.prefab")]
        public GameObject bossMandala;

        /// <summary>读索引资产；没放 / 没导入成功时返回 null。</summary>
        public static DungeonPrefabIndex Load()
        {
            return Resources.Load<DungeonPrefabIndex>(ResourcePath);
        }
    }
}
