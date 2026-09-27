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
            AppLog.Error($"Could not load animation {item.Name}: {ex.Message}");
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
