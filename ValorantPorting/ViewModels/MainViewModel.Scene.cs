using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
using ValorantPorting.Views;
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
    // an ability model (held by the agent, like the gun) and its own animation
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SceneText), nameof(SceneVisibility))]
    private AbilityItem? sceneAbility;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SceneText), nameof(SceneVisibility))]
    private SceneAnimation? sceneAbilityAnimation;

    public Visibility SceneVisibility =>
        SceneAgent != null || SceneGun != null || SceneAgentAnimation != null || SceneGunAnimation != null ||
        SceneAbility != null || SceneAbilityAnimation != null ? Visibility.Visible : Visibility.Collapsed;

    public string SceneText
    {
        get
        {
            static string Animation(SceneAnimation? a) => a is null ? "—" : a.Repeat > 1 ? $"{a.Name} ×{a.Repeat}" : a.Name;
            var text = $"Agent: {SceneAgent?.Name ?? "—"}    ·    Gun: {SceneGun?.Name ?? "—"}    ·    " +
                       $"Agent animation: {Animation(SceneAgentAnimation)}    ·    Gun animation: {Animation(SceneGunAnimation)}";
            if (SceneAbility != null || SceneAbilityAnimation != null)
                text += $"    ·    Ability: {SceneAbility?.Title ?? "—"}    ·    Ability animation: {Animation(SceneAbilityAnimation)}";
            return text;
        }
    }

    [ObservableProperty] private Visibility addToSceneVisibility = Visibility.Collapsed; // agents and gun skins

    // the agent or skin shown on the right, with the options picked there (models, level, variant)
    [RelayCommand]
    public void AddAssetToScene()
    {
        if (currentAsset is null) return;
        // agent or gun skin: from the list the item is in (not the tab state, which can lag while a tab loads)
        EAssetType? type = currentAsset is AssetSelectorItem tile
            ? Outfits.Contains(tile) ? EAssetType.Character : Weapons.Contains(tile) ? EAssetType.Weapon : null
            : CurrentAssetType is EAssetType.Character or EAssetType.Weapon ? CurrentAssetType : null;
        if (type is not { } kind) return;
        var asset = new SceneAsset(currentAsset, kind, GetSelectedStyles(), GetExportChoices(),
            currentAsset.DisplayName + ExportNameSuffix());
        if (kind == EAssetType.Character) SceneAgent = asset;
        else SceneGun = asset;
        AppLog.Information($"Scene: {asset.Name} added.");
    }

    // the selected ability model (Abilities tab): in the agent's hands, where the game holds it
    [RelayCommand]
    public void AddAbilityToScene()
    {
        if (SelectedAbility is not { } item)
        {
            AppLog.Warning("Select an ability model first.");
            return;
        }

        SceneAbility = item;
        AppLog.Information($"Scene: {item.Title} added.");
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
            case "AB" or "ABTP" or "ABCS": // an ability model's own animations
                SceneAbilityAnimation = animation;
                break;
            default:
                MessageBox.Show($"A scene holds an agent, a gun and an ability, and this animation is for another model ({(animation.Item ?? animation.Upper)!.View}). " +
                                "Apply it to the selected armature instead.", "Add to scene", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
        }

        AppLog.Information($"Scene: animation {animation.Name} added.");
    }

    // ---- saved scenes (presets): the scene bar's contents under a name, sent again in one click (Scenes tab)

    [ObservableProperty] private ObservableCollection<SavedSceneRow> savedSceneList = new();
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SelectedSavedSceneVisibility), nameof(NoSavedSceneSelectedVisibility))]
    private SavedSceneRow? selectedSavedScene;
    public Visibility SelectedSavedSceneVisibility => SelectedSavedScene != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoSavedSceneSelectedVisibility => SelectedSavedScene == null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoSavedScenesVisibility => SavedSceneList.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public event Action? SavedScenesChanged;

    public void RefreshSavedScenes()
    {
        var selected = SelectedSavedScene?.Name;
        SavedSceneList = new ObservableCollection<SavedSceneRow>(SavedScenes.All.Select(s => new SavedSceneRow(s)));
        SelectedSavedScene = SavedSceneList.FirstOrDefault(r => r.Name.Equals(selected ?? "", StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(NoSavedScenesVisibility));
        SavedScenesChanged?.Invoke();
    }

    [RelayCommand]
    public async Task SendSelectedSavedScene()
    {
        if (SelectedSavedScene is { } row) await SendSavedScene(row.Scene);
    }

    [RelayCommand]
    public async Task LoadSelectedSavedScene()
    {
        if (SelectedSavedScene is not { } row) return;
        if (row.Scene.AbilityId != null) await AbilitiesReady();
        if (LoadSavedScene(row.Scene))
            AppLog.Information($"\"{row.Name}\" is in the scene bar: change it, then send it or save it again.");
    }

    [RelayCommand]
    public void RenameSelectedSavedScene()
    {
        if (SelectedSavedScene is { } row) RenameSavedScene(row.Scene);
    }

    [RelayCommand]
    public void DeleteSelectedSavedScene()
    {
        if (SelectedSavedScene is { } row) DeleteSavedScene(row.Scene);
    }

    [RelayCommand]
    public void SaveCurrentScene()
    {
        if (SceneVisibility != Visibility.Visible)
        {
            MessageBox.Show("The scene is empty: add an agent, a gun skin or animations with \"Add to scene\" first.", "Save as preset",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var suggestion = string.Join(" + ", new[] { SceneAgent?.Name, SceneGun?.Name, SceneAgentAnimation?.Name }.Where(n => n != null));
        var name = InputDialog.Ask("Save as preset", "Name of this animation preset (it's kept in the Animation Presets tab):", suggestion)?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        if (SavedScenes.Exists(name) &&
            MessageBox.Show($"There's already a preset called \"{name}\". Replace it?", "Save as preset", MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        static SavedScenes.Asset? Saved(SceneAsset? asset) => asset is null
            ? null
            : new SavedScenes.Asset(asset.Item switch { AssetSelectorItem tile => tile.ObjectPath, SavedSceneItem saved => saved.ObjectPath, _ => "" },
                asset.Item.PackagePath, asset.Type, asset.Item.DisplayName, asset.Name, asset.Style?.GetPathName(),
                asset.Choices.WeaponLevel, asset.Choices.Models);
        static SavedScenes.Animation? SavedAnimation(SceneAnimation? animation) => animation is null
            ? null
            : new SavedScenes.Animation(animation.Name, animation.Item?.LibraryId, animation.Upper?.LibraryId, animation.Lower?.LibraryId, animation.Repeat);

        SavedScenes.Save(new SavedScenes.SavedScene(name, DateTime.Now, Saved(SceneAgent), Saved(SceneGun),
            SavedAnimation(SceneAgentAnimation), SavedAnimation(SceneGunAnimation),
            SceneAbility?.LibraryId, SceneAbility?.Title, SavedAnimation(SceneAbilityAnimation)));
        RefreshSavedScenes();
        AppLog.Information($"Saved as the animation preset \"{name}\" (Animation Presets tab).");
    }

    // puts a saved scene in the scene bar; false (with a message) if something in it isn't in the game files anymore
    public bool LoadSavedScene(SavedScenes.SavedScene saved)
    {
        LoadAnimations(); // the saved animations are looked up in the list
        var missing = new List<string>();

        SceneAsset? Asset(SavedScenes.Asset? asset)
        {
            if (asset is null) return null;
            if (asset.ObjectPath.Length == 0)
            {
                missing.Add(asset.Name);
                return null;
            }

            UObject? style = null;
            if (asset.StylePath != null)
                try { style = AppVM.CUE4ParseVM.Provider.LoadPackageObject(asset.StylePath); }
                catch (Exception) { missing.Add($"{asset.Name} (its variant)"); }
            return new SceneAsset(new SavedSceneItem(asset.ObjectPath, asset.PackagePath, asset.Type, asset.DisplayName), asset.Type, style,
                new ExportChoices(asset.WeaponLevel, asset.Models), asset.Name);
        }

        AnimationItem? Find(string? id) => id is null ? null : Animations.FirstOrDefault(a => a.LibraryId == id);

        SceneAnimation? Animation(SavedScenes.Animation? animation)
        {
            if (animation is null) return null;
            var item = Find(animation.ItemId);
            var upper = Find(animation.UpperId);
            var lower = Find(animation.LowerId);
            if (item is null && (upper is null || lower is null))
            {
                missing.Add(animation.Name);
                return null;
            }

            return new SceneAnimation(animation.Name, item, item is null ? upper : null, item is null ? lower : null, animation.Repeat);
        }

        SceneAgent = Asset(saved.Agent);
        SceneGun = Asset(saved.Gun);
        SceneAgentAnimation = Animation(saved.AgentAnimation);
        SceneGunAnimation = Animation(saved.GunAnimation);
        SceneAbility = saved.AbilityId is null ? null : Abilities.FirstOrDefault(a => a.LibraryId == saved.AbilityId);
        if (saved.AbilityId != null && SceneAbility is null) missing.Add(saved.AbilityName ?? "the ability");
        SceneAbilityAnimation = Animation(saved.AbilityAnimation);
        if (missing.Count == 0) return true;
        MessageBox.Show($"Some of \"{saved.Name}\" isn't in the game files anymore (a game update may have changed it), so it was left out:\n\n" +
                        string.Join("\n", missing), "Animation preset", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    public async Task SendSavedScene(SavedScenes.SavedScene saved)
    {
        if (saved.AbilityId != null) await AbilitiesReady();
        LoadSavedScene(saved);
        await SendScene();
    }

    public void RenameSavedScene(SavedScenes.SavedScene saved)
    {
        var name = InputDialog.Ask("Rename preset", $"New name for \"{saved.Name}\":", saved.Name)?.Trim();
        if (string.IsNullOrEmpty(name) || name == saved.Name) return;
        if (SavedScenes.Exists(name) &&
            MessageBox.Show($"There's already a preset called \"{name}\". Replace it?", "Rename preset", MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        SavedScenes.Rename(saved, name);
        RefreshSavedScenes();
        SelectedSavedScene = SavedSceneList.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public void DeleteSavedScene(SavedScenes.SavedScene saved)
    {
        if (MessageBox.Show($"Delete the animation preset \"{saved.Name}\"? (Only the preset; nothing in Blender is touched.)", "Delete preset",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        SavedScenes.Delete(saved);
        RefreshSavedScenes();
    }

    [RelayCommand]
    public void ClearScene()
    {
        SceneAgent = null;
        SceneGun = null;
        SceneAgentAnimation = null;
        SceneGunAnimation = null;
        SceneAbility = null;
        SceneAbilityAnimation = null;
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
        var sendGunAnimation = true;
        // a gun animation made for the other view (1st / 3rd person): a different animation, with its own timing.
        // Riot only made 3rd person gun animations where the gun's parts visibly move in 3rd person (an Operator's
        // bolt); a Vandal equip has none: in game the gun just moves with the hands.
        if (SceneAgentAnimation is { } agentPick && SceneGunAnimation is { Item: { } gunItem } gunPick &&
            (agentPick.Model == "FP") != (gunPick.Model == "GN"))
        {
            switch (AskAboutGunView(agentPick, gunItem, gunPick.Model))
            {
                case MessageBoxResult.Yes:
                    sendGunAnimation = false;
                    break;
                case MessageBoxResult.No:
                    break;
                default:
                    return;
            }
        }

        var needed = rig switch { "TP" => ECharacterModels.ThirdPerson, "FP" => ECharacterModels.FirstPerson, _ => ECharacterModels.CharacterSelect };
        if (SceneAgent != null && !agentModels.HasFlag(needed))
            warnings.Add($"The agent animation is for the {ModelOptions.First(o => o.Key == needed).Value} model, which isn't in the agent's " +
                         "\"Models\" choice: it will be skipped.");
        if (SceneAgent is null && SceneAgentAnimation != null)
            warnings.Add("There's no agent in the scene: the agent animation goes on the agent armature selected in Blender.");
        if (SceneGun is null && SceneGunAnimation != null)
            warnings.Add("There's no gun in the scene: the gun animation goes on the gun armature selected in Blender.");
        if (SceneAbility is null && SceneAbilityAnimation != null)
            warnings.Add("There's no ability model in the scene: the ability animation goes on the armature selected in Blender.");
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
            if (SceneAbility is { } ability && await ExportAbilityData(ability) is { } abilityData)
            {
                var settings = await AbilitySettings(ability);
                settings.SceneRole = "ability";
                settings.SceneTarget = SceneAgent != null ? $"agent:{rig}" : null; // held by the agent
                steps.Add(BlenderService.ExportMessage(abilityData, settings));
            }
            if (SceneAgentAnimation is { } agentAnimation && await AnimationMessage(agentAnimation, SceneAgent != null ? $"agent:{rig}" : null) is { } a)
                steps.Add(a);
            if (sendGunAnimation && SceneGunAnimation is { } gunAnimation && await AnimationMessage(gunAnimation, SceneGun != null ? "gun" : null) is { } g)
                steps.Add(g);
            if (SceneAbilityAnimation is { } abilityAnimation && await AnimationMessage(abilityAnimation, SceneAbility != null ? "ability" : null) is { } ab)
                steps.Add(ab);
            if (steps.Count == 0) return;

            var name = SceneAgent?.Name ?? SceneGun?.Name ?? SceneAbility?.Title ?? "Scene";
            BlenderService.SendScene(name, steps);
            if (SceneAgent != null) RegisterSentAsset(BuildAnimationFilterKey(EAssetType.Character, SceneAgent.Item));
            AppLog.Information($"Sent scene {name} ({steps.Count} steps) to BLENDER in {Math.Round(timer.Elapsed.TotalSeconds, 3)}s.");
            _ = Task.Run(() => MemoryHelper.ReleaseAfterLoading("After sending a scene"));
        }
        catch (Exception e)
        {
            AppLog.Error($"Scene export failed: {e.Message}");
            AppLog.Error(e.ToString()); // where it failed, for a bug report
        }
        finally
        {
            sceneExportRunning = false;
        }
    }

    // The scene's agent animation is 3rd person and the gun's 1st person (or the reverse): explains what that means
    // and asks. Yes = send without the gun animation, No = send it anyway, Cancel = go back.
    private MessageBoxResult AskAboutGunView(SceneAnimation agentPick, AnimationItem gunItem, string gunModel)
    {
        var agentView = agentPick.Model == "FP" ? "1st" : "3rd";
        var gunView = gunModel == "GN" ? "1st" : "3rd";
        var action = gunItem.Name.Split('_').Last(); // Equip, Reload, Inspect, ...
        var otherName = gunModel == "GN" ? "GNTP_" + gunItem.Name["GN_".Length..] : "GN_" + gunItem.Name["GNTP_".Length..];
        var other = Animations.FirstOrDefault(a => a.Name.Equals(otherName, StringComparison.OrdinalIgnoreCase));

        string text;
        if (other != null)
        {
            text = $"The agent animation is {agentView} person, but \"{gunItem.Title}\" is the gun's {gunView} person animation: it's timed " +
                   $"for the {gunView} person view, so it won't line up.\n\nThis gun has the right one: \"{other.Title}\" ({other.ModelTag}). " +
                   "Add that one to the scene instead (Animations tab > Add to scene).";
        }
        else if (agentPick.Model != "FP")
        {
            // which guns have a 3rd person gun animation for this action, from the game files
            var guns = Animations.Where(a => a.Name.StartsWith("GNTP_", StringComparison.OrdinalIgnoreCase) &&
                                             a.Name.EndsWith("_" + action, StringComparison.OrdinalIgnoreCase))
                .Select(a => a.Title.Split(':')[0].Trim()).Where(n => n.Length > 0).Distinct().OrderBy(n => n).ToList();
            text = $"Nothing is wrong, just good to know: this gun has no 3rd person {action.ToLowerInvariant()} animation of its own in the game files.\n\n" +
                   "In 3rd person the game doesn't animate the gun here: it simply moves with the agent's hands. " +
                   $"\"{gunItem.Title}\" is the 1st person version, timed for the 1st person arms, so on a 3rd person agent it won't line up." +
                   (guns.Count > 0 ? $"\n\nGuns that do have a 3rd person {action.ToLowerInvariant()}: {string.Join(", ", guns)}." : "");
        }
        else
        {
            text = $"The agent animation is 1st person, but \"{gunItem.Title}\" is the gun's 3rd person animation, and this gun has no " +
                   "1st person version of it: it won't line up with the arms.";
        }

        return MessageBox.Show(text + "\n\nYes: send the scene without the gun animation (the gun follows the hands, like in game)\n" +
                               "No: send it with this gun animation anyway\nCancel: go back",
            "About the gun animation", MessageBoxButton.YesNoCancel, MessageBoxImage.Information);
    }

    private static async Task<ExportData> ExportSceneAsset(SceneAsset asset)
    {
        // the item's game data is loaded again when needed (tiles free it when another one is clicked); a load can
        // fail while the game files are busy, so try again before giving up
        UObject? main = null;
        for (var attempt = 0; attempt < 5 && (main is null || main.Properties.Count == 0); attempt++)
        {
            if (attempt > 0) await Task.Delay(300);
            main = asset.Item.MainAsset;
        }

        if (main is null || main.Properties.Count == 0)
            throw new InvalidOperationException($"{asset.Name} couldn't be read from the game files; click it once in its tab and send the scene again.");
        var data = await ExportData.Create(asset.Item.Asset, asset.Type, asset.Style!, asset.Choices, main);
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
            return BlenderService.AnimationMessage(animation.Name, lowerPath, upperPath, animation.Repeat, lower!.IsLoop, upper.IsLoop, sceneTarget: target,
                sounds: await AppVM.MainVM.SoundsFor(upper, lower));
        }

        var item = animation.Item!;
        var paths = new List<string>();
        foreach (var clip in item.Kind == EAnimationKind.Sequence ? item.Clips : [item])
        {
            if (await Task.Run(() => AnimationExport.ExportPsa(clip)) is not { } path) return null;
            paths.Add(path);
        }

        return BlenderService.AnimationMessage(item.Name, paths[0], repeat: animation.Repeat, lowerLoops: item.IsLoop,
            sequencePaths: paths.Count > 1 ? paths : null, sceneTarget: target, sounds: await AppVM.MainVM.SoundsFor(item));
    }
}
