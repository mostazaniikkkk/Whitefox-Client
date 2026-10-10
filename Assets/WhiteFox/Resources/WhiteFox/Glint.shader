// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Enchantment glint (vanilla glint / entity_glint render types): the scrolling purple glint texture added on top of
// an item or armor. The "Gui" pass is used by Graphics.DrawTexture over an inventory icon (masked by the icon's
// alpha, glint coordinates from the icon uv; blitted into an overlay texture); the forward pass is an extra material on held items, dropped items and
// armor, with glint coordinates from the mesh uv (every mesh layout has it in TEXCOORD0.xy).
Shader "WhiteFox/Glint"
{
    Properties
    {
        _MainTex ("Icon", 2D) = "white" {}
        _Glint ("Glint", 2D) = "white" {}
        _Scale ("Glint scale", Float) = 2
    }

    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

    TEXTURE2D(_MainTex);
    SAMPLER(sampler_MainTex);
    TEXTURE2D(_Glint);
    SAMPLER(sampler_Glint);
    float _Scale;

    // vanilla RenderStateShard.setupGlintTexturing: scroll u over 110 s and v over 30 s, rotated by 10 degrees
    float2 GlintUV(float2 uv)
    {
        float time = _Time.y;
        float2 scroll = float2(-frac(time / 110.0) * 8.0, frac(time / 30.0) * 8.0);
        const float angle = 0.17453292;
        float2 rotated = float2(uv.x * cos(angle) - uv.y * sin(angle), uv.x * sin(angle) + uv.y * cos(angle));
        return rotated * _Scale + scroll;
    }
    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Transparent" "Queue" = "Transparent+20" }

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcColor One
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            struct Attributes
            {
                float3 positionOS : POSITION;
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
                output.positionCS = TransformObjectToHClip(input.positionOS);
                output.uv = input.uv;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                // only where the item / armor texture is opaque (sprite quads and armor layers have holes)
                clip(SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv).a - 0.1);
                half3 glint = SAMPLE_TEXTURE2D(_Glint, sampler_Glint, GlintUV(input.uv)).rgb;
                // vanilla glint strength 0.75 (accessibility option default); "src color, one" adds the square
                return half4(glint * 0.75, 1);
            }
            ENDHLSL
        }

        // IMGUI (Graphics.DrawTexture with pass 1); its own light mode keeps the pipeline from drawing it
        Pass
        {
            Name "Gui"
            Tags { "LightMode" = "WhiteFoxGui" }
            Blend Off
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            struct Attributes
            {
                float3 positionOS : POSITION;
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
                output.positionCS = TransformObjectToHClip(input.positionOS);
                output.uv = input.uv;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                // the overlay is drawn with alpha blending: dst * (1 - a) + rgb * a approximates vanilla's additive
                // "src color, one" blending (dst + glint * glint)
                half mask = step(0.1, SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv).a);
                half3 glint = SAMPLE_TEXTURE2D(_Glint, sampler_Glint, GlintUV(input.uv)).rgb;
                half3 add = glint * glint;
                half alpha = saturate(max(add.r, max(add.g, add.b))) * mask;
                return half4(alpha > 0.001 ? add / max(alpha, 0.001) : half3(0, 0, 0), alpha);
            }
            ENDHLSL
        }
    }
}
