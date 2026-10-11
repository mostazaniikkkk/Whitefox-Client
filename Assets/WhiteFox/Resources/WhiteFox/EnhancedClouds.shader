// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Volumetric clouds of the enhanced mode (WhiteFoxClouds.hlsl) over the classic sky: a full screen triangle at the
// far plane, so the depth test leaves only the pixels where the sky shows. The classic image has no tonemapping, so
// the light of the clouds is compressed into the displayable range here. Drawn after the opaque blocks.
Shader "WhiteFox/EnhancedClouds"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Transparent" "Queue" = "AlphaTest+60" }

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }
            Blend One SrcAlpha // sky * transmittance + cloud light
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "WhiteFoxCommon.hlsl"
            #include "WhiteFoxClouds.hlsl"

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
                // the mesh is a triangle in clip space covering the screen, at the far plane
                output.positionCS = float4(input.positionOS.xy, UNITY_RAW_FAR_CLIP_VALUE, 1.0);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.positionCS.xy / _ScaledScreenParams.xy;
                float depth = UNITY_RAW_FAR_CLIP_VALUE;
            #if !UNITY_REVERSED_Z
                depth = lerp(UNITY_NEAR_CLIP_VALUE, 1, depth);
            #endif
                float3 target = ComputeWorldSpacePosition(uv, depth, UNITY_MATRIX_I_VP);
                float3 direction = normalize(target - _WorldSpaceCameraPos);
                float transmittance;
                float3 light = CloudsAt(_WorldSpaceCameraPos, direction, input.positionCS.xy, transmittance);
                // soft shoulder: bright sunlit tops stay below white instead of clipping
                float3 color = 1.0 - exp(-light * 1.6);
                return half4(color, transmittance);
            }
            ENDHLSL
        }
    }
}
