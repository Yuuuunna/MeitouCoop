using Meitou.Simulation;

namespace Meitou.Networking.VNext;

/// <summary>Matches AnimationSystem.Publish's manager allocation for sampled raw weights. Metadata explicitly
/// distinguishes whole-body actions from upper-only techniques; clients do not infer flags from content names.
/// Raw-positive layer order is preserved; the bridge omits raw-zero layers as Publish does.</summary>
internal static class AnimationWeightNormalization
{
    internal static void Normalize(IReadOnlyList<AnimationPlaybackLayer> metadata, AnimationLayer[] layers)
    {
        float upperWanted = 0, upperFading = 0, fullWanted = 0, fullFading = 0, lowerWanted = 0, lowerFading = 0;
        for (int i = 0; i < layers.Length; i++)
        {
            float weight = layers[i].Weight;
            if (weight <= 0) continue;
            var layer = metadata[i];
            if (layer.Area == AnimationBlendArea.Lower)
            {
                if (layer.Wanted) lowerWanted += weight; else lowerFading += weight;
            }
            else if (layer.Area != AnimationBlendArea.Overlay)
            {
                if (layer.Wanted) upperWanted += weight; else upperFading += weight;
                if (layer.ConsumesLower) { if (layer.Wanted) fullWanted += weight; else fullFading += weight; }
            }
        }
        var (upperWantedScale, upperFadingScale) = Fill(upperWanted, upperFading, 1);
        float room = MathF.Max(0, 1 - fullWanted * upperWantedScale - fullFading * upperFadingScale);
        var (lowerWantedScale, lowerFadingScale) = Fill(lowerWanted, lowerFading, room);
        for (int i = 0; i < layers.Length; i++)
        {
            var layer = metadata[i];
            float scale = layer.Area switch
            {
                AnimationBlendArea.Lower => layer.Wanted ? lowerWantedScale : lowerFadingScale,
                AnimationBlendArea.Overlay => 1,
                _ => layer.Wanted ? upperWantedScale : upperFadingScale,
            };
            layers[i] = layers[i] with { Weight = layers[i].Weight * scale };
        }
    }
    static (float Wanted, float Fading) Fill(float wanted, float fading, float total)
    {
        float wantedScale = wanted > 0 && total - fading > wanted ? (total - fading) / wanted : 1;
        float sum = wanted * wantedScale + fading;
        float down = sum > total && sum > 0 ? total / sum : 1;
        return (wantedScale * down, down);
    }
}
