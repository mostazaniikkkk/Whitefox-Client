// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Realistic water: animated wave normals, Fresnel sky reflection, sun glint (shadowed), refraction of the scene
// behind it (camera opaque texture) with per channel absorption by the water depth (camera depth texture), and
// light scattered inside the water. Faces are double sided (meshed twice), so the surface is visible from below.
Shader "WhiteFox/Water"
{
    Properties
    {
        _Textures ("Albedo", 2DArray) = "" {}
        _WaveStrength ("Wave strength", Range(0, 2)) = 1
        _Absorption ("Absorption (per block)", Vector) = (1.4, 0.38, 0.22, 0)
        _Refraction ("Refraction", Range(0, 0.1)) = 0.03
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Transparent" "Queue" = "Transparent" }

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }
            Blend One Zero // the background is refracted and absorbed in the shader
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma require 2darray
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fog
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile _ _FORWARD_PLUS

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"
            #include "WhiteFoxCommon.hlsl"

            TEXTURE2D_ARRAY(_Textures);
            SAMPLER(sampler_Textures);

            CBUFFER_START(UnityPerMaterial)
                float _WaveStrength;
                float4 _Absorption;
                float _Refraction;
            CBUFFER_END
            float4 _WF_OriginOffset;

            struct Attributes
            {
                float3 positionOS : POSITION;
                float4 color : COLOR;
                float4 uv0 : TEXCOORD0;
                float2 light : TEXCOORD1;
                float4 normal : TEXCOORD2;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 uv : TEXCOORD1;
                float3 tint : TEXCOORD2;
                float3 normalWS : TEXCOORD3;
                float2 light : TEXCOORD4;
                float4 screen : TEXCOORD5;
                float fog : TEXCOORD6;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionWS = TransformObjectToWorld(input.positionOS);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.uv = input.uv0.xyz;
                output.tint = WF_Linear(input.color.rgb);
                output.normalWS = TransformObjectToWorldNormal(input.normal.xyz);
                output.light = input.light;
                output.screen = ComputeScreenPos(output.positionCS);
                output.fog = ComputeFogFactor(output.positionCS.z);
                return output;
            }

            // sum of directional waves; returns the height gradient
            float2 WaveGradient(float2 p, float t)
            {
                float2 gradient = 0;
                const float2 directions[4] = { float2(1.0, 0.2), float2(-0.4, 1.0), float2(0.7, -0.7), float2(-1.0, -0.3) };
                const float frequencies[4] = { 1.3, 2.1, 3.7, 5.3 };
                const float amplitudes[4] = { 0.05, 0.035, 0.02, 0.012 };
                const float speeds[4] = { 1.1, 1.5, 2.1, 2.9 };
                [unroll] for (int i = 0; i < 4; i++)
                {
                    float2 d = normalize(directions[i]);
                    float phase = dot(d, p) * frequencies[i] + t * speeds[i];
                    gradient += d * (amplitudes[i] * frequencies[i] * cos(phase));
                }
                return gradient;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float3 normal = normalize(input.normalWS);
                float horizontal = saturate(abs(normal.y));
                float2 gradient = WaveGradient(input.positionWS.xz + _WF_OriginOffset.xy, _Time.y) * _WaveStrength * horizontal;
                normal = normalize(normal + float3(-gradient.x, 0, -gradient.y));

                float3 viewDirection = GetWorldSpaceNormalizeViewDir(input.positionWS);
                float nDotV = saturate(dot(normal, viewDirection));
                float fresnel = 0.02 + 0.98 * pow(1.0 - nDotV, 5.0);

                float skyVisibility = input.light.y * input.light.y;
                float sunGate = 1.0;
                float3 reflected = reflect(-viewDirection, normal);
                float3 reflection = WF_SkyColor(reflected) * max(skyVisibility, 0.03);

                Light mainLight = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                float glint = pow(saturate(dot(reflected, mainLight.direction)), 400.0) * 6.0;
                float3 specular = mainLight.color * mainLight.shadowAttenuation * glint * sunGate;

                // refraction: look up the scene behind the surface, slightly displaced by the waves
                float2 screenUV = input.screen.xy / input.screen.w;
                float surfaceDepth = LinearEyeDepth(input.positionCS.z, _ZBufferParams);
                bool fromBelow = input.normalWS.y < -0.5; // camera under water looking up through the surface
                float2 refractedUV = screenUV + gradient * _Refraction / max(surfaceDepth * 0.15, 1.0);
                float sceneDepth = LinearEyeDepth(SampleSceneDepth(refractedUV), _ZBufferParams);
                if (sceneDepth < surfaceDepth) // something in front of the water: do not refract it in
                {
                    refractedUV = screenUV;
                    sceneDepth = LinearEyeDepth(SampleSceneDepth(screenUV), _ZBufferParams);
                }
                float3 background = SampleSceneColor(refractedUV);
                // absorption per color channel through the water between the surface and what is behind it
                float thickness = fromBelow ? 0.5 : max(sceneDepth - surfaceDepth, 0.0);
                float3 transmittance = exp(-thickness * _Absorption.rgb);

                float3 ambient = WF_SkyAmbient(float3(0, 1, 0)) * skyVisibility * _WF_AmbientStrength;
            #if defined(_ADDITIONAL_LIGHTS)
                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.normalWS = normal;
                inputData.viewDirectionWS = viewDirection;
                inputData.normalizedScreenSpaceUV = screenUV;
                uint additionalLights = GetAdditionalLightsCount();
                LIGHT_LOOP_BEGIN(additionalLights)
                    Light pointLight = GetAdditionalLight(lightIndex, input.positionWS, half4(1, 1, 1, 1));
                    float3 radiance = pointLight.color * pointLight.distanceAttenuation * pointLight.shadowAttenuation;
                    ambient += radiance * 0.5;
                    specular += radiance * pow(saturate(dot(reflected, pointLight.direction)), 200.0) * 3.0;
                LIGHT_LOOP_END
            #endif
                ambient += mainLight.color * mainLight.shadowAttenuation * sunGate * 0.15;
                half4 texel = SAMPLE_TEXTURE2D_ARRAY(_Textures, sampler_Textures, input.uv.xy, input.uv.z);
                float detail = 0.8 + dot(texel.rgb, float3(0.3, 0.6, 0.1)) * 0.4;
                // light scattered inside the water (blue-green), more of it the deeper the water is
                float3 scattering = input.tint * ambient * 0.5 * detail;
                float3 water = background * transmittance + scattering * (1.0 - transmittance);
                float3 color = water * (1.0 - fresnel) + reflection * fresnel + specular;
                color = MixFog(color, input.fog);
                return half4(color, 1.0);
            }
            ENDHLSL
        }
    }
}
