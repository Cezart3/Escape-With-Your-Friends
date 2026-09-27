// The one surface shader every model on the island wears: Kenney atlases, Quaternius painted
// textures, flat kit colours and the greybox palette alike (docs/ART-PLAN.md P6, V6).
//
// Why a shader and not a grade: two kits from two artists stop reading as two kits when they are
// lit the same way, and URP/Lit lights a swatch atlas and a painted bark texture very differently -
// a smooth gradient that shows every bake in the painted one and none in the swatch. Here light
// arrives in two bands, a lit side and a shade side with a narrow soft edge, the shade side takes
// a cool tint instead of going grey, and the painted detail can be turned down per kit until it
// sits at the swatches' level. Numbers are written per material by StyleLook.Apply from one table,
// so the whole look is one C# file to tune.
//
// Hand-written for the same reason as Water.shader: a .shader diffs in a pull request.
// Forward renderer only (every tier is Forward, RenderTuning), so no cluster light loop.
Shader "EWYF/Stylized"
{
    Properties
    {
        [MainTexture] _BaseMap ("Albedo", 2D) = "white" {}
        [MainColor] _BaseColor ("Colour", Color) = (1, 1, 1, 1)

        [Header(Kit harmony)]
        _Detail ("Painted detail kept", Range(0, 1)) = 1
        _Saturation ("Saturation", Range(0, 2)) = 1
        _Brightness ("Brightness", Range(0, 2)) = 1

        [Header(Light bands)]
        _RampCentre ("Terminator (N.L)", Range(-1, 1)) = 0.05
        _RampSoftness ("Terminator softness", Range(0.001, 0.5)) = 0.08
        _ShadowTint ("Shade side tint", Color) = (0.86, 0.9, 1.05, 1)
        _RimStrength ("Rim light", Range(0, 1)) = 0.2
        _Smoothness ("Smoothness (hard highlight above 0.3)", Range(0, 1)) = 0.1
        [HDR] _EmissionColor ("Emission (with _EMISSION)", Color) = (0, 0, 0, 1)

        [Header(Surface)]
        _Cutoff ("Alpha cutoff", Range(0, 1)) = 0.5
        [Toggle(_ALPHATEST_ON)] _AlphaClip ("Alpha clip", Float) = 0
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Lit"
            "IgnoreProjector" = "True"
        }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_BaseMap);    SAMPLER(sampler_BaseMap);

        // Every per-material number, in every pass, identical: the SRP Batcher's one condition.
        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor;
            half4 _ShadowTint;
            half4 _EmissionColor;
            half _Detail;
            half _Saturation;
            half _Brightness;
            half _RampCentre;
            half _RampSoftness;
            half _RimStrength;
            half _Smoothness;
            half _Cutoff;
            half _AlphaClip;
            half _Cull;
        CBUFFER_END

        half4 BaseSample(float2 uv)
        {
            return SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv) * _BaseColor;
        }

        void Clip(half alpha)
        {
            #if defined(_ALPHATEST_ON)
                clip(alpha - _Cutoff);
            #endif
        }
        ENDHLSL

        Pass
        {
            Name "StylizedForward"
            Tags { "LightMode" = "UniversalForward" }

            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma target 3.0

            #pragma shader_feature_local _ALPHATEST_ON
            #pragma shader_feature_local_fragment _DETAIL_SOFTEN
            #pragma shader_feature_local_fragment _EMISSION

            // Per-pixel lamps only: every tier renders them per pixel, and a per-vertex lamp would be
            // the smooth gradient this shader exists to remove.
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                half3 normalWS : TEXCOORD2;
                half fogFactor : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = position.positionCS;
                output.positionWS = position.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.fogFactor = ComputeFogFactor(position.positionCS.z);
                return output;
            }

            // Two bands with a soft edge. The same curve for the sun and every lamp, so a torch-lit
            // crate and a sunlit one are drawn by the same hand.
            half Band(half ndl)
            {
                return smoothstep(_RampCentre - _RampSoftness, _RampCentre + _RampSoftness, ndl);
            }

            half3 Albedo(float2 uv, out half alpha)
            {
                half4 fine = BaseSample(uv);
                alpha = fine.a;

                // Painted detail turned down by blending toward a blurred read of the same texture:
                // three mips up is the texture's broad colours without its brush strokes. Compiled in
                // only where StyleLook set _Detail below one (the nature kit), so nothing else pays
                // for the second fetch.
                half3 colour = fine.rgb;
                #if defined(_DETAIL_SOFTEN)
                    half3 broad = SAMPLE_TEXTURE2D_BIAS(_BaseMap, sampler_BaseMap, uv, 3).rgb * _BaseColor.rgb;
                    colour = lerp(broad, colour, _Detail);
                #endif

                half luma = dot(colour, half3(0.2126, 0.7152, 0.0722));
                return max(0, lerp(luma.xxx, colour, _Saturation)) * _Brightness;
            }

            half4 Fragment(Varyings input, FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                half alpha;
                half3 albedo = Albedo(input.uv, alpha);
                Clip(alpha);

                // Leaf cards and grass are drawn from both sides; the back face lights as its own front.
                half3 normal = normalize(input.normalWS);
                normal = IS_FRONT_VFACE(face, normal, -normal);
                half3 view = SafeNormalize(GetWorldSpaceViewDir(input.positionWS));

                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                Light sun = GetMainLight(shadowCoord);

                // Not banded: the attenuation already carries the light's shadow strength (the moon's
                // is 0.35) and URP's fade at the shadow distance, and a step would erase the first and
                // turn the second into a ring that follows the camera.
                half shadow = sun.shadowAttenuation * sun.distanceAttenuation;
                half lit = Band(dot(normal, sun.direction)) * shadow;

                half3 ambient = SampleSH(normal);
                half directOcclusion = 1;

                #if defined(_SCREEN_SPACE_OCCLUSION)
                    AmbientOcclusionFactor occlusion =
                        GetScreenSpaceAmbientOcclusion(GetNormalizedScreenSpaceUV(input.positionCS));
                    ambient *= occlusion.indirectAmbientOcclusion;
                    directOcclusion = occlusion.directAmbientOcclusion;
                #endif

                // The shade side is ambient only, tinted cool; the lit side adds the sun in full.
                half3 light = ambient * lerp(_ShadowTint.rgb, half3(1, 1, 1), lit) + sun.color * lit * directOcclusion;

                // Rim: a thin bright edge on the lit side, what makes a silhouette pop against the sea.
                half rim = pow(1 - saturate(dot(normal, view)), 4) * _RimStrength * lit;
                light += sun.color * rim;

                // Only metal and gold shine, and when they do it is one hard spot, not a gradient.
                half3 halfway = SafeNormalize(sun.direction + view);
                half glossy = saturate((_Smoothness - 0.3) * 4);
                half spot = smoothstep(0.5, 0.55, pow(saturate(dot(normal, halfway)), exp2(_Smoothness * 10 + 1)));
                half3 specular = sun.color * spot * glossy * lit;

                #if defined(_ADDITIONAL_LIGHTS)
                    uint count = GetAdditionalLightsCount();
                    for (uint i = 0u; i < count; ++i)
                    {
                        Light lamp = GetAdditionalLight(i, input.positionWS, half4(1, 1, 1, 1));
                        half lampLit = Band(dot(normal, lamp.direction))
                                       * lamp.distanceAttenuation * lamp.shadowAttenuation;
                        light += lamp.color * lampLit;
                    }
                #endif

                half3 colour = albedo * light + specular;

                // The campfire's flame (StationBuilder): it has to read at night.
                #if defined(_EMISSION)
                    colour += _EmissionColor.rgb;
                #endif
                colour = MixFog(colour, input.fogFactor);
                return half4(colour, 1);
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
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma target 3.0

            #pragma shader_feature_local _ALPHATEST_ON
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings Vertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

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

                output.positionCS = positionCS;
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                Clip(BaseSample(input.uv).a);
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
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma target 3.0

            #pragma shader_feature_local _ALPHATEST_ON
            #pragma multi_compile_instancing

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half Fragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                Clip(BaseSample(input.uv).a);
                return input.positionCS.z;
            }
            ENDHLSL
        }

        // What SSAO on the High tier reads (RenderTuning: Source = DepthNormals).
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma target 3.0

            #pragma shader_feature_local _ALPHATEST_ON
            #pragma multi_compile_instancing

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 Fragment(Varyings input, FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                Clip(BaseSample(input.uv).a);

                half3 normal = normalize(input.normalWS);
                return half4(IS_FRONT_VFACE(face, normal, -normal), 0);
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Lit"
}
