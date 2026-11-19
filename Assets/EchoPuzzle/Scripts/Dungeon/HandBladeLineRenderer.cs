using System.Collections.Generic;
using UnityEngine;

namespace EchoPuzzle.Dungeon
{
    /// <summary>
    /// 【地牢模式】手刀线段特效的**统一渲染器**（对象池 + 共享网格/材质）。
    ///
    /// ── 为什么要有它 ────────────────────────────────────
    ///   需求："手刀会创建一堆 handbladeline。
    ///         我希望建两个渲染器将它们分别容纳，免得大量创建和删除东西。"
    ///
    ///   ★ 以前每根线段都是 new GameObject + new Mesh + new Material，
    ///     淡完再 Destroy 掉。一次手刀 13 根，一秒能连挥 2~10 次 ——
    ///     每秒上百次网格/材质分配，GC 压力很大。
    ///
    ///   ✔ 现在：
    ///     · 网格**只有一份**（单位长度、沿 +Z、宽 = 1 的细长条），
    ///       需要多长就改 Transform 的 z 缩放，需要多粗就改 x 缩放
    ///     · 材质**只有一份**，颜色/不透明度用 MaterialPropertyBlock 实时写
    ///     · 线段从池里借，淡完只是 SetActive(false) 还回去，不销毁
    ///
    /// ── 用法（由 DungeonWeapon.BladeFxRoutine 调用）────────
    ///   object token = HandBladeLineRenderer.Rent(位置, 朝向角度, 长度, 粗细, 颜色);
    ///   HandBladeLineRenderer.SetAlpha(token, a);
    ///   HandBladeLineRenderer.Release(token);
    /// </summary>
    [AddComponentMenu("Echo Puzzle/Dungeon/Hand Blade Line Renderer")]
    public sealed class HandBladeLineRenderer : MonoBehaviour
    {
        public static HandBladeLineRenderer Instance { get; private set; }

        // ---------------------------------------------------------------- 池

        private sealed class Line
        {
            public GameObject go;
            public MeshRenderer mr;
            public MaterialPropertyBlock mpb;
            public bool inUse;
        }

        private readonly List<Line> m_Pool = new List<Line>(32);

        [Header("外观")]
        [Tooltip("线段默认的淡出起止亮度（和旧扇形特效同口径）")]
        [Range(0f, 1f)] public float baseAlpha = 0.85f;

        [Header("网格")]
        [Tooltip("线段沿长度方向的细分（贴地四边形其实 1 段就够，留几段方便将来做弧度）")]
        [Range(1, 8)] public int lengthSegments = 1;

        private Mesh m_SharedMesh;
        private Material m_SharedMat;

        // ---------------------------------------------------------------- 生命周期

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;

            BuildSharedResources();
        }

        private void OnDestroy()
        {
            if (Instance != this) return;
            Instance = null;

            if (m_SharedMesh != null) Destroy(m_SharedMesh);
            if (m_SharedMat  != null) Destroy(m_SharedMat);
        }

        /// <summary>
        /// 建一次共用网格 + 共用材质。
        ///
        /// ★ 网格是"单位尺寸"的：沿 +Z 从 0 到 1，X 方向宽 ±0.5，y = 0。
        ///   用的时候靠 Transform 的 (x, 1, z) 缩放撑成需要的粗细和长度。
        /// </summary>
        private void BuildSharedResources()
        {
            if (m_SharedMesh == null)
            {
                var verts = new List<Vector3>(4);
                var tris  = new List<int>(6);

                // 两个端点：z = 0（圆心）和 z = 1（半径处），x 方向 ±0.5
                verts.Add(new Vector3(-0.5f, 0f, 0f));
                verts.Add(new Vector3( 0.5f, 0f, 0f));
                verts.Add(new Vector3( 0.5f, 0f, 1f));
                verts.Add(new Vector3(-0.5f, 0f, 1f));

                tris.Add(0); tris.Add(1); tris.Add(2);
                tris.Add(0); tris.Add(2); tris.Add(3);

                m_SharedMesh = new Mesh { name = "HandBladeLine(shared)" };
                m_SharedMesh.SetVertices(verts);
                m_SharedMesh.SetTriangles(tris, 0);
                m_SharedMesh.RecalculateBounds();
            }

            if (m_SharedMat == null)
            {
                m_SharedMat = EchoRuntimeMaterials.AimOverlay(Color.white);

                if (m_SharedMat != null)
                {
                    m_SharedMat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.LessEqual);
                    m_SharedMat.SetInt("_ZWrite", 0);
                    m_SharedMat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                }
            }
        }

        // ---------------------------------------------------------------- 借用 / 归还

        private Line RentInternal()
        {
            for (int i = 0; i < m_Pool.Count; i++)
            {
                if (!m_Pool[i].inUse)
                {
                    m_Pool[i].inUse = true;
                    m_Pool[i].go.SetActive(true);
                    return m_Pool[i];
                }
            }

            BuildSharedResources();

            var go = new GameObject("[手刀] 线段");
            go.hideFlags = HideFlags.DontSave;

            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = m_SharedMesh;

            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = m_SharedMat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows    = false;

            Hide hide = go.AddComponent<Hide>();
            hide.SetHiding(0);

            go.transform.SetParent(transform, false);

            var l = new Line { go = go, mr = mr, mpb = new MaterialPropertyBlock(), inUse = true };
            m_Pool.Add(l);
            return l;
        }

        /// <summary>把一根线段还回池里（隐藏但不销毁）。</summary>
        public static void Release(object token)
        {
            if (Instance == null) return;
            if (!(token is Line l) || l == null) return;

            l.inUse = false;
            if (l.go != null) l.go.SetActive(false);
        }

        // ---------------------------------------------------------------- 对外

        /// <summary>
        /// 借一根线段：从 <paramref name="center"/> 出发，朝 <paramref name="yawDeg"/> 方向，
        /// 长度 <paramref name="length"/> 米，粗细 <paramref name="width"/> 米。
        /// </summary>
        /// <returns>token，用完交给 <see cref="Release"/></returns>
        public static object Rent(Vector3 center, float yawDeg,
                                  float length, float width, Color color, float alpha)
        {
            if (Instance == null) return null;

            Line l = Instance.RentInternal();

            // 朝向：yaw 0 = +Z
            l.go.transform.position = center;
            l.go.transform.rotation = Quaternion.Euler(0f, yawDeg, 0f);

            // 网格是单位长度/单位宽度，靠缩放撑开
            l.go.transform.localScale = new Vector3(Mathf.Max(0.002f, width), 1f,
                                                    Mathf.Max(0.05f, length));

            Instance.WriteColor(l, color, alpha);

            return l;
        }

        /// <summary>
        /// 重新摆放某根线段（位置 / 朝向 / 长度）。
        ///
        /// ★ 给"正在扫动的那条线"用 —— 它每帧换个角度，
        ///   不需要每次新建一个对象。
        /// </summary>
        public static void SetTransform(object token, Vector3 center,
                                        float yawDeg, float length)
        {
            if (Instance == null) return;
            if (!(token is Line l) || l == null || l.go == null) return;

            l.go.transform.position = center;
            l.go.transform.rotation = Quaternion.Euler(0f, yawDeg, 0f);
            l.go.transform.localScale = new Vector3(
                Mathf.Max(0.002f, l.go.transform.localScale.x), 1f,
                Mathf.Max(0.05f, length));
        }

        /// <summary>更新某根线段的颜色 / 不透明度。</summary>
        public static void SetColor(object token, Color color, float alpha)
        {
            if (Instance == null) return;
            if (!(token is Line l) || l == null) return;

            Instance.WriteColor(l, color, alpha);
        }

        private void WriteColor(Line l, Color color, float alpha)
        {
            if (l.mr == null) return;

            Color c = color;
            c.a = Mathf.Clamp01(alpha);

            l.mpb.Clear();
            l.mpb.SetColor("_Color", c);

            if (m_SharedMat != null && m_SharedMat.HasProperty("_BaseColor"))
                l.mpb.SetColor("_BaseColor", c);

            l.mr.SetPropertyBlock(l.mpb);
        }
    }
}
