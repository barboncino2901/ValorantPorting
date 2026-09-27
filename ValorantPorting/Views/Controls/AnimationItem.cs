using System;
using CommunityToolkit.Mvvm.ComponentModel;
using ValorantPorting.AppUtils;
using ValorantPorting.Export;
using ValorantPorting.Services.Endpoints;

namespace ValorantPorting.Views.Controls;

// One animation in the Animations tab (Valorant animations have no icons, so this is a text entry).
public partial class AnimationItem : ObservableObject, ILibraryItem
{
    public AnimationItem(string packageName, string assetName)
    {
        Name = assetName;
        ObjectPath = $"{packageName}.{assetName}";

        var folder = packageName;
        var lastSlash = folder.LastIndexOf('/');
        if (lastSlash > 0) folder = folder[..lastSlash];
        if (folder.StartsWith("/Game/")) folder = folder["/Game/".Length..];
        else if (folder.StartsWith("ShooterGame/Content/")) folder = folder["ShooterGame/Content/".Length..];
        Folder = folder;

        (Title, View) = AnimationNamer.Describe(assetName, ValorantNames.Agents, ValorantNames.Guns, ValorantNames.Skins);
        Details = View.Length > 0 ? $"{View}  ·  {Name}" : Name;
        IsFavorite = UserLibrary.IsFavorite(LibraryId);
    }

    [ObservableProperty] private bool isFavorite;
    public string LibraryId => "anim:" + ObjectPath;
    public int RecentRank => UserLibrary.RecentRank(LibraryId);

    public string Title { get; }     // e.g. "Jett · Tailwind (E): Dash East"
    public string View { get; }      // e.g. "3rd person"
    public string Details { get; }   // second line in the list: view + original file name
    public string Name { get; }
    public string Folder { get; }
    public string ObjectPath { get; }

    // Every search word must appear in the name or folder, e.g. "wushu dash" or "AK reload".
    public bool Match(string filter)
    {
        foreach (var word in filter.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Name.Contains(word, StringComparison.OrdinalIgnoreCase) &&
                !Title.Contains(word, StringComparison.OrdinalIgnoreCase) &&
                !View.Contains(word, StringComparison.OrdinalIgnoreCase) &&
                !Folder.Contains(word, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }
}
