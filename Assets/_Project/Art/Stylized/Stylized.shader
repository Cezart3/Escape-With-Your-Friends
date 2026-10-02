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

        [Header(Wind)]
        [Toggle(_WIND)] _Wind ("Sways in the wind", Float) = 0
        _WindSway ("Sway (bend by height above the pivot)", Range(0, 8)) = 1
        _WindFlutter ("Leaf flutter", Range(0, 1)) = 0
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
            half _Wind;
            half _WindSway;
            half _WindFlutter;
        CBUFFER_END

        // Set every frame by Wind.cs, for every material at once (#244). xy: direction on the ground,
        // z: strength, w: gust 0-1. _WindDetail is 0 on the lowest grass tier: trunks still sway,
        // leaves stop fluttering.
        float4 _WindParams;
        float _WindDetail;

        // The wind, in world space, so all four passes move a vertex the same way and the shadow
        // follows the leaf. Height above the model's own pivot is the mask: a trunk's foot stays put
        // and its crown moves most, without anybody painting vertex colours. The phase comes from
        // where the tree stands, so a grove does not sway in step.
        float3 ApplyWind(float3 positionWS)
        {
            #if defined(_WIND)
                float3 origin = GetObjectToWorldMatrix()._m03_m13_m23;
                float h = max(0, positionWS.y - origin.y);
                float phase = dot(origin.xz, float2(0.37, 0.21));
                float t = _Time.y;

                float strength = _WindParams.z;
                float gust = _WindParams.w;
                float sway = (sin(t * 1.3 + phase) * 0.6 + sin(t * 2.3 + phase * 1.7) * 0.25 + gust * 0.6) * strength;
                positionWS.xz += _WindParams.xy * sway * (h * h * 0.004 * _WindSway);

                float flutter = sin(t * 9.0 + dot(positionWS, float3(1.7, 2.3, 1.1)))
                              * 0.05 * _WindFlutter * _WindDetail * strength * (1 + gust) * saturate(h * 0.3);
                positionWS += float3(flutter, flutter * 0.6, -flutter);
            #endif
            return positionWS;
        }

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
            #pragma shader_feature_local_vertex _WIND
            #pragma shader_feature_local_fragment _DETAIL_SOFTEN
            #pragma shader_feature_local_fragment _EMISSION

            // Per-pixel lamps only: every tier renders them per pixel, and a per-vertex lamp would be
            // the smooth gradient this shader exists to remove.
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "StylizedLighting.hlsl"
            #include "StylizedFog.hlsl"

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

                float3 positionWS = ApplyWind(TransformObjectToWorld(input.positionOS.xyz));
                output.positionCS = TransformWorldToHClip(positionWS);
                output.positionWS = positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.fogFactor = ComputeFogFactor(output.positionCS.z);
                return output;
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

                // A canopy is lit as one mass from above, not card by card: a leaf card's own normal
                // points anywhere, and half of them banding into shade reads as black speckle.
                #if defined(_ALPHATEST_ON)
                    normal = normalize(lerp(normal, half3(0, 1, 0), 0.6));
                #endif

                half3 colour = StylizedLighting(albedo, input.positionWS, normal, input.positionCS);

                // The campfire's flame (StationBuilder): it has to read at night.
                #if defined(_EMISSION)
                    colour += _EmissionColor.rgb;
                #endif
                colour = StylizedFog(colour, input.fogFactor, input.positionWS);
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
            #pragma shader_feature_local_vertex _WIND
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

                float3 positionWS = ApplyWind(TransformObjectToWorld(input.positionOS.xyz));
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
            #pragma shader_feature_local_vertex _WIND
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
                output.positionCS = TransformWorldToHClip(ApplyWind(TransformObjectToWorld(input.positionOS.xyz)));
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
            #pragma shader_feature_local_vertex _WIND
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
                output.positionCS = TransformWorldToHClip(ApplyWind(TransformObjectToWorld(input.positionOS.xyz)));
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
