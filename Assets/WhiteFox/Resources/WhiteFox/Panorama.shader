// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Title screen panorama: the six vanilla panorama images (textures/gui/title/background/panorama_0..5) on a cube
// around the camera, like vanilla's RotatingCubeMap. 0 front, 1 right, 2 back, 3 left, 4 up, 5 down.
Shader "WhiteFox/Panorama"
{
    Properties
    {
        _Faces ("Panorama faces", 2DArray) = "" {}
        _Brightness ("Brightness", Float) = 1
    }

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
            #pragma require 2darray
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D_ARRAY(_Faces);
            SAMPLER(sampler_Faces);
            float _Brightness;

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

            // uv on the cube face with the given forward/right/up axes
            float2 FaceUV(float3 d, float3 forward, float3 right, float3 up)
            {
                float depth = dot(d, forward);
                return float2(dot(d, right), dot(d, up)) / depth * 0.5 + 0.5;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float3 d = normalize(input.direction);
                float3 a = abs(d);
                float2 uv;
                float face;
                if (a.y >= a.x && a.y >= a.z)
                {
                    if (d.y > 0) { face = 4; uv = FaceUV(d, float3(0, 1, 0), float3(1, 0, 0), float3(0, 0, -1)); }
                    else { face = 5; uv = FaceUV(d, float3(0, -1, 0), float3(1, 0, 0), float3(0, 0, 1)); }
                }
                else if (a.x >= a.z)
                {
                    if (d.x > 0) { face = 1; uv = FaceUV(d, float3(1, 0, 0), float3(0, 0, -1), float3(0, 1, 0)); }
                    else { face = 3; uv = FaceUV(d, float3(-1, 0, 0), float3(0, 0, 1), float3(0, 1, 0)); }
                }
                else
                {
                    if (d.z > 0) { face = 0; uv = FaceUV(d, float3(0, 0, 1), float3(1, 0, 0), float3(0, 1, 0)); }
                    else { face = 2; uv = FaceUV(d, float3(0, 0, -1), float3(-1, 0, 0), float3(0, 1, 0)); }
                }
                half4 color = SAMPLE_TEXTURE2D_ARRAY_LOD(_Faces, sampler_Faces, uv, face, 0);
                return half4(color.rgb * _Brightness, 1);
            }
            ENDHLSL
        }
    }
}
