// The sea. Hand-written rather than a Shader Graph for the same reason everything else here is
// generated: a .shader is text, so it can be written, diffed and reviewed from a terminal, and a
// .shadergraph is a blob nobody can read in a pull request.
//
// The one design decision worth reading: this shader never samples the camera depth texture.
// Depth-faded water is the standard trick, but in URP it means turning the depth prepass on for the
// whole frame, and the whole point of this project is that it runs on an integrated GPU. Instead the
// water depth is baked once, from the same island function the terrain is built from, into a small
// mask texture: one texture fetch, no prepass, and the shoreline foam follows the coast exactly
// because it is the coast. What it cannot do is fade against things that move - a boat hull gets no
// intersection foam. Fair trade for a frame that renders at all.
Shader "EWYF/Water"
{
    Properties
    {
        [Header(Colour)]
        _ShallowColor ("Shallow", Color) = (0.20, 0.60, 0.62, 1)
        _DeepColor ("Deep", Color) = (0.02, 0.16, 0.30, 1)
        _HorizonColor ("Horizon (fresnel)", Color) = (0.52, 0.70, 0.82, 1)
        _FoamColor ("Foam", Color) = (0.92, 0.96, 0.96, 1)

        [Header(Depth mask)]
        _DepthMask ("Baked water depth", 2D) = "black" {}
        _IslandSize ("Island size (m)", Float) = 1024
        _ShoreDepth ("Metres mapped to full deep", Float) = 14
        _FoamWidth ("Foam band (m)", Float) = 1.6
        _ShallowAlpha ("Alpha at the shore", Range(0,1)) = 0.45
        _DeepAlpha ("Alpha out deep", Range(0,1)) = 0.96

        [Header(Waves)]
        _WaveA ("Wave A (dirx, dirz, amp, k)", Vector) = (0.86, 0.51, 0.34, 0.153)
        _WaveB ("Wave B", Vector) = (-0.42, 0.91, 0.19, 0.273)
        _WaveC ("Wave C", Vector) = (0.60, -0.80, 0.09, 0.370)
        _WaveSpeed ("Angular speeds (a, b, c)", Vector) = (0.475, 0.656, 0.628, 0)
        _PatchFade ("Wave fade (start, end) in object space", Vector) = (250, 320, 0, 0)

        [Header(Surface detail)]
        [Normal] _NormalMap ("Ripple normals", 2D) = "bump" {}
        _NormalTiling ("Ripple tiling (m per tile)", Float) = 9
        _NormalStrength ("Ripple strength", Range(0,2)) = 0.55
        _NormalScroll ("Ripple scroll (m per s)", Vector) = (0.12, 0.07, -0.09, 0.05)

        [Header(Lighting)]
        _Smoothness ("Smoothness", Range(0,1)) = 0.88
        _SpecularStrength ("Sun glint", Range(0,8)) = 2.4
        _FresnelPower ("Fresnel falloff", Range(1,8)) = 4.0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "WaterForward"
            Tags { "LightMode" = "UniversalForward" }

            // No depth write: the water is the last thing drawn and nothing needs to sort against
            // it. Blended rather than clipped so the sand shows through in the shallows, which is
            // most of what sells it as water without a single refraction sample.
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma target 3.0
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            // Global, not per-material: the C# that floats boats and this shader have to be reading
            // the same instant, and a global float is the only way that stays true if a second water
            // material ever appears.
            float _WaterTime;

            TEXTURE2D(_DepthMask);      SAMPLER(sampler_DepthMask);
            TEXTURE2D(_NormalMap);      SAMPLER(sampler_NormalMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _ShallowColor;
                float4 _DeepColor;
                float4 _HorizonColor;
                float4 _FoamColor;
                float4 _DepthMask_ST;
                float4 _NormalMap_ST;
                float4 _WaveA;
                float4 _WaveB;
                float4 _WaveC;
                float4 _WaveSpeed;
                float4 _PatchFade;
                float4 _NormalScroll;
                float _IslandSize;
                float _ShoreDepth;
                float _FoamWidth;
                float _ShallowAlpha;
                float _DeepAlpha;
                float _NormalTiling;
                float _NormalStrength;
                float _Smoothness;
                float _SpecularStrength;
                float _FresnelPower;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float  fade       : TEXCOORD2;
                float  fogFactor  : TEXCOORD3;
            };

            // One wave: its height, and the slope it contributes along x and z. Same expression as
            // WaterWaves.cs, deliberately - if these two drift apart, boats float in the wrong place.
            void AddWave(float4 wave, float speed, float2 p, inout float height, inout float2 slope)
            {
                float phase = dot(wave.xy, p) * wave.w + _WaterTime * speed;
                height += wave.z * sin(phase);
                slope += wave.z * wave.w * cos(phase) * wave.xy;
            }

            void Waves(float2 p, out float height, out float2 slope)
            {
                height = 0.0;
                slope = float2(0.0, 0.0);
                AddWave(_WaveA, _WaveSpeed.x, p, height, slope);
                AddWave(_WaveB, _WaveSpeed.y, p, height, slope);
                AddWave(_WaveC, _WaveSpeed.z, p, height, slope);
            }

            Varyings Vertex(Attributes IN)
            {
                Varyings OUT;

                float3 positionWS = TransformObjectToWorld(IN.positionOS.xyz);

                // The near patch carries the waves and the far ring does not, so the waves have to
                // reach exactly zero at the seam or there is a visible crack in the ocean. The fade
                // is object space, because the patch follows the camera and the seam moves with it;
                // the wave phase stays world space, so crests do not slide when the patch moves.
                float edge = max(abs(IN.positionOS.x), abs(IN.positionOS.z));
                float fade = 1.0 - smoothstep(_PatchFade.x, _PatchFade.y, edge);

                float height;
                float2 slope;
                Waves(positionWS.xz, height, slope);

                positionWS.y += height * fade;

                OUT.positionWS = positionWS;
                OUT.normalWS = normalize(float3(-slope.x * fade, 1.0, -slope.y * fade));
                OUT.fade = fade;
                OUT.positionCS = TransformWorldToHClip(positionWS);
                OUT.fogFactor = ComputeFogFactor(OUT.positionCS.z);
                return OUT;
            }

            half4 Fragment(Varyings IN) : SV_Target
            {
                // Baked depth: 0 at the waterline, 1 once the seabed is _ShoreDepth metres down.
                // Clamped sampling means everything past the edge of the island square reads as open
                // ocean, which is what it is.
                float2 maskUV = saturate(IN.positionWS.xz / _IslandSize + 0.5);
                float mask = SAMPLE_TEXTURE2D(_DepthMask, sampler_DepthMask, maskUV).r;

                // Two scrolling samples of the same ripple tile at different scales, which is the
                // cheapest thing that stops a tiling normal map from looking like a tiling normal
                // map. Both fade out with the waves, so the far ring stays mirror flat and free.
                float tiling = max(0.01, _NormalTiling);
                float2 baseUV = IN.positionWS.xz / tiling;
                float2 uv1 = baseUV + _NormalScroll.xy * _WaterTime / tiling;
                float2 uv2 = baseUV * 0.47 + _NormalScroll.zw * _WaterTime / tiling;

                float3 n1 = UnpackNormal(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, uv1));
                float3 n2 = UnpackNormal(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, uv2));
                float2 ripple = (n1.xy + n2.xy) * _NormalStrength * IN.fade;

                float3 normalWS = normalize(IN.normalWS + float3(ripple.x, 0.0, ripple.y));

                float3 viewDir = SafeNormalize(GetWorldSpaceViewDir(IN.positionWS));
                Light sun = GetMainLight();

                // Water is not diffuse in any honest sense, but a little of it keeps the shallows
                // from going flat when the sun is low.
                float wrapped = saturate(dot(normalWS, sun.direction)) * 0.5 + 0.5;

                float3 halfDir = SafeNormalize(sun.direction + viewDir);
                float exponent = exp2(_Smoothness * 11.0 + 1.0);
                float glint = pow(saturate(dot(normalWS, halfDir)), exponent) * _SpecularStrength;

                // Fresnel: looking straight down you see the water, looking along it you see the
                // sky. This is what makes a flat plane read as a surface at all.
                float fresnel = pow(1.0 - saturate(dot(normalWS, viewDir)), _FresnelPower);

                float3 body = lerp(_ShallowColor.rgb, _DeepColor.rgb, saturate(pow(mask, 0.75)));
                float3 colour = body * (wrapped * sun.color + SampleSH(normalWS) * 0.35);
                colour = lerp(colour, _HorizonColor.rgb, saturate(fresnel));
                colour += sun.color * glint;

                // Foam, in the band where the seabed is within _FoamWidth of the surface, torn up by
                // two crossing sines so the edge is lacy and moving instead of a contour line.
                float band = 1.0 - smoothstep(0.0, saturate(_FoamWidth / max(0.01, _ShoreDepth)), mask);
                float churn = sin(dot(IN.positionWS.xz, float2(0.42, 0.31)) - _WaterTime * 1.7)
                            * sin(dot(IN.positionWS.xz, float2(-0.27, 0.36)) + _WaterTime * 1.1);
                float foam = saturate(band * (0.62 + 0.55 * churn));

                colour = lerp(colour, _FoamColor.rgb, foam);

                float alpha = lerp(_ShallowAlpha, _DeepAlpha, saturate(mask * 1.35));
                alpha = saturate(alpha + foam * 0.7);

                colour = MixFog(colour, IN.fogFactor);
                return half4(colour, alpha);
            }
            ENDHLSL
        }
    }

    Fallback "Universal Render Pipeline/Unlit"
}
