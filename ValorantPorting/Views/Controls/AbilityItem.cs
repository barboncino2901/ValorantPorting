using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using ValorantPorting.AppUtils;
using ValorantPorting.Services.Endpoints;

namespace ValorantPorting.Views.Controls;

// One ability model in the Abilities tab, e.g. Skye's dog:
// Characters/Guide/S0/Ability_Q/3P/Models/AB_Guide_S0_Q_Wolf_Skelmesh -> "Skye · Trailblazer (Q): Wolf", in the world.
public partial class AbilityItem : ObservableObject, ILibraryItem
{
    // ability folders -> key (Riot keeps the C ability in "Ability_4")
    private static readonly Dictionary<string, string> Keys = new(StringComparer.OrdinalIgnoreCase)
        { ["Q"] = "Q", ["E"] = "E", ["4"] = "C", ["C"] = "C", ["X"] = "X", ["Molotov"] = "Q", ["SpeedStim"] = "C" }; // Brimstone
    private static readonly string[] KeyOrder = ["Q", "E", "C", "X"];

    // abilityInfo: the ability's name and key from its folder's UIData (see ReadAbilityInfo); the folder letter is
    // only a fallback, because Riot kept old keybinds there (Raze's "Ability_E" is the Boom Bot, now on C)
    public AbilityItem(string packagePath, (string Name, string Key)? abilityInfo = null)
    {
        // "ShooterGame/Content/Characters/Guide/S0/Ability_Q/3P/Models/AB_Guide_S0_Q_Wolf_Skelmesh"
        var path = packagePath.StartsWith("ShooterGame/Content/") ? "/Game/" + packagePath["ShooterGame/Content/".Length..] : packagePath;
        FileName = path[(path.LastIndexOf('/') + 1)..];
        ObjectPath = $"{path}.{FileName}";
        var parts = path.TrimStart('/').Split('/'); // Game, Characters, Guide, S0, Ability_Q, 3P, Models, file
        Codename = parts[2];
        var abilityFolder = parts[4]["Ability_".Length..];
        Folder = string.Join('/', parts.Skip(1).Take(4)); // Characters/Guide/S0/Ability_Q
        Key = abilityInfo?.Key is { Length: > 0 } infoKey ? infoKey : Keys.GetValueOrDefault(abilityFolder, "");
        View = parts.Contains("1P") ? "held (1st person)" : parts.Contains("3P") ? "in the world (3rd person)" : "";

        var agent = ValorantNames.Agents.GetValueOrDefault(Codename);
        // the current key of the ability with this name (the official list), e.g. Boom Bot -> C
        if (abilityInfo?.Name is { } infoName && agent?.Abilities.FirstOrDefault(a => a.Value.Equals(infoName, StringComparison.OrdinalIgnoreCase)).Key is { } officialKey)
            Key = officialKey;
        AgentName = agent?.Name ?? Codename;
        AbilityName = abilityInfo?.Name ??
                      (Key.Length > 0 && agent?.Abilities.GetValueOrDefault(Key) is { } ability ? ability : Readable(abilityFolder));
        IconUrl = Key.Length > 0 ? ValorantNames.AbilityIcons.GetValueOrDefault($"{Codename}|{Key}") : null;
        Part = PartName(FileName, Codename, abilityFolder);

        Title = $"{AgentName} · {AbilityName}{(Key.Length > 0 ? $" ({Key})" : "")}: {Part}";
        Details = View.Length > 0 ? $"{View}  ·  {FileName}" : FileName;
        SortKey = $"{AgentName}|{Array.IndexOf(KeyOrder, Key) switch { -1 => 9, var i => i }}|{AbilityName}|{(View.StartsWith("held") ? 0 : 1)}|{Part}";
        IsFavorite = UserLibrary.IsFavorite(LibraryId);
    }

    private static readonly Dictionary<string, string> SlotKeys = new(StringComparer.OrdinalIgnoreCase)
        { ["Ability1"] = "Q", ["Ability2"] = "E", ["Grenade"] = "C", ["Ultimate"] = "X" };

    // The ability's name and current key from the "UIData_…" asset in its folder: DisplayName "Paint Shells" with the
    // localization key "Ability2_DisplayName" (Ability1 = Q, Ability2 = E, Grenade = C, Ultimate = X).
    public static (string Name, string Key)? ReadAbilityInfo(CUE4Parse.FileProvider.IFileProvider provider, string abilityFolder)
    {
        var prefix = $"ShooterGame/Content/{abilityFolder}/UIData_";
        foreach (var file in provider.Files.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                                                             k.EndsWith(".uasset") && k.IndexOf('/', prefix.Length) < 0))
        {
            try
            {
                var package = "/Game/" + file["ShooterGame/Content/".Length..^".uasset".Length];
                if (!provider.TryLoadPackageObject(package + "." + package[(package.LastIndexOf('/') + 1)..] + "_C",
                        out CUE4Parse.UE4.Objects.Engine.UBlueprintGeneratedClass uiClass)) continue;
                var defaults = uiClass.ClassDefaultObject.Load();
                if (defaults?.GetOrDefault<CUE4Parse.UE4.Objects.Core.i18N.FText>("DisplayName") is not { } text ||
                    string.IsNullOrWhiteSpace(text.Text)) continue;
                var locKey = (text.TextHistory as CUE4Parse.UE4.Objects.Core.i18N.FTextHistory.Base)?.Key ?? "";
                var slot = locKey.Split('_')[0];
                return (text.Text, SlotKeys.GetValueOrDefault(slot, ""));
            }
            catch (Exception)
            {
                // unreadable: try the next one / fall back to the folder name
            }
        }

        return null;
    }

    // "AB_Guide_S0_Q_WolfTotem_Skelmesh" -> "Wolf Totem"
    private static string PartName(string fileName, string codename, string abilityFolder)
    {
        var name = Regex.Replace(fileName, @"_(Skel|Static)mesh$", "", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, $@"^(AB|ABTP|ABCS|TP|FP|CS)_{Regex.Escape(codename)}_(S0_)?", "", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, $@"^{Regex.Escape(abilityFolder)}_", "", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @"^(Q|E|C|X|4)(_|$)", "", RegexOptions.IgnoreCase);
        var readable = Readable(name);
        if (readable.Length == 0) readable = "Model"; // named after the ability key only
        // the same model is sometimes also there as a static (unanimated) mesh
        return fileName.EndsWith("Staticmesh", StringComparison.OrdinalIgnoreCase) ? readable + " (static)" : readable;
    }

    private static string Readable(string name) =>
        Regex.Replace(name.Replace('_', ' '), @"(?<=[a-z])(?=[A-Z])", " ").Trim();

    [ObservableProperty] private bool isFavorite;
    public string LibraryId => "ability:" + ObjectPath;
    public int RecentRank => UserLibrary.RecentRank(LibraryId);

    public string ObjectPath { get; }   // "/Game/Characters/Guide/S0/Ability_Q/3P/Models/AB_Guide_S0_Q_Wolf_Skelmesh.AB_…"
    public string FileName { get; }
    public string Folder { get; }       // "Characters/Guide/S0/Ability_Q": its animations live below it
    public string Codename { get; }     // "Guide"
    public string AgentName { get; }    // "Skye"
    public string Key { get; private set; } // "Q", "E", "C", "X" or "" (other ability folders)
    public string AbilityName { get; }  // "Trailblazer"
    public string Part { get; }         // "Wolf"
    public string View { get; }         // "held (1st person)" / "in the world (3rd person)"
    public string? IconUrl { get; }
    public string Title { get; }
    public string Details { get; }
    public string SortKey { get; }

    public bool Match(string filter)
    {
        foreach (var word in filter.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Title.Contains(word, StringComparison.OrdinalIgnoreCase) &&
                !Details.Contains(word, StringComparison.OrdinalIgnoreCase) &&
                !Codename.Contains(word, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }
}
