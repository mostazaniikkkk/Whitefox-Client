// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Procedural skybox for the realistic mode: scattering sky, sun and moon disks, stars at night, and volumetric
// clouds: a ray march through a cloud layer at y 192..300 with a tiling Perlin-Worley noise (shape, erosion and
// coverage), lit by the sun with self shadowing (a few steps towards it, Beer-Lambert) and a two lobe phase
// function, plus the sky as ambient light (the approach of Iris shader packs like Arc Shader, MIT, and of Nubis).
Shader "WhiteFox/Sky"
{
    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" }
        Cull Off
        ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "WhiteFoxCommon.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 direction : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.direction = input.positionOS.xyz;
                return output;
            }

            TEXTURE3D(_WF_CloudNoise);
            SAMPLER(sampler_WF_CloudNoise);
            float _WF_CloudSteps;      // 0: no volumetric clouds
            float _WF_CloudLightSteps;
            float _WF_CloudCoverage;   // 0..1
            float4 _WF_CloudWind;      // xz offset of the wind (blocks)
            float4 _WF_OriginOffset;   // floating origin (x, z mod 4096)

            static const float CloudBottom = 192.0;
            static const float CloudTop = 300.0;

            float RemapClouds(float value, float fromMin, float fromMax)
            {
                return saturate((value - fromMin) / max(fromMax - fromMin, 1e-4));
            }

            // cloud density at a world position (render space): coverage map, billowy shape, eroded edges
            float CloudDensity(float3 position, bool detail)
            {
                float height = saturate((position.y - CloudBottom) / (CloudTop - CloudBottom));
                float2 xz = position.xz + _WF_OriginOffset.xy + _WF_CloudWind.xz;
                // periods 2048 / 512 / 128 blocks divide the 4096 of the floating origin: no seams when it moves
                float coverage = SAMPLE_TEXTURE3D_LOD(_WF_CloudNoise, sampler_WF_CloudNoise, float3(xz.x / 2048.0, 0.37, xz.y / 2048.0), 0).b;
                coverage = RemapClouds(coverage, 1.0 - _WF_CloudCoverage - 0.15, 1.0 - _WF_CloudCoverage + 0.35);
                if (coverage <= 0.0) return 0.0;
                float shape = SAMPLE_TEXTURE3D_LOD(_WF_CloudNoise, sampler_WF_CloudNoise, float3(xz.x, position.y * 1.6, xz.y) / 512.0, 0).r;
                // flat bottoms, rounded tops
                float profile = saturate(height * 6.0) * saturate((1.0 - height) * 2.2);
                float density = RemapClouds(shape * profile, 1.0 - coverage, 1.0);
                if (detail && density > 0.0)
                {
                    float erosion = SAMPLE_TEXTURE3D_LOD(_WF_CloudNoise, sampler_WF_CloudNoise, float3(xz.x, position.y, xz.y) / 128.0, 0).g;
                    density = saturate(density - (1.0 - erosion) * 0.35 * (1.0 - density));
                }
                return density;
            }

            // the clouds in front of the sky: color (premultiplied) and the transmittance left for the sky behind
            float3 Clouds(float3 origin, float3 direction, float2 pixel, out float transmittance)
            {
                transmittance = 1.0;
                int steps = (int)_WF_CloudSteps;
                if (steps <= 0) return 0;
                float t0, t1;
                if (origin.y < CloudBottom)
                {
                    if (direction.y <= 0.01) return 0;
                    t0 = (CloudBottom - origin.y) / direction.y;
                    t1 = (CloudTop - origin.y) / direction.y;
                }
                else if (origin.y > CloudTop)
                {
                    if (direction.y >= -0.01) return 0;
                    t0 = (CloudTop - origin.y) / direction.y;
                    t1 = (CloudBottom - origin.y) / direction.y;
                }
                else
                {
                    t0 = 0.0;
                    t1 = direction.y > 0.0 ? (CloudTop - origin.y) / direction.y : direction.y < 0.0 ? (CloudBottom - origin.y) / direction.y : 2000.0;
                }
                if (t0 > 6000.0) return 0;
                t1 = min(t1, t0 + 1200.0);
                float stepLength = (t1 - t0) / steps;
                float noise = frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
                float3 sunDirection = _WF_SunDirection.xyz;
                float cosine = dot(direction, sunDirection);
                float phase = lerp(WF_Phase(cosine, -0.2), WF_Phase(cosine, 0.65), 0.6);
                float3 sunlight = _WF_SunColor.rgb;
                float3 color = 0;
                int lightSteps = (int)_WF_CloudLightSteps;
                [loop] for (int i = 0; i < steps; i++)
                {
                    float3 position = origin + direction * (t0 + stepLength * (i + noise));
                    float density = CloudDensity(position, true);
                    if (density <= 0.0) continue;
                    // light reaching this point: density towards the sun
                    float towardsSun = 0.0;
                    [loop] for (int j = 1; j <= lightSteps; j++)
                    {
                        towardsSun += CloudDensity(position + sunDirection * (14.0 * j), false);
                    }
                    float sunTransmittance = exp(-towardsSun * 14.0 * 0.12);
                    // the "powder" darkening of the sunlit edges seen from the side (Nubis)
                    float powder = 1.0 - exp(-density * 6.0);
                    float height = saturate((position.y - CloudBottom) / (CloudTop - CloudBottom));
                    float3 ambient = lerp(_WF_SkyHorizon.rgb, _WF_SkyZenith.rgb, height) * (0.35 + 0.65 * height);
                    float3 light = sunlight * sunTransmittance * phase * powder * 6.0 + ambient;
                    float extinction = density * 0.085;
                    float stepTransmittance = exp(-extinction * stepLength);
                    color += transmittance * (1.0 - stepTransmittance) * light;
                    transmittance *= stepTransmittance;
                    if (transmittance < 0.02) break;
                }
                // far clouds fade into the haze of the horizon
                float fade = exp(-t0 / 4500.0);
                transmittance = lerp(1.0, transmittance, fade);
                return color * fade;
            }

            float Hash(float3 p)
            {
                p = frac(p * 0.3183099 + 0.1);
                p *= 17.0;
                return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float3 direction = normalize(input.direction);
                float3 color = WF_SkyColor(direction);

                float sun = dot(direction, _WF_SunDirection.xyz);
                // in rain the clouds hide the sun, the moon and the stars
                float clear = 1.0 - _WF_Overcast;
                color += _WF_SunColor.rgb * smoothstep(0.9988, 0.9992, sun) * 12.0 * clear;

                float moon = dot(direction, -_WF_SunDirection.xyz);
                color += float3(0.75, 0.8, 0.95) * smoothstep(0.9991, 0.9994, moon) * (0.2 + _WF_NightFactor * 1.5) * clear;

                if (_WF_NightFactor > 0.0 && direction.y > 0.0)
                {
                    float3 cell = floor(direction * 300.0);
                    float star = step(0.9975, Hash(cell));
                    color += star * _WF_NightFactor * saturate(direction.y * 3.0) * 1.5 * clear;
                }
                float transmittance;
                float3 clouds = Clouds(_WorldSpaceCameraPos, direction, input.positionCS.xy, transmittance);
                return half4(color * transmittance + clouds, 1.0);
            }
            ENDHLSL
        }
    }
}
