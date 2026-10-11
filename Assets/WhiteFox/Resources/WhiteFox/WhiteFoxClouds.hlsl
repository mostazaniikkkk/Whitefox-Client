// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Volumetric clouds, shared by the realistic sky (Sky.shader) and the enhanced mode (EnhancedClouds.shader): a ray
// march through a cloud layer at y 192..300 with a tiling Perlin-Worley noise (shape, erosion and coverage), lit by
// the sun with self shadowing and a two lobe phase function, plus the sky as ambient light. Needs WhiteFoxCommon.hlsl.
#ifndef WHITEFOX_CLOUDS_INCLUDED
#define WHITEFOX_CLOUDS_INCLUDED

TEXTURE3D(_WF_CloudNoise);
SAMPLER(sampler_WF_CloudNoise);
float _WF_CloudSteps;      // 0: no volumetric clouds
float _WF_CloudLightSteps;
float _WF_CloudCoverage;   // 0..1
float4 _WF_CloudWind;      // xz offset of the wind (blocks)
#ifndef WHITEFOX_ORIGIN_OFFSET
#define WHITEFOX_ORIGIN_OFFSET
float4 _WF_OriginOffset;   // floating origin (x, z mod 4096)
#endif

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
    // far away the clouds are small on screen: fewer light samples
    if (t0 > 2500.0) lightSteps = min(lightSteps, 2);
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

// The half resolution cloud texture of CloudPass.cs (rgb light, a transmittance) and the matrix it was made with.
TEXTURE2D(_WF_CloudTexture);
SAMPLER(sampler_WF_CloudTexture);
float4x4 _WF_CloudViewProjection; // OpenGL convention
float _WF_CloudLowRes;            // 1 while CloudPass renders the clouds into _WF_CloudTexture

// The clouds in a view direction: from the half resolution texture, or marched here if there is none.
float3 CloudsAt(float3 origin, float3 direction, float2 pixel, out float transmittance)
{
    if (_WF_CloudLowRes > 0.5)
    {
        float4 clip = mul(_WF_CloudViewProjection, float4(origin + direction, 1.0));
        float2 uv = clip.xy / clip.w * 0.5 + 0.5;
        float4 clouds = SAMPLE_TEXTURE2D_LOD(_WF_CloudTexture, sampler_WF_CloudTexture, uv, 0);
        transmittance = clouds.a;
        return clouds.rgb;
    }
    return Clouds(origin, direction, pixel, transmittance);
}

// Shadows of the volumetric clouds on the ground: the cloud density where the ray towards the light crosses the lower
// part of the cloud layer (one cheap sample, no detail noise). 1 without volumetric clouds or with a low light.
float CloudShadow(float3 positionWS, float3 lightDirection)
{
    if (_WF_CloudSteps <= 0.0 || lightDirection.y <= 0.05) return 1.0;
    float t = (CloudBottom + 40.0 - positionWS.y) / lightDirection.y;
    if (t <= 0.0) return 1.0;
    float density = CloudDensity(positionWS + lightDirection * t, false);
    // thin cloud edges barely shade (like in the sky, where they are see through); some light always gets through
    return lerp(1.0, 0.4, smoothstep(0.1, 0.45, density));
}

#endif
