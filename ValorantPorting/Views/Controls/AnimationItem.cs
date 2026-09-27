using System;

namespace ValorantPorting.Views.Controls;

// One animation in the Animations tab (Valorant animations have no icons, so this is a text entry).
public class AnimationItem
{
    public AnimationItem(string packageName, string assetName)
    {
        Name = assetName;
        ObjectPath = $"{packageName}.{assetName}";

        var folder = packageName;
        var lastSlash = folder.LastIndexOf('/');
        if (lastSlash > 0) folder = folder[..lastSlash];
        if (folder.StartsWith("/Game/")) folder = folder["/Game/".Length..];
        Folder = folder;
    }

    public string Name { get; }
    public string Folder { get; }
    public string ObjectPath { get; }

    // Every search word must appear in the name or folder, e.g. "wushu dash" or "AK reload".
    public bool Match(string filter)
    {
        foreach (var word in filter.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Name.Contains(word, StringComparison.OrdinalIgnoreCase) &&
                !Folder.Contains(word, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }
}
