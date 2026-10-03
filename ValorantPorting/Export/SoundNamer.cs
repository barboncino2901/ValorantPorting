using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ValorantPorting.Export;

// Turns Valorant sound event names into readable titles, e.g.
//   Play_Wp_AK47_Chg_Hndl_Back_FP          -> "Vandal: Charging handle back (1st person)"
//   Play_Wp_AK47_AfterGlow_Chg_Hndl_Back_FP -> "Vandal (RGX 11z Pro): Charging handle back (1st person)"
//   Play_Wushu_AbilE_Cast_Forward          -> "Jett · Tailwind (E): Cast forward"
//   Play_VO_Wushu_E07_LastKill_Rift        -> "Jett voice line: Last kill Astra"
//   Play_FS_Mvt_Breach_Run                 -> "Breach: Footsteps run"
public static class SoundNamer
{
    // sound files use some gun codenames of their own; these point to the weapon folder names used everywhere else
    private static readonly Dictionary<string, string> GunAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AK47"] = "AK", ["BurstRifle"] = "Burst", ["ShotgunAuto"] = "AutoShotgun", ["Shotgun"] = "PumpShotgun",
        ["Pump"] = "PumpShotgun", ["PistolCompact"] = "Compact", ["Sawed"] = "Slim", ["Lever"] = "Leversniper",
        ["LeverSniper"] = "Leversniper", ["BoltSniper"] = "Boltsniper", ["DoubleSniper"] = "Doublesniper",
        ["Mp5"] = "MP5", ["Knife"] = "Melee"
    };

    private static readonly Dictionary<string, string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Chg"] = "charging", ["Hndl"] = "handle", ["Hnd"] = "handle", ["Mag"] = "magazine", ["Rls"] = "release",
        ["Mvt"] = "movement", ["Ele"] = "element", ["Amb"] = "ambience", ["BG"] = "background", ["Abil"] = "ability",
        ["Ult"] = "ultimate", ["Lp"] = "loop", ["Sel"] = "select", ["Char"] = "", ["Wp"] = "", ["SFX"] = "", ["Emote"] = "",
        ["Mvmt"] = "movement", ["FS"] = "footsteps", ["Ads"] = "aiming", ["Eqp"] = "equip", ["Fwd"] = "forward",
        ["Bwd"] = "backward", ["Med"] = "medium", ["Rld"] = "reload", ["Insp"] = "inspect", ["Lt"] = "light"
    };

    private static readonly Dictionary<string, string> Views = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FP"] = "1st person", ["1P"] = "1st person", ["TP"] = "3rd person", ["3P"] = "3rd person"
    };

    private static readonly Regex AbilityToken = new("^Abil(Q|E|C|X|4|[1-4])$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CamelCase = new("(?<=[a-z])(?=[A-Z0-9])|(?<=[0-9])(?=[A-Za-z])|(?<=[A-Z])(?=[A-Z][a-z])", RegexOptions.Compiled);

    // eventName: "Play_Wushu_AbilE_Cast_Forward"; folder: its folder under WwiseAudio/Events ("SFX/Characters/Events_Char_Wushu_SFX/...")
    public static (string Title, string Category) Describe(string eventName, string folder,
        IReadOnlyDictionary<string, AnimationNamer.Agent> agents,
        IReadOnlyDictionary<string, string> guns,
        IReadOnlyDictionary<string, string> skins)
    {
        var name = Regex.Replace(eventName, "^Play_", "", RegexOptions.IgnoreCase);
        var tokens = name.Split('_', StringSplitOptions.RemoveEmptyEntries);

        string? agentName = null, gunName = null, skinName = null, ability = null, view = null;
        AnimationNamer.Agent? agent = null;
        var voice = false;
        var words = new List<string>();

        foreach (var token in tokens)
        {
            if (token.Equals("VO", StringComparison.OrdinalIgnoreCase)) { voice = true; continue; }
            if (Views.TryGetValue(token, out var viewName)) { view ??= viewName; continue; }
            if (Regex.IsMatch(token, "^(S0|E[0-9]{2})$")) continue; // default skin, voice episode ("E07")

            if (agentName is null && agents.TryGetValue(token, out var foundAgent)) { agent = foundAgent; agentName = foundAgent.Name; continue; }
            // another agent later on (a voice line about them): their real name, so "jett astra" finds it
            if (agents.TryGetValue(token, out var otherAgent)) { words.Add(otherAgent.Name); continue; }
            if (gunName is null && (guns.TryGetValue(token, out var foundGun) ||
                                    GunAliases.TryGetValue(token, out var alias) && guns.TryGetValue(alias, out foundGun)))
            {
                gunName = foundGun == "Melee" ? "Knife" : foundGun;
                continue;
            }
            if (agent is null && gunName is not null && skinName is null && skins.TryGetValue(token, out var foundSkin)) { skinName = foundSkin; continue; }

            var abilityMatch = AbilityToken.Match(token);
            if (abilityMatch.Success)
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

        var subject = agentName ?? gunName;
        if (skinName is not null) subject = subject is null ? skinName : $"{subject} ({skinName})";
        if (voice) subject = subject is null ? "Voice line" : $"{subject} voice line";
        if (ability is not null) subject = subject is null ? ability : $"{subject} · {ability}";

        var action = string.Join(" ", words);
        if (action.Length > 0) action = char.ToUpperInvariant(action[0]) + action[1..];
        if (view is not null) action = action.Length > 0 ? $"{action} ({view})" : view;

        var title = subject is null ? action : action.Length > 0 ? $"{subject}: {action}" : subject;
        if (string.IsNullOrWhiteSpace(title)) title = eventName;
        return (title, Category(folder, voice));
    }

    // "SFX/Weapons/Events_Guns_RE/..." -> "Weapons", "VO/Characters/..." -> "Voice lines", "SFX/Maps/Events_Map_Plummet" -> "Maps"
    private static string Category(string folder, bool voice)
    {
        if (voice || folder.StartsWith("VO/", StringComparison.OrdinalIgnoreCase)) return "Voice lines";
        var parts = folder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var top = parts.Length > 1 && parts[0].Equals("SFX", StringComparison.OrdinalIgnoreCase) ? parts[1] : parts.FirstOrDefault() ?? "";
        return top switch
        {
            "Characters" => "Agents",
            "Weapons" => "Weapons",
            "UI" => "Interface",
            "Maps" => "Maps",
            "Music" => "Music",
            "Modes" => "Game modes",
            "Character_Select" or "CharacterSelect" => "Character select",
            _ => "Other"
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
