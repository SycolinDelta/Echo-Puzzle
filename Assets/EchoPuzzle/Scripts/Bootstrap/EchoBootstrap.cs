using UnityEngine;

namespace EchoPuzzle
{
    /// <summary>
    /// M0 + M1 自举脚本：运行时搭建教学关、玩家、相机和后处理。
    ///
    /// 用法：新建空场景 -> 建一个空物体 -> 挂上这个组件 -> 按 Play。
    /// 不需要手工摆放任何场景物体、材质或预制体。
    ///
    /// 关卡（走廊 -> 圆形房间 -> 走廊）在 LevelBuilder 里用常量手写，不是随机生成。
    /// </summary>
    [DefaultExecutionOrder(-500)]
    public sealed class EchoBootstrap : MonoBehaviour
    {
        [Header("墙体")]
        [Tooltip("勾上 = 墙体/地板/天花板显示为不透明灰色并打光（**测试路径时用这个**）；\n" +
                 "取消勾选 = 全部完全透明，也就是正式玩法效果。\n" +
                 "运行中可以在组件上右键『切换墙体可见』实时切换。")]
        public bool wallsVisibleForTesting = true;

        [Tooltip("只在 wallsVisibleForTesting 为 true 时创建的方向光")]
        public bool debugLight = true;

        [Header("渲染")]
        public float bloomIntensity = 1.1f;
        public float bloomThreshold = 0.85f;

        [Header("测试用：出生即发声")]
        public bool echoOnStart = false;

        public PlayerController Player { get; private set; }

        private Material m_WallMat;
        private Material m_OutlineMat;
        private bool     m_HasDebugLight;

        private void Awake()
        {
            // 关掉垂直同步：帧耗时偶尔超过 16.7ms 时不再丢帧，彻底消除 exe 里的"时间轴抖动"
            QualitySettings.vSyncCount = 0;
            // 锁目标帧率：避免帧率狂飙导致的画面撕裂和过热
            Application.targetFrameRate = 60;

            // 如果场景里已经用菜单生成过关卡，就别再自建一份了
            // （否则会出现两个玩家、两套墙、两层屏幕文字）
            if (GameObject.Find("EchoPuzzle_Level") != null)
            {
                enabled = false;
                return;
            }

            SetupEnvironment();

            // 墙体 / 地板 / 天花板：
            //   测试路径阶段**全部不透明**（灰色 + 弱光照），方便核对尺寸和走位。
            //   路径确认无误后，把 wallsVisibleForTesting 关掉，整关立刻变成完全透明。
            m_WallMat    = wallsVisibleForTesting
                         ? EchoMaterials.CreateWall(true)
                         : EchoMaterials.CreateInvisible();
            m_OutlineMat = EchoMaterials.CreateOutline();

            BuildLight();

            // ---------------- 关卡 ----------------
            var levelRoot = new GameObject("Level");
            levelRoot.transform.SetParent(transform, false);

            // 地板/天花板传 null -> 跟随墙体材质，所以测试期它们也是不透明的
            Vector3 spawn = LevelBuilder.Build(levelRoot.transform, m_WallMat, null,
                                               out Vector3 goalPosition, out Transform gate);

            LevelBuilder.AddGoal(levelRoot.transform, goalPosition);

            // 整关的碰撞体都是在这一帧里新建并摆放的，PhysX 那边的变换还是旧的。
            // 不同步的话会出现"看不见的墙"：角色/射线撞到的是物体移动前的旧位置。
            Physics.SyncTransforms();

            // ---------------- 玩家 / 相机 / 声波 ----------------
            Camera      cam   = SpawnCamera();
            SonarSystem sonar = SpawnSonar();

            Player = SpawnPlayer(spawn);
            Player.playerCamera = cam;
            Player.echo         = sonar;

            // ---------------- UI / 标记 ----------------
            // 必须最后创建：GameUI 在 Awake 里会缓存玩家的出生位置用于"重玩"
            SpawnMarkerSystem();
            SpawnZoneTrigger();
            SpawnGameUI();

            if (echoOnStart)
                sonar.Emit(Player.FootPosition, EmitParams.Default);
        }

        /// <summary>特殊空间的进出检测：踩机关、踩陷阱。</summary>
        private void SpawnZoneTrigger()
        {
            var go = new GameObject("ZoneTrigger");
            go.transform.SetParent(transform, false);
            var zt = go.AddComponent<ZoneTrigger>();
            zt.player = Player;
            m_Spawned.Add(go);
        }

        private void SpawnGameUI()
        {
            var go = new GameObject("GameUI");
            go.transform.SetParent(transform, false);
            go.AddComponent<GameUI>();
            m_Spawned.Add(go);
        }

        /// <summary>放置标记（按 E 在脚下放白色光点，最多 3 个）。</summary>
        private void SpawnMarkerSystem()
        {
            var go = new GameObject("MarkerSystem");
            go.transform.SetParent(transform, false);
            var ms = go.AddComponent<MarkerSystem>();
            ms.player = Player;
            m_Spawned.Add(go);
        }

        /// <summary>运行时切换"墙体可见"，不需要重开 Play。</summary>
        [ContextMenu("切换墙体可见")]
        public void ToggleWallsVisible()
        {
            wallsVisibleForTesting = !wallsVisibleForTesting;
            ApplyWallVisibility();
        }

        public void ApplyWallVisibility()
        {
            if (m_WallMat == null) return;

            if (m_WallMat.HasProperty("_Color"))
                m_WallMat.SetColor("_Color", wallsVisibleForTesting ? EchoMaterials.WallDebug : EchoMaterials.WallFinal);
            if (m_WallMat.HasProperty("_DebugLit"))
                m_WallMat.SetFloat("_DebugLit", wallsVisibleForTesting ? 1f : 0f);

            SetLightActive(wallsVisibleForTesting && debugLight);
            ApplyAmbient();
        }

        // ---------------------------------------------------------------- 渲染环境

        private void SetupEnvironment()
        {
            UnityEngine.RenderSettings.fog    = false;
            UnityEngine.RenderSettings.skybox = null;

            ApplyAmbient();
        }

        /// <summary>
        /// 正式模式环境光全黑——场景里除了自发光什么都没有。
        /// 调试模式给一点很弱的环境光，方便看清灰色几何。
        /// </summary>
        private void ApplyAmbient()
        {
            bool debug = wallsVisibleForTesting;

            UnityEngine.RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            UnityEngine.RenderSettings.ambientIntensity = 1f;

            UnityEngine.RenderSettings.ambientSkyColor = debug
                ? new Color(0.17f, 0.18f, 0.21f) : Color.black;
            UnityEngine.RenderSettings.ambientEquatorColor = debug
                ? new Color(0.10f, 0.10f, 0.12f) : Color.black;
            UnityEngine.RenderSettings.ambientGroundColor = debug
                ? new Color(0.03f, 0.03f, 0.04f) : Color.black;

            // 同时压掉 ambientLight 通道，确保正式模式一点光都不漏
            UnityEngine.RenderSettings.ambientLight = debug
                ? new Color(0.14f, 0.15f, 0.17f) : Color.black;
        }

        private void SetLightActive(bool active)
        {
            if (!m_HasDebugLight) return;

            Light l = m_DebugLight;
            if (l == null) return;
            l.enabled = active;
            l.intensity = active ? 0.55f : 0f;
        }

        private Light m_DebugLight;

        private void BuildLight()
        {
            var go = new GameObject("DebugLight");
            go.transform.SetParent(transform, false);
            go.transform.rotation = Quaternion.Euler(50f, -35f, 0f);

            var l = go.AddComponent<Light>();
            l.type      = LightType.Directional;
            l.color     = new Color(0.9f, 0.93f, 1f);
            l.shadows   = LightShadows.Soft;
            l.intensity = 0.55f;

            m_DebugLight   = l;
            m_HasDebugLight = true;

            SetLightActive(wallsVisibleForTesting && debugLight);
        }

        // ---------------------------------------------------------------- 对象创建

        private PlayerController SpawnPlayer(Vector3 spawn)
        {
            var go = new GameObject("Player");
            go.transform.SetParent(transform, false);
            go.transform.position = spawn;

            var cc = go.AddComponent<CharacterController>();
            cc.height          = 1.8f;
            cc.radius          = 0.34f;
            cc.center          = new Vector3(0f, 0.9f, 0f);
            cc.slopeLimit      = 55f;
            cc.stepOffset      = 0.4f;
            cc.skinWidth       = 0.03f;
            cc.minMoveDistance = 0f;

            // 身体：只显示发光轮廓
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Body";
            body.transform.SetParent(go.transform, false);
            body.transform.localPosition = new Vector3(0f, 0.9f, 0f);
            body.transform.localScale    = new Vector3(0.68f, 0.9f, 0.68f);
            Destroy(body.GetComponent<Collider>());   // 碰撞交给 CharacterController

            if (m_OutlineMat != null)
            {
                body.GetComponent<MeshRenderer>().sharedMaterial = m_OutlineMat;
            }
            else
            {
                Debug.LogWarning("[EchoPuzzle] 找不到 shader 'EchoPuzzle/Outline'，角色将不可见。");
            }

            m_Spawned.Add(go);
            return go.AddComponent<PlayerController>();
        }

        private Camera SpawnCamera()
        {
            var go = new GameObject("EchoCamera");
            go.transform.SetParent(transform, false);

            var cam = go.AddComponent<Camera>();
            cam.clearFlags      = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.fieldOfView     = 68f;
            cam.nearClipPlane   = 0.05f;
            cam.farClipPlane    = 400f;
            cam.tag             = "MainCamera";

            var data = go.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            data.renderPostProcessing = true;
            data.renderShadows        = true;
            data.antialiasing         = UnityEngine.Rendering.Universal.AntialiasingMode.FastApproximateAntialiasing;

            SetupVolume(go);

            m_Spawned.Add(go);
            return cam;
        }

        /// <summary>给相机挂一个全局 Volume：Bloom 是声波发光的关键。</summary>
        private void SetupVolume(GameObject cameraGo)
        {
            var profile = ScriptableObject.CreateInstance<UnityEngine.Rendering.VolumeProfile>();
            profile.name      = "EchoVolumeProfile";
            profile.hideFlags = HideFlags.DontSave;

            var bloom = profile.Add<UnityEngine.Rendering.Universal.Bloom>(true);
            bloom.intensity.overrideState = true;
            bloom.intensity.value         = bloomIntensity;
            bloom.threshold.overrideState = true;
            bloom.threshold.value         = bloomThreshold;
            bloom.scatter.overrideState   = true;
            bloom.scatter.value           = 0.72f;

            var vignette = profile.Add<UnityEngine.Rendering.Universal.Vignette>(true);
            vignette.intensity.overrideState  = true;
            vignette.intensity.value          = 0.30f;
            vignette.smoothness.overrideState = true;
            vignette.smoothness.value         = 0.6f;

            var color = profile.Add<UnityEngine.Rendering.Universal.ColorAdjustments>(true);
            color.postExposure.overrideState = true;
            color.postExposure.value         = 0.1f;
            color.contrast.overrideState     = true;
            color.contrast.value             = 10f;
            color.saturation.overrideState   = true;
            color.saturation.value           = -10f;

            var volumeGo = new GameObject("EchoVolume");
            volumeGo.transform.SetParent(cameraGo.transform, false);

            var volume = volumeGo.AddComponent<UnityEngine.Rendering.Volume>();
            volume.isGlobal      = true;
            volume.priority      = 10f;
            volume.sharedProfile = profile;

            m_Spawned.Add(volumeGo);
        }

        private SonarSystem SpawnSonar()
        {
            var go = new GameObject("SonarSystem");
            go.transform.SetParent(transform, false);

            var rendererGo    = new GameObject("SonarTrailRenderer");
            rendererGo.transform.SetParent(go.transform, false);
            var trailRenderer = rendererGo.AddComponent<SonarTrailRenderer>();

            var sonar = go.AddComponent<SonarSystem>();
            sonar.trailRenderer = trailRenderer;

            m_Spawned.Add(go);
            return sonar;
        }

        // ---------------------------------------------------------------- 清理

        private readonly System.Collections.Generic.List<GameObject> m_Spawned =
            new System.Collections.Generic.List<GameObject>();

        private void OnDestroy()
        {
            for (int i = 0; i < m_Spawned.Count; i++)
                if (m_Spawned[i] != null) Destroy(m_Spawned[i]);

            Destroy(m_WallMat);
            Destroy(m_OutlineMat);
        }
    }
}
