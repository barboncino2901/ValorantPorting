using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using ValorantPorting.AppUtils;
using ValorantPorting.Export;
using ValorantPorting.Services;
using ValorantPorting.Services.Endpoints;

namespace ValorantPorting.Views.Controls;

// One sound in the Sounds tab: "Vandal: Charging handle back (1st person)", found by its readable name or Riot's.
public partial class SoundItem : ObservableObject, ILibraryItem
{
    // abilities / maps: see SoundNamer.Describe
    public SoundItem(GameSounds.Entry entry, SoundAbilities? abilities = null, System.Collections.Generic.IReadOnlyDictionary<string, string>? maps = null)
    {
        EventPath = entry.EventPath;
        EventName = entry.EventName;
        (Title, Category) = SoundNamer.Describe(entry.EventName, entry.Folder, ValorantNames.Agents, ValorantNames.Guns, ValorantNames.Skins,
            abilities, maps);
        Details = $"{Category}  ·  {EventName}";
        searchText = $"{Title} {EventName} {Category} {entry.Folder}".Replace('_', ' ');
        isFavorite = UserLibrary.IsFavorite(LibraryId);
    }

    private readonly string searchText;

    public string EventPath { get; } // "ShooterGame/Content/WwiseAudio/Events/.../Play_X"
    public string EventName { get; } // "Play_Wp_AK47_Chg_Hndl_Back_FP"
    public string Title { get; }
    public string Category { get; }
    public string Details { get; }

    [ObservableProperty] private bool isFavorite;
    public string LibraryId => "sound:" + EventPath;
    public int RecentRank => UserLibrary.RecentRank(LibraryId);

    // every search word in the name, Riot's name or the folder: "vandal reload", "jett dash", "spike defuse"
    public bool Match(string filter) =>
        filter.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(word => searchText.Contains(word, StringComparison.OrdinalIgnoreCase));
}

// One audio file of the selected sound (a version the game picks at random, a 1st/3rd person one, a language)
public class SoundVariantRow
{
    public SoundVariantRow(GameSounds.Variant variant, int number)
    {
        Variant = variant;
        Label = $"Version {number}";
        Details = variant.Name;
    }

    public GameSounds.Variant Variant { get; }
    public string Label { get; }
    public string Details { get; }
}
