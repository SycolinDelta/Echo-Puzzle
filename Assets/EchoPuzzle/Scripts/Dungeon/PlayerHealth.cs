using UnityEngine;

namespace EchoPuzzle.Dungeon
{
    /// <summary>
    /// 【地牢模式】玩家的生命值。
    ///
    /// ── 和探秘模式的关系 ────────────────────────────────────
    ///   ★ 这是一个**完全独立**的组件，只在 DungeonMode 场景里使用。
    ///     探秘模式（PuzzleMode）不挂它，也不受影响。
    ///
    /// ── 规则 ────────────────────────────────────────────────
    ///   · 默认生命值 8、护盾 5（战前准备界面可以再调整上限）
    ///   · 子弹 / 任何伤害调 <see cref="TakeDamage"/>
    ///   · 降到 0 时：
    ///       - 在玩家脚下释放一次**探秘模式同样的死亡光带**
    ///         （SonarSystem.DefaultTrap 那一套）
    ///       - 播放**探秘模式同样的死亡音效**（AudioSystem.Cue.Death）
    ///       - 调用 GameUI.NotifyPlayerDied() 进入死亡界面
    ///
    /// ── 适用范围 ────────────────────────────────────────────
    ///   ★ 只处理"生命值 / 受伤 / 死亡表现"。
    ///     枪械、子弹、怪物 AI 由别的脚本负责（见下方 TODO）。
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Echo Puzzle/Dungeon/Player Health")]
    public sealed class PlayerHealth : MonoBehaviour
    {
        // ------------------------------------------------------------------ 配置

        [Header("生效范围")]
        [Tooltip("只在**地牢模式（DungeonMode）**里生效。\n\n" +
                 "★ 这个脚本可以安全地挂在 Player.prefab 上 ——\n" +
                 "  在探秘模式（PuzzleMode）里它会自动变成空壳：\n" +
                 "    · 不响应任何伤害\n" +
                 "    · 不触发死亡\n" +
                 "    · 不影响探秘模式的任何玩法")]
        public bool dungeonModeOnly = true;

        [Header("生命值")]
        [Tooltip("最大生命值")]
        [Min(1)] public int maxHealth = 8;

        [Tooltip("开局生命值。留 -1 表示用 Max Health")]
        public int startingHealth = -1;

        [Tooltip("受伤后多久内免疫再次伤害（秒）。0 = 不免疫")]
        [Min(0f)] public float invincibleDuration = 0.5f;

        [Header("护盾")]
        [Tooltip("护盾上限（需求：四点）。\n\n" +
                 "★ 护盾挡在血量前面：受伤先扣护盾，护盾扣完才扣血。")]
        [Min(0)] public int maxShield = 5;

        [Tooltip("开局护盾。留 -1 表示用 Max Shield")]
        public int startingShield = -1;

        [Tooltip("受伤后多久没再受伤才开始回盾（秒）。需求：4 秒")]
        [Min(0f)] public float shieldRegenDelay = 4f;

        [Tooltip("回盾速度（每秒回几个）。需求：1 点 / 秒")]
        [Min(0.01f)] public float shieldRegenPerSecond = 1f;

        [Header("死亡表现")]
        [Tooltip("死亡时在脚下释放光带。取消勾选则只播音效 + 进死亡界面。")]
        public bool emitDeathSonar = true;

        [Tooltip("死亡光带的参数。\n\n" +
                 "默认用 SonarSystem.DefaultTrap —— 和探秘模式里\n" +
                 "「踩陷阱 / 被怪物击杀」是**同一套**视觉。")]
        public SonarEmitSettings deathSonar = SonarEmitSettings.DefaultTrap;

        // ------------------------------------------------------------------ 运行时

        /// <summary>当前生命值。</summary>
        public int Current { get; private set; }

        /// <summary>当前护盾值（0..maxShield）。</summary>
        public int Shield { get; private set; }

        /// <summary>护盾上限（至少 0）。</summary>
        public int MaxShield => Mathf.Max(0, maxShield);

        /// <summary>护盾变化时触发（当前护盾、最大护盾、距离回盾还剩几秒）。给 HUD 用。</summary>
        public event System.Action<int, int> ShieldChanged;

        /// <summary>已经死了吗。</summary>
        public bool IsDead => Current <= 0;

        /// <summary>
        /// 这个组件当前**是否生效**。
        ///
        /// ★ 挂在 Player.prefab 上的同一个实例，在探秘模式里返回 false，
        ///   地牢模式里返回 true。所有公开接口都先过这一关。
        /// </summary>
        public bool IsEnabledHere => !dungeonModeOnly || GameUI.IsDungeonMode;

        /// <summary>生命值变化时触发（当前值、最大值）。给 HUD 用。</summary>
        public event System.Action<int, int> HealthChanged;

        private PlayerController m_Player;
        private float           m_InvincibleUntil;
        private bool            m_DeathHandled;

        /// <summary>回盾用的累加器：攒够 1 点才真正 +1（这样 1/秒 的节奏才匀）。</summary>
        private float m_ShieldRegenAccum;

        /// <summary>下一次可以开始回盾的时刻（受伤后 4 秒）。</summary>
        private float m_ShieldRegenFrom;

        private float Now => Time.timeSinceLevelLoad;

        // ------------------------------------------------------------------ 生命周期

        private void Awake()
        {
            m_Player = GetComponent<PlayerController>();
            if (m_Player == null) m_Player = GetComponentInParent<PlayerController>();
            if (m_Player == null) m_Player = FindObjectOfType<PlayerController>();

            Current = startingHealth >= 0
                    ? Mathf.Clamp(startingHealth, 0, maxHealth)
                    : maxHealth;

            Shield = startingShield >= 0
                    ? Mathf.Clamp(startingShield, 0, MaxShield)
                    : MaxShield;

            HealthChanged?.Invoke(Current, maxHealth);
            ShieldChanged?.Invoke(Shield, MaxShield);
        }

        private void Start()
        {
            // ★ 探秘模式下这个组件是空壳，不用推事件
            if (!IsEnabledHere) return;

            // Awake 时 HUD 可能还没建好，这里再推一次
            HealthChanged?.Invoke(Current, maxHealth);
            ShieldChanged?.Invoke(Shield, MaxShield);
        }

        /// <summary>
        /// 护盾自动恢复。
        ///
        /// ★ 需求："受伤后，如果玩家四秒内没有再次受到伤害，
        ///         则护盾会以一秒一点的速度恢复。"
        ///
        ///   实现：受伤时把 m_ShieldRegenFrom 设成 now + 4s，
        ///        过了这个时刻才用累加器按 1 点/秒 往上补。
        /// </summary>
        private void Update()
        {
            if (!IsEnabledHere) return;

            // 死人、满盾、还没到回盾时刻 —— 都不用算
            if (IsDeadOrDone()) return;
            if (Shield >= MaxShield)
            {
                m_ShieldRegenAccum = 0f;
                return;
            }

            // 地牢模式只在真正进行中回盾（菜单 / 暂停 / 结算不回）
            if (GameUI.IsDungeonMode && GameUI.Current != GameState.Playing) return;

            float now = Time.timeSinceLevelLoad;

            // 受伤后 4 秒内不回
            if (now < m_ShieldRegenFrom) return;

            m_ShieldRegenAccum += Mathf.Max(0.01f, shieldRegenPerSecond) * Time.deltaTime;

            if (m_ShieldRegenAccum >= 1f)
            {
                int whole = Mathf.FloorToInt(m_ShieldRegenAccum);
                m_ShieldRegenAccum -= whole;

                AddShield(whole);
            }
        }

        // ------------------------------------------------------------------ 对外接口

        /// <summary>
        /// 扣血。
        /// </summary>
        /// <param name="amount">扣多少（正数）。</param>
        /// <returns>这次伤害有没有真的扣到血（无敌期间 / 已死会返回 false）。</returns>
        public bool TakeDamage(int amount)
        {
            // ★ 探秘模式下这个组件不生效
            if (!IsEnabledHere) return false;

            if (IsDeadOrDone()) return false;
            if (amount <= 0) return false;

            // 无敌期间不掉血
            if (Time.timeSinceLevelLoad < m_InvincibleUntil) return false;

            m_InvincibleUntil = Time.timeSinceLevelLoad + Mathf.Max(0f, invincibleDuration);

            // ★★ 受伤 -> 重置回盾倒计时（4 秒）★★
            m_ShieldRegenFrom  = Time.timeSinceLevelLoad + Mathf.Max(0f, shieldRegenDelay);
            m_ShieldRegenAccum = 0f;

            // ★★ 先扣护盾，护盾扣完才扣血 ★★
            int remain = amount;

            if (Shield > 0)
            {
                int absorb = Mathf.Min(Shield, remain);
                Shield  -= absorb;
                remain  -= absorb;
                ShieldChanged?.Invoke(Shield, MaxShield);
            }

            if (remain <= 0) return true;      // 全被护盾挡下了，血量不变

            Current = Mathf.Max(0, Current - remain);

            HealthChanged?.Invoke(Current, maxHealth);

            if (Current <= 0) Die();

            return true;
        }

        /// <summary>
        /// 让玩家无敌一段时间（**不产生**默认的受击无敌，也不扣血）。
        ///
        /// ★ 给「闪避」用：闪避的 0.5 秒无敌帧直接走这套，
        ///   避免和 invincibleDuration（受击后的常规无敌）互相覆盖。
        /// </summary>
        public void GrantInvincibility(float seconds)
        {
            if (!IsEnabledHere) return;
            if (IsDeadOrDone()) return;
            if (seconds <= 0f) return;

            float until = Time.timeSinceLevelLoad + seconds;

            // 只延长、不缩短
            if (until > m_InvincibleUntil) m_InvincibleUntil = until;
        }

        /// <summary>当前是否处于无敌（给 HUD / 闪避视觉判断用）。</summary>
        public bool IsInvincible => Time.timeSinceLevelLoad < m_InvincibleUntil;

        /// <summary>回血（不会超过最大值）。</summary>
        public void Heal(int amount)
        {
            // ★ 探秘模式下这个组件不生效
            if (!IsEnabledHere) return;

            if (IsDeadOrDone() || amount <= 0) return;

            Current = Mathf.Min(maxHealth, Current + amount);

            HealthChanged?.Invoke(Current, maxHealth);
        }

        /// <summary>
        /// 加护盾（不会超过上限）。
        ///
        /// ★ 给自动回盾用：攒够 1 点就调一次。
        /// </summary>
        public void AddShield(int amount)
        {
            if (!IsEnabledHere) return;
            if (amount <= 0) return;
            if (Shield >= MaxShield) return;

            SetShield(Shield + amount);
        }

        /// <summary>直接设成某个值（读档 / 重开时用）。</summary>
        public void SetShield(int value)
        {
            int v = Mathf.Clamp(value, 0, MaxShield);

            if (v == Shield) return;

            Shield = v;
            ShieldChanged?.Invoke(Shield, MaxShield);
        }

        /// <summary>直接设成某个值（读档 / 重开时用）。</summary>
        public void SetHealth(int value)
        {
            Current = Mathf.Clamp(value, 0, maxHealth);

            HealthChanged?.Invoke(Current, maxHealth);
        }

        /// <summary>满血满盾并清掉死亡标记（重开时用）。</summary>
        public void ResetHealth()
        {
            m_DeathHandled     = false;
            m_InvincibleUntil  = 0f;
            m_ShieldRegenFrom  = 0f;
            m_ShieldRegenAccum = 0f;

            Current = startingHealth >= 0
                    ? Mathf.Clamp(startingHealth, 0, maxHealth)
                    : maxHealth;

            Shield = startingShield >= 0
                    ? Mathf.Clamp(startingShield, 0, MaxShield)
                    : MaxShield;

            HealthChanged?.Invoke(Current, maxHealth);
            ShieldChanged?.Invoke(Shield, MaxShield);
        }

        /// <summary>已经死过（死亡表现已触发）或者当前血量为 0。</summary>
        private bool IsDeadOrDone() => m_DeathHandled || Current <= 0;

        // ------------------------------------------------------------------ 死亡

        /// <summary>
        /// 死亡：光带 + 音效 + 死亡界面。
        ///
        /// ★ 表现和探秘模式**完全一致** ——
        ///   用的就是 TriggerVolume 里 Lose 效果那一套：
        ///     · 光带：SonarSystem.DefaultTrap（红色细光柱）
        ///     · 音效：AudioSystem.Cue.Death
        ///     · 界面：GameUI.NotifyPlayerDied()
        /// </summary>
        private void Die()
        {
            // ★ 保险：探秘模式下永远不会走到这里
            if (!IsEnabledHere) return;

            if (m_DeathHandled) return;
            m_DeathHandled = true;


            // ---- 1) 光带 ----
            if (emitDeathSonar)
            {
                EmitDeathSonar();
            }

            // ---- 2) 音效 ----
            AudioSystem.Play(AudioSystem.Cue.Death);

            // ---- 3) 死亡界面 ----
            GameUI.NotifyPlayerDied();
        }

        /// <summary>在玩家脚下放一次死亡光带（和探秘模式踩陷阱一样）。</summary>
        private void EmitDeathSonar()
        {
            SonarSystem sonar = FindObjectOfType<SonarSystem>();
            if (sonar == null) return;

            if (m_Player == null) return;

            // 方位角起点：玩家正前方的水平分量（和常规声波一致）
            Vector3 facing = m_Player.transform.forward;
            facing.y = 0f;
            if (facing.sqrMagnitude < 1e-6f) facing = Vector3.forward;
            facing.Normalize();

            sonar.Emit(m_Player.FootPosition, deathSonar.ToEmitParams(facing));
        }

        // ------------------------------------------------------------------ TODO（交给别人）

        // ★★ 以下部分**不属于**这个脚本，是地牢模式需要新写的： ★★
        //
        //   1) 枪械系统
        //      · 取代 DaggerSystem（匕首）/ StoneThrowSystem（投石）
        //      · 按住左键射击、子弹飞行、命中判定
        //      · 建议新建：Scripts/Dungeon/DungeonGun.cs
        //
        //   2) 子弹
        //      · 撞墙 / 超时 / 命中玩家后消失
        //      · 命中玩家时调用 PlayerHealth.TakeDamage(1)
        //      · 建议新建：Scripts/Dungeon/DungeonBullet.cs
        //
        //   3) 怪物 AI（地牢版）
        //      · 不再近身追击、不再秒杀
        //      · 在地图上随机移动 + 朝玩家/随机方向开火
        //      · 建议新建：Scripts/Dungeon/DungeonEnemy.cs
        //      · ★ 不要复用 PuzzleMonster.cs（那是探秘模式的）
        //
        //   4) 生命值 HUD
        //      · 左上角去掉电池（BatterySystem）和石头（StoneSystem）
        //      · 换成生命值显示
        //      · 建议新建：Scripts/Dungeon/DungeonHUD.cs
        //      · ★ 需要在下面这几处让旧 HUD 在地牢模式不画：
        //          BatterySystem.OnGUI()
        //          StoneSystem.OnGUI()
    }
}
