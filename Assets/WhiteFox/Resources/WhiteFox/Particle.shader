// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Particles (Minosoft's particle shader): camera facing quads built on the CPU, textured from the particle sprite
// array or from the block texture array (break particles), tinted, lit by the lightmap curve (or fullbright).
// Vertex: position, color, uv0 = (u, v, layer, source 0 particle / 1 block / 2 item), uv1 = (block light, sky light).
Shader "WhiteFox/Particle"
{
    Properties
    {
        _Particles ("Particle sprites", 2DArray) = "" {}
        _Blocks ("Block textures", 2DArray) = "" {}
        _Items ("Item sprites", 2DArray) = "" {}
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Transparent" "Queue" = "Transparent+10" }

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fog
            #pragma require 2darray
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D_ARRAY(_Particles);
            SAMPLER(sampler_Particles);
            TEXTURE2D_ARRAY(_Blocks);
            SAMPLER(sampler_Blocks);
            TEXTURE2D_ARRAY(_Items);
            SAMPLER(sampler_Items);

            float _WF_DayLight;
            float _WF_Brightness;

            struct Attributes
            {
                float3 positionOS : POSITION;
                float4 color : COLOR;
                float4 uv0 : TEXCOORD0;
                float2 light : TEXCOORD1;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 uv : TEXCOORD0;
                float4 color : TEXCOORD1;
                float fog : TEXCOORD2;
            };

            float3 Lightmap(float2 light)
            {
                float blockLight = light.x / (4.0 - 3.0 * light.x);
                float skyLight = light.y / (4.0 - 3.0 * light.y) * _WF_DayLight;
                float3 result = max(blockLight * float3(1.0, 0.93, 0.82), skyLight.xxx);
                float3 inverse = 1.0 - result;
                result = lerp(result, 1.0 - inverse * inverse * inverse * inverse, _WF_Brightness);
                return result * 0.96 + 0.03;
            }

            Varyings Vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs position = GetVertexPositionInputs(input.positionOS);
                output.positionCS = position.positionCS;
                output.uv = input.uv0;
                float3 light = input.light.x > 1.5 ? float3(1, 1, 1) : Lightmap(input.light);
                float3 factor = input.color.rgb * light;
            #if !defined(UNITY_COLORSPACE_GAMMA)
                factor = pow(max(factor, 0.0), 2.2);
            #endif
                output.color = float4(factor, input.color.a);
                output.fog = ComputeFogFactor(position.positionCS.z);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                half4 texel = input.uv.w > 1.5
                    ? SAMPLE_TEXTURE2D_ARRAY(_Items, sampler_Items, input.uv.xy, input.uv.z)
                    : input.uv.w > 0.5
                    ? SAMPLE_TEXTURE2D_ARRAY(_Blocks, sampler_Blocks, input.uv.xy, input.uv.z)
                    : SAMPLE_TEXTURE2D_ARRAY(_Particles, sampler_Particles, input.uv.xy, input.uv.z);
                half4 color = texel * input.color;
                clip(color.a - 0.02);
                color.rgb = MixFog(color.rgb, input.fog);
                return color;
            }
            ENDHLSL
        }
    }
}
