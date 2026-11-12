using System.Collections.Generic;
using UnityEngine;

namespace EchoPuzzle
{
    /// <summary>
    /// 全局音效播放器。
    ///
    /// 音效资源放在 `Assets/Resources/AudioPack/&lt;分类&gt;/` 下，
    /// 靠 <see cref="Resources.LoadAll"/> 一次性加载，所以**不需要在 Inspector 上挂任何东西**，
    /// 也不会因为"某个 clip 没被资产引用到"而在打包后丢失
    /// （Resources 目录里的资源一定会进包）。
    ///
    /// 分类与触发时机：
    ///   Click       —— 点击任意 UI 按钮
    ///   Walk        —— 走路时的一步（潜行不响）
    ///   Run         —— 跑步时的一步
    ///   Stomp       —— 按空格发喊声
    ///   Trigger     —— TriggerVolume 的 OperateObjects 被触发
    ///   Destination —— TriggerVolume 的 Win 被触发
    ///   Death       —— TriggerVolume 的 Lose 被触发
    ///
    /// 一个分类里有多个音效时**随机**挑一个播。
    /// </summary>
    [DefaultExecutionOrder(-600)]
    public sealed class AudioSystem : MonoBehaviour
    {
        /// <summary>Resources 下的音效根目录。</summary>
        public const string ResourceRoot = "AudioPack";

        public static AudioSystem Instance { get; private set; }

        // ---------------------------------------------------------------- 分类

        public enum Cue
        {
            Click,
            Walk,
            Run,
            Stomp,
            Trigger,
            Destination,

            /// <summary>玩家死亡（Dead.ogg）。</summary>
            Death,

            /// <summary>
            /// 怪物死亡（MonsterDead.ogg）。
            ///
            /// ★ 单独一个分类，和玩家的 Death 完全分开 ——
            ///   否则 Resources.LoadAll 会把两个文件夹里的音频混在一起，
            ///   玩家死亡时可能随机播到 MonsterDead。
            /// </summary>
            MonsterDead,
        }

        // ---------------------------------------------------------------- Inspector

        [Header("音量（各分类独立）")]
        [Range(0f, 1f)] public float clickVolume       = 0.7f;
        [Range(0f, 1f)] public float walkVolume        = 0.5f;
        [Range(0f, 1f)] public float runVolume         = 0.7f;
        [Range(0f, 1f)] public float stompVolume       = 0.8f;
        [Range(0f, 1f)] public float triggerVolume     = 0.8f;
        [Range(0f, 1f)] public float destinationVolume = 0.9f;
        [Range(0f, 1f)] public float deathVolume        = 0.9f;
        [Range(0f, 1f)] public float monsterDeadVolume  = 0.9f;

        [Header("音高随机范围（让重复的音效不至于太机械）")]
        [Range(0f, 0.5f)] public float pitchJitter = 0.06f;

        [Header("同时最多几个声音")]
        [Tooltip("走路 / 跑步很容易一秒好几个，太多会糊在一起")]
        public int maxVoices = 16;

        // ---------------------------------------------------------------- 内部

        private sealed class Voice
        {
            public AudioSource source;
            public float       freeAt;      // 这个音源什么时候可以再被占用
        }

        private readonly List<Voice> m_Voices = new List<Voice>(16);

        /// <summary>每个分类加载到的所有 clip。</summary>
        private readonly Dictionary<Cue, AudioClip[]> m_Clips = new Dictionary<Cue, AudioClip[]>();

        /// <summary>每个分类上一次播的是哪条 —— 用来避免连着播同一条。</summary>
        private readonly Dictionary<Cue, int> m_LastIndex = new Dictionary<Cue, int>();

        private bool m_Ready;

        /// <summary>是否已经因为"没有 AudioListener"警告过一次（避免刷屏）。</summary>
        private bool m_WarnedNoListener;

        /// <summary>缓存的 AudioListener，避免每次播放都全场景查找。</summary>
        private AudioListener m_Listener;

        // ---------------------------------------------------------------- 生命周期

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;

            LoadAll();
            BuildVoices();

            m_Ready = true;
        }

        private void Start()
        {
            // 场景里没监听器就等于全程静音，而且 Unity 会给**每个**播放的
            // AudioSource 都刷一条 "There are no audio listeners" 警告 —— 非常吵。
            // 这里自己检查一次，报一条清楚的，之后不再提。
            EnsureListener();
        }

        /// <summary>没有 AudioListener 就补一个，并只在第一次给出明确提示。</summary>
        private void EnsureListener()
        {
            if (m_Listener != null) return;

            m_Listener = Object.FindObjectOfType<AudioListener>();
            if (m_Listener != null) return;

            // 优先挂到主相机上；没有相机就挂到 AudioSystem 自己身上
            Camera cam = Camera.main;
            GameObject host = cam != null ? cam.gameObject : gameObject;

            m_Listener = host.AddComponent<AudioListener>();

            if (!m_WarnedNoListener)
            {
                m_WarnedNoListener = true;
                Debug.LogWarning(
                    "[EchoPuzzle] 场景里原本没有 AudioListener，已自动补到 " +
                    $"《{host.name}》上。\n" +
                    "  如果之后还听到『There are no audio listeners』，说明这个物体被销毁或停用了。");
            }
        }

        /// <summary>
        /// 全局收尾：关闭 Enguine 前把音源全部停掉。
        ///
        /// ★ 这一条是为了修"关闭 Unity 时卡在 Application.Shutdown.CleanupEngine"。
        ///
        ///   我们池化了 16 个 AudioSource，播放中的音源在引擎销毁阶段
        ///   仍会参与音频线程的清理。如果此时还有 clip 在播（比如停在
        ///   播报音效的瞬间就关掉工程），音频线程可能迟迟不退出，
        ///   主线程就卡在 CleanupEngine 等待，界面上显示
        ///   "Waiting for Unity's code to finish executing"。
        ///
        ///   显式 Stop() + 释放 clip 引用，让音频子系统能干净退出。
        /// </summary>
        private void OnApplicationQuit()
        {
            ShutdownAudio();
        }

        private void OnDestroy()
        {
            ShutdownAudio();
            if (Instance == this) Instance = null;
        }

        private void ShutdownAudio()
        {
            if (m_Voices == null) return;

            for (int i = 0; i < m_Voices.Count; i++)
            {
                Voice v = m_Voices[i];
                AudioSource src = v.source;
                if (src == null) continue;

                src.Stop();
                src.clip = null;       // 断开对 AudioClip 的引用
                src.enabled = false;
            }
        }

        /// <summary>把每个分类的 clip 从 Resources 里读出来。</summary>
        private void LoadAll()
        {
            foreach (Cue cue in System.Enum.GetValues(typeof(Cue)))
            {
                string path = $"{ResourceRoot}/{cue}";
                AudioClip[] clips = Resources.LoadAll<AudioClip>(path);

                m_Clips[cue] = clips;
                m_LastIndex[cue] = -1;

                if (clips == null || clips.Length == 0)
                {
                    Debug.LogWarning(
                        $"[EchoPuzzle] 音效分类「{cue}」没有加载到任何文件。\n" +
                        $"  期望位置：Assets/Resources/{path}/ 下的 .ogg / .wav 等。\n" +
                        $"  注意音频文件必须在 Resources 目录里才能被 Resources.LoadAll 找到。");
                }
            }
        }

        /// <summary>建一批 AudioSource 轮流用（比每次 new 一个便宜）。</summary>
        private void BuildVoices()
        {
            int n = Mathf.Max(1, maxVoices);

            for (int i = 0; i < n; i++)
            {
                var go = new GameObject($"Voice{i:00}");
                go.transform.SetParent(transform, false);

                var src = go.AddComponent<AudioSource>();
                src.playOnAwake    = false;
                src.spatialBlend   = 0f;      // 纯 2D，不受位置影响
                src.loop           = false;
                src.dopplerLevel   = 0f;
                src.rolloffMode    = AudioRolloffMode.Linear;
                src.bypassReverbZones = true;

                m_Voices.Add(new Voice { source = src, freeAt = 0f });
            }
        }

        // ---------------------------------------------------------------- 播放

        /// <summary>播放一个分类的音效（分类里有多个时随机挑一条）。</summary>
        public static void Play(Cue cue)
        {
            if (Instance != null) Instance.PlayInternal(cue);
        }

        /// <summary>
        /// 直接播放一个指定的 AudioClip（不经过 Cue 分类）。
        ///
        /// 给"自定义触发音效"这类需要从 Inspector 拖入资源的场景用。
        /// 复用同一套声部池和音高抖动。
        /// </summary>
        /// <param name="clip">要播的音频。</param>
        /// <param name="volume">音量 0..1。传负数则用 1。</param>
        public static void PlayClip(AudioClip clip, float volume = -1f)
        {
            if (Instance != null) Instance.PlayClipInternal(clip, volume);
        }

        private void PlayClipInternal(AudioClip clip, float volume)
        {
            if (!m_Ready || clip == null) return;

            if (m_Listener == null) EnsureListener();

            AudioSource src = TakeVoice(clip.length);
            if (src == null) return;

            src.clip        = clip;
            src.volume      = volume < 0f ? 1f : Mathf.Clamp01(volume);
            src.pitch       = 1f + Random.Range(-pitchJitter, pitchJitter);
            src.timeSamples = 0;
            src.Play();
        }

        private void PlayInternal(Cue cue)
        {
            if (!m_Ready) return;

            // 监听器可能在运行中丢失（相机被销毁、物体被停用……）。
            // 但这里**不能**每次播放都 FindObjectOfType —— 走路一秒好几个音，
            // 那样等于每帧全场景扫描。改成只在缓存失效时才重新找。
            if (m_Listener == null) EnsureListener();

            if (!m_Clips.TryGetValue(cue, out AudioClip[] clips) ||
                clips == null || clips.Length == 0)
                return;

            AudioClip clip = PickClip(cue, clips);
            if (clip == null) return;

            AudioSource src = TakeVoice(clip.length);
            if (src == null) return;      // 声部全忙，这一声就丢掉

            src.clip         = clip;
            src.volume       = VolumeOf(cue);
            src.pitch        = 1f + Random.Range(-pitchJitter, pitchJitter);
            src.timeSamples  = 0;
            src.Play();
        }

        /// <summary>
        /// 随机挑一条。有多条时**避开上一次播过的那条**，
        /// 否则连着两次听到同一个音的概率会明显偏高。
        /// </summary>
        private AudioClip PickClip(Cue cue, AudioClip[] clips)
        {
            if (clips.Length == 1) return clips[0];

            int last = m_LastIndex.TryGetValue(cue, out int l) ? l : -1;

            int idx;
            if (last < 0 || last >= clips.Length)
            {
                idx = Random.Range(0, clips.Length);
            }
            else
            {
                // 在"除 last 之外"的范围里取，再把下标映射回去
                idx = Random.Range(0, clips.Length - 1);
                if (idx >= last) idx++;
            }

            m_LastIndex[cue] = idx;
            return clips[idx];
        }

        /// <summary>拿一个空闲的 AudioSource；全忙就抢占最早空出来的那个。</summary>
        private AudioSource TakeVoice(float duration)
        {
            float now = Time.unscaledTime;

            // 优先找真正空闲的
            for (int i = 0; i < m_Voices.Count; i++)
            {
                Voice v = m_Voices[i];
                if (!v.source.isPlaying && v.freeAt <= now)
                {
                    v.freeAt = now + duration;
                    return v.source;
                }
            }

            // 都忙 -> 抢最早能空出来的那个
            Voice best = null;
            for (int i = 0; i < m_Voices.Count; i++)
            {
                Voice v = m_Voices[i];
                if (best == null || v.freeAt < best.freeAt) best = v;
            }

            if (best == null) return null;

            best.source.Stop();
            best.freeAt = now + duration;
            return best.source;
        }

        private float VolumeOf(Cue cue)
        {
            switch (cue)
            {
                case Cue.Click:       return clickVolume;
                case Cue.Walk:        return walkVolume;
                case Cue.Run:         return runVolume;
                case Cue.Stomp:       return stompVolume;
                case Cue.Trigger:     return triggerVolume;
                case Cue.Destination: return destinationVolume;
                case Cue.Death:       return deathVolume;
                case Cue.MonsterDead: return monsterDeadVolume;
                default:              return 1f;
            }
        }
    }
}
