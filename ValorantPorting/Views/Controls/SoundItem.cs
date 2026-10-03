using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using ValorantPorting.AppUtils;
using ValorantPorting.Export;
using ValorantPorting.Services;
using ValorantPorting.Services.Endpoints;

namespace ValorantPorting.Views.Controls;

// A heading of the Sounds list: one gun skin, one ability, one map, ... (a record: equal by value, so the list
// groups by it)
public record SoundGroup(string Category, string Name)
{
    public override string ToString() => Name;
}

// A heading row in the Sounds list, above its group's sounds (the list is flat: WPF's own grouping is far too slow
// for ~21,000 sounds)
public record SoundHeader(string Name, string Category, int Count)
{
    public string CountText => $"{Category}  ·  {Count}";
}

// One sound in the Sounds tab, e.g. "Charging handle back (1st person)" under "Vandal · RGX 11z Pro"; found by
// its full readable name or Riot's.
public partial class SoundItem : ObservableObject, ILibraryItem
{
    // abilities / maps: see SoundNamer.Describe
    public SoundItem(GameSounds.Entry entry, SoundAbilities? abilities = null, IReadOnlyDictionary<string, string>? maps = null)
    {
        EventPath = entry.EventPath;
        EventName = entry.EventName;
        var name = SoundNamer.Describe(entry.EventName, entry.Folder, ValorantNames.Agents, ValorantNames.Guns, ValorantNames.Skins,
            abilities, maps);
        Title = name.Title;
        Short = name.Short;
        Category = name.Category;
        Group = new SoundGroup(name.Category, name.Group);
        CategoryRank = Array.IndexOf(SoundNamer.Categories, Category) is var rank and >= 0 ? rank : SoundNamer.Categories.Length;
        Details = $"{Category}  ·  {name.Group}  ·  {EventName}";
        searchText = $"{Title} {name.Group} {EventName} {Category} {entry.Folder}".Replace('_', ' ');
        isFavorite = UserLibrary.IsFavorite(LibraryId);
    }

    private readonly string searchText;

    public string EventPath { get; } // "ShooterGame/Content/WwiseAudio/Events/.../Play_X"
    public string EventName { get; } // "Play_Wp_AK47_Chg_Hndl_Back_FP"
    public string Title { get; }     // "Vandal · RGX 11z Pro: Charging handle back (1st person)"
    public string Short { get; }     // "Charging handle back (1st person)"
    public string Category { get; }  // "Weapons"
    public SoundGroup Group { get; } // Weapons / "Vandal · RGX 11z Pro"
    public int CategoryRank { get; }
    public string Details { get; }

    [ObservableProperty] private bool isFavorite;
    public string LibraryId => "sound:" + EventPath;
    public int RecentRank => UserLibrary.RecentRank(LibraryId);

    // every search word in the name, its group, Riot's name or the folder: "vandal reload", "raze boom bot", "kill 5"
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
