// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Leaves and plants swaying in the wind (enhanced and realistic modes; the classic mode sets no wind). The mesher
// stores the sway weight in the magnitude of the tangent sign (0.05 + weight * 0.4): 0 at the roots of a plant, 1 at
// its top, 2 at the top of a tall plant, 0.5 for leaves; |w| = 1 is a block that doesn't move. The offset only
// depends on the world position, so corners shared by neighbouring blocks stay joined.
#ifndef WHITEFOX_WIND_INCLUDED
#define WHITEFOX_WIND_INCLUDED

#ifndef WHITEFOX_ORIGIN_OFFSET
#define WHITEFOX_ORIGIN_OFFSET
float4 _WF_OriginOffset;  // floating origin (x, z mod 4096)
#endif
float _WF_WindStrength;   // 0: no wind (stronger in rain and storms)

float3 WindOffset(float3 positionWS, float tangentW)
{
    float magnitude = abs(tangentW);
    if (magnitude >= 0.95 || _WF_WindStrength <= 0.0) return 0;
    float weight = max(magnitude - 0.05, 0.0) / 0.4;
    float2 p = positionWS.xz + _WF_OriginOffset.xy;
    float t = _Time.y;
    // a slow gust travelling along the wind (east), a sway and a faster flutter
    float gust = sin(t * 1.3 - p.x * 0.18 + p.y * 0.07) * 0.5 + 0.5;
    float sway = sin(t * 2.1 + p.x * 0.55 + p.y * 0.37 + positionWS.y * 0.3) * 0.6 + sin(t * 3.7 + p.x * 0.9 - p.y * 0.6) * 0.25;
    float flutter = sin(t * 5.3 + p.x * 1.7 + p.y * 1.3 + positionWS.y * 1.9);
    float3 offset;
    offset.x = (0.35 + 0.65 * gust) * (0.6 + sway) * 0.07;
    offset.z = (sin(t * 1.7 + p.y * 0.5 + p.x * 0.21) * 0.5 + flutter * 0.2) * 0.05;
    offset.y = flutter * 0.012;
    return offset * weight * _WF_WindStrength;
}

#endif
