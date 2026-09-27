using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CUE4Parse.UE4.AssetRegistry.Objects;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using ValorantPorting.AppUtils;
using ValorantPorting.Views.Controls;

namespace ValorantPorting.ViewModels;

public class AssetHandlerViewModel
{
    private readonly AssetHandlerData _buddyHandler = new()
    {
        AssetType = EAssetType.GunBuddy,
        TargetCollection = AppVM.MainVM.Gunbuddies,
        ClassNames = new List<string> { "EquippableCharmDataAsset" },
        IconGetter = GetDisplayIcon
    };

    private readonly AssetHandlerData _characterHandler = new()
    {
        AssetType = EAssetType.Character,
        TargetCollection = AppVM.MainVM.Outfits,
        ClassNames = new List<string> { "CharacterDataAsset" },
        IconGetter = GetDisplayIcon
    };

    private readonly AssetHandlerData _weaponHandler = new()
    {
        AssetType = EAssetType.Weapon,
        TargetCollection = AppVM.MainVM.Weapons,
        ClassNames = new List<string> { "EquippableSkinDataAsset" },
        IconGetter = GetDisplayIcon
    };

    // Newer items (e.g. Outlaw, Bandit, Warden) store DisplayIcon as a soft reference instead of a hard one.
    private static UTexture2D? GetDisplayIcon(UObject uiAsset)
    {
        if (uiAsset.TryGetValue(out UTexture2D? previewImage, "DisplayIcon"))
            return previewImage;
        if (uiAsset.TryGetValue(out FSoftObjectPath softIcon, "DisplayIcon") &&
            softIcon.TryLoad(AppVM.CUE4ParseVM.Provider, out UTexture2D? softImage))
            return softImage;
        return null;
    }

    public readonly Dictionary<EAssetType, AssetHandlerData> Handlers;


    public AssetHandlerViewModel()
    {
        Handlers = new Dictionary<EAssetType, AssetHandlerData>
        {
            { EAssetType.Character, _characterHandler },
            { EAssetType.Weapon, _weaponHandler },
            { EAssetType.GunBuddy, _buddyHandler },
        };
    }

    public async Task Initialize()
    {
        await _characterHandler.Execute(); // default tab
    }
}

public class AssetHandlerData
{
    public EAssetType AssetType;
    public List<string> ClassNames;
    public Func<UObject, UTexture2D?> IconGetter;
    public ObservableCollection<AssetSelectorItem> TargetCollection;
    public bool HasStarted { get; private set; }
    public Pauser PauseState { get; } = new();

    // Some characters have 2 distinct underlying asset paths that both resolve to the same
    // visible entry (same UI name/icon) - dedupe on that name so it only loads once.
    private readonly ConcurrentDictionary<string, byte> _seenDisplayIds = new();

    public async Task Execute()
    {
        if (HasStarted) return;
        HasStarted = true;

        var cue4ParseVm = AppVM.CUE4ParseVM;
        if (cue4ParseVm is null || cue4ParseVm.AssetDataBuffers is null || cue4ParseVm.AssetDataBuffers.Count == 0)
        {
            AppLog.Warning("Asset handler could not initialize because no asset data buffers were available.");
            return;
        }

        if (TargetCollection is null || ClassNames is null || IconGetter is null)
        {
            AppLog.Warning("Asset handler could not initialize because one or more required configuration values were missing.");
            return;
        }

        var items = new List<FAssetData>();
        var seenTypes = new HashSet<string>();
        var addedPaths = new HashSet<string>();
        foreach (var variable in cue4ParseVm.AssetDataBuffers)
        {
            if (variable is null || variable.TagsAndValues is null) continue;

            foreach (var tagsAndValue in variable.TagsAndValues)
            {
                if (tagsAndValue.Key.PlainText == "PrimaryAssetType")
                    seenTypes.Add(tagsAndValue.Value);

                if (ClassNames.Contains(tagsAndValue.Value) && tagsAndValue.Key.PlainText == "PrimaryAssetType")
                {
                    var objectPath = variable.ObjectPath;
                    if (objectPath.Contains("/_Core/", StringComparison.OrdinalIgnoreCase)) continue; // base templates, not real items
                    var itemKey = objectPath.EndsWith("_C") ? objectPath[..^2] : objectPath; // "X" and "X_C" are the same item
                    if (addedPaths.Add(itemKey))
                        items.Add(variable);
                }
            }
        }

        if (items.Count == 0)
        {
            AppLog.Warning($"No items found for {string.Join(", ", ClassNames)}. Available PrimaryAssetType values: {string.Join(", ", seenTypes)}");
        }

        AppLog.Information($"{AssetType} handler found {items.Count} matching items.");

        await Parallel.ForEachAsync(items, async (data, token) => //load if found
        {
            await DoLoad(data);
        });

        MemoryHelper.ReleaseAfterLoading($"{AssetType} tab loaded");
    }

    // UIData may be a hard or (on newer items) a soft class reference.
    private static UObject? ResolveUiData(UObject asset)
    {
        if (!asset.TryGetValue(out UBlueprintGeneratedClass? uiObject, "UIData") &&
            asset.TryGetValue(out FSoftObjectPath softUiData, "UIData"))
        {
            softUiData.TryLoad(AppVM.CUE4ParseVM.Provider, out uiObject);
        }

        return uiObject?.ClassDefaultObject?.Load();
    }

    private async Task DoLoad(FAssetData data, bool random = false)
    {
        await PauseState.WaitIfPaused();
        var actualAsset = new UObject();
        var uiAsset = new UObject();
        var firstTag = data.ObjectPath;

        if (firstTag.Contains("NPE") || firstTag.Contains("Random")) return;

        try
        {
            actualAsset = AppVM.CUE4ParseVM.Provider.LoadPackageObject(firstTag);
        }
        catch
        {
            try
            {
                actualAsset = AppVM.CUE4ParseVM.Provider.LoadPackageObject(firstTag + "_C");
            }
            catch (Exception ex2)
            {
                AppLog.Warning($"[{AssetType}] LoadPackageObject failed even with _C fallback for: {firstTag}\n{ex2}");
                return;
            }
        }
        if (actualAsset == null) return;

        if (actualAsset is not UBlueprintGeneratedClass uBlueprintGeneratedClass)
        {
            AppLog.Warning($"[{AssetType}] Loaded asset was not a UBlueprintGeneratedClass for: {firstTag} (actual type: {actualAsset.GetType().Name})");
            return;
        }

        var classDefaultObject = uBlueprintGeneratedClass.ClassDefaultObject?.Load();
        if (classDefaultObject == null)
        {
            AppLog.Warning($"[{AssetType}] ClassDefaultObject was null/failed to load for: {firstTag}");
            return;
        }

        actualAsset = classDefaultObject;
        var mainA = actualAsset;

        var uiDefaultObject = ResolveUiData(actualAsset);
        if (uiDefaultObject != null)
            uiAsset = uiDefaultObject;
        else
            AppLog.Warning($"[{AssetType}] Could not resolve UIData for: {firstTag}");
        UObject? levelUiAsset = null;

        // switch on asset type
        var loadable = "None";
        switch (AssetType)
        {
            case EAssetType.Character:
                loadable = "Character";
                break;
            case EAssetType.Weapon:
            {
                var hasLevels = actualAsset.TryGetValue<UBlueprintGeneratedClass[]>(out var bGg, "Levels");
                if (!hasLevels)
                {
                    AppLog.Warning($"[Weapon] No 'Levels' property found for: {firstTag}");
                    return;
                }
                if (bGg is not { Length: > 0 })
                {
                    AppLog.Warning($"[Weapon] 'Levels' property was empty for: {firstTag}");
                    return;
                }
                var weaponDefaultObject = bGg[0]?.ClassDefaultObject?.Load();
                if (weaponDefaultObject is null)
                {
                    AppLog.Warning($"[Weapon] Levels[0].ClassDefaultObject.Load() returned null for: {firstTag}");
                    return;
                }

                actualAsset = weaponDefaultObject;
                levelUiAsset = ResolveUiData(weaponDefaultObject); // some skins only have an icon on their first level
                loadable = "None";
                break;
            }
            case EAssetType.GunBuddy:
            {
                if (actualAsset.TryGetValue<UBlueprintGeneratedClass[]>(out var bGb, "Levels") &&
                    bGb is { Length: > 0 } &&
                    bGb[0]?.ClassDefaultObject?.Load() is { } buddyDefaultObject)
                {
                    actualAsset = buddyDefaultObject;
                }
                else
                {
                    return;
                }

                loadable = "CharmAttachment";
                break;
            }
        }

        if (loadable != "None")
        {
            if (actualAsset.TryGetValue(out UBlueprintGeneratedClass? blueprintObject, loadable))
            {
                var blueprintDefaultObject = blueprintObject?.ClassDefaultObject?.Load();
                if (blueprintDefaultObject != null)
                    actualAsset = blueprintDefaultObject;
                else
                    return;
            }
            else
            {
                return;
            }
        }

        var previewImage = IconGetter(uiAsset) ?? (levelUiAsset is null ? null : IconGetter(levelUiAsset));
        if (previewImage is null)
        {
            AppLog.Warning($"[{AssetType}] No DisplayIcon found, skipping: {firstTag}");
            return;
        }

        var dedupeKey = uiAsset.Name;
        if (!string.IsNullOrEmpty(dedupeKey) && !_seenDisplayIds.TryAdd(dedupeKey, 0))
            return; // already added this one under a different asset path, skip the duplicate

        await Application.Current.Dispatcher.InvokeAsync(
            () => TargetCollection.Add(new AssetSelectorItem(actualAsset, uiAsset, mainA, previewImage, random)),
            DispatcherPriority.Background);
    }
}
