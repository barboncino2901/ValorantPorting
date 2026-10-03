using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using CUE4Parse.FileProvider;

namespace ValorantPorting.Export;

// Which ability an agent's sound belongs to. Sound names rarely say it the way the game does: Raze's are
// "Play_Abil_Boomba_EnemySpottedAlert" (Boomba = Boom Bot), Harbor's "E_CoveSmoke", Jett's "AbilE". So:
//   - a slot letter (AbilE, a folder named "E", "Ability_X") points to the agent's ability folder of that letter,
//   - a word that is the ability's name ("TourDeForce", "Cove") matches the official name,
//   - an internal word ("Boomba", "Satchel") is looked up in the agent's ability folders: the one whose files use it.
// Folder letters are old keybinds (Raze's "Ability_E" is the Boom Bot, now on C), so every folder's name and current
// key come from its UIData, like the Abilities tab.
public class SoundAbilities
{
    public record Ability(string Name, string Key)
    {
        public override string ToString() => Key.Length > 0 ? $"{Name} ({Key})" : Name;
    }

    // (agent codename, folder letter "Q"/"E"/"4"/"X"/...) -> ability
    private readonly Dictionary<(string Agent, string Letter), Ability> byLetter = new(new KeyComparer());
    // (agent codename, lower-case word) -> ability
    private readonly Dictionary<(string Agent, string Word), Ability> byWord = new(new KeyComparer());
    private readonly IReadOnlyDictionary<string, AnimationNamer.Agent> agents;

    // words that say nothing about which ability (they're in every folder, or in none in particular)
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "ability", "abil", "anims", "anim", "montage", "equip", "unequip", "idle", "cast", "throw", "fire", "loop", "skelmesh",
        "staticmesh", "mat", "mats", "material", "materials", "models", "model", "textures", "texture", "walk", "run", "jump",
        "land", "crouch", "aim", "aimset", "modes", "start", "end", "base", "inst", "instance", "masked", "mask", "physics",
        "physicsasset", "skeleton", "lod", "vfx", "fx", "temp", "new", "old", "test", "default", "add", "pose", "mesh"
    };

    private static readonly Regex Folder = new(@"^ShooterGame/Content/Characters/([^/_][^/]*)/S0/Ability_([^/]+)/(.+)\.uasset$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public SoundAbilities(IFileProvider provider, IReadOnlyDictionary<string, AnimationNamer.Agent> agents)
    {
        this.agents = agents;
        var counts = new Dictionary<(string Agent, string Word), Dictionary<string, int>>(new KeyComparer());
        var folders = new HashSet<(string Agent, string Letter)>(new KeyComparer());
        foreach (var file in provider.Files.Keys)
        {
            var match = Folder.Match(file);
            if (!match.Success) continue;
            var (agent, letter) = (match.Groups[1].Value, match.Groups[2].Value);
            folders.Add((agent, letter));
            foreach (var word in Words(match.Groups[3].Value[(match.Groups[3].Value.LastIndexOf('/') + 1)..]))
            {
                if (word.Length < 4 || Generic.Contains(word) || word.Equals(agent, StringComparison.OrdinalIgnoreCase)) continue;
                if (!counts.TryGetValue((agent, word), out var perFolder)) counts[(agent, word)] = perFolder = new();
                perFolder[letter] = perFolder.GetValueOrDefault(letter) + 1;
            }
        }

        foreach (var (agent, letter) in folders)
        {
            var info = Views.Controls.AbilityItem.ReadAbilityInfo(provider, $"Characters/{agent}/S0/Ability_{letter}");
            var agentInfo = agents.GetValueOrDefault(agent);
            var key = info?.Name is { } name && agentInfo?.Abilities.FirstOrDefault(a => a.Value.Equals(name, StringComparison.OrdinalIgnoreCase)).Key is { } official
                ? official
                : info?.Key is { Length: > 0 } infoKey ? infoKey : letter == "4" ? "C" : letter.Length == 1 ? letter.ToUpperInvariant() : "";
            var abilityName = info?.Name ?? (key.Length > 0 ? agentInfo?.Abilities.GetValueOrDefault(key) : null);
            if (abilityName is null) continue;
            byLetter[(agent, letter)] = new Ability(FixCaps(abilityName), key);
        }

        // a word used (almost) only in one ability's files names that ability
        foreach (var ((agent, word), perFolder) in counts)
        {
            var total = perFolder.Values.Sum();
            var (letter, most) = perFolder.MaxBy(f => f.Value);
            if (total >= 2 && most >= total * 0.8 && byLetter.TryGetValue((agent, letter), out var ability))
                byWord[(agent, word)] = ability;
        }
    }

    private static readonly Regex NameSlot = new(@"^Abil(?:ity)?([QECX4])$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // a folder "E", "Ability_E", "AbilX", "E_CoveSmoke", "Cashew_C"
    private static readonly Regex FolderSlot = new(@"^(?:Abil(?:ity)?_?)?([QECX4])$|^([QECX4])_[A-Za-z]\w*$|^[A-Za-z]\w*_([QECX4])$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // the ability for a sound of this agent, from its name's parts ("Abil", "Boomba", ...) and its folders below the
    // agent's events folder; also returns the part that told (to leave out of the readable name)
    public (Ability Ability, string UsedToken)? Find(string agent, IReadOnlyList<string> nameTokens, IReadOnlyList<string> folders)
    {
        // 1. a slot letter (bare letters only as folder names: in a name, "_C" is mostly a variant, like "Element_C")
        foreach (var (token, slot) in nameTokens.Select(t => (t, NameSlot.Match(t))).Concat(folders.Select(f => (f, FolderSlot.Match(f)))))
        {
            if (!slot.Success) continue;
            var value = slot.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value.ToUpperInvariant();
            foreach (var folder in value == "C" ? new[] { "C", "4" } : [value])
                if (byLetter.TryGetValue((agent, folder), out var ability)) return (ability, token);
        }

        var tokens = nameTokens.Concat(folders).ToList();

        // 2. the ability's own name in a word ("TourDeForce", "E_CoveSmoke")
        if (agents.GetValueOrDefault(agent) is { } agentInfo)
            foreach (var token in tokens)
            {
                var plain = Plain(token);
                foreach (var (key, name) in agentInfo.Abilities)
                {
                    var abilityPlain = Plain(name);
                    if (abilityPlain.Length >= 4 && plain.Contains(abilityPlain))
                        return (new Ability(FixCaps(name), key), token);
                }
            }

        // 3. an internal word of one ability's files ("Boomba")
        foreach (var token in tokens)
            foreach (var word in Words(token))
                if (byWord.TryGetValue((agent, word), out var ability)) return (ability, token);

        return null;
    }

    // "Boomba_EnemySpottedAlert" -> Boomba, Enemy, Spotted, Alert (and the whole camel-case word)
    private static IEnumerable<string> Words(string text)
    {
        foreach (var part in text.Split('_', StringSplitOptions.RemoveEmptyEntries))
        {
            yield return part;
            foreach (var piece in Regex.Split(part, "(?<=[a-z])(?=[A-Z])"))
                if (piece != part) yield return piece;
        }
    }

    private static string Plain(string text) => Regex.Replace(text.ToLowerInvariant(), "[^a-z0-9]", "");

    private static string FixCaps(string text) =>
        text.Any(char.IsLower) ? text : System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.ToLowerInvariant());

    private sealed class KeyComparer : IEqualityComparer<(string, string)>
    {
        public bool Equals((string, string) x, (string, string) y) =>
            string.Equals(x.Item1, y.Item1, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Item2, y.Item2, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string, string) obj) =>
            HashCode.Combine(obj.Item1.ToLowerInvariant(), obj.Item2.ToLowerInvariant());
    }
}
