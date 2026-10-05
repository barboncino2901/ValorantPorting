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

        // additive animations (an upper body's run over its idle pose, 1st person walks and aims) only store the change
        // from a base pose; the export adds them to that base (named in the animation), giving the animation as made.
        // Those measured against their own first frame can't be rebuilt: that frame isn't stored
        if (animation is UAnimSequence { AdditiveAnimType: not EAdditiveAnimationType.AAT_None } additive && !additive.IsValidAdditive())
        {
            AppLog.Warning($"{item.Name} is an additive animation measured against its own first pose, which the game " +
                           "files don't keep, so it can't be rebuilt on its own and was not sent.");
            return null;
        }

        return await WritePsa(animation, item.Name);
    }

    // An additive animation's base pose, for putting just its change on top of another animation (the "Add on top"
    // mode): the base pose as .psa (null: the skeleton's rest pose), where in it the base frame is (0 to 1, or
    // scaled along with the animation), and the kind of change. Null when the animation isn't a rebuildable additive.
    public record AdditiveInfo(string Type, string? BasePath, double BaseFraction, bool BaseScaled);

    public static async Task<AdditiveInfo?> AdditiveBase(AnimationItem item)
    {
        try
        {
            if (await AppVM.CUE4ParseVM.Provider.LoadPackageObjectAsync<UAnimationAsset>(item.ObjectPath) is not UAnimSequence
                { AdditiveAnimType: not EAdditiveAnimationType.AAT_None } sequence || !sequence.IsValidAdditive() ||
                sequence.RefPoseType == EAdditiveBasePoseType.ABPT_LocalAnimFrame)
                return null;
            var type = sequence.AdditiveAnimType == EAdditiveAnimationType.AAT_RotationOffsetMeshSpace ? "MeshRotation" : "Local";
            if (sequence.RefPoseType == EAdditiveBasePoseType.ABPT_RefPose)
                return new AdditiveInfo(type, null, 0, false);
            if (sequence.RefPoseSeq?.Load<UAnimSequence>() is not { } basePose ||
                await WritePsa(basePose, $"{item.Name} (base pose)") is not { } basePath)
                return null;
            return new AdditiveInfo(type, basePath, Math.Clamp((double) sequence.RefFrameIndex / Math.Max(1, basePose.NumFrames - 1), 0, 1),
                sequence.RefPoseType == EAdditiveBasePoseType.ABPT_AnimScaled);
        }
        catch (Exception ex)
        {
            AppLog.Warning($"{item.Name}: its base pose couldn't be exported ({ex.Message}); it goes on as the whole animation.");
            return null;
        }
    }

    private static async Task<string?> WritePsa(UAnimationAsset animation, string name)
    {
        var results = await new ExportSession { MaxDegreeOfParallelism = 1 }
            .Add(new AnimationExporter(animation))
            .RunAsync(App.AssetsFolder.FullName, Options);

        foreach (var result in results)
        {
            if (!result.Success)
            {
                var error = result.Error?.ToString() ?? "unknown error";
                if (error.Contains("CUE4Parse-Natives", StringComparison.OrdinalIgnoreCase) || result.Error is DllNotFoundException)
                    AppLog.Error($"{name} uses ACL compression, which needs CUE4Parse-Natives.dll (not included yet).");
                else
                    AppLog.Error($"Animation export failed for {name}: {result.Error?.Message}");
                continue;
            }

            var psa = result.DiskFilePaths?.FirstOrDefault(p => p.EndsWith(".psa", StringComparison.OrdinalIgnoreCase));
            if (psa is not null && File.Exists(psa)) return psa;
        }

        return null;
    }
}
