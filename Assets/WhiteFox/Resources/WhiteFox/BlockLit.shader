// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Realistic chunk shader: physically based sun light with cascaded shadows, LabPBR normal/specular maps,
// sky ambient and reflections from the sky model, Minecraft block light as warm local light and emission.
// Minecraft's sky light level is kept as "sky visibility", so caves and interiors stay dark.
Shader "WhiteFox/BlockLit"
{
    Properties
    {
        _Textures ("Albedo", 2DArray) = "" {}
        _Normals ("LabPBR normal (_n)", 2DArray) = "" {}
        _Specular ("LabPBR specular (_s)", 2DArray) = "" {}
        _Cutoff ("Alpha cutoff", Range(0, 1)) = 0.1
        _NormalStrength ("Normal strength", Range(0, 2)) = 1
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
        TEXTURE2D_ARRAY(_Normals);
        TEXTURE2D_ARRAY(_Specular);

        CBUFFER_START(UnityPerMaterial)
            float _Cutoff;
            float _NormalStrength;
            float4 _Light;
        CBUFFER_END

        struct Attributes
        {
            float3 positionOS : POSITION;
            float4 color : COLOR;      // tint, alpha = direction shade (unused here)
            float4 uv0 : TEXCOORD0;    // u, v, layer, ambient occlusion
            float2 light : TEXCOORD1;  // block light, sky light
            float4 normal : TEXCOORD2; // normal, w = emission
            float4 tangent : TEXCOORD3; // tangent, w = bitangent sign; |w| < 0.95: plant swaying in the wind (WhiteFoxWind.hlsl)
        };

        #include "WhiteFoxWind.hlsl"
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
            #pragma require 2darray
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fog
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile _ _FORWARD_PLUS

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "WhiteFoxCommon.hlsl"

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 uv : TEXCOORD1;
                float4 tintAO : TEXCOORD2;
                float3 normalWS : TEXCOORD3;
                float4 tangentWS : TEXCOORD4;
                float3 lightEmission : TEXCOORD5;
                float fog : TEXCOORD6;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionWS = TransformObjectToWorld(input.positionOS);
                output.positionWS += WindOffset(output.positionWS, input.tangent.w);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.uv = input.uv0.xyz;
                output.tintAO = float4(WF_Linear(input.color.rgb), input.uv0.w);
                output.normalWS = TransformObjectToWorldNormal(input.normal.xyz);
                output.tangentWS = float4(TransformObjectToWorldDir(input.tangent.xyz), input.tangent.w >= 0.0 ? 1.0 : -1.0);
                float2 light = _Light.x >= 0.0 ? _Light.xy : input.light;
                output.lightEmission = float3(light, max(input.normal.w, 0.0));
                output.fog = ComputeFogFactor(output.positionCS.z);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                half4 texel = SAMPLE_TEXTURE2D_ARRAY(_Textures, sampler_Textures, input.uv.xy, input.uv.z);
                clip(texel.a - _Cutoff);
                float3 albedo = texel.rgb * input.tintAO.rgb;

                // LabPBR normal: RG = xy (DirectX, green points down), B = AO
                float4 normalTex = SAMPLE_TEXTURE2D_ARRAY(_Normals, sampler_Textures, input.uv.xy, input.uv.z);
                float2 nxy = (normalTex.rg * 2.0 - 1.0) * float2(1.0, -1.0) * _NormalStrength;
                float3 normalTS = float3(nxy, sqrt(saturate(1.0 - dot(nxy, nxy))));
                float3 geometric = normalize(input.normalWS);
                float3 tangent = normalize(input.tangentWS.xyz);
                float3 bitangent = cross(geometric, tangent) * input.tangentWS.w;
                float3 normal = normalize(normalTS.x * tangent + normalTS.y * bitangent + normalTS.z * geometric);

                // LabPBR specular: R = perceptual smoothness, G = F0 (>= 230 metal), A = emission (255 = none)
                float4 specular = SAMPLE_TEXTURE2D_ARRAY(_Specular, sampler_Textures, input.uv.xy, input.uv.z);
                float smoothness = specular.r;
                float metallic = specular.g * 255.0 >= 229.5 ? 1.0 : 0.0;
                float emissionMap = specular.a * 255.0 < 254.5 ? specular.a : 0.0;

                float alpha = texel.a;
                BRDFData brdf;
                InitializeBRDFData(albedo, metallic, half3(0, 0, 0), smoothness, alpha, brdf);

                float blockLight = input.lightEmission.x;
                float skyLight = input.lightEmission.y;
                float skyVisibility = skyLight * skyLight;
                // the shadow map shapes where the sun reaches; the (per vertex smoothed) sky light keeps it out of places
                // the sky never reaches, like caves under solid ground: the shadow map leaks thin lines of light
                // through the seams between blocks there. Beyond the shadow distance only the sky light decides.
                float distanceToCamera = distance(input.positionWS, _WorldSpaceCameraPos);
                float sunGate = lerp(smoothstep(0.0, 0.35, skyLight), smoothstep(0.3, 0.9, skyLight), saturate((distanceToCamera - 110.0) / 15.0));
                float ao = input.tintAO.a * normalTex.b;

                float3 viewDirection = GetWorldSpaceNormalizeViewDir(input.positionWS);
                Light mainLight = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                float facing = saturate(dot(geometric, mainLight.direction) * 4.0); // no light through the back of a face
                float3 direct = LightingPhysicallyBased(brdf, mainLight, normal, viewDirection) * sunGate * facing;

                // light emitting blocks are real point lights (LightSourceManager), not Minecraft's block light levels
                float3 irradiance = WF_SkyAmbient(normal) * skyVisibility * _WF_AmbientStrength * ao;
                float3 reflection = WF_SkyColor(reflect(-viewDirection, normal)) * skyVisibility * ao;
                float fresnel = Pow4(1.0 - saturate(dot(normal, viewDirection)));
                float3 color = EnvironmentBRDF(brdf, irradiance, reflection, fresnel) + direct;

            #if defined(_ADDITIONAL_LIGHTS)
                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.normalWS = normal;
                inputData.viewDirectionWS = viewDirection;
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                uint additionalLights = GetAdditionalLightsCount();
                LIGHT_LOOP_BEGIN(additionalLights)
                    Light pointLight = GetAdditionalLight(lightIndex, input.positionWS, half4(1, 1, 1, 1));
                    color += LightingPhysicallyBased(brdf, pointLight, normal, viewDirection) * (0.35 + 0.65 * ao);
                    // light bounced by the walls around: a soft fill that also reaches faces turned away and shadows
                    color += brdf.diffuse * pointLight.color * pointLight.distanceAttenuation * 0.12 * ao;
                LIGHT_LOOP_END
            #endif

                // emissive blocks (lava, glowstone, torches): bright texels glow, or the LabPBR emission map
                float brightness = max(texel.r, max(texel.g, texel.b));
                float emission = max(emissionMap, input.lightEmission.z * smoothstep(0.45, 0.85, brightness));
                color += albedo * emission * 3.0;

                color = MixFog(color, input.fog);
                return half4(color, alpha);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma require 2darray
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 uv : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                float3 positionWS = TransformObjectToWorld(input.positionOS);
                positionWS += WindOffset(positionWS, input.tangent.w);
                float3 normalWS = TransformObjectToWorldNormal(input.normal.xyz);
            #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                float3 lightDirection = normalize(_LightPosition - positionWS);
            #else
                float3 lightDirection = _LightDirection;
            #endif
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirection));
            #if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #else
                positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #endif
            #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                // light sources don't shadow their own light: the stick of a torch right under the flame would throw
                // a big dark patch on the wall behind it (the light sits in the emitting block)
                if (input.normal.w > 0.0) positionCS = float4(0.0, 0.0, 0.0, 0.0);
            #endif
                output.positionCS = positionCS;
                output.uv = input.uv0.xyz;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                clip(SAMPLE_TEXTURE2D_ARRAY(_Textures, sampler_Textures, input.uv.xy, input.uv.z).a - _Cutoff);
                return 0;
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
                float3 positionWS = TransformObjectToWorld(input.positionOS);
                output.positionCS = TransformWorldToHClip(positionWS + WindOffset(positionWS, input.tangent.w));
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
}
