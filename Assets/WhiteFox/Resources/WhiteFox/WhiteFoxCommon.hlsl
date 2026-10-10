// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Shared by the realistic shaders: sky model (also used for ambient light and reflections, so no reflection probe
// has to be baked at runtime) and Minecraft light helpers. All globals are set by RealisticEnvironment.cs.
#ifndef WHITEFOX_COMMON_INCLUDED
#define WHITEFOX_COMMON_INCLUDED

float4 _WF_SunDirection;   // direction towards the sun (render space)
float4 _WF_SunColor;       // linear, includes intensity
float4 _WF_SkyZenith;      // linear
float4 _WF_SkyHorizon;     // linear
float4 _WF_BlockLightColor; // linear, includes intensity
float _WF_NightFactor;
float _WF_AmbientStrength;

float3 WF_SkyColor(float3 direction)
{
    direction = normalize(direction);
    float up = direction.y;
    float3 sky = lerp(_WF_SkyHorizon.rgb, _WF_SkyZenith.rgb, pow(saturate(up), 0.45));
    float3 ground = _WF_SkyHorizon.rgb * 0.3;
    if (up < 0.0) sky = lerp(_WF_SkyHorizon.rgb, ground, saturate(-up * 4.0));
    float sun = saturate(dot(direction, _WF_SunDirection.xyz));
    sky += _WF_SunColor.rgb * (pow(sun, 16.0) * 0.15 + pow(sun, 3.0) * 0.04) * saturate(1.2 - abs(up));
    return sky;
}

// cheap irradiance of the sky dome for a normal, plus light bounced from the sunlit ground
float3 WF_SkyAmbient(float3 normal)
{
    float3 zenith = lerp(_WF_SkyZenith.rgb, _WF_SkyHorizon.rgb, 0.45);
    float3 side = lerp(_WF_SkyHorizon.rgb, _WF_SkyZenith.rgb, 0.2) * 0.8;
    float3 bounce = _WF_SunColor.rgb * saturate(_WF_SunDirection.y) * 0.12;
    float3 down = _WF_SkyHorizon.rgb * 0.15 + bounce;
    float3 sky = normal.y >= 0.0 ? lerp(side, zenith, normal.y) : lerp(side, down, -normal.y);
    // real sky irradiance is far less saturated than the sky color seen directly
    sky = lerp(dot(sky, float3(0.2126, 0.7152, 0.0722)).xxx, sky, 0.55);
    return sky + bounce * (1.0 - abs(normal.y)) * 0.6;
}

// Minecraft light level (0..1) to a falloff that is close to physical but keeps level 15 at 1
float WF_BlockLight(float level)
{
    float curve = level / (4.0 - 3.0 * level);
    return curve * curve;
}

// gamma space vertex color (tint) to linear
float3 WF_Linear(float3 color)
{
#if defined(UNITY_COLORSPACE_GAMMA)
    return color;
#else
    return pow(max(color, 0.0), 2.2);
#endif
}

#endif
