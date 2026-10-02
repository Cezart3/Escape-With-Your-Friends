// The ground, lit like everything standing on it: the same two bands and cool shade side as
// EWYF/Stylized, through the same StylizedLighting.hlsl. On URP Terrain/Lit the sand under a banded
// rock was smoothly shaded, and the seam V6 set out to remove moved to where every model meets the
// ground.
//
// Deliberately small, for the Radeon 760M: exactly four layers in one pass (IslandSplat.LayerCount;
// no add pass, so a fifth layer is not drawn), one control fetch plus four albedo fetches, no normal
// maps, no height blend, no holes, no instancing (TerrainGenerator turns drawInstanced off). No
// basemap either: without a BaseMapShader dependency the far terrain would fall to a shader URP does
// not have, so TerrainGenerator pushes basemapDistance past the fog and this draws to the horizon.
//
// The terrain fills _Control, _Splat0..3 and their _ST through a property block; StyleLook writes
// the look numbers into the material.
Shader "EWYF/StylizedTerrain"
{
    Properties
    {
        [HideInInspector] _Control ("Control (RGBA)", 2D) = "red" {}
        [HideInInspector] _Splat0 ("Layer 0", 2D) = "grey" {}
        [HideInInspector] _Splat1 ("Layer 1", 2D) = "grey" {}
        [HideInInspector] _Splat2 ("Layer 2", 2D) = "grey" {}
        [HideInInspector] _Splat3 ("Layer 3", 2D) = "grey" {}

        [Header(Light bands)]
        _RampCentre ("Terminator (N.L)", Range(-1, 1)) = 0.05
        _RampSoftness ("Terminator softness", Range(0.001, 0.5)) = 0.08
        _ShadowTint ("Shade side tint", Color) = (0.86, 0.9, 1.05, 1)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Geometry-100"
            "RenderPipeline" = "UniversalPipeline"
            "TerrainCompatible" = "True"
        }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_Control);    SAMPLER(sampler_Control);
        TEXTURE2D(_Splat0);     SAMPLER(sampler_Splat0);
        TEXTURE2D(_Splat1);     SAMPLER(sampler_Splat1);
        TEXTURE2D(_Splat2);     SAMPLER(sampler_Splat2);
        TEXTURE2D(_Splat3);     SAMPLER(sampler_Splat3);

        CBUFFER_START(UnityPerMaterial)
            float4 _Control_TexelSize;
            float4 _Splat0_ST;
            float4 _Splat1_ST;
            float4 _Splat2_ST;
            float4 _Splat3_ST;
            half4 _ShadowTint;
            half _RampCentre;
            half _RampSoftness;
        CBUFFER_END

        // The ground has no rim and no highlight; StylizedLighting reads these, so they are constants.
        static const half _RimStrength = 0;
        static const half _Smoothness = 0;

        struct Attributes
        {
            float4 positionOS : POSITION;
            float3 normalOS : NORMAL;
            float2 uv : TEXCOORD0;
        };
        ENDHLSL

        Pass
        {
            Name "TerrainForward"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma target 3.0

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "StylizedLighting.hlsl"
            #include "StylizedFog.hlsl"

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                half3 normalWS : TEXCOORD2;
                half fogFactor : TEXCOORD3;
            };

            Varyings Vertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = position.positionCS;
                output.positionWS = position.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.uv = input.uv;
                output.fogFactor = ComputeFogFactor(position.positionCS.z);
                return output;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                // Texel centres, the way URP's terrain reads its control map: without the remap the
                // layer borders drift half a texel and shimmer as the camera moves.
                float2 controlUV = (input.uv * (_Control_TexelSize.zw - 1) + 0.5) * _Control_TexelSize.xy;
                half4 weights = SAMPLE_TEXTURE2D(_Control, sampler_Control, controlUV);
                weights /= max(dot(weights, half4(1, 1, 1, 1)), 0.001);

                half3 albedo = weights.r * SAMPLE_TEXTURE2D(_Splat0, sampler_Splat0, TRANSFORM_TEX(input.uv, _Splat0)).rgb
                             + weights.g * SAMPLE_TEXTURE2D(_Splat1, sampler_Splat1, TRANSFORM_TEX(input.uv, _Splat1)).rgb
                             + weights.b * SAMPLE_TEXTURE2D(_Splat2, sampler_Splat2, TRANSFORM_TEX(input.uv, _Splat2)).rgb
                             + weights.a * SAMPLE_TEXTURE2D(_Splat3, sampler_Splat3, TRANSFORM_TEX(input.uv, _Splat3)).rgb;

                half3 colour = StylizedLighting(albedo, input.positionWS, normalize(input.normalWS), input.positionCS);
                return half4(StylizedFog(colour, input.fogFactor, input.positionWS), 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma target 3.0

            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            float4 Vertex(Attributes input) : SV_POSITION
            {
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);

                #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                    float3 toLight = normalize(_LightPosition - positionWS);
                #else
                    float3 toLight = _LightDirection;
                #endif

                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, toLight));
                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                return positionCS;
            }

            half4 Fragment() : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma target 3.0

            float4 Vertex(Attributes input) : SV_POSITION
            {
                return TransformObjectToHClip(input.positionOS.xyz);
            }

            half Fragment(float4 positionCS : SV_POSITION) : SV_Target
            {
                return positionCS.z;
            }
            ENDHLSL
        }

        // What SSAO on the High tier reads (RenderTuning: Source = DepthNormals).
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On

            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma target 3.0

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half3 normalWS : TEXCOORD0;
            };

            Varyings Vertex(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return output;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                return half4(normalize(input.normalWS), 0);
            }
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
