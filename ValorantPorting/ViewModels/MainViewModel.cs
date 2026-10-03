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
using ValorantPorting.Services.Endpoints;
using ValorantPorting.Views;
using ValorantPorting.Views.Controls;
using StyleSelector = ValorantPorting.Views.Controls.StyleSelector;

namespace ValorantPorting.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StyleImage))]
    [NotifyPropertyChangedFor(nameof(StyleVisibility), nameof(AssetHintVisibility))]
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
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SelectedAnimationText), nameof(SelectedAnimationVisibility))]
    private AnimationItem? selectedAnimation;

    // Riot splits many 3rd person animations into an upper body ("_UB") and a lower body ("_LB") half
    partial void OnSelectedAnimationChanged(AnimationItem? value)
    {
        UpdateRepeatVisibility();
    }

    // Combining any two animations: the legs of one ("lower body") with everything else of another ("upper body"),
    // e.g. an equip while running. Picked with a right-click in the Animations tab.
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CombineVisibility), nameof(CombineHintVisibility), nameof(CombineUpperText))]
    private AnimationItem? upperBodyPick;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CombineVisibility), nameof(CombineHintVisibility), nameof(CombineLowerText))]
    private AnimationItem? lowerBodyPick;
    public Visibility CombineVisibility => UpperBodyPick != null || LowerBodyPick != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CombineHintVisibility => CombineVisibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    public string CombineUpperText => UpperBodyPick?.Title ?? "Empty";
    public string CombineLowerText => LowerBodyPick?.Title ?? "Empty";

    [RelayCommand]
    public void UseSelectedAsUpper()
    {
        if (SelectedAnimation is { } item) UpperBodyPick = item;
        else AppLog.Warning("Click an animation in the list first.");
    }

    [RelayCommand]
    public void UseSelectedAsLower()
    {
        if (SelectedAnimation is { } item) LowerBodyPick = item;
        else AppLog.Warning("Click an animation in the list first.");
    }
    public string SelectedAnimationText => SelectedAnimation?.Title ?? "Pick an animation on the left.";
    partial void OnUpperBodyPickChanged(AnimationItem? value) => UpdateRepeatVisibility();
    partial void OnLowerBodyPickChanged(AnimationItem? value) => UpdateRepeatVisibility();

    [RelayCommand]
    public void ClearUpper() => UpperBodyPick = null;

    [RelayCommand]
    public void ClearLower() => LowerBodyPick = null;

    // How many times looping animations (runs, walks, idles) play in a row
    public List<int> RepeatOptions { get; } = [1, 2, 3, 4, 5, 6, 8, 10];
    [ObservableProperty] private int repeatCount = 1;
    // "Play ×N" next to the selected animation when it loops, and next to the combination when either half loops
    [ObservableProperty] private Visibility repeatSingleVisibility = Visibility.Collapsed;
    [ObservableProperty] private Visibility repeatCombinedVisibility = Visibility.Collapsed;

    private void UpdateRepeatVisibility()
    {
        RepeatSingleVisibility = SelectedAnimation?.IsLoop == true ? Visibility.Visible : Visibility.Collapsed;
        RepeatCombinedVisibility = UpperBodyPick?.IsLoop == true || LowerBodyPick?.IsLoop == true ? Visibility.Visible : Visibility.Collapsed;
    }

    [RelayCommand]
    public void ClearCombine()
    {
        UpperBodyPick = null;
        LowerBodyPick = null;
    }

    [RelayCommand]
    public async Task ExportCombinedBlender()
    {
        if (UpperBodyPick is not { } upper || LowerBodyPick is not { } lower)
        {
            MessageBox.Show("Pick both halves first: right-click an animation > \"Use as upper body\", and another > \"Use as lower body\".",
                "Combine animations", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (CheckCombine(upper, lower)) await SendCombined(upper, lower, $"{upper.Name} + {lower.Name}");
    }

    // whether two animations can be combined (same model, full-body skeleton); asks about odd picks
    private static bool CheckCombine(AnimationItem upper, AnimationItem lower)
    {
        const string title = "Combine animations";

        // different skeletons can't be combined (3rd person body, 1st person arms, guns, ...): the file name's first part
        // says which model an animation is for ("TP_" 3rd person, "FP_" 1st person, "CS_" character select, "GN_" gun)
        string ModelOf(AnimationItem item) => item.Name.Split('_')[0].ToUpperInvariant();
        if (ModelOf(upper) != ModelOf(lower))
        {
            MessageBox.Show($"These two animations are for different models ({upper.View} and {lower.View}), so they can't be combined.",
                title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (ModelOf(upper) is not ("TP" or "CS"))
        {
            MessageBox.Show($"Upper and lower body can only be combined on full-body (3rd person) animations, not \"{upper.View}\" ones.",
                title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var warnings = new List<string>();
        if (upper == lower) warnings.Add("Both halves are the same animation.");
        if (upper.Name.EndsWith("_LB")) warnings.Add($"\"{upper.Title}\" is a lower-body animation: as the upper body it will barely move.");
        if (lower.Name.EndsWith("_UB")) warnings.Add($"\"{lower.Title}\" is an upper-body animation: as the lower body the legs will barely move.");
        if (!upper.Name.EndsWith("_UB") && !upper.Name.EndsWith("_LB")) warnings.Add($"\"{upper.Title}\" is a full-body animation: only its upper half is used.");
        if (!lower.Name.EndsWith("_LB") && !lower.Name.EndsWith("_UB")) warnings.Add($"\"{lower.Title}\" is a full-body animation: only its legs are used.");
        return warnings.Count == 0 ||
               MessageBox.Show(string.Join("\n\n", warnings) + "\n\nCombine anyway?", title,
                   MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private async Task SendCombined(AnimationItem upper, AnimationItem lower, string name)
    {
        if (animationExportRunning) return;
        animationExportRunning = true;
        try
        {
            var timer = Stopwatch.StartNew();
            var lowerPath = await Task.Run(() => AnimationExport.ExportPsa(lower));
            var upperPath = await Task.Run(() => AnimationExport.ExportPsa(upper));
            if (lowerPath is null || upperPath is null) return;

            var sounds = await SoundsFor(upper, lower);
            BlenderService.SendAnimation(name, lowerPath, upperPath, RepeatCount, lower.IsLoop, upper.IsLoop, sounds: sounds);
            UserLibrary.AddRecent(upper.LibraryId);
            UserLibrary.AddRecent(lower.LibraryId);
            AppLog.Information($"Sent {name} (upper + lower body) to BLENDER in {Math.Round(timer.Elapsed.TotalSeconds, 3)}s.");
            _ = Task.Run(() => MemoryHelper.ReleaseAfterLoading($"After sending {name}"));
        }
        finally
        {
            animationExportRunning = false;
        }
    }

    [ObservableProperty] private ObservableCollection<MapItem> maps = new();
    [ObservableProperty] private MapItem? selectedMap;
    private bool mapsLoaded;
    private bool mapExportRunning;
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
    public event Action? AbilitiesLoaded;

    // "Show:" dropdown next to the search box: everything, only favorites, or recently sent items (newest first).
    public KeyValuePair<ELibraryFilter, string>[] LibraryFilters { get; } =
    [
        new(ELibraryFilter.All, "Show: All"),
        new(ELibraryFilter.Favorites, "★ Favorites"),
        new(ELibraryFilter.Recent, "🕘 Recent")
    ];
    [ObservableProperty] private ELibraryFilter libraryFilter = ELibraryFilter.All;
    public event Action? LibraryFilterChanged;
    partial void OnLibraryFilterChanged(ELibraryFilter value) => LibraryFilterChanged?.Invoke();

    // Abilities tab: every model in the agents' ability folders (Skye's dog, Raze's grenade, ...)
    [ObservableProperty] private ObservableCollection<AbilityItem> abilities = new();
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SelectedAbilityVisibility))] private AbilityItem? selectedAbility;
    private bool abilitiesLoaded;
    private bool abilityExportRunning;

    private static readonly System.Text.RegularExpressions.Regex AbilityModel = new(
        @"^ShooterGame/Content/Characters/[^_/][^/]*/S0/Ability_[^/]+/.*(Skelmesh|Staticmesh)\.uasset$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public void LoadAbilities()
    {
        if (abilitiesLoaded || AppVM.CUE4ParseVM is null) return;
        abilitiesLoaded = true;
        var provider = AppVM.CUE4ParseVM.Provider;
        // built in the background (about a second: it reads every ability model), so the window doesn't freeze
        Task.Run(() => BuildAbilityList(provider));
    }

    private void BuildAbilityList(CUE4Parse.FileProvider.IFileProvider provider)
    {
        ValorantNames.WaitUntilLoaded(TimeSpan.FromSeconds(10));
        var infos = new Dictionary<string, (string Name, string Key)?>(StringComparer.OrdinalIgnoreCase);
        (string Name, string Key)? InfoOf(string file)
        {
            var parts = file.Split('/'); // ShooterGame, Content, Characters, Guide, S0, Ability_Q, ...
            var folder = string.Join('/', parts.Skip(2).Take(4));
            if (!infos.TryGetValue(folder, out var info)) infos[folder] = info = AbilityItem.ReadAbilityInfo(provider, folder);
            return info;
        }

        var items = AbilityResolver.BuildList(provider, provider.Files.Keys.Where(p => AbilityModel.IsMatch(p)).ToList(), InfoOf);
        Application.Current.Dispatcher.Invoke(() =>
        {
            Abilities = new ObservableCollection<AbilityItem>(items);
            AbilitiesLoaded?.Invoke();
            AppLog.Information($"Ability list loaded: {items.Count} models.");
        });
    }

    [RelayCommand]
    public async Task ExportAbilityBlender()
    {
        if (SelectedAbility is not { } item)
        {
            AppLog.Warning("Select an ability model first.");
            return;
        }

        if (abilityExportRunning) return;
        abilityExportRunning = true;
        try
        {
            var timer = Stopwatch.StartNew();
            var data = new ExportData { Name = item.Title, Type = "Ability" };
            await ExportHelpers.ExportLock.WaitAsync(); // one export at a time (they share ExportHelpers.Tasks)
            try
            {
            await Task.Run(() =>
            {
                // parts of one model come in together, placed as in game; a rig with the models on its bones
                data.Parts.AddRange(AbilityResolver.ExportParts(AppVM.CUE4ParseVM.Provider, item));
            });
            await Task.WhenAll(ExportHelpers.Tasks.ToArray());
            ExportHelpers.Tasks.Clear();
            }
            finally
            {
                ExportHelpers.ExportLock.Release();
            }

            if (data.Parts.Count == 0)
            {
                AppLog.Warning($"{item.Title}: the model could not be read.");
                return;
            }

            BlenderService.Send(data, new BlenderExportSettings
            {
                ReorientBones = false, // like guns: ability props animate with their own bone orientation
                AnimationFilterKey = $"ability|{item.Folder}/|{item.AgentName} {item.AbilityName}: {item.Part}"
            });
            UserLibrary.AddRecent(item.LibraryId);
            AppLog.Information($"Sent {item.Title} to BLENDER in {Math.Round(timer.Elapsed.TotalSeconds, 3)}s.");
            _ = Task.Run(() => MemoryHelper.ReleaseAfterLoading($"After sending {item.Title}"));
        }
        catch (Exception ex)
        {
            AppLog.Error($"Could not send {item.Title}: {ex.Message}");
        }
        finally
        {
            abilityExportRunning = false;
        }
    }

    // Weapon upgrade level to export ("Level 1" .. fully upgraded, the default) and which agent models
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(LevelVisibility))]
    private List<string> levelOptions = new();
    [ObservableProperty] private int selectedLevel;
    public Visibility LevelVisibility => LevelOptions.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

    public List<KeyValuePair<ECharacterModels, string>> ModelOptions { get; } = new()
    {
        new(ECharacterModels.All, "All models"),
        new(ECharacterModels.ThirdPerson, "3rd person"),
        new(ECharacterModels.FirstPerson, "1st person (arms)"),
        new(ECharacterModels.CharacterSelect, "Character select"),
        new(ECharacterModels.FirstPerson | ECharacterModels.ThirdPerson, "1st + 3rd person")
    };
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(FirstPersonCameraVisibility))]
    private ECharacterModels selectedModels = ECharacterModels.All;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(FirstPersonCameraVisibility))]
    private Visibility modelVisibility = Visibility.Collapsed;

    // 1st person arms can come with a camera on their "Camera" bone: the in-game view, following the animations
    public bool FirstPersonCamera
    {
        get => AppSettings.Current.FirstPersonCamera;
        set
        {
            AppSettings.Current.FirstPersonCamera = value;
            OnPropertyChanged();
        }
    }

    // the game's sounds (gun handling, ability casts, ...) come with animations that have them
    public bool IncludeAnimationSounds
    {
        get => !AppSettings.Current.SkipAnimationSounds;
        set
        {
            AppSettings.Current.SkipAnimationSounds = !value;
            OnPropertyChanged();
        }
    }

    // the .wav files and moments of an animation's sounds (none when switched off, or it has none)
    // (a mix: the upper body's sounds first, else the legs')
    public async Task<List<AnimationSounds.Placed>?> SoundsFor(AnimationItem item, AnimationItem? lower = null)
    {
        if (!IncludeAnimationSounds) return null;
        var provider = AppVM.CUE4ParseVM.Provider;
        var sources = AnimationSounds.SourcesOf(item);
        if (lower is not null) sources.AddRange(AnimationSounds.SourcesOf(lower));
        var sounds = await Task.Run(() => AnimationSounds.Prepare(provider, sources, AnimationSounds.IsFirstPerson(item),
            AbilityResolver.Shared(provider).FilesUsing));
        if (sounds.Count > 0) AppLog.Information($"{item.Title}: {sounds.Count} game sound(s) included (Blender: Video Sequencer).");
        return sounds.Count > 0 ? sounds : null;
    }

    public Visibility FirstPersonCameraVisibility =>
        ModelVisibility == Visibility.Visible && SelectedModels.HasFlag(ECharacterModels.FirstPerson) ? Visibility.Visible : Visibility.Collapsed;

    // The current choices; the level is null when the fully upgraded skin is picked
    public ExportChoices GetExportChoices() => new(
        CurrentAssetType == EAssetType.Weapon && LevelOptions.Count > 1 && SelectedLevel < LevelOptions.Count - 1 ? SelectedLevel : null,
        CurrentAssetType == EAssetType.Character ? SelectedModels : ECharacterModels.All);

    // Update banner: shown when GitHub has a newer release (checked at startup and from Help > Check for updates)
    [ObservableProperty] private Visibility updateVisibility = Visibility.Collapsed;
    [ObservableProperty] private string updateText = "";
    [ObservableProperty] private bool updateIdle = true;
    private UpdateService.Release? availableUpdate;

    public async Task CheckForUpdates(bool manual)
    {
        if (!manual && !UpdateService.IsReleaseBuild) return;
        var release = await UpdateService.FindNewerReleaseAsync();
        if (release is null)
        {
            if (manual)
                MessageBox.Show($"You have the latest version ({UpdateService.CurrentVersion.ToString(3)}), or GitHub couldn't be reached.",
                    "Check for updates", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        availableUpdate = release;
        UpdateText = $"Version {release.Version.ToString(3)} is available (you have {UpdateService.CurrentVersion.ToString(3)}).";
        UpdateVisibility = Visibility.Visible;
    }

    [RelayCommand]
    public async Task InstallUpdate()
    {
        if (availableUpdate is not { } release || !UpdateIdle) return;
        UpdateIdle = false;
        var status = new Progress<string>(text => UpdateText = text);
        if (await UpdateService.InstallAsync(release, status))
        {
            Application.Current.Shutdown(); // the new version is running
            return;
        }

        UpdateIdle = true;
        UpdateText = $"Version {release.Version.ToString(3)} is available, but the update failed. Try again, or download it from GitHub.";
    }

    // the new version's notes (its GitHub release text, from CHANGELOG.md); the release page if it has none
    [RelayCommand]
    public void ShowUpdateNotes()
    {
        if (availableUpdate is not { } release || string.IsNullOrWhiteSpace(release.Notes))
        {
            AppHelper.Launch(availableUpdate?.PageUrl ?? UpdateService.ReleasesPage);
            return;
        }

        ReleaseNotes.Show("What's new", $"What's new in version {release.Version.ToString(3)}", release.Notes, release.PageUrl);
    }

    [RelayCommand]
    public void DismissUpdate() => UpdateVisibility = Visibility.Collapsed;

    public ImageSource StyleImage => currentAsset?.FullSource;

    // " (Level 2)" / " (1st person)" so different picks of the same item get their own Blender collection
    private string ExportNameSuffix()
    {
        var choices = GetExportChoices();
        if (choices.WeaponLevel is { } level) return $" ({LevelOptions[level]})";
        if (choices.Models != ECharacterModels.All) return $" ({ModelOptions.First(o => o.Key == choices.Models).Value})";
        return "";
    }
    public Visibility StyleVisibility => currentAsset is null ? Visibility.Collapsed : Visibility.Visible;

    // The right column: the picked agent/skin's options, or the panel of the Animations / Abilities / Maps tab
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnimationPanelVisibility), nameof(AbilityPanelVisibility), nameof(MapPanelVisibility),
        nameof(ScenePanelVisibility), nameof(SoundPanelVisibility), nameof(AssetHintVisibility))]
    private EAssetType activeTab;
    public Visibility ScenePanelVisibility => ActiveTab == EAssetType.Scene ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AnimationPanelVisibility => ActiveTab == EAssetType.Animation ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AbilityPanelVisibility => ActiveTab == EAssetType.Ability ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MapPanelVisibility => ActiveTab == EAssetType.Map ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AssetHintVisibility =>
        currentAsset is null && ActiveTab is EAssetType.Character or EAssetType.Weapon or EAssetType.GunBuddy ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SelectedAnimationVisibility => SelectedAnimation is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SelectedAbilityVisibility => SelectedAbility is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility LoadingVisibility => IsReady ? Visibility.Collapsed : Visibility.Visible;

    public async Task Initialize()
    {
        await Task.Run(async () =>
        {
            var loadTime = new Stopwatch();
            loadTime.Start();

            ValorantNames.StartLoading();
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
                BlenderSelectionListener.AddonVersionSeen += OnAddonVersionSeen;
                BlenderSelectionListener.Start(OnBlenderSelection);
            });

            await AppVM.AssetHandlerVM.Initialize();
        });
        _ = CheckForUpdates(manual: false);
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
            case "Help_Update":
                _ = CheckForUpdates(manual: true);
                break;
            case "Help_Addon":
                UpdateAddonInBlender();
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

        if (CurrentAsset is not { } sending) return;
        var sendingType = CurrentAssetType;
        var sendingStyle = GetSelectedStyles();
        var sendingChoices = GetExportChoices();
        var sendingMain = sending.MainAsset; // captured now: clicking another tile meanwhile must not change the export
        const int maxAttempts = 5;
        Export.ExportData data = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                data = await ExportData.Create(sending.Asset, sendingType, sendingStyle, sendingChoices, sendingMain);
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

        data.Name = currentAsset.DisplayName + ExportNameSuffix();
        var filterKey = BuildAnimationFilterKey(CurrentAssetType, currentAsset);
        var reorient = CurrentAssetType != EAssetType.Weapon;
        BlenderService.Send(data, new BlenderExportSettings
        {
            ReorientBones = reorient,
            AnimationFilterKey = filterKey,
            FirstPersonCamera = CurrentAssetType == EAssetType.Character && FirstPersonCamera &&
                                GetExportChoices().Models.HasFlag(ECharacterModels.FirstPerson)
        });
        RegisterSentAsset(filterKey);
        if (currentAsset is ILibraryItem sentItem) UserLibrary.AddRecent(sentItem.LibraryId);
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
    // Blender running an older add-on than this app: new things (scenes, ...) fail there, so say how to update it
    [ObservableProperty] private Visibility addonWarningVisibility = Visibility.Collapsed;
    [ObservableProperty] private Visibility addonUpdateButtonVisibility = Visibility.Collapsed;
    [ObservableProperty] private string addonWarningText = "";

    private void OnAddonVersionSeen(string version)
    {
        var expected = BlenderAddonInstaller.BuiltInVersion ?? UpdateService.CurrentVersion;
        if (System.Version.TryParse(version, out var addon) && addon >= new Version(expected.Major, expected.Minor, Math.Max(0, expected.Build))) return;
        if (BlenderAddonInstaller.InstalledAddonsUpToDate() && BlenderAddonInstaller.FindBlenders().Any(b => b.AddonVersion != null))
        {
            // the new add-on is installed, Blender just hasn't loaded it yet
            AddonWarningText = $"Blender is still running the old Valorant Porting add-on ({version}). The new one ({expected.ToString(3)}) is " +
                               "already installed: close Blender and open it again.";
            AddonUpdateButtonVisibility = Visibility.Collapsed;
        }
        else
        {
            AddonWarningText = $"Blender is running an older Valorant Porting add-on ({version}) than this app ({expected.ToString(3)}). " +
                               "Click \"Update add-on in Blender\", then close Blender and open it again.";
            AddonUpdateButtonVisibility = Visibility.Visible;
        }

        AddonWarningVisibility = Visibility.Visible;
        AppLog.Warning(AddonWarningText);
    }

    // Help menu / banner: puts the built-in add-on into Blender 5+ (updates older copies; a first install goes into
    // the newest Blender and has to be ticked there once)
    [RelayCommand]
    public void UpdateAddonInBlender()
    {
        const string title = "Blender add-on";
        var version = BlenderAddonInstaller.BuiltInVersion?.ToString(3) ?? "?";
        try
        {
            var updated = BlenderAddonInstaller.UpdateInstalledAddons();
            var blenders = BlenderAddonInstaller.FindBlenders();
            if (updated.Count > 0)
            {
                AddonWarningText = $"The add-on was updated to {version} in Blender {string.Join(", ", updated)}: close Blender and open it again.";
                AddonUpdateButtonVisibility = Visibility.Collapsed;
                MessageBox.Show($"The Valorant Porting add-on was updated to {version} in Blender {string.Join(", ", updated)}.\n\n" +
                                "If Blender is open, close it and open it again: Blender only loads add-ons when it starts.",
                    title, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (blenders.Any(b => b.AddonVersion != null))
            {
                MessageBox.Show($"The add-on in Blender {string.Join(", ", blenders.Where(b => b.AddonVersion != null).Select(b => b.Blender.ToString(2)))} " +
                                $"is already version {version} or newer.\n\nIf Blender still uses an old one, close Blender and open it again.",
                    title, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (BlenderAddonInstaller.InstallIntoNewestBlender() is { } blender)
            {
                MessageBox.Show($"The Valorant Porting add-on {version} was copied into Blender {blender}.\n\nOne last step, in Blender:\n" +
                                "1. If Blender is open, close it and open it again.\n2. Edit > Preferences > Add-ons, search \"Valorant\".\n3. Tick the box next to Valorant Porting.",
                    title, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("No Blender 5 was found for this Windows user. Install the add-on by hand: in Blender, Edit > Preferences > Add-ons > " +
                                "Install from Disk > the zip in the app's \"Blender Add-ons\" folder.",
                    title, MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"The add-on couldn't be put into Blender ({ex.Message}).\n\nInstall it by hand: in Blender, Edit > Preferences > Add-ons > " +
                            "Install from Disk > the zip in the app's \"Blender Add-ons\" folder.", title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    public void DismissAddonWarning() => AddonWarningVisibility = Visibility.Collapsed;

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
        if (agent is not null) return $"agent|{parts[1]}|{agent.DisplayName.Replace('|', '/')}|{parts[3]}";
        return ValorantNames.Agents.TryGetValue(parts[2], out var named) ? $"agent|{parts[1]}|{named.Name}|{parts[3]}" : key;
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

    // Lists the maps (names from valorant-api.com) that exist in the local game files.
    public void LoadMaps()
    {
        if (mapsLoaded || AppVM.CUE4ParseVM is null) return;
        mapsLoaded = true;
        ValorantNames.WaitUntilLoaded(TimeSpan.FromSeconds(10));

        var files = AppVM.CUE4ParseVM.Provider.Files;
        var items = ValorantNames.Maps
            .Where(m => files.ContainsKey("ShooterGame/Content/" + m.MapUrl["/Game/".Length..] + ".umap"))
            .GroupBy(m => m.MapUrl).Select(g => g.First())
            .Select(m => new MapItem(m.Name, m.MapUrl, m.Description))
            .OrderBy(m => m.Details.StartsWith("Other") ? 1 : 0)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Maps = new ObservableCollection<MapItem>(items);
        AppLog.Information($"Map list loaded: {items.Count} maps.");
    }

    [RelayCommand]
    public async Task ExportMapBlender()
    {
        if (SelectedMap is not { } map)
        {
            AppLog.Warning("Select a map first.");
            return;
        }

        if (mapExportRunning) return;
        mapExportRunning = true;
        try
        {
            var exported = await Task.Run(() => MapExport.ExportUsd(map));
            if (exported is not { } files) return;

            BlenderService.SendMap(map.Name, files.Scene, files.Materials);
            UserLibrary.AddRecent(map.LibraryId);
            AppLog.Information($"Sent map {map.Name} to BLENDER. Blender may freeze for a while during the import.");
        }
        finally
        {
            mapExportRunning = false;
            _ = Task.Run(() => MemoryHelper.ReleaseAfterLoading("After map export"));
        }
    }

    // Lists every animation sequence in the game from the asset registry (once).
    public void LoadAnimations()
    {
        if (animationsLoaded || AppVM.CUE4ParseVM is null) return;
        animationsLoaded = true;
        ValorantNames.WaitUntilLoaded(TimeSpan.FromSeconds(10));
        // ability names with their current keys (the files use old keybinds for some agents)
        try
        {
            var slots = SoundAbilities.Shared(AppVM.CUE4ParseVM.Provider, ValorantNames.Agents);
            AnimationNamer.AbilityLookup = (agent, letter) => slots.ForFolder(agent, letter)?.ToString();
        }
        catch (Exception ex)
        {
            AppLog.Warning($"Ability keys for animation names unavailable ({ex.Message}).");
        }

        var registry = AppVM.CUE4ParseVM.AssetDataBuffers.Where(a => a is not null).ToList();
        var items = registry
            .Where(a => a.AssetClass.Text == "AnimSequence")
            .Select(a => new AnimationItem(a.PackageName.Text, a.AssetName.Text))
            .ToList();

        if (items.Count == 0)
        {
            // Valorant's shipped asset registry leaves animations out, so fall back to the file list:
            // every package inside a folder whose path mentions "anim".
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
        }

        items = items
            .GroupBy(a => a.ObjectPath).Select(g => g.First())
            .OrderBy(a => a.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        items.AddRange(AnimationMontages.FullBodyPairs(items));
        items = items
            .OrderBy(a => a.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Animations = new ObservableCollection<AnimationItem>(items);
        AppLog.Information($"Animation list loaded: {items.Count} entries.");
        MemoryHelper.ReleaseAfterLoading("Animation list loaded");

        // montages: hide the ones that only repeat animations already listed, mark the ones playing several in a row
        var provider = AppVM.CUE4ParseVM.Provider;
        Task.Run(() =>
        {
            var result = AnimationMontages.ClassifyCached(provider, items, MapExport.LastGameUpdate());
            Application.Current.Dispatcher.Invoke(() =>
            {
                foreach (var (montage, clips) in result.Sequences) montage.MakeSequence(clips);
                foreach (var (clip, montage) in result.Wrappers) clip.Wrapper = montage;
                Animations = new ObservableCollection<AnimationItem>(items.Where(i => !result.Hidden.Contains(i)));
                AnimationFilterChanged?.Invoke();
                AppLog.Information($"Animation list: {result.Hidden.Count} duplicate montages hidden, {result.Sequences.Count} sequences.");
            });
            MemoryHelper.ReleaseAfterLoading("Animation montages sorted");
        });
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
            if (item.Kind == EAnimationKind.FullBody)
            {
                animationExportRunning = false;
                await SendCombined(item.UpperHalf!, item.LowerHalf!, item.Name);
                UserLibrary.AddRecent(item.LibraryId);
                return;
            }

            var timer = Stopwatch.StartNew();
            var clips = item.Kind == EAnimationKind.Sequence ? item.Clips : [item];
            var paths = new List<string>();
            foreach (var clip in clips)
            {
                var path = await Task.Run(() => AnimationExport.ExportPsa(clip));
                if (path is null) return;
                paths.Add(path);
            }

            var sounds = await SoundsFor(item);
            BlenderService.SendAnimation(item.Name, paths[0], repeat: item.IsLoop ? RepeatCount : 1, lowerLoops: item.IsLoop,
                sequencePaths: paths.Count > 1 ? paths : null, sounds: sounds);
            UserLibrary.AddRecent(item.LibraryId);
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
        var data = await ExportData.Create(CurrentAsset.Asset, CurrentAssetType, GetSelectedStyles(), GetExportChoices());
        data.Name = currentAsset.DisplayName + ExportNameSuffix();
        UnrealService.Send(data);
        loadTimez.Stop();
        AppLog.Information(
            $"Finished exporting {data.Name} to UNREAL in {Math.Round(loadTimez.Elapsed.TotalSeconds, 3)}s");
    }
}