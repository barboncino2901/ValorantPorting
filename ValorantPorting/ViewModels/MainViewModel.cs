using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CUE4Parse.UE4.Assets.Exports;
using ValorantPorting.AppUtils;
using ValorantPorting.Export;
using ValorantPorting.Export.Blender;
using ValorantPorting.Services;
using ValorantPorting.Views;
using ValorantPorting.Views.Controls;
using StyleSelector = ValorantPorting.Views.Controls.StyleSelector;

namespace ValorantPorting.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StyleImage))]
    [NotifyPropertyChangedFor(nameof(StyleVisibility))]
    private IExportableAsset? currentAsset;

    public EAssetType CurrentAssetType;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StyleImage))]
    [NotifyPropertyChangedFor(nameof(StyleVisibility))]
    private List<IExportableAsset> extendedAssets = new();

    
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(LoadingVisibility))]
    private bool isReady;
    
    [ObservableProperty] private ObservableCollection<AssetSelectorItem> outfits = new();
    [ObservableProperty] private ObservableCollection<StyleSelector> styles = new();
    [ObservableProperty] private ObservableCollection<AssetSelectorItem> weapons = new();
    [ObservableProperty] private ObservableCollection<AssetSelectorItem> gunbuddies = new();
    [ObservableProperty] private ObservableCollection<AnimationItem> animations = new();
    [ObservableProperty] private AnimationItem? selectedAnimation;
    private bool animationsLoaded;

    // Animations tab filter ("Show animations for" dropdown). "Follow Blender selection" uses whatever Valorant
    // armature is selected in Blender (reported by the add-on); other entries lock the filter to one model.
    public ObservableCollection<AnimationFilterOption> AnimationFilters { get; } = new()
    {
        new AnimationFilterOption("Follow Blender selection", null, isFollow: true),
        new AnimationFilterOption("All animations", null)
    };

    [ObservableProperty] private AnimationFilterOption? selectedAnimationFilter;
    private string? followedFilterKey;
    private (string Folder, string[] Prefixes, bool SharedAgentAnimations)? activeAnimationFilter;

    public event Action? AnimationFilterChanged;

    public ImageSource StyleImage => currentAsset?.FullSource;
    public Visibility StyleVisibility => currentAsset is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility LoadingVisibility => IsReady ? Visibility.Collapsed : Visibility.Visible;

    public async Task Initialize()
    {
        await Task.Run(async () =>
        {
            var loadTime = new Stopwatch();
            loadTime.Start();

            AppVM.CUE4ParseVM =
                new CUE4ParseViewModel(AppSettings.Current.ArchivePath, AppSettings.Current.InstallType);
            await AppVM.CUE4ParseVM.Initialize();
            loadTime.Stop();

            AppLog.Information($"Finished loading game files in {Math.Round(loadTime.Elapsed.TotalSeconds, 3)}s");
            MemoryHelper.ReleaseAfterLoading("Game files loaded");
            IsReady = true;

            AppVM.AssetHandlerVM = new AssetHandlerViewModel();
            Application.Current.Dispatcher.Invoke(() =>
            {
                SelectedAnimationFilter = AnimationFilters[0];
                BlenderSelectionListener.Start(OnBlenderSelection);
            });

            await AppVM.AssetHandlerVM.Initialize();
        });
    }

    public UObject GetSelectedStyles()
    {
        var ObjectStyle = Styles.Select(style =>
            ((StyleSelectorItem)style.Options.Items[style.Options.SelectedIndex]).ObjectData).ToList();
        if (ObjectStyle.Count > 0) return ObjectStyle[0];
        return null;
    }

    [RelayCommand]
    public void Menu(string parameter)
    {
        switch (parameter)
        {
            case "Open_Assets":
                AppHelper.Launch(App.AssetsFolder.FullName);
                break;
            case "Open_Data":
                AppHelper.Launch(App.DataFolder.FullName);
                break;
            case "Open_Exports":
                AppHelper.Launch(App.ExportsFolder.FullName);
                break;
            case "File_Restart":
                AppVM.Restart();
                break;
            case "File_Quit":
                AppVM.Quit();
                break;
            case "Settings_Options":
                AppHelper.OpenWindow<SettingsView>();
                break;
            case "Settings_Startup":
                AppHelper.OpenWindow<StartupView>();
                break;
            case "Tools_Update":
                // TODO
                break;
            case "Help_Discord":
                AppHelper.Launch(Globals.DISCORD_URL);
                break;
            case "Help_GitHub":
                AppHelper.Launch(Globals.GITHUB_URL);
                break;
            case "Help_About":
                // TODO
                break;
            case "Settings_Blender":
                AppHelper.OpenWindow<BlenderView>();
                break;
        }
    }

    [RelayCommand]
    public async Task ExportBlender()
    {
        var loadTimez = new Stopwatch();
        loadTimez.Start();

        const int maxAttempts = 5;
        Export.ExportData data = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                data = await ExportData.Create(CurrentAsset.Asset, CurrentAssetType, GetSelectedStyles());
                break;
            }
            catch (Exception ex) when (attempt < maxAttempts && ex.ToString().Contains("being used by another process"))
            {
                AppLog.Warning($"Export attempt {attempt} hit a file-lock race, retrying...");
                await Task.Delay(250);
            }
        }

        if (data is null)
        {
            AppLog.Warning("Export failed after multiple retries.");
            return;
        }

        data.Name = currentAsset.DisplayName;
        var filterKey = BuildAnimationFilterKey(CurrentAssetType, currentAsset);
        var reorient = CurrentAssetType != EAssetType.Weapon;
        BlenderService.Send(data, new BlenderExportSettings
        {
            ReorientBones = reorient,
            AnimationFilterKey = filterKey
        });
        RegisterSentAsset(filterKey);
        loadTimez.Stop();
        AppLog.Information(
            $"Finished exporting {data.Name} to BLENDER in {Math.Round(loadTimez.Elapsed.TotalSeconds, 3)}s");
        _ = Task.Run(() => MemoryHelper.ReleaseAfterLoading($"After exporting {data.Name}"));
    }

    partial void OnSelectedAnimationFilterChanged(AnimationFilterOption? value) => RefreshAnimationFilter();

    private void RefreshAnimationFilter()
    {
        var option = SelectedAnimationFilter ?? AnimationFilters[0];
        var key = option.IsFollow ? followedFilterKey : option.Key;
        activeAnimationFilter = AnimationFilterOption.Parse(key);
        AnimationFilters[0].Label = followedFilterKey is null
            ? "Follow Blender selection (select a Valorant armature)"
            : $"Follow Blender selection: {AnimationFilterOption.Describe(followedFilterKey)}";
        AnimationFilterChanged?.Invoke();
    }

    private void AddAnimationFilter(string key)
    {
        if (AnimationFilters.Any(o => o.Key == key)) return;
        AnimationFilters.Add(new AnimationFilterOption(AnimationFilterOption.Describe(key), key));
    }

    // Called with the tag of the armature selected in Blender.
    public void OnBlenderSelection(string key)
    {
        if (AnimationFilterOption.Parse(key) is null) return;
        key = NormalizeFilterKey(key);
        AddAnimationFilter(key);
        if (key == followedFilterKey) return;
        followedFilterKey = key;
        RefreshAnimationFilter();
    }

    // Models imported before tagging are identified by name only (e.g. codename "Wushu"): reuse an existing entry
    // for the same model, or look the agent's display name up in the Agents tab.
    private string NormalizeFilterKey(string key)
    {
        var parts = key.Split('|');
        if (parts.Length < 4 || parts[0] != "agent") return key;

        var existing = AnimationFilters.FirstOrDefault(o => o.Key is { } k &&
            k.Split('|') is { Length: >= 4 } p && p[0] == "agent" &&
            p[1].Equals(parts[1], StringComparison.OrdinalIgnoreCase) && p[3] == parts[3]);
        if (existing?.Key is not null) return existing.Key;

        var agent = Outfits.FirstOrDefault(o => BuildAnimationFilterKey(EAssetType.Character, o) is { } k &&
            k.Split('|')[1].Equals(parts[1], StringComparison.OrdinalIgnoreCase));
        return agent is null ? key : $"agent|{parts[1]}|{agent.DisplayName.Replace('|', '/')}|{parts[3]}";
    }

    // Key describing the agent or gun being sent to Blender; the add-on stores it on the imported armatures.
    // Agents: agent|<folder>|<name> (the add-on appends TP/FP/CS per model). Weapons: weapon|Equippables/Guns/<category>/<gun>/|<name>.
    private static string? BuildAnimationFilterKey(EAssetType type, IExportableAsset asset)
    {
        var package = asset.PackagePath;
        if (package.StartsWith("/Game/")) package = package["/Game/".Length..];
        var folder = package.Contains('/') ? package[..package.LastIndexOf('/')] : package;
        var name = asset.DisplayName.Replace('|', '/');

        switch (type)
        {
            case EAssetType.Character:
                return $"agent|{folder}/|{name}";
            case EAssetType.Weapon:
            {
                // Animations for a gun and all its skins live under Equippables/Guns/<category>/<gun>/
                // (skins are usually in a subfolder of that, but not always, e.g. the standard Bandit).
                var segments = folder.Split('/');
                var isMelee = segments.Length >= 2 && segments[1].Equals("Melee", StringComparison.OrdinalIgnoreCase);
                var gunFolder = isMelee
                    ? string.Join('/', segments.Take(2))
                    : string.Join('/', segments.Take(Math.Min(4, segments.Length)));
                return $"weapon|{gunFolder}/|{name}";
            }
            default:
                return null;
        }
    }

    private void RegisterSentAsset(string? baseKey)
    {
        if (baseKey is null) return;
        if (baseKey.StartsWith("agent|"))
        {
            foreach (var variant in new[] { "TP", "FP", "CS" }) AddAnimationFilter($"{baseKey}|{variant}");
            followedFilterKey = $"{baseKey}|TP";
        }
        else
        {
            AddAnimationFilter(baseKey);
            followedFilterKey = baseKey;
        }

        RefreshAnimationFilter();
    }

    public bool MatchesAnimationContext(AnimationItem item)
    {
        if (activeAnimationFilter is not { } filter) return true;
        if (!filter.Prefixes.Any(p => item.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return false;

        var folder = item.Folder + "/";
        if (folder.StartsWith(filter.Folder, StringComparison.OrdinalIgnoreCase)) return true;

        // Shared agent animations (e.g. TP_Core_AK_* in weapon folders), but not other agents' own animations.
        return filter.SharedAgentAnimations &&
               item.Name.Contains("_Core_", StringComparison.OrdinalIgnoreCase) &&
               (!folder.StartsWith("Characters/", StringComparison.OrdinalIgnoreCase) ||
                folder.StartsWith("Characters/_", StringComparison.OrdinalIgnoreCase));
    }

    // Lists every animation sequence in the game from the asset registry (once).
    public void LoadAnimations()
    {
        if (animationsLoaded || AppVM.CUE4ParseVM is null) return;
        animationsLoaded = true;

        var registry = AppVM.CUE4ParseVM.AssetDataBuffers.Where(a => a is not null).ToList();
        var items = registry
            .Where(a => a.AssetClass.Text == "AnimSequence")
            .Select(a => new AnimationItem(a.PackageName.Text, a.AssetName.Text))
            .ToList();

        if (items.Count == 0)
        {
            // Valorant's shipped asset registry leaves animations out, so fall back to the file list:
            // every package inside a folder whose path mentions "anim".
            var topClasses = registry.GroupBy(a => a.AssetClass.Text).OrderByDescending(g => g.Count()).Take(15)
                .Select(g => $"{g.Key}={g.Count()}");
            AppLog.Information($"[Diag] Asset registry classes: {string.Join(", ", topClasses)}");

            items = AppVM.CUE4ParseVM.Provider.Files.Keys
                .Where(p => p.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) &&
                            p.Contains("/anim", StringComparison.OrdinalIgnoreCase))
                .Select(p =>
                {
                    var package = p[..^".uasset".Length];
                    var name = package[(package.LastIndexOf('/') + 1)..];
                    return new AnimationItem(package, name);
                })
                .ToList();

            var prefixes = items.GroupBy(i => i.Name.Split('_')[0]).OrderByDescending(g => g.Count()).Take(20)
                .Select(g => $"{g.Key}={g.Count()}");
            AppLog.Information($"[Diag] Files in anim folders: {items.Count}. Name prefixes: {string.Join(", ", prefixes)}");
            AppLog.Information($"[Diag] Samples: {string.Join(" | ", items.Where((_, i) => i % Math.Max(1, items.Count / 12) == 0).Take(12).Select(i => i.Folder + "/" + i.Name))}");
        }

        items = items
            .GroupBy(a => a.ObjectPath).Select(g => g.First())
            .OrderBy(a => a.Folder, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Animations = new ObservableCollection<AnimationItem>(items);
        AppLog.Information($"Animation list loaded: {items.Count} entries.");
        MemoryHelper.ReleaseAfterLoading("Animation list loaded");
    }

    [RelayCommand]
    public async Task ExportAnimationBlender()
    {
        if (SelectedAnimation is not { } item)
        {
            AppLog.Warning("Select an animation first.");
            return;
        }

        if (animationExportRunning) return; // ignore double-click spam while an export is running
        animationExportRunning = true;
        try
        {
            var timer = Stopwatch.StartNew();
            var psaPath = await Task.Run(() => AnimationExport.ExportPsa(item));
            if (psaPath is null) return;

            BlenderService.SendAnimation(item.Name, psaPath);
            AppLog.Information($"Sent animation {item.Name} to BLENDER in {Math.Round(timer.Elapsed.TotalSeconds, 3)}s (applies to the selected armature).");
            _ = Task.Run(() => MemoryHelper.ReleaseAfterLoading($"After sending {item.Name}"));
        }
        finally
        {
            animationExportRunning = false;
        }
    }

    private bool animationExportRunning;

    [RelayCommand]
    public async Task ExportUnreal()
    {
        var loadTimez = new Stopwatch();
        loadTimez.Start();
        var data = await ExportData.Create(CurrentAsset.Asset, CurrentAssetType, GetSelectedStyles());
        data.Name = currentAsset.DisplayName;
        UnrealService.Send(data);
        loadTimez.Stop();
        AppLog.Information(
            $"Finished exporting {data.Name} to UNREAL in {Math.Round(loadTimez.Elapsed.TotalSeconds, 3)}s");
    }
}