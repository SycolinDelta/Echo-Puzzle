using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EchoPuzzle
{
    /// <summary>这个立方体被玩家碰到时要触发的效果。</summary>
    public enum TriggerEffect
    {
        /// <summary>什么都不做（只改声波外观）。</summary>
        None = 0,

        /// <summary>
        /// 按**名字**查找，把配置里列出的物体统统启用 / 删除，然后**删掉自己**。
        /// 具体要启用哪些、删除哪些，在检查器的 Operate Objects 面板里配。
        /// </summary>
        OperateObjects = 1,

        /// <summary>直接跳转到 YOU WIN。</summary>
        Win = 2,

        /// <summary>直接跳转到 YOU LOSE。</summary>
        Lose = 3,

        /// <summary>
        /// 补充电池。增加几个由 <see cref="TriggerVolume.chargeAmount"/> 决定。
        /// </summary>
        Charge = 4,

        /// <summary>
        /// 激活指定的怪物：把它们的 Hide.hiding 改成 0（显示），
        /// 并把 captured 改成 1（立刻锁定玩家并持续追击）。
        /// 具体是哪些怪物，在 <see cref="TriggerVolume.activateMonsters"/> 里配。
        /// </summary>
        Activate = 5,

        /// <summary>
        /// 【地牢模式】下一层：玩家进入后地图淡出 -> 生成下一层 -> 淡入，
        /// 玩家重新出现在新的起点。
        ///
        /// ★ 光带外观 / 玩家进入时的光带和音效，默认和 <see cref="Win"/> 一样。
        ///   实际生下一层的逻辑由 DungeonMapGenerator 监听 OnNextStage 事件处理。
        /// </summary>
        NextStage = 6,
    }

    /// <summary>一条"按名字操作物体"的配置。</summary>
    [System.Serializable]
    public sealed class NameOperation
    {
        [Tooltip("物体的**名字**。会查找场景里所有同名的物体，全部一起处理。\n" +
                 "可以直接手打，也可以从「层级」面板把物体拖到右边的槽里自动填名字。")]
        public string objectName = "";
    }

    /// <summary>
    /// 一个**不可见、可穿过**的立方体。玩家走进去就触发效果。
    ///
    /// 它同时也能改变声波光带的外观（和终点空间一个思路）：
    ///   落到这个立方体里的声波光带，宽度 / 不透明度 / 颜色都能被强制指定。
    ///
    /// 想要"显示文字"请用 <see cref="HintZone"/>。
    ///
    /// 用法：拖进场景 → 用 Scale 工具拉成想要的大小 → 在 Inspector 里配效果。
    /// </summary>
    [RequireComponent(typeof(BoxCollider))]
    public sealed class TriggerVolume : MonoBehaviour
    {
        // ------------------------------------------------------------------ 效果

        [Header("玩家进入时触发")]
        [Tooltip("要触发什么效果。\n" +
                 "OperateObjects = 按名字启用/删除物体，然后删掉自己。\n" +
                 "None = 只改声波外观、不做任何触发。")]
        public TriggerEffect effect = TriggerEffect.None;

        [Tooltip("OperateObjects：要**启用**的物体名字（同名的一起启用）。\n\n" +
                 "★ 只对当前**已被删除**的物体生效；本来就在场景里的不受影响。")]
        public List<NameOperation> spawnObjects = new List<NameOperation>();

        [Tooltip("OperateObjects：要**删除**的物体名字（同名的一起删除）。\n\n" +
                 "★ 只对当前**启用着**的物体生效；本来就被删除的不受影响。")]
        public List<NameOperation> removeObjects = new List<NameOperation>();

        [Tooltip("触发后把自己也删掉（OperateObjects 默认开启）")]
        public bool destroySelfAfterTrigger = true;

        [Tooltip("Charge：进入后补充几个电池")]
        [Min(0)] public int chargeAmount = 1;

        [Tooltip("Activate：要激活的怪物。\n\n" +
                 "★ 触发后会把它们的 hiding 改成 0（显示出来），\n" +
                 "  并把 captured 改成 1（立刻锁定玩家并持续追击）。")]
        public List<PuzzleMonster> activateMonsters = new List<PuzzleMonster>();

        // ------------------------------------------------------------------ 自定义触发音效

        [Header("自定义触发音效（OperateObjects / Charge）")]
        [Tooltip("勾上 = 用下面列表里的音效替换默认的触发音效。\n\n" +
                 "★ 可以拖入**一个或多个**音频资源。\n" +
                 "  多个时，每次触发**随机播放其中一个**。\n" +
                 "  列表为空或全部是一项都没填时，回落到默认音效。")]
        public bool useSpecialSound = false;

        [Tooltip("自定义触发音效。从项目栏把 .wav / .ogg / .mp3 拖进来。")]
        public List<AudioClip> specialSounds = new List<AudioClip>();

        public bool Consumed { get; private set; }

        // ------------------------------------------------------------------ 密码

        [Header("密码（只对 OperateObjects 有效）")]
        [Tooltip("勾上 = 玩家进入后必须**输入正确密码**才会生效。\n\n" +
                 "不勾（默认）= 玩家一进入就立即生效。\n\n" +
                 "★ 勾上后，玩家站在区域内时右下角会出现「输入密码并回车：」，\n" +
                 "  此时数字键改为输入密码（不再放置标记）。")]
        public bool requirePassword = false;

        [Tooltip("密码内容。玩家输入这个字符串并回车后触发区才会生效。")]
        public string password = "1234";

        /// <summary>玩家当前是否正站在本区域内（决定要不要显示输入框）。</summary>
        public bool PlayerInside { get; private set; }

        /// <summary>玩家已输入的内容（GameUI 读它来显示）。</summary>
        public string PasswordInput { get; private set; } = string.Empty;

        /// <summary>上一次输入是否错误（界面给个反馈）。</summary>
        public bool PasswordWrong { get; private set; }

        /// <summary>密码是否已经输入正确过。</summary>
        public bool PasswordAccepted { get; private set; }

        /// <summary>
        /// 这个触发区当前是否需要玩家输入密码。
        /// = 开了密码机制 + 玩家在区域内 + 还没输对 + 还没触发过。
        /// </summary>
        public bool WantsPasswordInput =>
            requirePassword
            && !PasswordAccepted
            && !Consumed
            && PlayerInside
            && effect == TriggerEffect.OperateObjects;

        /// <summary>
        /// 每帧处理密码输入（数字键 + 退格 + 回车）。
        ///
        /// 返回 true 表示"这一帧吃掉了数字键输入"，
        /// MarkerSystem 看到它会跳过本帧的放标记逻辑。
        /// </summary>
        public bool TickPasswordInput()
        {
            if (!WantsPasswordInput) return false;

            // ---- 退格 ----
            if (Input.GetKeyDown(KeyCode.Backspace) && PasswordInput.Length > 0)
            {
                PasswordInput = PasswordInput.Substring(0, PasswordInput.Length - 1);
                PasswordWrong = false;
            }

            // ---- 数字：0-9 主键盘 + 小键盘 ----
            for (int d = 0; d <= 9; d++)
            {
                if (Input.GetKeyDown(KeyCode.Alpha0 + d) ||
                    Input.GetKeyDown(KeyCode.Keypad0 + d))
                {
                    PasswordInput += (char)('0' + d);
                    PasswordWrong = false;
                }
            }

            // ---- 回车：校验 ----
            if (Input.GetKeyDown(KeyCode.Return) ||
                Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                if (PasswordInput == password)
                {
                    PasswordAccepted = true;
                    PasswordInput    = string.Empty;
                    PasswordWrong    = false;

                    // 密码正确 -> 立刻生效
                    Fire();
                }
                else
                {
                    PasswordWrong = true;
                    PasswordInput = string.Empty;
                }
            }

            return true;
        }

        /// <summary>玩家离开区域时清掉输入状态（但保留"已输对"）。</summary>
        public void OnPlayerExit()
        {
            PlayerInside  = false;
            PasswordInput = string.Empty;
            PasswordWrong = false;
        }

        // ------------------------------------------------------------------ 声波外观

        [Header("声波光带落进来时的外观")]
        [Tooltip("勾上 = 把光带宽度改成下面的值")]
        public bool overrideWidth = false;
        [Tooltip("固定的光带宽度（米）")]
        public float waveWidth = 0.22f;

        [Tooltip("勾上 = 把光带不透明度改成下面的值")]
        public bool overrideAlpha = true;

        [Range(0f, 1f)] public float waveAlpha = 1f;

        [Tooltip("勾上 = 把光带颜色改成下面的值")]
        public bool overrideColor = false;
        public Color waveColor = Color.white;

        // ------------------------------------------------------------------ 触发时发声

        [Header("触发时在玩家脚下发一次声波")]
        [Tooltip("勾上 = 本触发区被触发时，在玩家脚下释放一次特殊声波。\n\n" +
                 "采样方式与消失方式和常规声波**完全一致**（六层俯仰角 + 上半球、\n" +
                 "波前和光带同样会因寿命和不透明度衰减而消失），\n" +
                 "只是下面这一套数值**每个触发区各自独立**。")]
        public bool emitSonarOnTrigger = true;

        [Tooltip("这一次特殊声波的全部数值。默认按「空格发声」填")]
        public SonarEmitSettings sonar = SonarEmitSettings.DefaultShout;

        // ------------------------------------------------------------------ 判定

        [Header("判定")]
        [Tooltip("除了碰撞触发，还额外用玩家脚底位置做范围判定（更可靠）")]
        public bool useBoundsCheck = true;

        // ⚠ 性能关键：原来这是个属性，每次访问都要 GetComponent<BoxCollider>()、
        //    TransformPoint、lossyScale、Vector3.Scale —— 全是 Unity 属性调用，
        //    单个可能微秒级。而铺光带时每步都要查它（FindTrigger + Contains），
        //    每帧就是几万次。（Profiler 实测：光带渲染占 85% 帧时间。）
        //
        //    改成缓存字段，数据变化时由 RecacheBounds() 刷新。
        private Bounds m_CachedBounds;
        private bool   m_BoundsReady;

        /// <summary>世界包围盒（内部有缓存，别在每帧路径上重建）。</summary>
        public Bounds WorldBounds
        {
            get
            {
                if (!m_BoundsReady) RecacheBounds();
                return m_CachedBounds;
            }
        }

        /// <summary>重新计算缓存的包围盒。</summary>
        public void RecacheBounds()
        {
            m_CachedBounds = ComputeBoundsLive();
            m_BoundsReady  = true;
        }

        /// <summary>
        /// 现场计算包围盒（**不**走缓存）。
        ///
        /// 只给编辑器的 Gizmo 用：在 Scene 视图里拖动 / 缩放物体时 OnValidate 不会触发，
        /// 缓存会停在旧位置，画出来的框就"不动"了。放在 Gizmo 里现算，
        /// 既不污染运行时缓存、也不给运行时增加任何开销。
        /// </summary>
        private Bounds ComputeBoundsLive()
        {
            var col = GetComponent<BoxCollider>();

            if (col == null) return new Bounds(transform.position, Vector3.zero);

            Vector3 c = transform.TransformPoint(col.center);
            Vector3 s = Vector3.Scale(col.size, Abs(transform.lossyScale));
            return new Bounds(c, s);
        }

        public bool Contains(Vector3 worldPos) => WorldBounds.Contains(worldPos);

        /// <summary>
        /// 超廉价的预筛：只比较中心距离和半尺寸，完全不碰 Unity 属性。
        /// 用来在正式 Contains 之前快速排除掉绝大多数触发体。
        /// </summary>
        public bool QuickReject(Vector3 worldPos)
        {
            // 直接读缓存字段，绕开 getter —— 这是每步都要走的热路径
            if (!m_BoundsReady) RecacheBounds();

            Vector3 c = m_CachedBounds.center;
            Vector3 e = m_CachedBounds.extents;

            float dx = worldPos.x - c.x; if (dx < 0f) dx = -dx;
            float dy = worldPos.y - c.y; if (dy < 0f) dy = -dy;
            float dz = worldPos.z - c.z; if (dz < 0f) dz = -dz;

            return dx > e.x || dy > e.y || dz > e.z;
        }

        private static Vector3 Abs(Vector3 v)
            => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        private void Reset()
        {
            var col = GetComponent<BoxCollider>();
            col.isTrigger = true;
            col.size = Vector3.one;
        }

        private PlayerController m_Player;

        // 所有 TriggerVolume 的启用顺序号。
        // 物体的启用时机各不相同（有的要等别的触发器把它显现出来），
        // 所以每次有触发器启用就补跑一次开局隐藏；用版本号去重，保证同一次启用里不重复跑。
        // ★ 隐藏哪些物体由各物体自己的 Hide.hiding 决定，**与 spawnObjects 无关**。
        private static int s_Wave;
        private int m_HiddenAtWave = -1;

        private void Awake()
        {
            var col = GetComponent<BoxCollider>();
            if (col != null) col.isTrigger = true;

            RecacheBounds();
        }

        private void OnEnable()
        {
            s_Wave++;
            m_HiddenAtWave = -1;          // 重新启用 -> 允许再补藏一次
            HideSpawnTargets();

            RecacheBounds();
            ZoneSystem.RegisterTrigger(this);
        }

        private void OnDisable() => ZoneSystem.UnregisterTrigger(this);

        /// <summary>
        /// 开局隐藏：只处理**挂了 Hide 并且 hiding = 1** 的物体。
        ///
        /// ★ 这里**不再**根据 spawnObjects 自动藏东西。
        ///   旧行为是"spawnObjects 里列出的物体开局一律藏起来"，
        ///   现在改成由物体自己身上的 <see cref="Hide"/> 组件决定。
        ///
        ///   这样"开局藏哪些东西"完全由场景作者控制，
        ///   而不是由某个触发器的配置倒推出来。
        /// 
        /// 处理范围：**整个场景**（含未激活的），不受本触发器的 spawnObjects 限制 ——
        /// 因为 hiding = 1 的物体哪怕没被任何触发器引用，也应当开局消失。
        /// </summary>
        private void HideSpawnTargets()
        {
            // ★ 去重只用于 OnEnable 这种"同一帧可能被多个触发器触发"的场景。
            //   但即使跳过，也**必须**保证 HideAllMarkedObjects 已经被某个
            //   调用者执行过 —— 所以这里不做早退，直接执行。
            //   HideAllMarkedObjects 是幂等的，重复调用没有副作用。
            m_HiddenAtWave = s_Wave;

            HideAllMarkedObjects();
        }

        /// <summary>
        /// 把场景里所有 Hiding = 1 的物体设为不可见。
        /// 静态方法，可以安全地被重复调用（已经隐藏的会跳过）。
        /// </summary>
        public static void HideAllMarkedObjects()
        {
            Hide[] all = Resources.FindObjectsOfTypeAll<Hide>();

            for (int i = 0; i < all.Length; i++)
            {
                Hide h = all[i];
                if (h == null) continue;
                if (h.hiding == 0) continue;      // Hiding = 0 -> 保持启用

                // 只处理当前场景里的（排除 Prefab 资产 / 预览对象）
                GameObject go = h.gameObject;
                if (go.scene != SceneManager.GetActiveScene()) continue;
                if ((go.hideFlags & HideFlags.DontSave) != 0) continue;

                if (go.activeSelf) go.SetActive(false);
            }
        }

        private void OnTriggerEnter(Collider other)
        {
            PlayerController pc = other.GetComponentInParent<PlayerController>();
            if (pc == null) return;

            PlayerInside = true;

            TryEnterOrFire(pc.FootPosition);
        }

        private void OnTriggerExit(Collider other)
        {
            if (other.GetComponentInParent<PlayerController>() == null) return;

            OnPlayerExit();
        }

        private void Update()
        {
            if (GameUI.Current != GameState.Playing) return;

            // 玩家全场只有一个，缓存起来 —— 不用每帧全场景扫一遍找它
            if (m_Player == null) m_Player = FindObjectOfType<PlayerController>();
            if (m_Player == null) return;

            // ---- 更新"玩家是否在区域内" ----
            //
            // ★ 两种判定都要做：
            //   · OnTriggerEnter 依赖 BoxCollider，玩家刚进区域时会给一次
            //   · useBoundsCheck 用包围盒每帧算，玩家瞬移 / 卡在边界时更可靠
            //   这里统一在 Update 里算一次，两个入口共用同一个 PlayerInside。
            //
            //   ★ 注意：**不能**在 Consumed 时提前返回 ——
            //     密码模式下要先把 PlayerInside 置上，GameUI 才会显示输入框。
            if (useBoundsCheck)
            {
                bool inside = Contains(m_Player.FootPosition);

                if (inside != PlayerInside)
                {
                    if (inside) PlayerInside = true;
                    else        OnPlayerExit();
                }

                if (inside) TryEnterOrFire(m_Player.FootPosition);
            }

            // 已经触发过了就不再处理密码
            if (Consumed)
            {
                PlayerInside = false;
                return;
            }

            // ---- 密码输入 ----
            //   放在这里是为了：即使 GameUI 没接管，密码也能正常输入。
            TickPasswordInput();
        }

        /// <summary>
        /// 玩家进入时的分派：
        ///   · 没开密码机制 -> 立即 Fire()
        ///   · 开了密码机制 -> 什么都不做，等玩家输入密码后由 TickPasswordInput 调 Fire()
        /// </summary>
        private void TryEnterOrFire(Vector3 playerFoot)
        {
            if (Consumed) return;

            if (requirePassword && effect == TriggerEffect.OperateObjects)
            {
                // 密码模式下**不自动触发**，只把 PlayerInside 置上（上面的逻辑已做）
                return;
            }

            Fire();
        }

        // ------------------------------------------------------------------ 触发

        /// <summary>执行效果。机关空间只触发一次。</summary>
        public void Fire()
        {
            // ★ 全场锁定中（NextStage 已触发、还没换完地图）—— 谁都不许触发
            if (GlobalLocked) return;

            if (Consumed) return;

            // ★ 开了密码机制的 OperateObjects 必须密码正确才能触发
            if (requirePassword && effect == TriggerEffect.OperateObjects && !PasswordAccepted)
                return;

            Consumed = true;

            // ★ 先发声，再做效果 —— 声波应该是"触发的那一刻"发出去的。
            EmitTriggerSonar();

            // 音效按效果类型分：
            //   OperateObjects -> Trigger（开关门之类），或自定义音效
            //   Win            -> Destination
            //   Lose           -> Death
            //   Charge         -> Trigger（和 OperateObjects 一样）
            //   其它（None）   -> 不响
            switch (effect)
            {
                case TriggerEffect.OperateObjects:
                    PlayTriggerSound();
                    Operate();
                    break;

                case TriggerEffect.Win:
                    AudioSystem.Play(AudioSystem.Cue.Destination);
                    GameUI.NotifyGoalReached();
                    break;

                case TriggerEffect.Lose:
                    AudioSystem.Play(AudioSystem.Cue.Death);
                    GameUI.NotifyPlayerDied();
                    break;

                case TriggerEffect.Charge:
                    // ★ 音效和 OperateObjects 一样
                    PlayTriggerSound();

                    if (BatterySystem.Instance != null)
                        BatterySystem.Instance.Add(Mathf.Max(0, chargeAmount));

                    // ★ 触发后把自己"删除"（hiding = 1），
                    //   和 OperateObjects 的自删除走同一套机制。
                    DeleteSelf();
                    break;

                case TriggerEffect.Activate:
                    // ★ 音效和 OperateObjects 一样
                    PlayTriggerSound();

                    ActivateMonsters();

                    // 触发后把自己"删除"
                    DeleteSelf();
                    break;

                case TriggerEffect.NextStage:
                    // ★★ 下一层 ★★
                    //
                    //   音效和 Win 一样（Destination）；光带由 EmitTriggerSonar 统一发，
                    //   外观是 nextStageSonar（粗细 0.22、不透明度 1、纯白）。
                    //
                    //   真正"淡出 -> 生成下一层 -> 淡入"的逻辑由
                    //   DungeonMapGenerator 订阅 OnNextStage 事件处理。
                    AudioSystem.Play(AudioSystem.Cue.Destination);

                    // ★★ 锁住全场触发区 ★★
                    //
                    //   需求："终点位置的 triggervolume 被触发后，在进入下一张地图前，
                    //         其他 triggervolume 不会被触发。"
                    //
                    //   ★ 必须在 Invoke 之前锁 —— 否则同一帧里紧挨着的另一个
                    //     触发区还能趁换图前触发一次。
                    LockAll();

                    OnNextStage?.Invoke();
                    break;
            }
        }

        /// <summary>
        /// 【地牢模式】玩家进入 NextStage 触发区时触发。
        ///
        /// ★ 由 <see cref="EchoPuzzle.Dungeon.DungeonMapGenerator"/> 订阅，
        ///   负责：地图淡出 -> 生成下一层 -> 淡入 -> 玩家出现在新起点。
        /// </summary>
        public static event System.Action OnNextStage;

        /// <summary>
        /// 【地牢模式】NextStage 已经被触发过 —— 全场 TriggerVolume 一律锁死。
        ///
        /// ★ 需求："终点位置的 triggervolume 被触发后，在进入下一张地图前，
        ///         其他 triggervolume 不会被触发。"
        ///
        /// ★ 由 DungeonMapGenerator 在换层时调 <see cref="UnlockAll"/> 解锁。
        /// </summary>
        public static bool GlobalLocked { get; private set; }

        /// <summary>锁住全场触发区（NextStage 触发时调用）。</summary>
        public static void LockAll()
        {
            GlobalLocked = true;
        }

        /// <summary>解锁（换层完成后调用）。</summary>
        public static void UnlockAll()
        {
            GlobalLocked = false;
        }

        /// <summary>
        /// 播放触发音效。
        ///
        /// ★ 勾了 <see cref="useSpecialSound"/> 且列表里至少有一个有效音效时，
        ///   从这些**自定义音效**里随机挑一个播；
        ///   否则用默认的 Trigger 分类音效。
        /// </summary>
        private void PlayTriggerSound()
        {
            if (useSpecialSound && specialSounds != null)
            {
                // 先收集所有非空项（列表里可能留了空位）
                m_SoundBuffer.Clear();

                for (int i = 0; i < specialSounds.Count; i++)
                {
                    AudioClip c = specialSounds[i];
                    if (c != null) m_SoundBuffer.Add(c);
                }

                if (m_SoundBuffer.Count > 0)
                {
                    AudioClip picked = m_SoundBuffer[Random.Range(0, m_SoundBuffer.Count)];
                    AudioSystem.PlayClip(picked);
                    return;
                }
            }

            AudioSystem.Play(AudioSystem.Cue.Trigger);
        }

        /// <summary>收集自定义音效时用的临时缓冲（避免每次分配）。</summary>
        private static readonly List<AudioClip> m_SoundBuffer = new List<AudioClip>(8);

        /// <summary>
        /// 激活配置里的所有怪物：
        /// 把它们的 Hide.hiding 改成 0（显示），并把 captured 改成 1（锁定追逐）。
        ///
        /// ★ 顺序很重要：先 hiding = 0 让它出现在场上，
        ///   再 captured = 1 让它立刻开始追玩家。
        ///   captured 一置 1，Update 下一帧就会把目标刷成玩家当前位置。
        /// </summary>
        private void ActivateMonsters()
        {
            if (activateMonsters == null) return;

            int  done = 0;

            for (int i = 0; i < activateMonsters.Count; i++)
            {
                PuzzleMonster mon = activateMonsters[i];
                if (mon == null) continue;

                // 1) 先显示出来
                Hide h = mon.GetComponent<Hide>();
                if (h != null) h.SetHiding(0);
                else if (!mon.gameObject.activeSelf) mon.gameObject.SetActive(true);

                // 2) 再锁定追击
                mon.SetCaptured(1);

                done++;
            }

        }

        /// <summary>把自己"删除"：hiding = 1。没挂 Hide 时退化为 SetActive(false)。</summary>
        private void DeleteSelf()
        {
            Hide self = GetComponent<Hide>();

            if (self != null) self.SetHiding(1);
            else              gameObject.SetActive(false);
        }

        /// <summary>
        /// 触发时在玩家脚下释放一次特殊声波。
        ///
        /// 数值来自本组件上的 <see cref="sonar"/>，所以**每个触发区各有一套独立参数**。
        /// 采样方式 / 消失方式和常规声波完全相同（都走 SonarSystem.Emit），
        /// 方位角起点也用玩家正前方。
        /// </summary>
        private void EmitTriggerSonar()
        {
            if (!emitSonarOnTrigger) return;

            var sonarSystem = FindObjectOfType<SonarSystem>();
            if (sonarSystem == null) return;

            PlayerController p = ResolvePlayer();
            if (p == null) return;

            // 方位角起点：玩家正前方的水平分量（和常规声波一致）
            Vector3 facing = p.transform.forward;
            facing.y = 0f;
            if (facing.sqrMagnitude < 1e-6f) facing = Vector3.forward;
            facing.Normalize();

            EmitParams emit = sonar.ToEmitParams(facing);

            // ★★ 触发区放出的声波**永远不该被怪物捕捉** ★★
            //
            //   理由：这些声波是"机关启动"的视觉反馈，声源就是**玩家脚下**。
            //   如果怪物能捕捉到，等于玩家一碰触发区就暴露自己的位置 ——
            //   不管是 OperateObjects、Charge，还是别的类型，都不该这样。
            //
            //   注意：ignoreMonster = true 只让怪物"不捕捉"，
            //   光带照常显示，视觉反馈不受影响。
            emit.ignoreMonster = true;

            sonarSystem.Emit(p.FootPosition, emit);
        }

        /// <summary>取玩家；没有就全场景找一次并缓存。</summary>
        private PlayerController ResolvePlayer()
        {
            if (m_Player == null) m_Player = FindObjectOfType<PlayerController>();
            return m_Player;
        }

        /// <summary>
        /// 按名字操作物体，最后把自己收起来。
        ///
        /// ★ 语义（按需求）：
        ///   · spawnObjects：**只对已被删除的**生效 —— 把它们启用
        ///                   （本来就启用着的不受影响）
        ///   · removeObjects：**只对启用着的**生效 —— 把它们删除
        ///                   （本来就被删除的不受影响）
        ///
        ///   所以这个操作是"把状态拨一次"，不会把已经正确的物体又翻回去。
        ///
        /// 改动前会记下每个物体的原始激活状态，重玩时按原始状态还原。
        /// </summary>
        private void Operate()
        {
            m_Changed.Clear();

            // ★ 需求：不管原物体的 Hiding 是什么，
            //   待启用的统一改成 0，待删除的统一改成 1。
            int on  = SetHidingByName(spawnObjects,  0);
            int off = SetHidingByName(removeObjects, 1);


            // ★ 触发后把自己"删除" —— 通过把 Hide.hiding 改成 1，
            //   而不是 SetActive(false) / Destroy。
            //
            //   这样：
            //     · 物体依然在场景里，重开游戏时 ResetTrigger() 能把它恢复
            //     · 和场景里其它物体的"删除"语义完全一致（都是 hiding = 1）
            //     · 不会因为 SetActive(false) 导致 OnDisable 里的注销逻辑出问题
            if (destroySelfAfterTrigger) DeleteSelf();
        }

        /// <summary>记录被本触发器改动过的物体 + 它们的原始激活状态。</summary>
        private readonly List<GameObject> m_Changed = new List<GameObject>(8);
        private readonly List<bool>       m_OriginalActive = new List<bool>(8);

        /// <summary>
        /// 按名字查找场景里所有匹配的物体并设置激活状态。返回改动了几个。
        ///
        /// 它找的是**当前场景**里的物体（含未激活的），不会碰到 Project 里的 Prefab 资产。
        /// </summary>
        private int SetHidingByName(List<NameOperation> list, int hiding)
        {
            if (list == null || list.Count == 0) return 0;

            int changed = 0;

            for (int i = 0; i < list.Count; i++)
            {
                NameOperation op = list[i];
                if (op == null || string.IsNullOrEmpty(op.objectName)) continue;

                GameObject[] found = FindAllByName(op.objectName);

                for (int k = 0; k < found.Length; k++)
                {
                    GameObject go = found[k];
                    if (go == null) continue;
                    if (go == gameObject) continue;      // 别把自己算进去

                    // ★ 没挂 Hide 的物体不受 OperateObjects 影响
                    Hide h = go.GetComponent<Hide>();
                    if (h == null) continue;

                    // ★★ 顺序很重要：**先记录原始状态，再改 hiding** ★★
                    //
                    //   原来先 SetHiding 再记录 go.activeSelf —— 记下来的是
                    //   "改动之后"的状态，重开时等于没还原。
                    if (!m_Changed.Contains(go))
                    {
                        m_Changed.Add(go);
                        m_OriginalActive.Add(go.activeSelf);   // 改动前的状态
                    }

                    // ★ 不管原本 Hiding 是几，一律改成目标值
                    h.SetHiding(hiding);

                    changed++;
                }
            }

            return changed;
        }

        /// <summary>查找当前场景里所有叫这个名字的物体（包含未激活的）。</summary>
        public static GameObject[] FindAllByName(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return new GameObject[0];

            // 换场景没关系，每次都按当前激活场景过滤
            Scene scene = SceneManager.GetActiveScene();

            var result = new List<GameObject>(8);

            // FindObjectsOfTypeAll 能拿到未激活的对象，但也会带上 Prefab 资产，
            // 所以下面必须用 "属于当前场景" + "不是资产" 两道过滤。
            Object[] all = Resources.FindObjectsOfTypeAll(typeof(GameObject));

            for (int i = 0; i < all.Length; i++)
            {
                GameObject go = all[i] as GameObject;
                if (go == null) continue;

                if (go.name != objectName) continue;
                if (go.scene != scene) continue;

                // HideFlags 里带 DontSave 的一般是 Prefab 资产或临时对象
                if ((go.hideFlags & HideFlags.DontSave) != 0) continue;

                result.Add(go);
            }

            return result.ToArray();
        }

        /// <summary>
        /// 重玩时恢复成**新开局的状态**：
        ///   1. 把自己放回来（触发时若把自己收起来了）
        ///   2. 把触发时改动过的物体还原成原始状态
        ///   3. **重新应用开局隐藏** —— hiding = 1 的 Hide 物体要再次消失
        /// </summary>
        public void ResetTrigger()
        {
            Consumed = false;

            // 先还原被改动的物体（要在自己重新激活之前做，
            // 因为 m_Changed 是本组件的数据，不依赖自己的激活状态）
            //
            // ★ 必须通过 Hide.hiding 还原，**不能直接 go.SetActive()**。
            //
            //   物体的启用状态只应该由 hiding 决定；这里如果绕过它去 SetActive，
            //   后面 WorldSnapshot.RestoreStart() 就算把 hiding 改对了，
            //   这个错误的 activeSelf 也会一直留着 ——
            //   表现就是"hiding 恢复了，但物体没有重新启用"。
            for (int i = 0; i < m_Changed.Count; i++)
            {
                GameObject go = m_Changed[i];
                if (go == null) continue;

                bool original = i < m_OriginalActive.Count && m_OriginalActive[i];
                int  wantHiding = original ? 0 : 1;

                Hide h = go.GetComponent<Hide>();

                if (h != null)
                {
                    h.SetHiding(wantHiding);
                }
                else
                {
                    // 没挂 Hide 的物体（理论上不该走到这里）退化为直接 SetActive
                    go.SetActive(original);
                }
            }

            m_Changed.Clear();
            m_OriginalActive.Clear();

            // 触发时把自己"删除"了（hiding = 1），这里恢复成 0
            if (destroySelfAfterTrigger)
            {
                Hide self = GetComponent<Hide>();

                if (self != null) self.SetHiding(0);
                else if (!gameObject.activeSelf) gameObject.SetActive(true);
            }

            // ★ 重新应用"开局隐藏"。
            //
            //   ★★ 这里**必须直接调 HideAllMarkedObjects()**，不能经过
            //      HideSpawnTargets()；后者有静态版本号去重（m_HiddenAtWave），
            //      ZoneSystem.ResetAllTriggers() 会依次调用每个触发器的 ResetTrigger()，
            //      第一个调用者就把这一轮的"已隐藏"标记消耗掉了，
            //      于是后续被其它 ResetTrigger() **刚还原成启用**的物体
            //      再也不会被隐藏 —— 表现为"开局该删的怪物重开后还留在场上"。
            //
            //      HideAllMarkedObjects 本身是幂等的（已经隐藏的跳过），
            //      重复调用没有副作用。
            m_HiddenAtWave = -1;
            HideAllMarkedObjects();
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            // 参数可能在检查器里被改过，包围盒缓存要跟着刷新
            RecacheBounds();

            if (effect != TriggerEffect.OperateObjects) return;


            WarnIfNameMissing(spawnObjects, "启用");
            WarnIfNameMissing(removeObjects, "删除");

            // 把自己填进去会连自己一起处理掉
            if (ContainsSelf(spawnObjects) || ContainsSelf(removeObjects))
            {
                Debug.LogWarning($"[EchoPuzzle] 「{name}」的名单里包含它自己的名字。" +
                                 "触发后会把自己也一起启用/删除，通常不是你想要的。", this);
            }
        }

        private void WarnIfNameMissing(List<NameOperation> list, string label)
        {
            for (int i = 0; i < list.Count; i++)
            {
                NameOperation op = list[i];
                if (op == null || string.IsNullOrEmpty(op.objectName)) continue;

                if (FindAllByName(op.objectName).Length == 0)
                {
                    Debug.LogWarning($"[EchoPuzzle] 「{name}」的{label}名单里有 " +
                                     $"「{op.objectName}」，但当前场景里找不到这个物体。", this);
                }
            }
        }

        private bool ContainsSelf(List<NameOperation> list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                NameOperation op = list[i];
                if (op != null && op.objectName == name) return true;
            }
            return false;
        }

        private void OnDrawGizmos()
        {
            // ★ 现场计算，**不用**缓存。
            //   在 Scene 视图里拖动 / 缩放物体时 OnValidate 不会触发，
            //   用缓存的话这个框会停在旧位置不动。
            //   放在 Gizmo 里现算，运行时完全不受影响。
            Bounds b = ComputeBoundsLive();

            Gizmos.color = new Color(0.4f, 0.8f, 1f, 0.25f);
            Gizmos.DrawCube(b.center, b.size);
            Gizmos.color = new Color(0.4f, 0.8f, 1f, 0.9f);
            Gizmos.DrawWireCube(b.center, b.size);
        }
#endif
    }
}
