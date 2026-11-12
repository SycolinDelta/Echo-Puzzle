// 角色轮廓线的**半透明版本** —— 和 EchoPuzzle/Outline 外观完全一致，
// 但支持整体淡出（用于地牢模式的闪避效果）。
//
// 和 EchoPuzzle/Outline 的区别只有两点：
//   1) 加了 _Alpha 属性，用它整体缩放不透明度
//   2) 打开透明混合、关掉深度写入、队列改成 Transparent
//
// ★ 为什么不能直接改 EchoPuzzle/Outline 的 alpha：
//   那个 shader 没有 Blend 指令（不透明管线），alpha 值根本不参与混合，
//   改 _OutlineColor.a 在画面上毫无变化。
//
// ── 用法 ────────────────────────────────────────────────────
//   闪避时把这个材质换成渲染器上的 sharedMaterial，
//   把 _Alpha 从 1 调到 dodgeAlpha（默认 0.5）；闪避结束再换回去。
Shader "EchoPuzzle/OutlineTransparent"
{
    Properties
    {
        _OutlineColor  ("Outline Color", Color)   = (1, 1, 1, 1)
        _OutlineWidth  ("Outline Width", Range(0, 0.2)) = 0.022
        _Glow          ("Glow", Range(0, 8))      = 2.0
        _BodyColor     ("Body Color", Color)      = (0, 0, 0, 1)
        _RimPower      ("Rim Power", Range(0.5, 8)) = 2.5
        _RimStrength   ("Rim Strength", Range(0, 4)) = 1.1

        _Alpha         ("Alpha", Range(0, 1))     = 0.1
    }

    SubShader
    {
        Tags
        {
            "RenderType"      = "Transparent"
            "RenderPipeline"  = "UniversalPipeline"
            "IgnoreProjector" = "True"
            "Queue"           = "Transparent"
        }

        // ---------------- Pass 1: 外扩描边 ----------------
        Pass
        {
            Name "OutlineShell"
            Tags { "LightMode" = "UniversalForward" }

            Cull Front
            ZWrite Off
            ZTest LEqual

            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target 3.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _OutlineColor;
                float _OutlineWidth;
                float _Glow;
                half4 _BodyColor;
                float _RimPower;
                float _RimStrength;
                float _Alpha;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };
            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                float3 posOS = IN.positionOS.xyz + normalize(IN.normalOS) * _OutlineWidth;
                OUT.positionCS = TransformObjectToHClip(posOS);
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                half4 c = _OutlineColor * _Glow;
                c.a *= _Alpha;
                return c;
            }
            ENDHLSL
        }

        // ---------------- Pass 2: 黑体 + 边缘辉光 ----------------
        Pass
        {
            Name "OutlineBody"
            Tags { "LightMode" = "UniversalForward" }

            Cull Back
            ZWrite Off
            ZTest LEqual

            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target 3.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _OutlineColor;
                float _OutlineWidth;
                float _Glow;
                half4 _BodyColor;
                float _RimPower;
                float _RimStrength;
                float _Alpha;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS   : TEXCOORD0;
                float3 viewDirWS  : TEXCOORD1;
            };

            Varyings vert (Attributes IN)
            {
                Varyings OUT;

                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);

                OUT.normalWS  = TransformObjectToWorldNormal(IN.normalOS);
                OUT.viewDirWS = normalize(_WorldSpaceCameraPos - TransformObjectToWorld(IN.positionOS.xyz));

                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                float3 n = normalize(IN.normalWS);
                float3 v = normalize(IN.viewDirWS);

                // 和 EchoPuzzle/Outline 里的边缘辉光同一个公式
                float rim = pow(saturate(1.0 - saturate(dot(n, v))), _RimPower);

                half3 col = _BodyColor.rgb + _OutlineColor.rgb * _Glow * rim * _RimStrength;

                half4 c = half4(col, _Alpha);
                return c;
            }
            ENDHLSL
        }
    }

    Fallback Off
}
