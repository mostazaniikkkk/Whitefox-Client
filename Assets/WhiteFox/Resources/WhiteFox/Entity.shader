// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Entity models (Minosoft's entity/skeletal shaders). Classic: vanilla entity lighting (two fixed directional lights
// on top of the lightmap). Modern (_WF_ModernEntities = 1): sun light with shadows, sky ambient and point lights.
// Per renderer: _Light (block, sky light 0..1) and _Overlay (hurt flash) through a MaterialPropertyBlock.
Shader "WhiteFox/Entity"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Cutoff ("Alpha cutoff", Range(0, 1)) = 0.1
        _Tint ("Tint", Color) = (1, 1, 1, 1)
        _Light ("Light (block, sky)", Vector) = (0, 1, 0, 0)
        _Overlay ("Overlay", Color) = (0, 0, 0, 0)
        _Fullbright ("Fullbright", Float) = 0
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" "Queue" = "AlphaTest" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_MainTex);
        SAMPLER(sampler_MainTex);

        CBUFFER_START(UnityPerMaterial)
            float4 _MainTex_ST;
            float _Cutoff;
            float4 _Tint;
            float4 _Light;
            float4 _Overlay;
            float _Fullbright;
        CBUFFER_END

        struct Attributes
        {
            float3 positionOS : POSITION;
            float3 normalOS : NORMAL;
            float2 uv : TEXCOORD0;
        };
        ENDHLSL

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "WhiteFoxCommon.hlsl"

            float _WF_ModernEntities;
            // inventory icons of models with "gui_light": "front" (banners, shields): lit straight from the viewer
            float _WF_ItemFrontLight;
            float _WF_DayLight;
            float _WF_Brightness;
            float4 _WF_DynamicLights[8];
            float _WF_DynamicLightCount;

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float2 uv : TEXCOORD2;
                float fog : TEXCOORD3;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionWS = TransformObjectToWorld(input.positionOS);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.uv = input.uv;
                output.fog = ComputeFogFactor(output.positionCS.z);
                return output;
            }

            float3 ClassicLight(float3 normal, float3 positionWS)
            {
                // vanilla Lighting.setupLevel: two lights from above, 40% ambient
                float3 light0 = normalize(float3(0.2, 1.0, 0.7));
                float3 light1 = normalize(float3(-0.2, 1.0, -0.7));
                float diffuse = min(1.0, 0.4 + 0.6 * (saturate(dot(normal, light0)) + saturate(dot(normal, light1))));
                diffuse = lerp(diffuse, 1.0, _WF_ItemFrontLight);
                float blockLevel = _Light.x;
                for (int i = 0; i < 8; i++)
                {
                    if (i >= (int)_WF_DynamicLightCount) break;
                    blockLevel = max(blockLevel, saturate((_WF_DynamicLights[i].w - distance(positionWS, _WF_DynamicLights[i].xyz)) / 15.0));
                }
                float block = blockLevel / (4.0 - 3.0 * blockLevel);
                float sky = _Light.y / (4.0 - 3.0 * _Light.y) * _WF_DayLight;
                float3 lightmap = max(block * float3(1.0, 0.93, 0.82), sky.xxx);
                float3 inverse = 1.0 - lightmap;
                lightmap = lerp(lightmap, 1.0 - inverse * inverse * inverse * inverse, _WF_Brightness) * 0.96 + 0.03;
                return WF_Linear(lightmap * diffuse);
            }

            half4 Frag(Varyings input, bool front : SV_IsFrontFace) : SV_Target
            {
                half4 texel = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv) * _Tint;
                clip(texel.a - _Cutoff);
                float3 normal = normalize(input.normalWS) * (front ? 1.0 : -1.0);
                float3 color;
                if (_Fullbright > 0.5)
                {
                    color = texel.rgb;
                }
                else if (_WF_ModernEntities > 0.5)
                {
                    float skyVisibility = _Light.y * _Light.y;
                    Light mainLight = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                    float3 direct = mainLight.color * mainLight.shadowAttenuation * saturate(dot(normal, mainLight.direction));
                    float3 ambient = WF_SkyAmbient(normal) * skyVisibility * _WF_AmbientStrength;
                    float3 lights = 0;
                #if defined(_ADDITIONAL_LIGHTS)
                    InputData inputData = (InputData)0;
                    inputData.positionWS = input.positionWS;
                    inputData.normalWS = normal;
                    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                    uint additionalLights = GetAdditionalLightsCount();
                    LIGHT_LOOP_BEGIN(additionalLights)
                        Light pointLight = GetAdditionalLight(lightIndex, input.positionWS, half4(1, 1, 1, 1));
                        lights += pointLight.color * pointLight.distanceAttenuation * pointLight.shadowAttenuation * saturate(dot(normal, pointLight.direction));
                    LIGHT_LOOP_END
                #endif
                    color = texel.rgb * (direct + ambient + lights);
                }
                else
                {
                    color = texel.rgb * ClassicLight(normal, input.positionWS);
                }
                color = lerp(color, _Overlay.rgb, _Overlay.a);
                color = MixFog(color, input.fog);
                return half4(color, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ColorMask 0
            Cull Off

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                float3 positionWS = TransformObjectToWorld(input.positionOS);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, _LightDirection));
            #if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #else
                positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #endif
                output.positionCS = positionCS;
                output.uv = input.uv;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                clip(SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv).a - _Cutoff);
                return 0;
            }
            ENDHLSL
        }
    }
}
