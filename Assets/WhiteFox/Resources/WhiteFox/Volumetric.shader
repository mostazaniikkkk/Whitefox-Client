// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Volumetric sun light ("god rays"), the way Iris shader packs like Arc Shader (MIT) do it: for every pixel a short
// ray march from the camera to the scene depth samples the sun shadow map; air in sunlight scatters light towards the
// camera (Henyey-Greenstein phase, strongest looking towards the sun), air in shadow does not, so light shafts appear
// between trees and through openings. Ground mist (denser low down, in the morning and in rain) also scatters the
// sun and the sky and hides what is behind it. Blended over the image after the transparent objects.
// Drawn by a full screen triangle in front of the camera (no renderer feature needed).
Shader "WhiteFox/Volumetric"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Transparent" "Queue" = "Transparent+100" }

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }
            Blend One SrcAlpha // scene * transmittance + scattered light
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            float _WF_VolumetricSteps;
            float _WF_VolumetricStrength;
            float _WF_MistDensity;     // per block at sea level, 0 off
            float _WF_MistSunlight;    // how much sun reaches the mist (outdoors, weather, night)
            float4 _WF_MistColor;      // ambient light of the mist (the sky, linear)
            float4 _WF_SunColor;

            struct Attributes
            {
                float3 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                // the mesh is a triangle in clip space covering the screen
                output.positionCS = float4(input.positionOS.xy, UNITY_NEAR_CLIP_VALUE, 1.0);
                return output;
            }

            float Phase(float cosine, float g)
            {
                float g2 = g * g;
                return (1.0 - g2) / (4.0 * PI * pow(max(1.0 + g2 - 2.0 * g * cosine, 1e-4), 1.5));
            }

            // ground mist: dense in the low valleys and over water (around sea level), thinning out with height
            float MistDensity(float y)
            {
                return _WF_MistDensity * exp(-max(y - 60.0, 0.0) / 18.0);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                int steps = (int)_WF_VolumetricSteps;
                if (steps <= 0 || (_WF_VolumetricStrength <= 0.0 && _WF_MistDensity <= 0.0)) return half4(0, 0, 0, 1);
                float2 uv = input.positionCS.xy / _ScaledScreenParams.xy;
                float depth = SampleSceneDepth(uv);
            #if !UNITY_REVERSED_Z
                depth = lerp(UNITY_NEAR_CLIP_VALUE, 1, depth);
            #endif
                float3 target = ComputeWorldSpacePosition(uv, depth, UNITY_MATRIX_I_VP);
                float3 origin = _WorldSpaceCameraPos;
                float3 ray = target - origin;
                float length = min(sqrt(dot(ray, ray)), 160.0);
                float3 direction = normalize(ray);
                // interleaved gradient noise hides the steps (vanilla shader packs use the same trick)
                float noise = frac(52.9829189 * frac(dot(input.positionCS.xy, float2(0.06711056, 0.00583715))));
                Light sun = GetMainLight();
                float cosine = dot(direction, sun.direction);
                float phase = Phase(cosine, 0.75) * 0.6 + Phase(cosine, 0.0) * 0.4;
                float3 sunlight = _WF_SunColor.rgb * phase;
                float transmittance = 1.0;
                float3 light = 0;
                float previous = 0.0;
                [loop] for (int i = 0; i < steps; i++)
                {
                    // more samples close to the camera, where the shafts are sharp
                    float f = (i + noise) / steps;
                    float t = length * f * f;
                    float dt = t - previous;
                    previous = t;
                    float3 position = origin + direction * t;
                    float shadow = MainLightRealtimeShadow(TransformWorldToShadowCoord(position));
                    float mist = MistDensity(position.y);
                    // thin air only scatters (god rays); the mist also absorbs what is behind it
                    float3 scattered = sunlight * shadow * (0.012 * _WF_VolumetricStrength + mist * _WF_MistSunlight) + _WF_MistColor.rgb * mist;
                    light += transmittance * scattered * dt;
                    transmittance *= exp(-mist * dt);
                }
                return half4(light, transmittance);
            }
            ENDHLSL
        }
    }
}
