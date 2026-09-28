using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using ValorantPorting.AppUtils;
using ValorantPorting.Services.Endpoints;

namespace ValorantPorting.Views.Controls;

// One ability model in the Abilities tab, e.g. Skye's dog:
// Characters/Guide/S0/Ability_Q/3P/Models/AB_Guide_S0_Q_Wolf_Skelmesh -> "Skye · Trailblazer (Q): Wolf".
// A model made of several parts (Chamber's trap: body, eye, rotator) is one entry with all of them (see Tidy).
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
        Prefix = FileName.Split('_')[0].ToUpperInvariant();
        IsStatic = FileName.EndsWith("Staticmesh", StringComparison.OrdinalIgnoreCase);
        ModelPaths.Add(ObjectPath);
        Directory = path[..path.LastIndexOf('/')];

        var agent = ValorantNames.Agents.GetValueOrDefault(Codename);
        // the current key of the ability with this name (the official list), e.g. Boom Bot -> C
        if (abilityInfo?.Name is { } infoName && agent?.Abilities.FirstOrDefault(a => a.Value.Equals(infoName, StringComparison.OrdinalIgnoreCase)).Key is { } officialKey)
            Key = officialKey;
        AgentName = agent?.Name ?? Codename;
        AbilityName = abilityInfo?.Name ??
                      (Key.Length > 0 && agent?.Abilities.GetValueOrDefault(Key) is { } ability ? ability : Readable(abilityFolder));
        IconUrl = Key.Length > 0 ? ValorantNames.AbilityIcons.GetValueOrDefault($"{Codename}|{Key}") : null;
        Part = PartName(FileName, Codename, abilityFolder);

        UpdateTitle();
        UpdateDetails();
        IsFavorite = UserLibrary.IsFavorite(LibraryId);
    }

    // Riot's file prefixes: AB_ the prop itself, ABTP_ the copy other players see, ABCS_ the character select copy
    private static readonly Dictionary<string, int> PrefixPreference = new(StringComparer.OrdinalIgnoreCase)
        { ["AB"] = 0, ["FP"] = 1, ["TP"] = 2, ["ABTP"] = 3, ["ABCS"] = 4 };

    // The Abilities tab's entries: one per model, duplicates dropped (a static copy of an animated model, the 3rd
    // person / character select copies of the same prop) and parts of one model merged ("Trap" + "Trap Eye" +
    // "Trap Rotator" -> "Trap", whose parts fit together as they are).
    // isRig: an invisible model the game hangs other models on (Jett's Blade Storm rig carries the 5 knives); it's its
    // own entry, next to the knife it carries
    public static List<AbilityItem> Tidy(IEnumerable<AbilityItem> items, Func<AbilityItem, bool>? isRig = null)
    {
        var result = new List<AbilityItem>();
        foreach (var folder in items.GroupBy(i => i.Folder, StringComparer.OrdinalIgnoreCase))
        {
            var rigs = folder.Where(i => i.Prefix == "AB" && isRig?.Invoke(i) == true).ToList();
            result.AddRange(rigs);
            var unique = folder
                .Where(i => !i.Part.StartsWith("Hidden ", StringComparison.OrdinalIgnoreCase)) // invisible helper meshes
                .Where(i => !rigs.Contains(i) && !rigs.Any(r => r.SamePropKey.Equals(i.SamePropKey, StringComparison.OrdinalIgnoreCase) && !i.IsStatic))
                .GroupBy(i => i.SamePropKey, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderBy(i => i.IsStatic ? 1 : 0).ThenBy(i => PrefixPreference.GetValueOrDefault(i.Prefix, 5))
                    .ThenBy(i => i.Part.StartsWith("TP", StringComparison.Ordinal) ? 1 : 0).First())
                .OrderBy(i => i.Part.Length)
                .ToList();

            var merged = new List<AbilityItem>();
            foreach (var item in unique)
            {
                // parts of one model sit next to each other; a held version ("Wolf Totem", "Drone Equip") is its own model
                var whole = merged.FirstOrDefault(m => item.Part.StartsWith(m.Part + " ", StringComparison.OrdinalIgnoreCase) &&
                                                       !m.Part.Equals("Model", StringComparison.OrdinalIgnoreCase) &&
                                                       m.Directory.Equals(item.Directory, StringComparison.OrdinalIgnoreCase) &&
                                                       !HeldVersion.IsMatch(item.Part[(m.Part.Length + 1)..]));
                if (whole is null) merged.Add(item);
                else whole.AddPart(item);
            }

            result.AddRange(merged);
        }

        return result;
    }

    private static readonly Regex HeldVersion = new(@"^(Totem|Equip|In Hand|Hand)", RegexOptions.IgnoreCase);

    // what makes two files the same prop: the part name without "(static)" or a leading "TP" ("TPHawk Totem")
    private string SamePropKey => Regex.Replace(Regex.Replace(Part, @" \(static\)$", ""), @"^TP(?=[A-Z])", "").Replace(" ", "");

    private readonly List<string> partNames = [];

    private void AddPart(AbilityItem part)
    {
        ModelPaths.Add(part.ObjectPath);
        partNames.Add(part.Part.StartsWith(Part + " ") ? part.Part[(Part.Length + 1)..] : part.Part);
        UpdateDetails();
    }

    // the rig of an ability, with the models the game hangs on its bones (they follow its animations)
    public void MakeRig(List<Export.AbilityResolver.BoneAttachment> attachments)
    {
        BoneAttachments = attachments;
        Part = "Animated set";
        UpdateTitle();
        UpdateDetails();
    }

    // a model the game only shows in the character select intro (Clove's butterfly)
    public void MarkCharacterSelectOnly()
    {
        characterSelectOnly = true;
        UpdateDetails();
    }

    private bool characterSelectOnly;

    private void UpdateTitle()
    {
        Title = $"{AgentName} · {AbilityName}{(Key.Length > 0 ? $" ({Key})" : "")}: {Part}";
        SortKey = $"{AgentName}|{Array.IndexOf(KeyOrder, Key) switch { -1 => 9, var i => i }}|{AbilityName}|{Part}";
    }

    private void UpdateDetails()
    {
        var notes = new List<string>();
        if (partNames.Count > 0) notes.Add($"{partNames.Count + 1} parts: {Part}, {string.Join(", ", partNames)}");
        if (BoneAttachments is { Count: > 0 } attached)
            notes.Add($"{attached.Count} × {attached[0].MeshPath[(attached[0].MeshPath.LastIndexOf('.') + 1)..]} on the rig's bones, for the ability's animations");
        if (Prefix is "ABTP" or "TP") notes.Add("3rd person version");
        if (Prefix == "ABCS" || characterSelectOnly) notes.Add("character select version");
        if (IsStatic) notes.Add("not animated");
        notes.Add(FileName);
        Details = string.Join("  ·  ", notes);
    }

    private static readonly Dictionary<string, string> SlotKeys = new(StringComparer.OrdinalIgnoreCase)
        { ["Ability1"] = "Q", ["Ability2"] = "E", ["Grenade"] = "C", ["GrenadeAbility"] = "C", ["Ultimate"] = "X" };

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
        return readable;
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
    public string Part { get; private set; } // "Wolf"
    public string Prefix { get; }       // "AB", "ABTP", "ABCS", ...
    public string Directory { get; }    // the model file's folder
    public bool IsStatic { get; }       // a static (not animated) mesh
    public List<string> ModelPaths { get; } = []; // this model's parts (object paths), sent together
    // for a model of several parts: each part's in-game offset/rotation/size (from the object holding them)
    public Dictionary<string, Export.AbilityResolver.Placement>? Placements { get; set; }
    public List<Export.AbilityResolver.BoneAttachment>? BoneAttachments { get; private set; }
    public string? IconUrl { get; }
    public string Title { get; private set; } = "";
    public string Details { get; private set; } = "";
    public string SortKey { get; private set; } = "";

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
