// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Text displays (vanilla TextDisplayRenderer): the text and its translucent background drawn into one texture,
// alpha blended, lit by the Minecraft light where the entity is (or its brightness override) like vanilla's text.
// Per renderer: _Light (block, sky light 0..1) through a MaterialPropertyBlock.
Shader "WhiteFox/TextDisplay"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Light ("Light (block, sky)", Vector) = (0, 1, 0, 0)
        _Fullbright ("Fullbright", Float) = 0
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("ZTest", Float) = 4
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Transparent" "Queue" = "Transparent" }

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest [_ZTest]
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _Light;
                float _Fullbright;
                float _ZTest;
            CBUFFER_END

            float _WF_DayLight;
            float _WF_Brightness;

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float fog : TEXCOORD1;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS);
                output.uv = input.uv;
                output.fog = ComputeFogFactor(output.positionCS.z);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                half4 texel = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);
                // vanilla lightmap curve: level / (4 - 3 * level), brightness option towards 1 - (1 - x)^4
                float block = _Light.x / (4.0 - 3.0 * _Light.x);
                float sky = _Light.y / (4.0 - 3.0 * _Light.y) * _WF_DayLight;
                float light = max(block, sky);
                float inverse = 1.0 - light;
                light = lerp(light, 1.0 - inverse * inverse * inverse * inverse, _WF_Brightness) * 0.96 + 0.03;
                light = _Fullbright > 0.5 ? 1.0 : light;
            #if defined(UNITY_COLORSPACE_GAMMA)
                float factor = light;
            #else
                float factor = pow(light, 2.2);
            #endif
                half3 color = MixFog(texel.rgb * factor, input.fog);
                return half4(color, texel.a);
            }
            ENDHLSL
        }
    }
}
