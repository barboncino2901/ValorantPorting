using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CUE4Parse.UE4.Assets.Exports;
using ValorantPorting.AppUtils;
using ValorantPorting.Export;
using ValorantPorting.Export.Blender;
using ValorantPorting.Services;
using ValorantPorting.Views.Controls;

namespace ValorantPorting.ViewModels;

// A scene: an agent, a gun in their hand and an animation on each, sent to Blender in one go. Each piece is picked
// the usual way (the agent/skin with its options, an animation or a combined upper + lower body) and added with
// "Add to scene"; Blender then gets them in order: agent, gun attached to the agent's hand, animations.
public partial class MainViewModel
{
    public record SceneAsset(IExportableAsset Item, EAssetType Type, UObject? Style, ExportChoices Choices, string Name);

    // an animation for the scene: one entry of the list (single, full body or sequence) or a combined upper + lower pick
    public record SceneAnimation(string Name, AnimationItem? Item, AnimationItem? Upper, AnimationItem? Lower, int Repeat)
    {
        // "TP" 3rd person, "FP" 1st person, "CS" character select, "GN" gun
        public string Model => (Item ?? Upper!).Name.Split('_')[0].ToUpperInvariant();
    }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SceneText), nameof(SceneVisibility))]
    private SceneAsset? sceneAgent;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SceneText), nameof(SceneVisibility))]
    private SceneAsset? sceneGun;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SceneText), nameof(SceneVisibility))]
    private SceneAnimation? sceneAgentAnimation;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SceneText), nameof(SceneVisibility))]
    private SceneAnimation? sceneGunAnimation;

    public Visibility SceneVisibility =>
        SceneAgent != null || SceneGun != null || SceneAgentAnimation != null || SceneGunAnimation != null ? Visibility.Visible : Visibility.Collapsed;

    public string SceneText
    {
        get
        {
            static string Animation(SceneAnimation? a) => a is null ? "—" : a.Repeat > 1 ? $"{a.Name} ×{a.Repeat}" : a.Name;
            return $"Agent: {SceneAgent?.Name ?? "—"}    ·    Gun: {SceneGun?.Name ?? "—"}    ·    " +
                   $"Agent animation: {Animation(SceneAgentAnimation)}    ·    Gun animation: {Animation(SceneGunAnimation)}";
        }
    }

    [ObservableProperty] private Visibility addToSceneVisibility = Visibility.Collapsed; // agents and gun skins

    // the agent or skin shown on the right, with the options picked there (models, level, variant)
    [RelayCommand]
    public void AddAssetToScene()
    {
        if (currentAsset is null || CurrentAssetType is not (EAssetType.Character or EAssetType.Weapon)) return;
        var asset = new SceneAsset(currentAsset, CurrentAssetType, GetSelectedStyles(), GetExportChoices(),
            currentAsset.DisplayName + ExportNameSuffix());
        if (CurrentAssetType == EAssetType.Character) SceneAgent = asset;
        else SceneGun = asset;
        AppLog.Information($"Scene: {asset.Name} added.");
    }

    // the selected animation (Animations tab)
    [RelayCommand]
    public void AddAnimationToScene()
    {
        if (SelectedAnimation is not { } item)
        {
            AppLog.Warning("Select an animation first.");
            return;
        }

        PutInScene(new SceneAnimation(item.Title, item, null, null, item.IsLoop ? RepeatCount : 1));
    }

    // the combine bar's upper + lower body picks
    [RelayCommand]
    public void AddCombinedToScene()
    {
        if (UpperBodyPick is not { } upper || LowerBodyPick is not { } lower || !CheckCombine(upper, lower)) return;
        PutInScene(new SceneAnimation($"{upper.Title} + {lower.Title} (legs)", null, upper, lower,
            upper.IsLoop || lower.IsLoop ? RepeatCount : 1));
    }

    private void PutInScene(SceneAnimation animation)
    {
        switch (animation.Model)
        {
            case "TP" or "FP" or "CS":
                SceneAgentAnimation = animation;
                break;
            case "GN" or "GNTP": // the gun's 1st / 3rd person animations
                SceneGunAnimation = animation;
                break;
            default:
                MessageBox.Show($"A scene holds an agent and a gun, and this animation is for another model ({(animation.Item ?? animation.Upper)!.View}). " +
                                "Apply it to the selected armature instead.", "Add to scene", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
        }

        AppLog.Information($"Scene: animation {animation.Name} added.");
    }

    [RelayCommand]
    public void ClearScene()
    {
        SceneAgent = null;
        SceneGun = null;
        SceneAgentAnimation = null;
        SceneGunAnimation = null;
    }

    private bool sceneExportRunning;

    [RelayCommand]
    public async Task SendScene()
    {
        const string title = "Send scene";
        if (sceneExportRunning) return;

        // the agent armature the gun and the agent animation go on: the animation's model, else 3rd person if exported
        var agentModels = SceneAgent?.Choices.Models ?? ECharacterModels.All;
        var rig = SceneAgentAnimation?.Model ??
                  (agentModels.HasFlag(ECharacterModels.ThirdPerson) ? "TP" : agentModels.HasFlag(ECharacterModels.FirstPerson) ? "FP" : "CS");
        var warnings = new List<string>();
        // a gun animation made for the other view: a different animation, with its own timing. Riot only made 3rd person
        // gun animations where the gun visibly moves by itself in 3rd person (an Operator's bolt): a Vandal equip has
        // none, the gun just moves with the hands.
        if (SceneAgentAnimation is { } agentPick && SceneGunAnimation is { Item: { } gunItem } gunPick &&
            (agentPick.Model == "FP") != (gunPick.Model == "GN"))
        {
            var otherName = gunPick.Model == "GN" ? "GNTP_" + gunItem.Name["GN_".Length..] : "GN_" + gunItem.Name["GNTP_".Length..];
            var other = Animations.FirstOrDefault(a => a.Name.Equals(otherName, StringComparison.OrdinalIgnoreCase));
            var agentView = agentPick.Model == "FP" ? "1st" : "3rd";
            warnings.Add($"The agent animation is {agentView} person but the gun animation \"{gunItem.Title}\" is the " +
                         $"{(gunPick.Model == "GN" ? "1st" : "3rd")} person one: a different animation that won't line up. " +
                         (other != null
                             ? $"Use \"{other.Title}\" ({other.ModelTag}) instead."
                             : agentPick.Model == "FP"
                                 ? "This gun has no 1st person version of it."
                                 : "In 3rd person the game plays no gun animation for this (the gun just moves with the hands): leave the gun animation out."));
        }

        var needed = rig switch { "TP" => ECharacterModels.ThirdPerson, "FP" => ECharacterModels.FirstPerson, _ => ECharacterModels.CharacterSelect };
        if (SceneAgent != null && !agentModels.HasFlag(needed))
            warnings.Add($"The agent animation is for the {ModelOptions.First(o => o.Key == needed).Value} model, which isn't in the agent's " +
                         "\"Models\" choice: it will be skipped.");
        if (SceneAgent is null && SceneAgentAnimation != null)
            warnings.Add("There's no agent in the scene: the agent animation goes on the agent armature selected in Blender.");
        if (SceneGun is null && SceneGunAnimation != null)
            warnings.Add("There's no gun in the scene: the gun animation goes on the gun armature selected in Blender.");
        if (warnings.Count > 0 &&
            MessageBox.Show(string.Join("\n\n", warnings) + "\n\nSend anyway?", title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        sceneExportRunning = true;
        try
        {
            var timer = Stopwatch.StartNew();
            var steps = new List<object>();
            if (SceneAgent is { } agent)
                steps.Add(BlenderService.ExportMessage(await ExportSceneAsset(agent), SceneSettings(agent, "agent", null)));
            if (SceneGun is { } gun)
                steps.Add(BlenderService.ExportMessage(await ExportSceneAsset(gun), SceneSettings(gun, "gun", SceneAgent != null ? $"agent:{rig}" : null)));
            if (SceneAgentAnimation is { } agentAnimation && await AnimationMessage(agentAnimation, SceneAgent != null ? $"agent:{rig}" : null) is { } a)
                steps.Add(a);
            if (SceneGunAnimation is { } gunAnimation && await AnimationMessage(gunAnimation, SceneGun != null ? "gun" : null) is { } g)
                steps.Add(g);
            if (steps.Count == 0) return;

            var name = SceneAgent?.Name ?? SceneGun?.Name ?? "Scene";
            BlenderService.SendScene(name, steps);
            if (SceneAgent != null) RegisterSentAsset(BuildAnimationFilterKey(EAssetType.Character, SceneAgent.Item));
            AppLog.Information($"Sent scene {name} ({steps.Count} steps) to BLENDER in {Math.Round(timer.Elapsed.TotalSeconds, 3)}s.");
            _ = Task.Run(() => MemoryHelper.ReleaseAfterLoading("After sending a scene"));
        }
        catch (Exception e)
        {
            AppLog.Error($"Scene export failed: {e.Message}");
        }
        finally
        {
            sceneExportRunning = false;
        }
    }

    private static async Task<ExportData> ExportSceneAsset(SceneAsset asset)
    {
        var data = await ExportData.Create(asset.Item.Asset, asset.Type, asset.Style!, asset.Choices, asset.Item.MainAsset);
        data.Name = asset.Name;
        return data;
    }

    private BlenderExportSettings SceneSettings(SceneAsset asset, string role, string? target) => new()
    {
        ReorientBones = asset.Type != EAssetType.Weapon,
        AnimationFilterKey = BuildAnimationFilterKey(asset.Type, asset.Item),
        FirstPersonCamera = asset.Type == EAssetType.Character && FirstPersonCamera && asset.Choices.Models.HasFlag(ECharacterModels.FirstPerson),
        SceneRole = role,
        SceneTarget = target
    };

    // exports the animation's .psa files and builds its Blender message, like applying it on its own
    private static async Task<object?> AnimationMessage(SceneAnimation animation, string? target)
    {
        var (upper, lower) = animation.Upper is not null ? (animation.Upper, animation.Lower!)
            : animation.Item!.Kind == EAnimationKind.FullBody ? (animation.Item.UpperHalf!, animation.Item.LowerHalf!) : (null, null);
        if (upper is not null)
        {
            var lowerPath = await Task.Run(() => AnimationExport.ExportPsa(lower!));
            var upperPath = await Task.Run(() => AnimationExport.ExportPsa(upper));
            if (lowerPath is null || upperPath is null) return null;
            return BlenderService.AnimationMessage(animation.Name, lowerPath, upperPath, animation.Repeat, lower!.IsLoop, upper.IsLoop, sceneTarget: target);
        }

        var item = animation.Item!;
        var paths = new List<string>();
        foreach (var clip in item.Kind == EAnimationKind.Sequence ? item.Clips : [item])
        {
            if (await Task.Run(() => AnimationExport.ExportPsa(clip)) is not { } path) return null;
            paths.Add(path);
        }

        return BlenderService.AnimationMessage(item.Name, paths[0], repeat: animation.Repeat, lowerLoops: item.IsLoop,
            sequencePaths: paths.Count > 1 ? paths : null, sceneTarget: target);
    }
}
