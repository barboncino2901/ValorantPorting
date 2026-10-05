using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ValorantPorting.Export;

// Turns Valorant animation file names into readable titles, e.g.
//   TP_Wushu_S0_E_Dash_East        -> "Jett · Tailwind (E): Dash East"                  (3rd person)
//   TP_Core_AK_S0_Equip_UB         -> "Vandal: Equip (upper body)"                      (3rd person, shared)
//   GN_Core_HMG_Cyberknight_Equip_Lv3_Montage -> "Odin (<skin line>): Equip, level 3"   (gun)
public static class AnimationNamer
{
    public record Agent(string Name, IReadOnlyDictionary<string, string> Abilities);

    // (agent codename, ability folder letter, the animation's object path) -> "Boom Bot (C)": the ability's real name
    // and current key. File names use the folder letter, an old keybind for some agents (Raze's "E" files are the Boom
    // Bot, now on C), and a few animations are filed under another ability than the one using them. Without it, the
    // letter is taken as the key.
    public static Func<string, string, string?, string?>? AbilityLookup { get; set; }

    private static readonly Dictionary<string, string> Views = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TP"] = "3rd person", ["FP"] = "1st person", ["CS"] = "Character select", ["GN"] = "Gun",
        ["GNTP"] = "Gun (3rd person)", ["EQ"] = "Knife", ["EQTP"] = "Spike", ["AB"] = "Ability prop (1st person)",
        ["ABTP"] = "Ability prop (3rd person)", ["ABCS"] = "Ability prop (character select)"
    };

    // the held items that aren't guns: the spike ("Bomb" in file names), its defuser, the knife ("Melee"; Aeris's
    // totems are a knife too). Named this way in titles, whatever ValorantNames calls them
    private static readonly Dictionary<string, string> HeldItems = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Bomb"] = "Spike", ["Defuser"] = "Spike defuser", ["Melee"] = "Knife", ["Totems"] = "Knife"
    };

    private static readonly Dictionary<string, string> Directions = new(StringComparer.Ordinal)
    {
        ["N"] = "forward", ["S"] = "backward", ["E"] = "right", ["W"] = "left",
        ["NE"] = "forward-right", ["NW"] = "forward-left", ["SE"] = "backward-right", ["SW"] = "backward-left"
    };

    private static readonly Dictionary<string, string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ADS"] = "aiming", ["Aimsoffset"] = "aim offset", ["Unequip"] = "unequip", ["Lp"] = "loop",
        ["Loop"] = "loop", ["Cosmetic"] = "cosmetic", ["Add"] = "additive", ["Reldod"] = "reload"
    };

    private static readonly Regex WordWithDirection = new("^(.*[a-z])(NE|NW|SE|SW|N|S|E|W)$", RegexOptions.Compiled);
    private static readonly Regex CamelCase = new("(?<=[a-z])(?=[A-Z0-9])|(?<=[0-9])(?=[A-Za-z])|(?<=[A-Z])(?=[A-Z][a-z])", RegexOptions.Compiled);

    // objectPath: the animation's (for AbilityLookup)
    public static (string Title, string View) Describe(string name,
        IReadOnlyDictionary<string, Agent> agents,
        IReadOnlyDictionary<string, string> guns,
        IReadOnlyDictionary<string, string> skins,
        string? objectPath = null)
    {
        var tokens = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return (name, string.Empty);

        var start = 0;
        var view = string.Empty;
        if (Views.TryGetValue(tokens[0], out var viewName))
        {
            view = viewName;
            start = 1;
        }

        string? agentName = null, gunName = null, skinName = null, ability = null, bodyPart = null, agentCode = null;
        Agent? agent = null;
        var shared = false;
        var previousWasSkin = false;
        var previousWasAgent = false;
        var words = new List<string>();

        for (var i = start; i < tokens.Length; i++)
        {
            var token = tokens[i];
            // the slot letter comes after the skin ("FP_Wraith_S0_4_..."), or right after the agent when a file has no skin ("FP_Wraith_4_...")
            var afterSkin = previousWasSkin || previousWasAgent;
            previousWasSkin = false;
            previousWasAgent = false;

            if (token.Equals("Core", StringComparison.OrdinalIgnoreCase)) { shared = true; continue; }
            // filler words first: some of them ("Montage") are also skin codenames
            if (token.Equals("Montage", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(token, "^SEQ[0-9]+$")) continue;
            if (token == "UB") { bodyPart = "upper body"; continue; }
            if (token == "LB") { bodyPart = "lower body"; continue; }
            if (agentName is null && agents.TryGetValue(token, out var foundAgent))
            {
                agent = foundAgent;
                agentName = foundAgent.Name;
                agentCode = token;
                previousWasAgent = true;
                continue;
            }
            if (gunName is null && HeldItems.TryGetValue(token, out var heldItem))
            {
                gunName = heldItem;
                if (token.Equals("Totems", StringComparison.OrdinalIgnoreCase)) words.Add("Totems");
                continue;
            }
            if (gunName is null && guns.TryGetValue(token, out var foundGun)) { gunName = foundGun; continue; }
            if (Regex.IsMatch(token, "^S[0-9]$")) { previousWasSkin = true; continue; } // S0 = default skin
            // skin lines only apply to weapons, melee and finishers, never to an agent's own animations
            if (agent is null && skinName is null && (skins.TryGetValue(token, out var foundSkin) || (foundSkin = SkinWithTypo(token, skins)) != null))
            {
                skinName = foundSkin;
                previousWasSkin = true;
                continue;
            }

            // Ability slot right after the skin token: Q, E, C (stored as "4" in file names) or X
            if (agent is not null && ability is null && afterSkin && token is "Q" or "E" or "C" or "X" or "4")
            {
                var key = token == "4" ? "C" : token;
                ability = AbilityLookup?.Invoke(agentCode!, token, objectPath) ??
                          (agent.Abilities.TryGetValue(key, out var abilityName) ? $"{FixCaps(abilityName)} ({key})" : $"Ability {key}");
                continue;
            }

            var level = Regex.Match(token, "^Lv([0-9])$");
            if (level.Success) { words.Add($"level {level.Groups[1].Value}"); continue; }

            words.Add(Humanize(token));
        }

        var subject = agentName ?? gunName;
        if (agentName is not null && gunName is not null) words.Insert(0, $"with {gunName}");
        if (skinName is not null) subject = subject is null ? skinName : $"{subject} ({skinName})";
        if (ability is not null) subject = subject is null ? ability : $"{subject} · {ability}";

        var action = string.Join(" ", words.Where(w => w.Length > 0));
        if (action.Length > 0) action = char.ToUpperInvariant(action[0]) + action[1..];
        if (bodyPart is not null) action = action.Length > 0 ? $"{action} ({bodyPart})" : bodyPart;

        var title = subject is null ? action : action.Length > 0 ? $"{subject}: {action}" : subject;
        if (string.IsNullOrWhiteSpace(title)) title = name;
        // the item's own animations (EQ_): a spike or defuser one isn't a knife's
        if (view == "Knife" && gunName is "Spike" or "Spike defuser") view = "Spike";
        if (shared && view.Length > 0) view += ", shared";
        return (title, view);
    }

    // Riot's own typos in file names ("Ninjia2" for the Kuronami folder "Ninja2"): a skin codename one letter off
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> typoSkins = new(StringComparer.OrdinalIgnoreCase);
    private static IReadOnlyDictionary<string, string>? typoSkinsFor;

    private static string? SkinWithTypo(string token, IReadOnlyDictionary<string, string> skins)
    {
        if (token.Length < 5 || !token.Any(char.IsLetter)) return null;
        if (!ReferenceEquals(typoSkinsFor, skins)) { typoSkins.Clear(); typoSkinsFor = skins; }
        return typoSkins.GetOrAdd(token, t =>
        {
            var close = skins.Keys.Where(k => k.Length >= 5 && OneEditApart(t.ToLowerInvariant(), k.ToLowerInvariant())).Select(k => skins[k]).Distinct().ToList();
            return close.Count == 1 ? close[0] : null;
        });
    }

    private static bool OneEditApart(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 1 || a == b) return false;
        int i = 0, j = 0, edits = 0;
        while (i < a.Length && j < b.Length)
        {
            if (a[i] == b[j]) { i++; j++; continue; }
            if (++edits > 1) return false;
            if (a.Length > b.Length) i++;
            else if (a.Length < b.Length) j++;
            else { i++; j++; }
        }
        return edits + (a.Length - i) + (b.Length - j) <= 1;
    }

    // Some ability names come in ALL CAPS ("GATECRASH") -> "Gatecrash"
    private static string FixCaps(string text) =>
        text.Any(char.IsLower) ? text : System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.ToLowerInvariant());

    private static string Humanize(string token)
    {
        if (Words.TryGetValue(token, out var word)) return word;
        if (Directions.TryGetValue(token, out var direction)) return direction;

        // RunNE -> "Run forward-right", AimE -> "Aim right"
        var withDirection = WordWithDirection.Match(token);
        if (withDirection.Success && withDirection.Groups[1].Value.Length >= 3)
            return $"{Humanize(withDirection.Groups[1].Value)} {Directions[withDirection.Groups[2].Value]}";

        // CycloneBoost -> "Cyclone Boost", DashUp45 -> "Dash Up 45"
        return CamelCase.Replace(token, " ");
    }
}
