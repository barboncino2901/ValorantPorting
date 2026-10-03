using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using ValorantPorting.Services;
using ValorantPorting.Services.Endpoints;
using ValorantPorting.Views.Controls;

namespace ValorantPorting.ViewModels;

// The window's frame: the section title, the Weapon Skins gun list, the version under the logo
public partial class MainViewModel
{
    public string AppVersionText => $"v{UpdateService.CurrentVersion.ToString(3)} · community update";

    public string ActiveTabTitle => ActiveTab switch
    {
        EAssetType.Character => "AGENTS",
        EAssetType.Weapon => "WEAPON SKINS",
        EAssetType.GunBuddy => "GUN BUDDIES",
        EAssetType.Ability => "ABILITIES",
        EAssetType.Animation => "ANIMATIONS",
        EAssetType.Map => "MAPS",
        EAssetType.Scene => "PRESETS",
        EAssetType.Sound => "SOUNDS",
        _ => ""
    };

    public string ActiveTabSubtitle => ActiveTab == EAssetType.Weapon && SelectedGunRow is { IsHeader: false, PathPart: not null } gun ? gun.Label : "";

    public string LogHint => "Exports, warnings and errors";

    // ---- Weapon Skins: the gun list beside it ("All weapon skins", then each class and its guns)

    public record GunRow(string Label, string? PathPart, bool IsHeader)
    {
        public Visibility RowVisibility => IsHeader ? Visibility.Collapsed : Visibility.Visible;
        public Visibility HeaderVisibility => IsHeader ? Visibility.Visible : Visibility.Collapsed;
    }

    [ObservableProperty] private List<GunRow> gunRows = [];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ActiveTabSubtitle))] private GunRow? selectedGunRow;
    public event Action? GunFilterChanged;

    public Visibility GunSidebarVisibility => ActiveTab == EAssetType.Weapon ? Visibility.Visible : Visibility.Collapsed;

    public void EnsureGunRows()
    {
        if (GunRows.Count > 0) return;
        ValorantNames.WaitUntilLoaded(TimeSpan.FromSeconds(5));
        var rows = new List<GunRow> { new("All weapon skins", null, false) };
        foreach (var group in ValorantNames.GunList.GroupBy(g => g.Class))
        {
            rows.Add(new GunRow(group.Key.ToUpperInvariant(), null, true));
            rows.AddRange(group.Select(g => new GunRow(g.Name, g.PathPart, false)));
        }

        GunRows = rows;
        SelectedGunRow = rows[0];
    }

    partial void OnSelectedGunRowChanged(GunRow? value)
    {
        if (value is { IsHeader: true }) return;
        GunFilterChanged?.Invoke();
    }

    // a weapon skin tile of the picked gun (other tiles, and all of them with "All weapon skins", always match)
    public bool MatchesGun(AssetSelectorItem tile)
    {
        if (SelectedGunRow is not { PathPart: { } part }) return true;
        var path = tile.ObjectPath ?? "";
        if (!path.Contains("/Equippables/Guns/", StringComparison.OrdinalIgnoreCase) &&
            !path.Contains("/Equippables/Melee/", StringComparison.OrdinalIgnoreCase)) return true;
        return path.Contains(part, StringComparison.OrdinalIgnoreCase);
    }
}
