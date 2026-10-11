// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later
//
// The volumetric clouds at half resolution (CloudPass.cs blits this into a texture every frame): the ray march of
// WhiteFoxClouds.hlsl for the view direction of each texel. The sky shaders sample the texture (bilinear) instead of
// marching every screen pixel, a quarter of the work; the clouds are soft, so the lower resolution does not show.
// rgb: cloud light (premultiplied), a: transmittance of the sky behind.
Shader "Hidden/WhiteFox/CloudPass"
{
    SubShader
    {
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "WhiteFoxCommon.hlsl"
            #include "WhiteFoxClouds.hlsl"

            float4x4 _WF_CloudInverseViewProjection; // OpenGL convention (CloudPass.cs)
            float4 _WF_CloudCamera;                  // camera position (render space)
            float4 _WF_CloudTargetSize;              // texels

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                // Graphics.Blit draws a quad with positions 0..1: straight into clip space, upright in uv like a
                // render texture is sampled on every graphics API
                output.positionCS = float4(input.positionOS.xy * 2.0 - 1.0, 0.5, 1.0);
            #if UNITY_UV_STARTS_AT_TOP
                output.positionCS.y = -output.positionCS.y;
            #endif
                output.uv = input.positionOS.xy;
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                float4 far = mul(_WF_CloudInverseViewProjection, float4(input.uv * 2.0 - 1.0, 1.0, 1.0));
                float3 direction = normalize(far.xyz / far.w - _WF_CloudCamera.xyz);
                float transmittance;
                float3 light = Clouds(_WF_CloudCamera.xyz, direction, input.uv * _WF_CloudTargetSize.xy, transmittance);
                return float4(light, transmittance);
            }
            ENDHLSL
        }
    }
}
