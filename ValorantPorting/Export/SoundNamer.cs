using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ValorantPorting.Export;

// Turns Valorant sound event names into readable names, sorted into categories and groups, e.g.
//   Play_Wp_AK47_Chg_Hndl_Back_FP            -> Weapons / "Vandal": "Charging handle back (1st person)"
//   Play_Wp_AK47_AfterGlow_Chg_Hndl_Back_FP  -> Weapons / "Vandal · RGX 11z Pro": "Charging handle back (1st person)"
//   Play_UI_KillBanner_AfterGlow_4           -> Kill sounds & finishers / "RGX 11z Pro": "Kill 4"
//   Play_Abil_Boomba_EnemySpottedAlert_1     -> Agent abilities / "Raze · Boom Bot (C)": "Enemy spotted alert 1"
//   Play_VO_Wushu_E07_LastKill_Rift          -> Voice lines / "Jett": "Last kill Astra"
//   Play_Jam_StoneDoor_Start_Rotate          -> Maps / "Lotus": "Stone door start rotate"
public static class SoundNamer
{
    // Title: the full name ("Raze · Boom Bot (C): Enemy spotted alert 1"); Short: the name within its group
    public record SoundName(string Title, string Short, string Category, string Group);

    public const string Weapons = "Weapons", Kills = "Kill sounds & finishers", Abilities = "Agent abilities",
        AgentOther = "Agent movement & more", Voice = "Voice lines", Maps = "Maps", Interface = "Interface",
        Modes = "Game modes", Music = "Music", Other = "Other";

    // in this order in the list
    public static readonly string[] Categories = [Weapons, Kills, Abilities, AgentOther, Voice, Maps, Interface, Modes, Music, Other];

    // sound files use some gun codenames of their own; these point to the weapon folder names used everywhere else
    private static readonly Dictionary<string, string> GunAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AK47"] = "AK", ["BurstRifle"] = "Burst", ["ShotgunAuto"] = "AutoShotgun", ["Shotgun"] = "PumpShotgun",
        ["Pump"] = "PumpShotgun", ["PistolCompact"] = "Compact", ["Sawed"] = "Slim", ["Lever"] = "Leversniper",
        ["LeverSniper"] = "Leversniper", ["BoltSniper"] = "Boltsniper", ["DoubleSniper"] = "Doublesniper",
        ["Mp5"] = "MP5", ["MP5SD"] = "MP5", ["Knife"] = "Melee", ["Vc"] = "Vector", ["AP"] = "AutoPistol", ["DS"] = "Doublesniper",
        ["SawedOff"] = "Slim", ["Judge"] = "AutoShotgun", ["PistolBase"] = "BasePistol", ["Bolt"] = "Boltsniper",
        ["SniperBolt"] = "Boltsniper"
    };

    // skin codenames that aren't (or not quite) the skin folders' names
    private static readonly Dictionary<string, string> SkinAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Base"] = "Default", ["Standard"] = "Default"
    };

    private static readonly Dictionary<string, string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Chg"] = "charging", ["Hndl"] = "handle", ["Hnd"] = "handle", ["Mag"] = "magazine", ["Rls"] = "release",
        ["Mvt"] = "movement", ["Ele"] = "element", ["Amb"] = "ambience", ["BG"] = "background", ["Abil"] = "ability",
        ["Ult"] = "ultimate", ["Lp"] = "loop", ["Sel"] = "select", ["Char"] = "", ["Wp"] = "", ["SFX"] = "", ["Emote"] = "",
        ["Mvmt"] = "movement", ["FS"] = "footsteps", ["Ads"] = "aiming", ["Eqp"] = "equip", ["Fwd"] = "forward",
        ["Bwd"] = "backward", ["Med"] = "medium", ["Explo"] = "explosion", ["Expl"] = "explosion", ["Proj"] = "projectile",
        ["Rld"] = "reload", ["Insp"] = "inspect", ["Lt"] = "light", ["UI"] = "", ["Guns"] = "", ["RE"] = "", ["BF"] = "",
        ["Totem"] = "", ["EoG"] = "end of game"
    };

    private static readonly Dictionary<string, string> Views = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FP"] = "1st person", ["1P"] = "1st person", ["TP"] = "3rd person", ["3P"] = "3rd person"
    };

    private static readonly Dictionary<string, string> InterfaceGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        ["OutofGame"] = "Menus", ["InGame"] = "In game", ["CharacterSelect"] = "Character select", ["Common"] = "Common",
        ["AssistBanner"] = "Assist banner"
    };

    private static readonly Regex AbilityToken = new("^Abil(Q|E|C|X|4|[1-4])$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CamelCase = new("(?<=[a-z])(?=[A-Z0-9])|(?<=[0-9])(?=[A-Za-z])(?!(?:st|nd|rd|th)(?:[^a-z]|$))|(?<=[A-Z])(?=[A-Z][a-z])", RegexOptions.Compiled);

    // agent sounds live in "Events_Char_<codename>_SFX" / "_VO" folders, voice reactions in "<codename>_NVC"
    private static readonly Regex AgentFolder = new(@"^(?:Events_Char_([A-Za-z]+?)(?:_SFX|_VO)?|([A-Za-z]+)_NVC)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MapFolder = new(@"^Events_Map_(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex KillBannerFolder = new(@"^UI_KillBanner_([A-Za-z0-9]+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FinisherFolder = new(@"^Events_Guns_([A-Za-z0-9]+)_Finisher$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex InterfaceFolder = new(@"^Events_UI_([A-Za-z]+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // eventName: "Play_Wushu_AbilE_Cast_Forward"; folder: its folder under WwiseAudio/Events ("SFX/Characters/Events_Char_Wushu_SFX/...")
    // skins: skin folder codename -> skin line ("Afterglow" -> "RGX 11z Pro"); abilities: names the ability of agent
    // sounds (Raze's "Boomba" -> Boom Bot); maps: map codename -> name ("Plummet" -> "Summit")
    public static SoundName Describe(string eventName, string folder,
        IReadOnlyDictionary<string, AnimationNamer.Agent> agents,
        IReadOnlyDictionary<string, string> guns,
        IReadOnlyDictionary<string, string> skins,
        SoundAbilities? abilities = null,
        IReadOnlyDictionary<string, string>? maps = null)
    {
        var name = Regex.Replace(eventName, "^Play_", "", RegexOptions.IgnoreCase);
        var tokens = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
        var folders = folder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var isVoice = folder.StartsWith("VO/", StringComparison.OrdinalIgnoreCase) || tokens.Contains("VO", StringComparer.OrdinalIgnoreCase);
        var isWeaponFolder = folder.StartsWith("SFX/Weapons", StringComparison.OrdinalIgnoreCase);

        // whose sound: an agent named in the sound, else the agent whose folder it's in; a map, a kill banner or a
        // finisher by its folder
        string? agentCode = tokens.FirstOrDefault(agents.ContainsKey), mapName = null, killSkinCode = null, interfaceGroup = null;
        var finisher = false;
        var below = new List<string>(); // folders below the agent's own, which may name the ability
        for (var i = 0; i < folders.Length; i++)
        {
            var agentMatch = AgentFolder.Match(folders[i]);
            var code = agentMatch.Success ? (agentMatch.Groups[1].Success ? agentMatch.Groups[1].Value : agentMatch.Groups[2].Value) : null;
            if (code != null && agents.ContainsKey(code))
            {
                agentCode ??= code;
                below = folders.Skip(i + 1).ToList();
            }
            if (maps != null && MapFolder.Match(folders[i]) is { Success: true } mapMatch) mapName ??= MapName(mapMatch.Groups[1].Value, maps);
            if (KillBannerFolder.Match(folders[i]) is { Success: true } kill) killSkinCode = kill.Groups[1].Value;
            if (FinisherFolder.Match(folders[i]) is { Success: true } fin) { killSkinCode = fin.Groups[1].Value; finisher = true; }
            if (InterfaceFolder.Match(folders[i]) is { Success: true } ui) interfaceGroup ??= InterfaceGroups.GetValueOrDefault(ui.Groups[1].Value);
        }
        if (killSkinCode != null) agentCode = null; // a kill banner named after an agent is still a gun skin's

        var agent = agentCode is null ? null : agents[agentCode];
        string? gunName = null, skinName = null, ability = null, view = null, abilityToken = null;
        if (agentCode != null && abilities?.Find(agentCode, tokens, below) is { } found)
        {
            ability = found.Ability.ToString();
            abilityToken = found.UsedToken;
        }

        var killSkin = killSkinCode is null ? null : SkinName(killSkinCode, skins);
        // weapon sounds: the skin from the name, else from the folders ("Events_Guns_RE/AfterGlow_RE/AfterGlow_AK_RE")
        if (killSkinCode is null && isWeaponFolder && agent is null)
            skinName = folders.SelectMany(f => f.Split('_'))
                .Where(t => !FolderWords.Contains(t) && !guns.ContainsKey(t) && !GunAliases.ContainsKey(t))
                .Select(t => KnownSkin(t, skins, allowPrefix: true)).FirstOrDefault(s => s != null);

        // weapon sounds whose name doesn't say the gun: the folders often do ("Fallen_RE/Fallen_Bolt_RE")
        string? folderGun = null;
        if (killSkinCode is null && isWeaponFolder && agent is null)
            folderGun = folders.SelectMany(f => f.Split('_')).Select(t => GunOf(t, guns)).FirstOrDefault(g => g != null);

        // the map's own codenames ("Triad" for Haven) say nothing more than its name
        var mapCodes = mapName is null || maps is null
            ? new HashSet<string>()
            : maps.Where(m => m.Value == mapName).Select(m => m.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var words = new List<string>();
        foreach (var token in tokens)
        {
            if (mapCodes.Contains(token) || mapName != null && token.Equals(mapName, StringComparison.OrdinalIgnoreCase)) continue;
            if (token.Equals("VO", StringComparison.OrdinalIgnoreCase)) continue;
            if (Views.TryGetValue(token, out var viewName)) { view ??= viewName; continue; }
            if (Regex.IsMatch(token, "^(S0|E[0-9]{2})$")) continue; // default skin, voice episode ("E07")
            if (token.Equals(agentCode, StringComparison.OrdinalIgnoreCase)) continue;
            if (token == abilityToken || token.Equals("Abil", StringComparison.OrdinalIgnoreCase) && ability != null) continue;
            if (killSkinCode != null && (token.Equals(killSkinCode, StringComparison.OrdinalIgnoreCase) ||
                                         Regex.IsMatch(token, "^(Kill ?banner|Finisher)$", RegexOptions.IgnoreCase) ||
                                         token.StartsWith(killSkinCode, StringComparison.OrdinalIgnoreCase))) continue;

            // another agent (a voice line about them): their real name, so "jett astra" finds it
            if (agents.TryGetValue(token, out var otherAgent)) { words.Add(otherAgent.Name); continue; }
            if (agent is null && killSkinCode is null && gunName is null && GunOf(token, guns) is { } foundGun)
            {
                gunName = foundGun;
                continue;
            }
            if (gunName is null && folderGun != null && GunOf(token, guns) == folderGun) continue;
            if (gunName is not null && KnownSkin(token, skins) is { } tokenSkin) { skinName ??= tokenSkin; continue; }
            // the skin's codename when the folder already told the skin ("Fallen" under "Operator · Forsaken")
            if (skinName != null && token.Length >= 4 && KnownSkin(token, skins, allowPrefix: true) == skinName) continue;

            var abilityMatch = AbilityToken.Match(token);
            if (abilityMatch.Success && ability is null)
            {
                var key = abilityMatch.Groups[1].Value.ToUpperInvariant() switch { "4" or "3" => "C", "1" => "Q", "2" => "E", var k => k };
                ability = agent?.Abilities.TryGetValue(key, out var abilityName) == true ? $"{FixCaps(abilityName)} ({key})" : $"Ability {key}";
                continue;
            }

            var level = Regex.Match(token, "^(?:Lv|Tier)([0-9])$", RegexOptions.IgnoreCase);
            if (level.Success) { words.Add($"level {level.Groups[1].Value}"); continue; }

            var word = Humanize(token).ToLowerInvariant();
            if (word == "movement" && words.Contains("footsteps")) continue; // "FS_Mvt": footsteps
            if (word.Length > 0) words.Add(word);
        }

        // what the group already says isn't repeated ("Killjoy · Lockdown (X): Lockdown dome position" -> "Dome position"),
        // nor the agent's short codename ("BH" = BountyHunter); single letters are variants ("C")
        gunName ??= folderGun;
        var said = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bh" };
        foreach (var known in new[] { ability, killSkin, skinName }.OfType<string>())
            foreach (var word in Regex.Split(known.ToLowerInvariant(), @"[^a-z0-9]+").Where(w => w.Length > 0)) said.Add(word);
        var actionWords = string.Join(" ", words).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !said.Contains(w))
            .Select(w => w.Length == 1 && char.IsLetter(w[0]) ? w.ToUpperInvariant() : w)
            .ToList();
        if (actionWords.Count == 0 && words.Count > 0 && killSkinCode is null) actionWords = words; // nothing else left: keep it as it was

        // kill banners: "Kill 1" ... "Kill 5" (the number of kills in the round that plays it)
        if (killSkinCode != null && !finisher && actionWords.Count >= 1 && int.TryParse(actionWords[^1], out var kills) && kills is >= 1 and <= 6)
            actionWords = [.. actionWords.Take(actionWords.Count - 1).Where(w => !int.TryParse(w, out _)), $"kill {kills}"];
        if (finisher) actionWords.Insert(0, "finisher");

        var action = string.Join(" ", actionWords);
        if (action.Length > 0) action = char.ToUpperInvariant(action[0]) + action[1..];
        if (view is not null) action = action.Length > 0 ? $"{action} ({view})" : view;

        // category and group
        string category, group;
        if (killSkinCode != null) (category, group) = (Kills, killSkin!);
        else if (isVoice) (category, group) = (Voice, agent?.Name ?? FolderGroup(folders) ?? "Other voices");
        else if (ability != null) (category, group) = (Abilities, $"{agent?.Name ?? "Agent"} · {ability}");
        else if (agent != null) (category, group) = (AgentOther, agent.Name);
        else if (gunName != null) (category, group) = (Weapons, skinName is null ? gunName : $"{gunName} · {skinName}");
        else if (mapName != null) (category, group) = (Maps, mapName);
        // shared by every agent: footsteps on each sole type, bodies falling; gun buddies are "Totems" in the files
        else if (tokens.FirstOrDefault() is "FS" && !isWeaponFolder) (category, group) = (AgentOther, "Footsteps (all agents)");
        else if (tokens.FirstOrDefault() is "BF") (category, group) = (AgentOther, "Body falls (all agents)");
        else if (tokens.FirstOrDefault() is "Totem") (category, group) = (Other, "Gun buddies");
        else
        {
            category = Category(folders);
            group = category switch
            {
                Interface => interfaceGroup ?? "Other",
                Weapons => "Other weapon sounds",
                Maps => "Other maps",
                _ => category
            };
        }

        var subject = category switch
        {
            Voice => $"{group} voice line",
            Kills => group,
            Weapons or Abilities or AgentOther or Maps => group,
            Interface => "Interface",
            _ => null
        };
        if (action.Length == 0) action = subject is null ? eventName : "Sound";
        var title = subject is null ? action : $"{subject}: {action}";
        return new SoundName(title, action, category, group);
    }

    // folder name parts that aren't skins (some would match one by their start: "Guns" -> Gunslinger)
    private static readonly HashSet<string> FolderWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SFX", "Weapons", "Events", "Guns", "RE", "Firing", "Finisher", "Melee", "Knife", "Base", "Tier1", "Tier2", "Tier3",
        "Shared", "Common", "Mvt", "Foley", "Equip", "Reload", "Inspect", "Fire", "Global"
    };

    // a gun's name for its codename in a sound ("AK47" -> Vandal, "Vc" -> Stinger, "Knife" -> Knife)
    private static string? GunOf(string token, IReadOnlyDictionary<string, string> guns)
    {
        if (!guns.TryGetValue(token, out var gun) && !(GunAliases.TryGetValue(token, out var alias) && guns.TryGetValue(alias, out gun)))
            return null;
        return gun == "Melee" ? "Knife" : gun;
    }

    // a skin line's name for a codename in a sound's name or folder: "AfterGlow" -> "RGX 11z Pro"; with allowPrefix
    // also short forms ("CyberK" -> CyberKnight's); null if it isn't one
    private static string? KnownSkin(string code, IReadOnlyDictionary<string, string> skins, bool allowPrefix = false)
    {
        if (code.Length < 3) return null;
        if (SkinAliases.TryGetValue(code, out var alias)) return alias;
        if (skins.TryGetValue(code, out var name)) return name;
        if (!allowPrefix || code.Length < 4) return null;
        var longer = skins.Where(s => s.Key.StartsWith(code, StringComparison.OrdinalIgnoreCase)).Select(s => s.Value).Distinct().ToList();
        return longer.Count == 1 ? longer[0] : null;
    }

    // the skin line of a kill banner / finisher folder, or its codename made readable ("Champions26" -> "Champions 26")
    private static string SkinName(string code, IReadOnlyDictionary<string, string> skins) =>
        KnownSkin(code, skins, allowPrefix: true) ?? CamelCase.Replace(code, " ");

    // "Plummet" -> "Summit", "Juliett_MUS_Diegetic" -> "Sunset": the longest map codename the folder starts with
    private static string? MapName(string code, IReadOnlyDictionary<string, string> maps) =>
        maps.Where(m => code.StartsWith(m.Key, StringComparison.OrdinalIgnoreCase) &&
                        (code.Length == m.Key.Length || code[m.Key.Length] == '_'))
            .OrderByDescending(m => m.Key.Length).Select(m => m.Value).FirstOrDefault();

    // map codenames from the maps' game paths ("/Game/Maps/Plummet/Plummet" -> Plummet = Summit); folders that hold
    // several maps (HURM, Duel) are left out
    public static Dictionary<string, string> MapCodenames(IEnumerable<(string Name, string MapUrl)> maps)
    {
        var all = maps.Select(m => (m.Name, Parts: m.MapUrl.Split('/', StringSplitOptions.RemoveEmptyEntries).SkipWhile(p => p != "Maps").Skip(1).ToList()))
            .Where(m => m.Parts.Count > 0).ToList();
        var shared = all.GroupBy(m => m.Parts[0], StringComparer.OrdinalIgnoreCase).Where(g => g.Select(m => m.Name).Distinct().Count() > 1)
            .Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (mapName, parts) in all)
            foreach (var part in parts.Where(p => !shared.Contains(p)))
                result.TryAdd(part, mapName);
        return result;
    }

    // voices that aren't an agent's: "Announcer", "NPE" (the tutorial) ...
    private static string? FolderGroup(string[] folders) =>
        folders.Length > 1 ? folders[1] switch
        {
            "Announcer" => "Announcer",
            "NPE" => "Tutorial",
            "CharacterMastery" => "Agent mastery",
            "GunSkinVO" => "Gun skins",
            _ => null
        } : null;

    // "SFX/Weapons/..." -> "Weapons", "SFX/Maps/..." -> "Maps", "SFX/UI/..." -> "Interface"
    private static string Category(string[] folders)
    {
        var top = folders.Length > 1 && folders[0].Equals("SFX", StringComparison.OrdinalIgnoreCase) ? folders[1] : folders.FirstOrDefault() ?? "";
        return top switch
        {
            "Weapons" => Weapons,
            "UI" => Interface,
            "Maps" => Maps,
            "Music" => Music,
            "Modes" => Modes,
            "CharacterSelect" or "Character_Select" => Interface,
            "Characters" => AgentOther,
            _ => Other
        };
    }

    private static string FixCaps(string text) =>
        text.Any(char.IsLower) ? text : System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.ToLowerInvariant());

    // "CastForward" -> "cast forward", "Chg" -> "charging"
    private static string Humanize(string token)
    {
        if (Words.TryGetValue(token, out var word)) return word;
        return CamelCase.Replace(token, " ");
    }
}
