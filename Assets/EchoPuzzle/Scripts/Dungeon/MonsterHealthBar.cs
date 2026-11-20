using UnityEngine;

namespace EchoPuzzle.Dungeon
{
    /// <summary>
    /// 【地牢模式】怪物头顶的血条。
    ///
    /// ══════════════════════════════════════════════════════════════
    /// 需求
    ///   · 显示在怪物"上方"—— 不是世界坐标的 +Y，而是**相机视角的上方**
    ///     （所以用屏幕空间画：把怪物位置投影到屏幕，再往上挪一段像素）
    ///   · 离镜头越远，血条**越短**
    ///   · 是一道槽：受伤后**从右侧开始**变空心
    ///   · 没有分隔符 / 格子 / 数字
    ///
    /// ══════════════════════════════════════════════════════════════
    /// 实现
    ///   用 IMGUI 在 OnGUI 里画（和 HUD 一致，不占渲染管线的开销）。
    ///   每帧：
    ///     1) WorldToScreenPoint 拿到怪物在屏幕上的位置
    ///     2) 按"相机距离"算出血条长度（越远越短）
    ///     3) 在怪物屏幕位置的**上方**画一条槽 + 红色的血量部分
    ///
    ///   ★ 只在地牢模式、并且怪物还活着时显示。
    /// ══════════════════════════════════════════════════════════════
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Echo Puzzle/Dungeon/Monster Health Bar")]
    public sealed class MonsterHealthBar : MonoBehaviour
    {
        // ---------------------------------------------------------------- 外观

        [Header("尺寸")]
        [Tooltip("离相机最近时的血条长度（屏幕像素，按 1080 高换算）")]
        [Min(4f)] public float fullLengthPixels = 255f;

        [Tooltip("离相机最远时的血条长度（屏幕像素）。不会小于这个值")]
        [Min(2f)] public float minLengthPixels = 45f;

        [Tooltip("血条厚度（屏幕像素）")]
        [Min(1f)] public float thicknessPixels = 20f;

        [Tooltip("血条底边在怪物屏幕位置上方多少像素")]
        public float offsetPixels = 90f;

        [Header("距离缩放")]
        [Tooltip("相机距离 <= 这个值时血条最长")]
        [Min(0.1f)] public float nearDistance = 6f;

        [Tooltip("相机距离 >= 这个值时血条最短")]
        [Min(0.2f)] public float farDistance = 40f;

        [Header("颜色")]
        [Tooltip("血量部分的颜色（红色）")]
        public Color fillColor = new Color(0.95f, 0.15f, 0.18f, 1f);

        [Tooltip("空槽的背景色")]
        public Color emptyColor = new Color(0f, 0f, 0f, 0.55f);

        [Tooltip("外描边颜色")]
        public Color borderColor = new Color(1f, 1f, 1f, 0.35f);

        [Tooltip("描边宽度（像素）")]
        [Min(0f)] public float borderPixels = 1.5f;

        [Header("状态图标（右上角）")]
        [Tooltip("★ 图标颜色。需求：纯红色")] 
        public Color iconColor = new Color(1f, 0f, 0f, 1f);

        [Tooltip("状态图标离怪物屏幕位置右上角多远（像素，x 向右 / y 向上）")] 
        public Vector2 iconOffset = new Vector2(34f, 46f);

        [Header("模板标记（左上角）")]
        [Tooltip("模板标记的边长（屏幕像素）：蓝色手枪/红色短剑/黄色圆圈/绿色加号")]
        [Min(8f)] public float typeIconSize = 20f;

        [Tooltip("模板标记离怪物屏幕位置的偏移（像素，x 向右 / y 向上）")]
        public Vector2 typeIconOffset = new Vector2(34f, 46f);

        [Tooltip("★ 感叹号的高度（屏幕像素）。宽度按比例自动算")] 
        [Min(4f)] public float iconHeight = 18f;

        [Tooltip("★ 省略号的点直径（屏幕像素）")] 
        [Min(2f)] public float iconDotSize = 4f;

        [Header("显隐")]
        [Tooltip("血量变化后到开始淡出之前，保持完全可见的时长（秒）")]
        [Min(0f)] public float holdSeconds = 3f;

        [Tooltip("淡入时长（秒）")]
        [Min(0.01f)] public float fadeInSeconds = 0.5f;

        [Tooltip("淡出时长（秒）")]
        [Min(0.01f)] public float fadeOutSeconds = 0.5f;

        // ---------------------------------------------------------------- 运行时

        private PuzzleMonster m_Monster;

        /// <summary>地牢模式的怪物外壳（刷出停顿 / 开火蓄力状态都从它读）。</summary>
        private DungeonEnemy   m_Enemy;

        private Camera         m_Cam;

        /// <summary>模板标记读的技能组件（刷怪时和 DungeonEnemy 一起加上，所以这里懒加载）。</summary>
        private DungeonMonsterSkills m_Skills;

        private Texture2D m_White;

        /// <summary>控场型的黄色圆圈标记用的环形贴图。</summary>
        private Texture2D m_Ring;

        // ---- 显隐状态 ----
        //
        //   平时 alpha = 0（不可见）；血量变化后从 0 淡入到 1，
        //   保持 holdSeconds 秒，再用 fadeOutSeconds 淡出到 0。
        private float m_Alpha;

        /// <summary>本次"可见窗口"开始淡出的时刻。0 = 还没开始。</summary>
        private float m_HoldUntil;
        private bool  m_VisibleWindow;

        /// <summary>上次已知的血量，用来检测"发生变化"。</summary>
        private float m_LastHealth = -1f;

        // ---------------------------------------------------------------- 生命周期

        private void Awake()
        {
            m_Monster = GetComponent<PuzzleMonster>();
            if (m_Monster == null) m_Monster = GetComponentInParent<PuzzleMonster>();

            // ★ 地牢模式的怪物外壳（刷出停顿 / 开火蓄力都从它读）
            //   探秘模式没有这个组件，m_Enemy 保持 null，图标不画。
            m_Enemy = GetComponent<DungeonEnemy>();
            if (m_Enemy == null) m_Enemy = GetComponentInParent<DungeonEnemy>();

            m_White = MakeWhite();
        }

        private void OnDestroy()
        {
            if (m_White != null) Destroy(m_White);
            if (m_Ring  != null) Destroy(m_Ring);
        }

        // ---------------------------------------------------------------- 绘制


        private void Update()
        {
            if (!GameUI.IsDungeonMode) return;

            if (m_Monster == null) return;

            // ★ 懒加载：DungeonEnemy 是 MonsterWaveManager 在 SpawnMonster 里
            //   AddComponent 的，可能晚于 Awake，所以这里补一次。
            if (m_Enemy == null)
            {
                m_Enemy = GetComponent<DungeonEnemy>();
                if (m_Enemy == null) m_Enemy = GetComponentInParent<DungeonEnemy>();
            }

            // 技能组件同样是刷怪时才 AddComponent 的
            if (m_Skills == null) m_Skills = GetComponent<DungeonMonsterSkills>();

            float now = Time.unscaledTime;

            // ---- 1) 检测血量变化 -> 开启可见窗口 ----
            float cur = m_Monster.DungeonHealth;

            if (m_LastHealth < 0f)
            {
                // 第一次运行：记下初始值，不触发显示
                m_LastHealth = cur;
            }
            else if (cur != m_LastHealth)
            {
                m_LastHealth = cur;

                // 血变了（增加或减少都算）-> 重新开始"淡入 + 保持 + 淡出"
                m_VisibleWindow = true;
                m_HoldUntil     = now + Mathf.Max(0f, holdSeconds) + Mathf.Max(0.01f, fadeInSeconds);
            }

            // ---- 2) 推进 alpha ----
            //
            //   窗口内（淡入 + 保持）目标 = 1，之后目标 = 0。
            //   用 MoveTowards 按各自的时长线性趋近，
            //   自然就得到"0.5s 淡入 -> 3s 保持 -> 0.5s 淡出"。
            bool inWindow = m_VisibleWindow && now < m_HoldUntil;

            float target = inWindow ? 1f : 0f;

            float speed = target > m_Alpha
                ? 1f / Mathf.Max(0.01f, fadeInSeconds)
                : 1f / Mathf.Max(0.01f, fadeOutSeconds);

            m_Alpha = Mathf.MoveTowards(m_Alpha, target, speed * Time.unscaledDeltaTime);

            if (m_VisibleWindow && !inWindow)
                m_VisibleWindow = false;
        }

        private void OnGUI()
        {
            if (!GameUI.IsDungeonMode) return;
            if (m_Monster == null) return;

            // ★ 暂停 / 结算 / 首页时**不显示**
            if (GameUI.Current != GameState.Playing) return;

            // 死了 / 被隐藏 -> 血条和图标都不画
            if (m_Monster.DungeonIsDead) return;

            Hide h = m_Monster.GetComponent<Hide>();
            if (h != null && h.hiding != 0) return;

            if (m_Cam == null) m_Cam = Camera.main;
            if (m_Cam == null) return;

            // ---- 1) 怪物在屏幕上的位置 ----
            Vector3 monsterPos = m_Monster.transform.position;

            Vector3 sp = m_Cam.WorldToScreenPoint(monsterPos);

            // 在相机后面 -> 不画
            if (sp.z <= 0f) return;

            // 屏幕外（留一点余量）-> 不画
            if (sp.x < -80f || sp.x > Screen.width  + 80f) return;
            if (sp.y < -80f || sp.y > Screen.height + 80f) return;

            // ---- 2) 按相机距离算缩放 ----
            float dist = Vector3.Distance(m_Cam.transform.position, monsterPos);

            float t = Mathf.InverseLerp(
                Mathf.Max(0.1f, nearDistance),
                Mathf.Max(nearDistance + 0.1f, farDistance),
                dist);

            t = Mathf.Clamp01(t);

            // ★ 离镜头越远 -> 血条越短
            float len   = Mathf.Lerp(fullLengthPixels, minLengthPixels, t);
            float thick = Mathf.Lerp(thicknessPixels, Mathf.Max(1f, thicknessPixels * 0.45f), t);
            float offY  = Mathf.Lerp(offsetPixels,    Mathf.Max(6f, offsetPixels * 0.4f),   t);

            // ---- 3) 屏幕上"相机视角的上方" ----
            //
            //   ★ 直接用屏幕像素往上挪，而不是用世界空间 +Y ——
            //     俯视时 +Y 投影到屏幕几乎不动，看不出"在怪物上方"。
            //     IMGUI 的 y 轴向下，所以往上 = y 减小。
            float cx = sp.x;
            float cy = (Screen.height - sp.y) - offY;

            // ---- 4) 血条（只在 m_Alpha > 0 时画）----
            if (m_Alpha > 0.001f)
                DrawBar(cx, cy, len, thick);

            // ---- 5) ★ 状态图标（右上角）★ ----
            //
            //   需求：
            //     · 开火前 1 秒的蓄力 -> 纯红色小感叹号
            //     · 刚刷出的 1.5 秒   -> 纯红色小省略号（三个点）
            //
            //   ★ 图标**不受血条 alpha 影响**，也不吃距离缩放，
            //     永远清晰可见。
            DrawStateIcon(sp);

            // ---- 6) ★ 模板标记（左上角，常驻显示，不吃血条淡入淡出）★ ----
            DrawTemplateMarker(sp);
        }

        /// <summary>
        /// 在怪物屏幕位置的**左上角**画模板标记：
        /// 蓝色手枪=输出型、红色短剑=近战型、黄色圆圈=控场型、绿色加号=支援型。
        /// </summary>
        private void DrawTemplateMarker(Vector3 screenPoint)
        {
            if (m_Skills == null) m_Skills = GetComponent<DungeonMonsterSkills>();
            if (m_Skills == null) return;
            if (m_White == null) m_White = MakeWhite();

            float size = Mathf.Max(8f, typeIconSize);
            float cx = screenPoint.x - typeIconOffset.x;
            float cy = (Screen.height - screenPoint.y) - typeIconOffset.y;

            Color prev = GUI.color;

            switch (m_Skills.Template)
            {
                case DungeonMonsterSkills.MonsterTemplate.Damage:
                    // 蓝色手枪：枪身 + 枪管 + 握把
                    GUI.color = new Color(0.25f, 0.55f, 1f, 1f);
                    GUI.DrawTexture(new Rect(cx - size * 0.40f, cy - size * 0.10f,
                                             size * 0.55f, size * 0.24f), m_White);
                    GUI.DrawTexture(new Rect(cx + size * 0.10f, cy - size * 0.14f,
                                             size * 0.30f, size * 0.12f), m_White);
                    GUI.DrawTexture(new Rect(cx - size * 0.30f, cy + size * 0.12f,
                                             size * 0.18f, size * 0.30f), m_White);
                    break;

                case DungeonMonsterSkills.MonsterTemplate.Melee:
                    // 红色短剑：斜 45° 的剑身 + 横向护手
                    {
                        Matrix4x4 old = GUI.matrix;
                        GUI.matrix = Matrix4x4.TRS(new Vector3(cx, cy, 0f),
                                                   Quaternion.Euler(0f, 0f, -45f),
                                                   Vector3.one);
                        GUI.color = new Color(1f, 0.2f, 0.2f, 1f);
                        GUI.DrawTexture(new Rect(-size * 0.06f, -size * 0.45f,
                                                 size * 0.12f, size * 0.70f), m_White);
                        GUI.DrawTexture(new Rect(-size * 0.25f, size * 0.20f,
                                                 size * 0.50f, size * 0.10f), m_White);
                        GUI.matrix = old;
                    }
                    break;

                case DungeonMonsterSkills.MonsterTemplate.Control:
                    // 黄色圆圈：环形贴图
                    if (m_Ring == null) m_Ring = MakeRing();
                    GUI.color = new Color(1f, 0.85f, 0.15f, 1f);
                    GUI.DrawTexture(new Rect(cx - size * 0.5f, cy - size * 0.5f,
                                             size, size), m_Ring);
                    break;

                case DungeonMonsterSkills.MonsterTemplate.Support:
                    // 绿色加号：两条色块交叉
                    GUI.color = new Color(0.2f, 0.95f, 0.35f, 1f);
                    GUI.DrawTexture(new Rect(cx - size * 0.09f, cy - size * 0.42f,
                                             size * 0.18f, size * 0.84f), m_White);
                    GUI.DrawTexture(new Rect(cx - size * 0.42f, cy - size * 0.09f,
                                             size * 0.84f, size * 0.18f), m_White);
                    break;
            }

            GUI.color = prev;
        }

        /// <summary>
        /// 在怪物屏幕位置的**右上角**画状态图标。
        ///
        /// 优先级：刷出停顿（省略号）> 开火蓄力（感叹号）。
        /// 两者理论上不会同时出现（刷出期间不开火），但留个保底。
        /// </summary>
        private void DrawStateIcon(Vector3 screenPoint)
        {
            if (m_Enemy == null) return;

            bool spawnPause = m_Enemy.IsSpawnInvulnerable;
            bool windup     = m_Enemy.IsFireWindup;

            if (!spawnPause && !windup) return;

            if (m_White == null) m_White = MakeWhite();

            // 怪物在屏幕上的位置 -> IMGUI 坐标（y 轴向下）
            float sx = screenPoint.x;
            float sy = Screen.height - screenPoint.y;

            // 右上角：x 往右、y 往上（IMGUI 里往上 = 减小）
            float cx = sx + iconOffset.x;
            float cy = sy - iconOffset.y;

            Color prev = GUI.color;
            GUI.color = iconColor;

            if (spawnPause)
                DrawEllipsis(cx, cy);
            else
                DrawExclamation(cx, cy);

            GUI.color = prev;
        }

        /// <summary>画一个纯红的感叹号（竖条 + 底下一个点）。</summary>
        private void DrawExclamation(float cx, float cy)
        {
            float h    = Mathf.Max(4f, iconHeight);
            float barW = Mathf.Max(2f, h * 0.20f);     // 竖条宽
            float gap  = Mathf.Max(2f, h * 0.16f);     // 竖条和点之间的间隙
            float dotS = Mathf.Max(2f, h * 0.22f);     // 点的边长

            // 整体竖直居中：总高 = 竖条 + 间隙 + 点
            float bodyH = h * 0.62f;
            float totalH = bodyH + gap + dotS;

            float topY = cy - totalH * 0.5f;

            // 竖条
            GUI.DrawTexture(new Rect(cx - barW * 0.5f, topY, barW, bodyH), m_White);

            // 下面的点
            GUI.DrawTexture(new Rect(cx - dotS * 0.5f, topY + bodyH + gap, dotS, dotS), m_White);
        }

        /// <summary>画一个纯红的省略号（三个并排的方块点）。</summary>
        private void DrawEllipsis(float cx, float cy)
        {
            float d    = Mathf.Max(2f, iconDotSize);
            float gap  = d * 0.9f;

            float totalW = d * 3f + gap * 2f;
            float startX = cx - totalW * 0.5f;

            float y = cy - d * 0.5f;

            for (int i = 0; i < 3; i++)
            {
                GUI.DrawTexture(new Rect(startX + i * (d + gap), y, d, d), m_White);
            }
        }

        /// <summary>画一条血条：外描边 + 空槽 + 左侧的红色血量。</summary>
        private void DrawBar(float cx, float cy, float len, float thick)
        {
            if (m_White == null) m_White = MakeWhite();

            float halfLen = len * 0.5f;

            float left = cx - halfLen;
            float top  = cy - thick * 0.5f;

            // ★ 整体透明度（平时 0 = 完全看不见）
            float a = Mathf.Clamp01(m_Alpha);

            // ---- 外描边 ----
            if (borderPixels > 0f)
            {
                Color prev = GUI.color;

                Color bc = borderColor;
                bc.a *= a;
                GUI.color = bc;

                GUI.DrawTexture(new Rect(left - borderPixels,
                                         top  - borderPixels,
                                         len  + borderPixels * 2f,
                                         thick + borderPixels * 2f), m_White);

                GUI.color = prev;
            }

            // ---- 空槽 ----
            {
                Color prev = GUI.color;

                Color ec = emptyColor;
                ec.a *= a;
                GUI.color = ec;

                GUI.DrawTexture(new Rect(left, top, len, thick), m_White);

                GUI.color = prev;
            }

            // ---- 血量：从左侧开始，受伤后**从右侧**收 ----
            float ratio = m_Monster.DungeonHealth01;

            if (ratio > 0f)
            {
                Color prev = GUI.color;

                Color fc = fillColor;
                fc.a *= a;
                GUI.color = fc;

                GUI.DrawTexture(new Rect(left, top, len * ratio, thick), m_White);

                GUI.color = prev;
            }
        }

        // ---------------------------------------------------------------- 工具

        private static Texture2D MakeWhite()
        {
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave
            };

            tex.SetPixel(0, 0, Color.white);
            tex.Apply();

            return tex;
        }

        /// <summary>白色的空心圆环贴图，画的时候用 GUI.color 染成黄色。</summary>
        private static Texture2D MakeRing()
        {
            const int size = 32;
            const float outer = 15f;
            const float inner = 9f;

            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear
            };

            Color[] px = new Color[size * size];

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - (size - 1) * 0.5f;
                    float dy = y - (size - 1) * 0.5f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);

                    float edge = Mathf.Clamp01(outer - d) * Mathf.Clamp01(d - inner);
                    px[y * size + x] = new Color(1f, 1f, 1f, edge);
                }
            }

            tex.SetPixels(px);
            tex.Apply();

            return tex;
        }
    }
}