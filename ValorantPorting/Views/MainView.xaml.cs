using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.Engine;
using ValorantPorting.AppUtils;
using ValorantPorting.Export;
using ValorantPorting.Services;
using ValorantPorting.ViewModels;
using ValorantPorting.Views.Controls;
using StyleSelector = ValorantPorting.Views.Controls.StyleSelector;

namespace ValorantPorting.Views;

public partial class MainView
{
    public MainView()
    {
        InitializeComponent();
        AppVM.MainVM = new MainViewModel();
        DataContext = AppVM.MainVM;

        AppLog.Logger = LoggerRtb;
        AppVM.MainVM.AnimationFilterChanged += () => ApplySearchFilter(AnimationList, SearchText);
        AppVM.MainVM.AbilitiesLoaded += () => ApplySearchFilter(AbilityList, SearchText);
        AppVM.MainVM.LibraryFilterChanged += RefreshListFilters;
        // favorites/recent changed (maybe from an export task): re-filter/re-sort when a library view is shown
        UserLibrary.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            if (AppVM.MainVM.LibraryFilter != ELibraryFilter.All) RefreshListFilters();
        });
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (App.StartedAfterUpdate)
        {
            UpdateService.ConfirmStarted();
            const string addonNote = "The Blender add-on was updated too (in the \"Blender Add-ons\" folder). Install it in Blender: " +
                                     "Edit > Preferences > Add-ons > Install from Disk, then restart Blender.";
            var version = UpdateService.CurrentVersion.ToString(3);
            if (ReleaseNotes.ForThisVersion() is { } notes)
                ReleaseNotes.Show("Update installed", $"Valorant Porting was updated to version {version}.\n\n{addonNote}\n\nWhat's new:", notes);
            else
                MessageBox.Show($"Valorant Porting was updated to version {version}.\n\n{addonNote}",
                    "Update installed", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        if (string.IsNullOrWhiteSpace(AppSettings.Current.ArchivePath))
        {
            AppHelper.OpenWindow<StartupView>();
            return;
        }

        await AppVM.MainVM.Initialize();
    }

    private async void OnAssetTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not TabControl tabControl) return;
        if (AppVM.AssetHandlerVM is null) return;

        var assetType = (EAssetType)tabControl.SelectedIndex;
        var handlers = AppVM.AssetHandlerVM.Handlers;
        AppVM.MainVM.ActiveTab = assetType; // the right column follows the tab

        if (assetType == EAssetType.Map)
        {
            foreach (var handlerData in handlers.Values) handlerData.PauseState.Pause();
            AppVM.MainVM.CurrentAsset = null;
            AppVM.MainVM.Styles.Clear();
            AppVM.MainVM.LoadMaps();
            ApplySearchFilter(MapList, SearchText);
            DiscordService.Update(assetType);
            AppVM.MainVM.CurrentAssetType = assetType;
            return;
        }

        if (assetType == EAssetType.Ability)
        {
            foreach (var handlerData in handlers.Values) handlerData.PauseState.Pause();
            AppVM.MainVM.CurrentAsset = null;
            AppVM.MainVM.Styles.Clear();
            AppVM.MainVM.LoadAbilities();
            ApplySearchFilter(AbilityList, SearchText);
            DiscordService.Update(assetType);
            AppVM.MainVM.CurrentAssetType = assetType;
            return;
        }

        if (assetType == EAssetType.Animation)
        {
            foreach (var handlerData in handlers.Values) handlerData.PauseState.Pause();
            AppVM.MainVM.CurrentAsset = null;
            AppVM.MainVM.Styles.Clear();
            AppVM.MainVM.LoadAnimations();
            ApplySearchFilter(AnimationList, SearchText);
            DiscordService.Update(assetType);
            AppVM.MainVM.CurrentAssetType = assetType;
            return;
        }

        foreach (var (handlerType, handlerData) in handlers)
            if (handlerType == assetType)
                handlerData.PauseState.Unpause();
            else
                handlerData.PauseState.Pause();

        if (!handlers[assetType].HasStarted) await handlers[assetType].Execute();

        DiscordService.Update(assetType);
        AppVM.MainVM.CurrentAssetType = assetType;
    }

    private async void OnStyleSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox listBox) return;
        if (listBox.SelectedItem is null) return;
        var selected = (AssetSelectorItem)listBox.SelectedItem;
    }

    private string SearchText = string.Empty;

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        SearchText = ((TextBox)sender).Text;
        RefreshListFilters();
    }

    private void RefreshListFilters()
    {
        foreach (var tab in AssetControls.Items.OfType<TabItem>())
        {
            var listBox = tab.Content as ListBox ?? (tab.Content as Grid)?.Children.OfType<ListBox>().FirstOrDefault();
            if (listBox is null) continue;
            ApplySearchFilter(listBox, SearchText);
        }
    }

    private static void ApplySearchFilter(ListBox listBox, string text)
    {
        var hasText = !string.IsNullOrWhiteSpace(text);
        var library = AppVM.MainVM.LibraryFilter;
        var animationSearch = AnimationSearch.Parse(text);
        listBox.Items.Filter = o =>
        {
            if (o is ILibraryItem item && !UserLibrary.Matches(item.LibraryId, library)) return false;
            return o switch
            {
                AssetSelectorItem asset => !hasText || asset.Match(text),
                // favorites/recent show across all models, so the "Show animations for" filter doesn't hide them
                AnimationItem animation => MatchesSearch(animation, animationSearch) &&
                                           (library != ELibraryFilter.All || AppVM.MainVM.MatchesAnimationContext(animation)),
                MapItem map => !hasText || map.Match(text),
                AbilityItem ability => !hasText || ability.Match(text),
                _ => true
            };
        };

        // Animations while searching: best match first
        if (System.Windows.Data.CollectionViewSource.GetDefaultView(listBox.ItemsSource) is System.Windows.Data.ListCollectionView view &&
            listBox.Name == "AnimationList")
        {
            var ranked = animationSearch != null && library != ELibraryFilter.Recent;
            if (ranked) view.CustomSort = BestMatchFirst.Instance;
            else if (view.CustomSort != null) view.CustomSort = null;
            if (ranked) return;
        }

        // Recent: newest first; otherwise the list's normal order
        var sort = listBox.Items.SortDescriptions;
        var wantRecent = library == ELibraryFilter.Recent;
        if (wantRecent)
        {
            using (listBox.Items.DeferRefresh())
            {
                sort.Clear();
                sort.Add(new System.ComponentModel.SortDescription(nameof(ILibraryItem.RecentRank), System.ComponentModel.ListSortDirection.Ascending));
            }
        }
        else if (sort.Count > 0)
        {
            sort.Clear();
        }
    }

    private static bool MatchesSearch(AnimationItem animation, AnimationSearch? search)
    {
        if (search is null) return true;
        var score = search.Score(animation);
        if (score is null) return false;
        animation.SearchScore = score.Value;
        return true;
    }

    private sealed class BestMatchFirst : System.Collections.IComparer
    {
        public static readonly BestMatchFirst Instance = new();

        public int Compare(object? x, object? y) =>
            ((y as AnimationItem)?.SearchScore ?? 0).CompareTo((x as AnimationItem)?.SearchScore ?? 0);
    }

    // Right-click menu on tiles, animations and maps: add/remove favorite.
    private static ILibraryItem? LibraryItemOf(ContextMenu? menu) =>
        menu?.PlacementTarget is ListBoxItem container
            ? container.Content as ILibraryItem ?? container.DataContext as ILibraryItem
            : null;

    private void OnLibraryMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || menu.Items[0] is not MenuItem entry) return;
        var item = LibraryItemOf(menu);
        entry.IsEnabled = item is not null;
        entry.Header = item?.IsFavorite == true ? "Remove from favorites" : "Add to favorites";
    }

    private void OnToggleFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Parent: ContextMenu menu }) return;
        if (LibraryItemOf(menu) is not { } item) return;
        item.IsFavorite = UserLibrary.ToggleFavorite(item.LibraryId);
    }


    private void OnUseAsUpperBodyClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Parent: ContextMenu menu } && LibraryItemOf(menu) is AnimationItem item)
            AppVM.MainVM.UpperBodyPick = item;
    }

    private void OnUseAsLowerBodyClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Parent: ContextMenu menu } && LibraryItemOf(menu) is AnimationItem item)
            AppVM.MainVM.LowerBodyPick = item;
    }

    private void OnAbilityDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AppVM.MainVM.SelectedAbility is not null)
            AppVM.MainVM.ExportAbilityBlenderCommand.Execute(null);
    }

    private void OnAnimationDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AppVM.MainVM.SelectedAnimation is not null)
            AppVM.MainVM.ExportAnimationBlenderCommand.Execute(null);
    }

    // Level picker for weapon skins with upgrade levels, model picker for agents
    private static void ShowExportChoices(AssetSelectorItem selected)
    {
        var vm = AppVM.MainVM;
        var levels = vm.CurrentAssetType == EAssetType.Weapon
            ? selected.MainAsset.GetOrDefault("Levels", Array.Empty<UBlueprintGeneratedClass>())
            : Array.Empty<UBlueprintGeneratedClass>();
        vm.LevelOptions = levels.Length > 1
            ? Enumerable.Range(1, levels.Length).Select(i => i == levels.Length ? $"Level {i} (max)" : $"Level {i}").ToList()
            : new List<string>();
        vm.SelectedLevel = Math.Max(0, levels.Length - 1);
        vm.ModelVisibility = vm.CurrentAssetType == EAssetType.Character ? Visibility.Visible : Visibility.Collapsed;
        vm.AddToSceneVisibility = vm.CurrentAssetType is EAssetType.Character or EAssetType.Weapon ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnAssetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox listBox) return;
        if (listBox.SelectedItem is null) return;
        var selected = (AssetSelectorItem)listBox.SelectedItem;

        if (AppVM.MainVM.CurrentAsset is AssetSelectorItem previous && !ReferenceEquals(previous, selected))
            previous.ReleaseGameData();
        AppVM.MainVM.CurrentAsset = selected;
        AppVM.MainVM.Styles.Clear();
        ShowExportChoices(selected);
        var chromas = selected.MainAsset.GetOrDefault("Chromas", Array.Empty<UObject>());
        var styles = new List<UObject>();
        foreach (UBlueprintGeneratedClass style in chromas)
        {
            if (style == null) continue;

            var cdo = style.ClassDefaultObject.Load();
            var channel = cdo.GetOrDefault("UIData", new UObject());
            var bpChannel = (UBlueprintGeneratedClass)channel;
            var uiData = await ExportData.CreateUiData(bpChannel);
            styles.Add(uiData);
        }

        var styleSelector = new StyleSelector(styles.ToArray(), chromas);
        if (styleSelector.Options.Items.Count == 0) return;
        AppVM.MainVM.Styles.Add(styleSelector);
    }

    private void StupidIdiotBadScroll(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer) return;
        switch (e.Delta)
        {
            case < 0:
                scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset + 88);
                break;
            case > 0:
                scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - 88);
                break;
        }
    }
}