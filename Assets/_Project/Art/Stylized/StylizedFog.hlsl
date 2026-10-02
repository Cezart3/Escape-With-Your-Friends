// Distance fog plus height fog (#243), for every shader that draws the island: models, the ground
// and the sea. Include after Core.hlsl and use in place of MixFog.
//
// The height fog lies on the sea and thins upward, so the jungle and the volcano fade into air
// instead of ending, while the view from the cliff stays clear. It is integrated along the view ray
// rather than read at the fragment's height, so a hilltop seen across a misty valley is fogged by the
// valley, which is what the eye expects.
#ifndef EWYF_STYLIZED_FOG_INCLUDED
#define EWYF_STYLIZED_FOG_INCLUDED

// Set by DayNightCycle. x: density at the base, per metre (0 turns it off). y: base height.
// z: falloff, per metre of height.
float4 _HeightFog;

half3 StylizedFog(half3 colour, half fogFactor, float3 positionWS)
{
    colour = MixFog(colour, fogFactor);

    #if defined(FOG_LINEAR) || defined(FOG_EXP) || defined(FOG_EXP2)
        if (_HeightFog.x > 0)
        {
            float3 ray = positionWS - _WorldSpaceCameraPos;
            float distance = length(ray);
            float k = _HeightFog.z;
            float above = max(_WorldSpaceCameraPos.y - _HeightFog.y, -20);

            // The integral of density * exp(-k * height) along the ray, in closed form. Near-level
            // rays take the series, which is where the closed form divides by zero.
            float t = k * ray.y;
            float alongRay = abs(t) > 0.001 ? (1 - exp(-t)) / t : 1 - 0.5 * t;
            float depth = _HeightFog.x * exp(-k * above) * alongRay * distance;

            colour = lerp(colour, unity_FogColor.rgb, 1 - exp(-depth));
        }
    #endif

    return colour;
}

#endif
