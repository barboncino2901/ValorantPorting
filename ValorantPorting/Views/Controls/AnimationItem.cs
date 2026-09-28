using System.Collections.Generic;
using System.Linq;
using System;
using CommunityToolkit.Mvvm.ComponentModel;
using ValorantPorting.AppUtils;
using ValorantPorting.Export;
using ValorantPorting.Services.Endpoints;

namespace ValorantPorting.Views.Controls;

public enum EAnimationKind
{
    Single,   // one animation
    FullBody, // an upper body (_UB) + lower body (_LB) pair, applied together
    Sequence  // a montage playing several animations one after the other
}

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
        // "Sprinter" is Neon's internal name, not a sprint
        var motion = assetName.Replace("Sprinter", "", StringComparison.OrdinalIgnoreCase);
        IsLoop = LoopingName.IsMatch(motion) && !OneShotName.IsMatch(motion);
        ModelTag = ModelTags.TryGetValue(assetName.Split('_')[0], out var tag) ? tag : "";
    }

    // "Vandal: Equip (full body)": both halves of an upper/lower body pair
    public static AnimationItem FullBody(AnimationItem upper, AnimationItem lower)
    {
        var package = upper.ObjectPath[..upper.ObjectPath.LastIndexOf('.')];
        var item = new AnimationItem(package[..^3], upper.Name[..^3]);
        item.Kind = EAnimationKind.FullBody;
        item.UpperHalf = upper;
        item.LowerHalf = lower;
        item.AddToTitle(" (full body)");
        return item;
    }

    // a montage that plays several animations in a row
    public void MakeSequence(IReadOnlyList<AnimationItem> clips)
    {
        Kind = EAnimationKind.Sequence;
        Clips = clips;
        AddToTitle(" (sequence)");
    }

    public EAnimationKind Kind { get; private set; } = EAnimationKind.Single;
    public AnimationItem? UpperHalf { get; private set; }
    public AnimationItem? LowerHalf { get; private set; }
    public IReadOnlyList<AnimationItem> Clips { get; private set; } = [];

    private void AddToTitle(string text)
    {
        Title += text;
        searchTitle = searchText = null;
        titleWords = allWords = null;
        OnPropertyChanged(nameof(Title));
    }

    // Which model an animation is for, from its name's first part; shown as a tag in the list
    private static readonly Dictionary<string, string> ModelTags = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TP"] = "3rd person", ["FP"] = "1st person", ["CS"] = "Char select", ["GN"] = "Gun", ["GNTP"] = "Gun 3rd person",
        ["AB"] = "Ability prop", ["ABTP"] = "Ability prop", ["ABCS"] = "Ability prop", ["EQ"] = "Melee"
    };

    public string ModelTag { get; }

    // Cycles that can repeat seamlessly (runs, walks, idles); one-shots (equip, reload, a run's start/stop) can't.
    private static readonly System.Text.RegularExpressions.Regex LoopingName =
        new(@"run|walk|jog|sprint|strafe|idle|loop|glide", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex OneShotName =
        new(@"equip|reload|fire|inspect|jump|land|start|stop|intro|outro|enter|exit|cast|throw|death|hit|add|subtract|blendspace|aimoffset|pose|montage",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public bool IsLoop { get; }

    // Search data (see AnimationSearch), built on first use: the title and all names in lower case, and their words
    // ("TP_Core_RunN_LB" -> tp, core, run, n, lb)
    private string? searchTitle, searchText;
    private HashSet<string>? titleWords, nameWords, allWords;
    public string SearchTitle => searchTitle ??= AnimationSearch.Normalize(Title);
    public string SearchText => searchText ??= AnimationSearch.Normalize($"{Title} {Name} {View} {Folder}");
    public HashSet<string> TitleWords => titleWords ??= [..SearchTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
    public HashSet<string> NameWords => nameWords ??= [..SplitName(Name).Concat(SplitName(Folder))];
    public HashSet<string> AllWords => allWords ??= [..TitleWords.Concat(NameWords)];
    public bool IsShared => View.Contains("shared", StringComparison.OrdinalIgnoreCase) || Folder.Contains("_Core", StringComparison.OrdinalIgnoreCase);
    public double SearchScore { get; set; } // of the current search, for sorting

    private static IEnumerable<string> SplitName(string name) =>
        System.Text.RegularExpressions.Regex.Split(name, @"[_/\s]+|(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")
            .Where(w => w.Length > 0).Select(w => w.ToLowerInvariant());

    [ObservableProperty] private bool isFavorite;
    public string LibraryId => "anim:" + ObjectPath;
    public int RecentRank => UserLibrary.RecentRank(LibraryId);

    public string Title { get; private set; } // e.g. "Jett · Tailwind (E): Dash East"
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
