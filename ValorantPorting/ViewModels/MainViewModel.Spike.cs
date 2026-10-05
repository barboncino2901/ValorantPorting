using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse_Conversion.Textures;
using ValorantPorting.AppUtils;
using ValorantPorting.Export;
using ValorantPorting.Export.Blender;
using ValorantPorting.Services;
using ValorantPorting.Views.Extensions;

namespace ValorantPorting.ViewModels;

// Weapon Skins > Other > Spike: the spike and its defuser, which aren't gun skins. Sent like an ability's model: with an
// agent selected in Blender it goes where the agent holds it (the spike in the left hand), otherwise on its own.
public partial class MainViewModel
{
    public const string SpikeRow = "spike"; // the gun list row's PathPart

    // Mesh: the model; Equippable: its file in the game, which says where agents hold it
    public record SpikeItem(string Title, string Description, string Mesh, string Equippable, string? IconPath)
    {
        public ImageSource? Icon { get; set; }
    }

    public List<SpikeItem> SpikeItems { get; } =
    [
        new("Spike", "The bomb: the closed spike; its Plant animations unfold it as when planted.",
            "/Game/Equippables/Bomb/S0/1P/Models/EQ_Bomb_S0_Skelmesh.EQ_Bomb_S0_Skelmesh",
            "ShooterGame/Content/Equippables/Bomb/BombEquippable",
            "/Game/UI/InGame/HUD/KillCallout/Assets/TX_Hud_Bomb_S.TX_Hud_Bomb_S"),
        new("Spike defuser", "The tool agents hold while defusing.",
            "/Game/Equippables/Bomb/S0/1P/Models/EQ_Bomb_Defuser_S0_Skelmesh.EQ_Bomb_Defuser_S0_Skelmesh",
            "ShooterGame/Content/Equippables/Bomb/Bomb_Defuser", null)
    ];

    public bool SpikeRowSelected => SelectedGunRow is { PathPart: SpikeRow };
    public Visibility SpikePanelVisibility => SpikeRowSelected ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WeaponGridVisibility => SpikeRowSelected ? Visibility.Collapsed : Visibility.Visible;

    private bool spikeIconsLoaded;

    private void LoadSpikeIcons()
    {
        if (spikeIconsLoaded) return;
        spikeIconsLoaded = true;
        foreach (var item in SpikeItems.Where(i => i.IconPath != null))
        {
            try
            {
                if (AppVM.CUE4ParseVM.Provider.TryLoadPackageObject(item.IconPath!, out UTexture2D texture) &&
                    texture.Decode()?.ToSkBitmap() is { } bitmap)
                    using (bitmap) item.Icon = bitmap.ToBitmapSource();
            }
            catch (Exception)
            {
                // no icon: the card shows its name only
            }
        }
        OnPropertyChanged(nameof(SpikeItems));
    }

    private bool spikeExportRunning;

    [RelayCommand]
    public async Task SendSpike(SpikeItem? item)
    {
        if (item is null || spikeExportRunning) return;
        spikeExportRunning = true;
        try
        {
            var timer = Stopwatch.StartNew();
            var provider = AppVM.CUE4ParseVM.Provider;
            var data = new ExportData { Name = item.Title, Type = "Ability" };
            await ExportHelpers.ExportLock.WaitAsync(); // one export at a time (they share ExportHelpers.Tasks)
            try
            {
                await Task.Run(() =>
                {
                    if (provider.TryLoadPackageObject(item.Mesh, out USkeletalMesh mesh)) ExportHelpers.Mesh(mesh, data.Parts);
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

            var (hold1P, hold3P) = await Task.Run(() => AbilityResolver.HoldSocketsOf(provider, item.Equippable));
            BlenderService.Send(data, new BlenderExportSettings
            {
                ReorientBones = false, // like guns: it animates with its own bone orientation
                AnimationFilterKey = $"item|Equippables/Bomb/|{item.Title}",
                HoldSocket1P = hold1P,
                HoldSocket3P = hold3P
            });
            AppLog.Information($"Sent {item.Title} to BLENDER in {Math.Round(timer.Elapsed.TotalSeconds, 3)}s " +
                               "(in the hands of the agent selected in Blender, if any).");
        }
        catch (Exception ex)
        {
            AppLog.Error($"Could not send {item.Title}: {ex.Message}");
        }
        finally
        {
            spikeExportRunning = false;
        }
    }
}
