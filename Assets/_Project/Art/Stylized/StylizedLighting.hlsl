// The island's one lighting model, shared by EWYF/Stylized (every model) and EWYF/StylizedTerrain
// (the ground), so a rock and the sand under it are lit by the same hand. See ARCHITECTURE.md,
// "One shader for every kit".
//
// Include after Lighting.hlsl, with these declared first (per material, or set by StyleLook):
// _RampCentre, _RampSoftness, _ShadowTint, _RimStrength, _Smoothness.
#ifndef EWYF_STYLIZED_LIGHTING_INCLUDED
#define EWYF_STYLIZED_LIGHTING_INCLUDED

// Two bands with a soft edge. The same curve for the sun and every lamp, so a torch-lit crate and a
// sunlit one are drawn by the same hand.
half StylizedBand(half ndl)
{
    return smoothstep(_RampCentre - _RampSoftness, _RampCentre + _RampSoftness, ndl);
}

// Lit colour for one fragment, before emission and fog.
half3 StylizedLighting(half3 albedo, float3 positionWS, half3 normal, float4 positionCS)
{
    half3 view = SafeNormalize(GetWorldSpaceViewDir(positionWS));

    float4 shadowCoord = TransformWorldToShadowCoord(positionWS);
    // The overload with the position is the one that fades shadows out near the shadow distance
    // (and applies a main-light cookie); the shadowCoord-only one cuts them off.
    Light sun = GetMainLight(shadowCoord, positionWS, half4(1, 1, 1, 1));

    // Not banded: the attenuation carries the light's shadow strength (the moon's is 0.35) and that
    // distance fade, and a step would erase the first and turn the second into a ring that follows
    // the camera.
    half shadow = sun.shadowAttenuation * sun.distanceAttenuation;
    half lit = StylizedBand(dot(normal, sun.direction)) * shadow;

    half3 ambient = SampleSH(normal);
    half directOcclusion = 1;

    #if defined(_SCREEN_SPACE_OCCLUSION)
        AmbientOcclusionFactor occlusion = GetScreenSpaceAmbientOcclusion(GetNormalizedScreenSpaceUV(positionCS));
        ambient *= occlusion.indirectAmbientOcclusion;
        directOcclusion = occlusion.directAmbientOcclusion;
    #endif

    // The shade side is the ambient, but never less than a share of the sun: a trilight ambient
    // under a jungle canopy is near black, and a toon look reads its shade as a colour, not a hole.
    // Scaled by the sun, so the night stays night. Tinted cool against the warm key.
    half3 shade = max(ambient, sun.color * 0.42) * _ShadowTint.rgb;
    half3 light = lerp(shade, ambient + sun.color * directOcclusion, lit);

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
            Light lamp = GetAdditionalLight(i, positionWS, half4(1, 1, 1, 1));
            half lampLit = StylizedBand(dot(normal, lamp.direction)) * lamp.distanceAttenuation * lamp.shadowAttenuation;
            light += lamp.color * lampLit;
        }
    #endif

    return albedo * light + specular;
}

#endif
