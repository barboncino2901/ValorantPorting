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
        AppVM.MainVM.LibraryFilterChanged += RefreshListFilters;
        // favorites/recent changed (maybe from an export task): re-filter/re-sort when a library view is shown
        UserLibrary.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            if (AppVM.MainVM.LibraryFilter != ELibraryFilter.All) RefreshListFilters();
        });
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
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
            var listBox = tab.Content as ListBox ??
                          (ReferenceEquals(tab.Content, MapList.Parent) ? MapList : AnimationList);
            ApplySearchFilter(listBox, SearchText);
        }
    }

    private static void ApplySearchFilter(ListBox listBox, string text)
    {
        var hasText = !string.IsNullOrWhiteSpace(text);
        var library = AppVM.MainVM.LibraryFilter;
        listBox.Items.Filter = o =>
        {
            if (o is ILibraryItem item && !UserLibrary.Matches(item.LibraryId, library)) return false;
            return o switch
            {
                AssetSelectorItem asset => !hasText || asset.Match(text),
                // favorites/recent show across all models, so the "Show animations for" filter doesn't hide them
                AnimationItem animation => (!hasText || animation.Match(text)) &&
                                           (library != ELibraryFilter.All || AppVM.MainVM.MatchesAnimationContext(animation)),
                MapItem map => !hasText || map.Match(text),
                _ => true
            };
        };

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


    private void OnAnimationDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AppVM.MainVM.SelectedAnimation is not null)
            AppVM.MainVM.ExportAnimationBlenderCommand.Execute(null);
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