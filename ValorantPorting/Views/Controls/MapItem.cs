using System;
using CommunityToolkit.Mvvm.ComponentModel;
using ValorantPorting.AppUtils;

namespace ValorantPorting.Views.Controls;

// One map in the Maps tab, e.g. Ascent -> /Game/Maps/Ascent/Ascent.
public partial class MapItem : ObservableObject, ILibraryItem
{
    public MapItem(string name, string mapUrl, string? description)
    {
        Name = name;
        MapUrl = mapUrl;
        var assetName = mapUrl[(mapUrl.LastIndexOf('/') + 1)..];
        ObjectPath = $"{mapUrl}.{assetName}";
        Codename = assetName;
        Details = $"{(string.IsNullOrEmpty(description) ? "Other mode" : description)}  ·  {mapUrl}";
        IsFavorite = UserLibrary.IsFavorite(LibraryId);
    }

    [ObservableProperty] private bool isFavorite;
    public string LibraryId => "map:" + ObjectPath;
    public int RecentRank => UserLibrary.RecentRank(LibraryId);

    public string Name { get; }       // "Ascent"
    public string MapUrl { get; }     // "/Game/Maps/Ascent/Ascent"
    public string ObjectPath { get; } // "/Game/Maps/Ascent/Ascent.Ascent"
    public string Codename { get; }   // "Ascent", "Bonsai" (Split), ...
    public string Details { get; }    // "A/B Sites · /Game/Maps/Ascent/Ascent"

    public bool Match(string filter)
    {
        foreach (var word in filter.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Name.Contains(word, StringComparison.OrdinalIgnoreCase) &&
                !Details.Contains(word, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }
}
