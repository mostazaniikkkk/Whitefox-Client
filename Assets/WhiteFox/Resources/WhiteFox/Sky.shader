// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Procedural skybox for the realistic mode: gradient sky, sun and moon disks and stars at night.
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
                return half4(color, 1.0);
            }
            ENDHLSL
        }
    }
}
