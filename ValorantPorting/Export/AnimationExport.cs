using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Exporters;
using CUE4Parse_Conversion.Options;
using CUE4Parse.UE4.Assets.Exports.Animation;
using ValorantPorting.AppUtils;
using ValorantPorting.Views.Controls;

namespace ValorantPorting.Export;

public static class AnimationExport
{
    private static readonly ExportOptions Options = new(meshFormat: EMeshFormat.ActorX, exportMaterials: false);

    // Exports the animation as .psa into the Assets folder and returns the file path, or null on failure.
    public static async Task<string?> ExportPsa(AnimationItem item)
    {
        UAnimationAsset animation;
        try
        {
            animation = await AppVM.CUE4ParseVM.Provider.LoadPackageObjectAsync<UAnimationAsset>(item.ObjectPath);
        }
        catch (Exception ex)
        {
            AppLog.Error($"{item.Name} is not an animation sequence or could not be loaded ({ex.Message}).");
            return null;
        }

        if (animation is UAnimMontage montage &&
            montage.SlotAnimTracks.SelectMany(t => t.AnimTrack?.AnimSegments ?? []).All(s => s.AnimReference?.ResolvedObject is null))
        {
            AppLog.Warning($"{item.Name} is an empty montage: the animation it played isn't in the game files any more, so there's nothing to export.");
            return null;
        }

        if (animation is UAnimSequence { AdditiveAnimType: not EAdditiveAnimationType.AAT_None })
        {
            AppLog.Warning($"{item.Name} is an additive animation (e.g. an aim pose). It is meant to be layered on top of " +
                           "another animation and will look wrong on its own, so it was not sent.");
            return null;
        }

        var results = await new ExportSession { MaxDegreeOfParallelism = 1 }
            .Add(new AnimationExporter(animation))
            .RunAsync(App.AssetsFolder.FullName, Options);

        foreach (var result in results)
        {
            if (!result.Success)
            {
                var error = result.Error?.ToString() ?? "unknown error";
                if (error.Contains("CUE4Parse-Natives", StringComparison.OrdinalIgnoreCase) || result.Error is DllNotFoundException)
                    AppLog.Error($"{item.Name} uses ACL compression, which needs CUE4Parse-Natives.dll (not included yet).");
                else
                    AppLog.Error($"Animation export failed for {item.Name}: {result.Error?.Message}");
                continue;
            }

            var psa = result.DiskFilePaths?.FirstOrDefault(p => p.EndsWith(".psa", StringComparison.OrdinalIgnoreCase));
            if (psa is not null && File.Exists(psa)) return psa;
        }

        return null;
    }
}
