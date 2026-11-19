using UnityEngine;

namespace EchoPuzzle.Dungeon
{
    /// <summary>
    /// 【地牢模式】左上角的生命值 HUD：六个爱心。
    ///
    /// ── 表现 ────────────────────────────────────────────────
    ///   · 满血 = 实心红心，掉血 = 空心红心
    ///   · 心和心之间等距排列，位置 / 大小和探秘模式的电池一致
    ///   · 只在「游戏中」显示（和 BatterySystem / StoneSystem 一个口径）
    ///
    /// ── 和探秘模式的关系 ────────────────────────────────────
    ///   ★ 完全独立的组件，只在 DungeonMode 场景里使用。
    ///     探秘模式的左上角仍是电池 + 石头，不受影响。
    ///     （那两套 HUD 已经在地牢模式下自动不画了。）
    ///
    /// ── 依赖 ────────────────────────────────────────────────
    ///   读 <see cref="PlayerHealth"/> 的血量。找不到就不画。
    /// </summary>
    [AddComponentMenu("Echo Puzzle/Dungeon/Dungeon HUD")]
    public sealed class DungeonHUD : MonoBehaviour
    {
        // ------------------------------------------------------------------ 外观

        [Header("位置")]
        [Tooltip("第一颗爱心的位置（左上角，按 1080 高度基准的像素）")]
        public Vector2 screenOrigin = new Vector2(62f, 73f);

        [Tooltip("爱心之间的水平间距")]
        public float heartSpacing = 70f;

        [Tooltip("爱心大小（半宽，像素）")]
        public float heartRadius = 25f;

        [Header("颜色")]
        [Tooltip("满血时的爱心颜色")]
        public Color filledColor = new Color(1f, 0.28f, 0.32f, 1f);

        [Tooltip("掉血后的空心颜色")]
        public Color emptyColor = new Color(1f, 0.28f, 0.32f, 0.35f);

        [Header("护盾")]
        [Tooltip("护盾图标与爱心之间的水平间距")]
        public float shieldSpacing = 70f;

        [Tooltip("护盾图标的间距（护盾之间）")]
        public float shieldGap = 70f;

        [Tooltip("护盾图标的半宽（像素）。和爱心差不多大")]
        public float shieldRadius = 25f;

        [Tooltip("有护盾时的灰色盾牌颜色")]
        public Color shieldColor = new Color(0.72f, 0.74f, 0.78f, 1f);

        [Tooltip("护盾已碎（空槽）时的颜色")]
        public Color shieldEmptyColor = new Color(0.72f, 0.74f, 0.78f, 0.22f);

        [Tooltip("护盾上小十字架的颜色（黄色）")]
        public Color shieldCrossColor = new Color(1f, 0.85f, 0.15f, 1f);

        /// <summary>参考高度：所有尺寸都按它等比缩放。</summary>
        private const float RefHeight = 1080f;

        // ------------------------------------------------------------------ 运行时

        private PlayerHealth m_Health;
        private Texture2D    m_Pixel;

        private void Awake()
        {
            m_Pixel = MakePixel();
        }

        private void OnDestroy()
        {
            if (m_Pixel != null) Destroy(m_Pixel);
        }

        private void Update()
        {
            if (m_Health == null)
                m_Health = FindObjectOfType<PlayerHealth>();
        }

        // ------------------------------------------------------------------ 绘制

        private void OnGUI()
        {
            // ★ 模态界面（装配 / 商店 / 三选一）开着时让路，否则会盖在面板上挡住内容
            if (DungeonModalUi.AnyOpen) return;

            // ★ 只有地牢模式才画
            if (!GameUI.IsDungeonMode) return;

            // 和探秘模式的电池 / 石头一个口径：只在游戏中 / 胜利时显示
            if (!GameUI.GameplayInputActive && GameUI.Current != GameState.Playing) return;

            if (m_Health == null) return;

            // 和 GameUI 一样：按 1080 高度等比缩放
            float scale = Screen.height / RefHeight;
            Matrix4x4 saved = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity,
                                       new Vector3(scale, scale, 1f));

            int   max    = Mathf.Max(1, m_Health.maxHealth);
            int   cur    = m_Health.Current;
            float r      = heartRadius;
            float sp     = heartSpacing;

            Color prev = GUI.color;

            for (int i = 0; i < max; i++)
            {
                // 从左到右依次表示第 1、2、3...颗心
                bool filled = i < cur;

                float cx = screenOrigin.x + i * sp;
                float cy = screenOrigin.y;

                GUI.color = filled ? filledColor : emptyColor;

                DrawHeart(cx, cy, r, filled);
            }

            // ★★ 护盾：画在**血量右边、蓝量左边** ★★
            //
            //   位置：血量那一排的最右侧 + shieldSpacing
            //   蓝条在 DungeonManaHUD 里，origin = (73,166)，
            //   而血量在 y = 73，两者本来就分两行；
            //   这里把护盾接在**血量同一行**的右端，视觉上正好夹在两者之间。
            int shieldMax = m_Health.MaxShield;
            int shieldCur = m_Health.Shield;

            if (shieldMax > 0)
            {
                float shieldStartX = screenOrigin.x + max * sp + shieldSpacing;
                float sr = shieldRadius;

                for (int i = 0; i < shieldMax; i++)
                {
                    bool filled = i < shieldCur;

                    float cx = shieldStartX + i * shieldGap;
                    float cy = screenOrigin.y;

                    DrawShield(cx, cy, sr, filled);
                }
            }

            GUI.color = prev;
            GUI.matrix = saved;
        }

        /// <summary>
        /// 画一颗爱心。
        ///
        /// 用"上半两个圆 + 下半一个三角"拼出来：
        ///   · filled = true  -> 实心
        ///   · filled = false -> 只画轮廓（内缩后再画一圈实心，挖空）
        ///
        /// 全部用 1×1 白色纹理 + GUI.color 染色，不依赖任何贴图资源。
        /// </summary>
        private void DrawHeart(float cx, float cy, float r, bool filled)
        {
            if (m_Pixel == null) return;

            // 实心心形的"点阵"参数
            float step = Mathf.Max(1.5f, r * 0.14f);

            // 心形由两部分合成：
            //   上半：两个半径为 r*0.5 的圆，圆心分别在 (±r*0.5, -r*0.25)
            //   下半：一个向下收窄的三角，从 y = -r*0.25 到 y = r

            float lobeR = r * 0.52f;
            float lobeY = cy - r * 0.22f;      // 两个圆的圆心（屏幕坐标 y 向下）
            float lobeDx = r * 0.48f;

            float tipY   = cy + r * 0.95f;     // 心尖
            float bodyTopY = lobeY;            // 三角形从这里往下收

            for (float y = cy - r; y <= tipY; y += step)
            {
                // 这一行的左右边界
                float lo, hi;

                if (y <= bodyTopY)
                {
                    // ---- 上半：两个圆 ----
                    // 左圆
                    float dy = y - lobeY;
                    float halfL = Mathf.Sqrt(Mathf.Max(0f, lobeR * lobeR - dy * dy));
                    lo = cx - lobeDx - halfL;
                    hi = cx + lobeDx + halfL;
                }
                else
                {
                    // ---- 下半：三角形向下收窄 ----
                    float t = Mathf.InverseLerp(bodyTopY, tipY, y);   // 0 -> 1

                    // 三角形顶部半宽 = 两圆外缘
                    float topHalf = lobeDx + r * 0.62f;
                    float halfW   = Mathf.Lerp(topHalf, 0f, t);

                    lo = cx - halfW;
                    hi = cx + halfW;
                }

                if (hi - lo <= 0.01f) continue;

                if (filled)
                {
                    GUI.DrawTexture(new Rect(lo, y, hi - lo, step), m_Pixel);
                }
                else
                {
                    // 空心：只画左右两条边
                    float t = Mathf.Max(1.5f, r * 0.13f);      // 线宽

                    GUI.DrawTexture(new Rect(lo, y, t, step), m_Pixel);
                    GUI.DrawTexture(new Rect(hi - t, y, t, step), m_Pixel);
                }
            }
        }

        /// <summary>
        /// 画一个护盾图标：灰色盾牌 + 上面一个黄色小十字架。
        ///
        /// 造型：
        ///   · 上半部分：矩形（盾牌的肩）
        ///   · 下半部分：向下收窄成尖（盾牌的底）
        ///   · 空槽（filled = false）用半透明灰，只画轮廓
        ///
        /// 全部用 1×1 白色纹理 + GUI.color 染色，不依赖任何贴图。
        /// </summary>
        private void DrawShield(float cx, float cy, float r, bool filled)
        {
            if (m_Pixel == null) return;

            float step = Mathf.Max(1.5f, r * 0.16f);

            // 盾牌外形参数
            float topY    = cy - r * 0.92f;     // 盾牌顶部（屏幕 y 向下）
            float tipY    = cy + r * 0.98f;     // 盾牌尖
            float halfW   = r * 0.80f;          // 顶部半宽
            float shoulderY = cy - r * 0.10f;   // 从这里开始往下收窄

            Color prev = GUI.color;

            // ---- 1) 盾牌本体 ----
            for (float y = topY; y <= tipY; y += step)
            {
                float lo, hi;

                if (y <= shoulderY)
                {
                    // 上半：直的矩形
                    lo = cx - halfW;
                    hi = cx + halfW;
                }
                else
                {
                    // 下半：向下收窄成尖
                    float t = Mathf.InverseLerp(shoulderY, tipY, y);
                    float w = Mathf.Lerp(halfW, 0f, t);
                    lo = cx - w;
                    hi = cx + w;
                }

                if (hi - lo <= 0.01f) continue;

                if (filled)
                {
                    GUI.color = shieldColor;
                    GUI.DrawTexture(new Rect(lo, y, hi - lo, step), m_Pixel);
                }
                else
                {
                    // 空心：只画左右两条边
                    float t = Mathf.Max(1.5f, r * 0.15f);
                    GUI.color = shieldEmptyColor;

                    GUI.DrawTexture(new Rect(lo, y, t, step), m_Pixel);
                    GUI.DrawTexture(new Rect(hi - t, y, t, step), m_Pixel);
                }
            }

            // ---- 2) 盾牌顶部的那条横边（让空槽看得清）----
            if (!filled)
            {
                GUI.color = shieldEmptyColor;
                GUI.DrawTexture(new Rect(cx - halfW, topY,
                                         halfW * 2f, Mathf.Max(1.5f, r * 0.15f)), m_Pixel);
            }

            // ---- 3) 黄色小十字架（只在有护盾时画）----
            if (filled)
            {
                GUI.color = shieldCrossColor;

                float armLen  = r * 0.42f;      // 十字臂长
                float armThk  = Mathf.Max(2f, r * 0.16f);   // 十字粗细
                float crossY  = cy - r * 0.30f; // 十字中心的 y

                // 竖条
                GUI.DrawTexture(new Rect(cx - armThk * 0.5f, crossY - armLen,
                                         armThk, armLen * 2f), m_Pixel);

                // 横条
                GUI.DrawTexture(new Rect(cx - armLen, crossY - armThk * 0.5f,
                                         armLen * 2f, armThk), m_Pixel);
            }

            GUI.color = prev;
        }

        /// <summary>1×1 白色纹理（用 GUI.color 染色）。</summary>
        private static Texture2D MakePixel()
        {
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave
            };

            tex.SetPixel(0, 0, Color.white);
            tex.Apply();
            return tex;
        }
    }
}
