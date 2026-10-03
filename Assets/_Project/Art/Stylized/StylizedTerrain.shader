// The ground, lit like everything standing on it: the same two bands and cool shade side as
// EWYF/Stylized, through the same StylizedLighting.hlsl. On URP Terrain/Lit the sand under a banded
// rock was smoothly shaded, and the seam V6 set out to remove moved to where every model meets the
// ground.
//
// Deliberately small, for the Radeon 760M: exactly four layers in one pass (IslandSplat.LayerCount;
// no add pass, so a fifth layer is not drawn), one control fetch plus six albedo fetches (sand and
// grass twice, at two scales), no normal maps, no height blend, no holes, no instancing
// (TerrainGenerator turns drawInstanced off).
//
// What no tile can carry is done in arithmetic (#245): a tint that wanders over forty metres, the
// grass yellowing in patches, slopes going to dirt and then to the rock layer whatever the splat
// says, the sand darkening and catching the sun in a band just above the waterline, and a lace
// of foam running up the beach on the water's clock. No
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

        [Header(Ground variation)]
        _MacroScale ("Variation size (m)", Float) = 40
        _MacroStrength ("Variation brightness +-", Range(0, 0.4)) = 0.12
        _FarScale ("Second sample scale (sand, grass)", Range(0.1, 0.9)) = 0.29
        _DrySpread ("Dry grass share", Range(0, 1)) = 0.35
        _SlopeDirt ("Slope to dirt (1 - normal.y)", Range(0, 1)) = 0.12
        _SlopeRock ("Slope to rock (1 - normal.y)", Range(0, 1)) = 0.3

        [Header(Wet sand)]
        _WetHeight ("Wet band above the sea (m)", Float) = 0.35
        _WetDarken ("Wet albedo", Color) = (0.6, 0.6, 0.66, 1)
        _WetGloss ("Wet sheen", Range(0, 1)) = 0.35
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
            float _MacroScale;
            half _MacroStrength;
            half _FarScale;
            half _DrySpread;
            half _SlopeDirt;
            half _SlopeRock;
            half _WetHeight;
            half4 _WetDarken;
            half _WetGloss;
        CBUFFER_END

        // IslandShape.SeaLevel: the wet band is measured from it.
        static const float SeaLevel = 0;
        // Water.shader's _FoamColor, so the lace on the sand is the same white as the foam on the sea.
        static const half3 FoamColour = half3(0.92, 0.96, 0.96);
        // WaterSurface's clock (a global), which the waves and the sea's foam run on.
        float _WaterTime;

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

            // Value noise in [0, 1] on a unit grid, from a hash: no texture, so it never tiles.
            float Hash(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float ValueNoise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                f = f * f * (3 - 2 * f);
                return lerp(lerp(Hash(i), Hash(i + float2(1, 0)), f.x),
                            lerp(Hash(i + float2(0, 1)), Hash(i + 1), f.x), f.y);
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                // Texel centres, the way URP's terrain reads its control map: without the remap the
                // layer borders drift half a texel and shimmer as the camera moves.
                float2 controlUV = (input.uv * (_Control_TexelSize.zw - 1) + 0.5) * _Control_TexelSize.xy;
                half4 weights = SAMPLE_TEXTURE2D(_Control, sampler_Control, controlUV);
                weights /= max(dot(weights, half4(1, 1, 1, 1)), 0.001);

                // Two octaves over the island: the slow tint and where the grass dries.
                float2 xz = input.positionWS.xz / _MacroScale;
                half macro = ValueNoise(xz) * 0.65 + ValueNoise(xz * 2.7 + 17.3) * 0.35;

                // Sand and grass cover most of the island, so they are read twice, the second time
                // larger, and the two mixed by the macro noise: the repeat every seven metres has no
                // grid left to show.
                float2 uv0 = TRANSFORM_TEX(input.uv, _Splat0);
                float2 uv1 = TRANSFORM_TEX(input.uv, _Splat1);
                half3 sand = lerp(SAMPLE_TEXTURE2D(_Splat0, sampler_Splat0, uv0).rgb,
                                  SAMPLE_TEXTURE2D(_Splat0, sampler_Splat0, uv0 * _FarScale + 0.37).rgb, 0.25 + 0.5 * macro);
                half3 grass = lerp(SAMPLE_TEXTURE2D(_Splat1, sampler_Splat1, uv1).rgb,
                                   SAMPLE_TEXTURE2D(_Splat1, sampler_Splat1, uv1 * _FarScale + 0.61).rgb, 0.25 + 0.5 * macro);
                half3 rock = SAMPLE_TEXTURE2D(_Splat2, sampler_Splat2, TRANSFORM_TEX(input.uv, _Splat2)).rgb;
                half3 dirt = SAMPLE_TEXTURE2D(_Splat3, sampler_Splat3, TRANSFORM_TEX(input.uv, _Splat3)).rgb;

                // Dry patches: the grass yellows where the macro noise is high.
                half dry = smoothstep(1 - _DrySpread, 1.15 - _DrySpread * 0.5, macro);
                grass = lerp(grass, grass * half3(1.35, 1.12, 0.62), dry * 0.7);

                half3 albedo = weights.r * sand + weights.g * grass + weights.b * rock + weights.a * dirt;

                // Slopes: grass and sand wear to dirt, then everything steep is rock. The noise
                // wobbles the line so a hillside does not have a contour drawn round it.
                half3 normal = normalize(input.normalWS);
                half steep = 1 - normal.y + (macro - 0.5) * 0.08;
                half toRock = smoothstep(_SlopeRock, _SlopeRock + 0.1, steep);
                half toDirt = smoothstep(_SlopeDirt, _SlopeDirt + 0.08, steep) * (weights.r + weights.g) * (1 - toRock);
                albedo = lerp(albedo, dirt, toDirt * 0.8);
                albedo = lerp(albedo, rock, toRock);

                albedo *= 1 + (macro - 0.5) * 2 * _MacroStrength;

                // Wet sand: a band from just under the waterline up _WetHeight, darker, cooler and
                // catching the sun. The beaches are flat, so a third of a metre is metres of sand, and
                // the band ends under the water too: the sea tints what it covers, and darkening that
                // as well turned every shallow sandbar seen through clear water a muddy grey.
                half above = input.positionWS.y - SeaLevel + (macro - 0.5) * 0.08;
                half wet = (1 - smoothstep(_WetHeight * 0.25, _WetHeight, above)) * smoothstep(-0.6, -0.15, above);
                albedo *= lerp(half3(1, 1, 1), _WetDarken.rgb, wet);

                // The swash: a lace of foam on the sand that runs up and back, churned by the same
                // two crossed waves the sea's foam band is (Water.shader), so the two meet as one.
                float2 world = input.positionWS.xz;
                half churn = sin(dot(world, float2(0.42, 0.31)) - _WaterTime * 1.7)
                           * sin(dot(world, float2(-0.27, 0.36)) + _WaterTime * 1.1);
                half reach = 0.06 + 0.05 * sin(_WaterTime * 0.6 + dot(world, float2(0.05, 0.04)));
                half lace = (1 - smoothstep(0.0, 0.03, abs(input.positionWS.y - SeaLevel - reach)))
                          * saturate(0.45 + 0.6 * churn)
                          // Close up only: from a summit, every tide pool's edge drew a white contour.
                          * saturate(2 - distance(input.positionWS, _WorldSpaceCameraPos) / 40);
                albedo = lerp(albedo, FoamColour, lace * 0.85);

                half3 colour = StylizedLighting(albedo, input.positionWS, normal, input.positionCS);

                Light sun = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                half3 view = SafeNormalize(GetWorldSpaceViewDir(input.positionWS));
                half sheen = pow(saturate(dot(normal, SafeNormalize(sun.direction + view))), 24);
                colour += sun.color * sheen * wet * _WetGloss * sun.shadowAttenuation;

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

            // Value noise in [0, 1] on a unit grid, from a hash: no texture, so it never tiles.
            float Hash(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float ValueNoise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                f = f * f * (3 - 2 * f);
                return lerp(lerp(Hash(i), Hash(i + float2(1, 0)), f.x),
                            lerp(Hash(i + float2(0, 1)), Hash(i + 1), f.x), f.y);
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
