// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Port of Minosoft's chunk shader (chunk.vsh/chunk.fsh): texture array lookup, tint, ambient occlusion,
// block/sky light through a lightmap curve and fog. Vertex layout: see WhiteFox.Core.Meshing.ChunkVertex.
Shader "WhiteFox/Block"
{
    Properties
    {
        _Textures ("Block textures", 2DArray) = "" {}
        _Cutoff ("Alpha cutoff", Range(0, 1)) = 0.1
        // block items (dropped, held, in frames) set the light where the entity is; chunks keep their vertex light
        _Light ("Light override (block, sky; negative: vertex light)", Vector) = (-1, -1, 0, 0)
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src blend", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst blend", Float) = 0
        [Toggle] _ZWrite ("ZWrite", Float) = 1
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D_ARRAY(_Textures);
        SAMPLER(sampler_Textures);

        CBUFFER_START(UnityPerMaterial)
            float _Cutoff;
            float4 _Light;
        CBUFFER_END

        // set globally by the client every frame
        float _WF_DayLight;
        float _WF_Brightness;
        // dynamic lights (held torches, burning entities...): render space position, light level 0..15
        float4 _WF_DynamicLights[8];
        float _WF_DynamicLightCount;

        // like OptiFine's dynamic lights: level minus distance, but per vertex so it fades smoothly
        float DynamicBlockLight(float3 positionWS, float blockLight)
        {
            for (int i = 0; i < 8; i++)
            {
                if (i >= (int)_WF_DynamicLightCount) break;
                float level = _WF_DynamicLights[i].w - distance(positionWS, _WF_DynamicLights[i].xyz);
                blockLight = max(blockLight, saturate(level / 15.0));
            }
            return blockLight;
        }

        struct Attributes
        {
            float3 positionOS : POSITION;
            float4 uv0 : TEXCOORD0;   // u, v, texture layer, ambient occlusion
            float4 color : COLOR;     // tint, alpha = direction shade
            float2 light : TEXCOORD1; // block light, sky light (0..1)
            float4 normal : TEXCOORD2;  // w: emission of light emitting blocks (the HDR glow of the enhanced mode)
            float4 tangent : TEXCOORD3; // w: sway in the wind (enhanced mode, WhiteFoxWind.hlsl)
        };

        // enhanced mode "HDR glow": bright texels of light emitting blocks go above 1 and bloom (0 in classic)
        float _WF_EnhancedGlow;

        #include "WhiteFoxWind.hlsl"

        float3 SwayedWorldPosition(Attributes input)
        {
            float3 positionWS = TransformObjectToWorld(input.positionOS);
            return positionWS + WindOffset(positionWS, input.tangent.w);
        }

        // vanilla lightmap curve: level / (4 - 3 * level)
        float3 Lightmap(float2 light)
        {
            float blockLight = light.x / (4.0 - 3.0 * light.x);
            float skyLight = light.y / (4.0 - 3.0 * light.y) * _WF_DayLight;
            float3 blockColor = blockLight * float3(1.0, 0.93, 0.82);
            float3 result = max(blockColor, skyLight.xxx);
            // gamma ("brightness" option): lerp towards 1 - (1 - x)^4
            float3 inverse = 1.0 - result;
            result = lerp(result, 1.0 - inverse * inverse * inverse * inverse, _WF_Brightness);
            return result * 0.96 + 0.03;
        }

        // Minecraft multiplies tint, shade, AO and light in gamma space. In a linear project the texel is linearized
        // by the sRGB sampler, so the factor has to be linearized too: (texel * f)^2.2 = texel^2.2 * f^2.2.
        float3 ToRenderSpace(float3 factor)
        {
        #if defined(UNITY_COLORSPACE_GAMMA)
            return factor;
        #else
            return pow(max(factor, 0.0), 2.2);
        #endif
        }
        ENDHLSL

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }
            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]
            Cull Back

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fog
            #pragma require 2darray

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 uv : TEXCOORD0;
                float4 color : TEXCOORD1;
                float fog : TEXCOORD2;
                float emission : TEXCOORD3;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.emission = max(input.normal.w, 0.0) * _WF_EnhancedGlow;
                float3 positionWS = SwayedWorldPosition(input);
                output.positionCS = TransformWorldToHClip(positionWS);
                output.uv = input.uv0.xyz;
                float2 vertexLight = _Light.x >= 0.0 ? _Light.xy : input.light;
                float2 light = float2(DynamicBlockLight(positionWS, vertexLight.x), vertexLight.y);
                output.color = float4(ToRenderSpace(input.color.rgb * input.color.a * input.uv0.w * Lightmap(light)), 1.0);
                output.fog = ComputeFogFactor(output.positionCS.z);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                half4 texel = SAMPLE_TEXTURE2D_ARRAY(_Textures, sampler_Textures, input.uv.xy, input.uv.z);
                clip(texel.a - _Cutoff);
                half4 color = texel * input.color;
                if (input.emission > 0.0)
                {
                    // the flame of a torch, lava, glowstone: their bright texels shine past white, the bloom spreads them
                    float brightness = max(texel.r, max(texel.g, texel.b));
                    color.rgb *= 1.0 + input.emission * smoothstep(0.55, 0.9, brightness) * 2.5;
                }
                color.rgb = MixFog(color.rgb, input.fog);
                return color;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R
            Cull Back

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma require 2darray

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 uv : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformWorldToHClip(SwayedWorldPosition(input));
                output.uv = input.uv0.xyz;
                return output;
            }

            half Frag(Varyings input) : SV_Target
            {
                clip(SAMPLE_TEXTURE2D_ARRAY(_Textures, sampler_Textures, input.uv.xy, input.uv.z).a - _Cutoff);
                return input.positionCS.z;
            }
            ENDHLSL
        }
    }

    // Built-in render pipeline fallback, so the project still renders if URP is not active.
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }

        Pass
        {
            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]
            Cull Back

            CGPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fog
            #pragma require 2darray
            #include "UnityCG.cginc"

            UNITY_DECLARE_TEX2DARRAY(_Textures);
            float _Cutoff;
            float _WF_DayLight;
            float _WF_Brightness;

            struct Attributes
            {
                float4 vertex : POSITION;
                float4 uv0 : TEXCOORD0;
                float4 color : COLOR;
                float2 light : TEXCOORD1;
            };

            struct Varyings
            {
                float4 pos : SV_POSITION;
                float3 uv : TEXCOORD0;
                float4 color : TEXCOORD1;
                UNITY_FOG_COORDS(2)
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
                output.pos = UnityObjectToClipPos(input.vertex);
                output.uv = input.uv0.xyz;
                float3 factor = input.color.rgb * input.color.a * input.uv0.w * Lightmap(input.light);
                #if !defined(UNITY_COLORSPACE_GAMMA)
                factor = pow(max(factor, 0.0), 2.2);
                #endif
                output.color = float4(factor, 1.0);
                UNITY_TRANSFER_FOG(output, output.pos);
                return output;
            }

            fixed4 Frag(Varyings input) : SV_Target
            {
                fixed4 texel = UNITY_SAMPLE_TEX2DARRAY(_Textures, input.uv);
                clip(texel.a - _Cutoff);
                fixed4 color = texel * input.color;
                UNITY_APPLY_FOG(input.fogCoord, color);
                return color;
            }
            ENDCG
        }
    }
}
