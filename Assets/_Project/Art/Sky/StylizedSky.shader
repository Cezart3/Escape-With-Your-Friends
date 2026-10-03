// The island's sky (#243): a gradient, a sun that blooms, a moon, stars and two layers of soft
// stylised cloud, in one fullscreen pass with no textures. Everything that changes over the day is
// written by DayNightCycle; the shader only draws.
//
// The horizon colour is the fog colour, so the sky and the fogged world meet without a seam, and
// below the horizon it is the far sea rather than Unity's black ground.
Shader "EWYF/Sky"
{
    Properties
    {
        _SkyTop ("Zenith", Color) = (0.22, 0.45, 0.85, 1)
        _SkyHorizon ("Horizon (the fog colour)", Color) = (0.68, 0.80, 0.92, 1)
        _SkyGround ("Below the horizon", Color) = (0.20, 0.36, 0.48, 1)
        [HDR] _SunColour ("Sun", Color) = (1, 0.95, 0.85, 1)
        _SunDir ("Direction to the sun", Vector) = (0, 0.7, 0.7, 0)
        _SunSize ("Sun size", Range(0.0005, 0.01)) = 0.0016
        [HDR] _MoonColour ("Moon", Color) = (0.8, 0.85, 1, 1)
        _CloudLit ("Cloud, lit side", Color) = (1, 1, 1, 1)
        _CloudShade ("Cloud, shade side", Color) = (0.62, 0.7, 0.82, 1)
        _CloudCover ("Cloud cover", Range(0, 1)) = 0.45
        _Stars ("Stars", Range(0, 1)) = 0
    }

    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" "RenderPipeline" = "UniversalPipeline" }
        Cull Off ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _SkyTop;
                half4 _SkyHorizon;
                half4 _SkyGround;
                half4 _SunColour;
                float4 _SunDir;
                float _SunSize;
                half4 _MoonColour;
                half4 _CloudLit;
                half4 _CloudShade;
                half _CloudCover;
                half _Stars;
            CBUFFER_END

            // Wind.cs: xy direction, z strength. The clouds drift with the same wind as the palms.
            float4 _WindParams;

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 direction : TEXCOORD0; };

            Varyings Vertex(Attributes input)
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

            float Noise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                float a = Hash(float3(i, 0));
                float b = Hash(float3(i + float2(1, 0), 0));
                float c = Hash(float3(i + float2(0, 1), 0));
                float d = Hash(float3(i + float2(1, 1), 0));
                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            float Clouds(float2 uv)
            {
                float n = Noise(uv) * 0.55 + Noise(uv * 2.1 + 3.7) * 0.28 + Noise(uv * 4.3 + 7.1) * 0.17;
                return n;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                float3 d = normalize(input.direction);
                float y = d.y;
                float3 toSun = normalize(_SunDir.xyz);
                float mu = dot(d, toSun);

                // The gradient. A square root keeps the pale band low and gives most of the dome to
                // the zenith colour, which is what makes a tropical sky read as deep rather than milky.
                half3 colour = lerp(_SkyHorizon.rgb, _SkyTop.rgb, sqrt(saturate(y)));
                colour = lerp(colour, _SkyGround.rgb, smoothstep(0.0, -0.08, y));

                // The air round the sun: a wide warm wash low in the sky, a tight halo round the disk.
                half sunUp = saturate(toSun.y * 4 + 0.3);
                colour += _SunColour.rgb * (pow(saturate(mu), 6) * 0.35 * (1 - saturate(y)) + pow(saturate(mu), 300) * 0.8) * sunUp;

                // Stars: one per cell of a grid on the direction, most cells empty, a slow twinkle.
                if (_Stars > 0.01 && y > 0)
                {
                    float3 p = d * 220;
                    float3 cell = floor(p);
                    float h = Hash(cell);
                    float twinkle = 0.6 + 0.4 * sin(_Time.y * (2 + h * 3) + h * 40);
                    float star = step(0.985, h) * smoothstep(0.35, 0.0, length(frac(p) - 0.5)) * twinkle;
                    colour += star * _Stars * saturate(y * 3) * 1.5;
                }

                // How much cloud stands in front of this pixel: the disks are drawn behind it.
                half hidden = 0;

                // Clouds on a flat ceiling: the direction projected up, so they crowd toward the
                // horizon like a real cloud deck. Two layers drift with the wind at different speeds.
                if (y > 0)
                {
                    float2 uv = d.xz / (y + 0.12) * 0.9;
                    float2 drift = _WindParams.xy * _Time.y * 0.004 * (0.5 + _WindParams.z);
                    // The second layer is one octave: it only breaks up the first, and every octave is
                    // four hashes on every sky pixel (the first pass cost 0.6 ms on the 4060).
                    float n = Clouds(uv + drift);
                    float n2 = Noise(uv * 3.6 + drift * 1.6 + 11.3);
                    float shape = n * 0.7 + n2 * 0.3;

                    float edge = 1 - _CloudCover;
                    half cover = smoothstep(edge, edge + 0.12, shape) * smoothstep(0.0, 0.18, y);

                    // Lit where the cloud is thin, shaded where it is thick: no second lookup toward the sun.
                    half light = saturate(1.6 - (shape - edge) * 6);
                    half3 cloud = lerp(_CloudShade.rgb, _CloudLit.rgb, light);
                    cloud += _SunColour.rgb * pow(saturate(mu), 12) * 0.6 * sunUp;   // silver lining near the sun

                    colour = lerp(colour, cloud, cover * 0.92);
                    hidden = cover;
                }

                // The disks last, bright enough that bloom catches them, and behind the cloud: added on
                // top at full strength they burnt a hole through any cloud that crossed them.
                half disk = smoothstep(1 - _SunSize, 1 - _SunSize * 0.6, mu) * (1 - hidden);
                colour += _SunColour.rgb * disk * 12 * saturate(y * 20 + 0.5);

                half moonDisk = smoothstep(1 - _SunSize * 0.8, 1 - _SunSize * 0.5, dot(d, -toSun));
                half moonHalo = pow(saturate(dot(d, -toSun)), 400) * 0.25;
                colour += _MoonColour.rgb * (moonDisk * 2.5 + moonHalo) * _Stars * (1 - hidden);

                return half4(colour, 1);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
