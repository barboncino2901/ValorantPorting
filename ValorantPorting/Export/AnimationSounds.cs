using System;
using System.Collections.Generic;
using System.Linq;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Objects.UObject;
using System.Text.RegularExpressions;

namespace ValorantPorting.Export;

// The sounds an animation plays in game. Valorant starts them in a few ways, looked at in this order:
//   1. sound cues on the animation's timeline (mostly on its montage), each at its exact moment: gun equips, reloads,
//      inspects, some ability equips ("Play_Wp_AK47_Chg_Hndl_Back_FP" at 0.403 s);
//   2. the effect ("FXC_...") that plays the animation starts sounds with it, sometimes with a delay each: ability
//      casts, equips, dashes;
//   3. an effect started next to it by the same ability or gun, named for the same thing: Neon's ultimate fire loop
//      ("FXC_Sprinter_X_Beam_DirectionalAudio" beside "..._Beam_Ability"), Sage's ultimate idle ("Equipped");
//   4. the agent's sounds named for what the animation is (Skye's wolf run: "Wolf_Run_Vox", "FS_Wolf_Run").
// Effects also name the voice line said with them (Jett's ultimate: "Cast"), kept apart so they can be left out.
// Sounds started only by the game's code (footsteps of a run, arm movements) aren't in the files.
public static class AnimationSounds
{
    // EventPath: "ShooterGame/Content/WwiseAudio/Events/.../Play_X"; Voice: a voice line
    public record Cue(double Time, string EventPath, bool Voice = false);

    // the import index: which files use a file / which files a file uses (AbilityResolver.Shared)
    public record Links(Func<string, IReadOnlyList<string>> FilesUsing, Func<string, IReadOnlyList<string>> FilesUsedBy);

    // cues of the montage (or animation) at objectPath, in time order; empty when it has none or can't be read
    public static List<Cue> Read(IFileProvider provider, string objectPath)
    {
        var cues = new List<Cue>();
        try
        {
            if (!provider.TryLoadPackageObject(objectPath, out UAnimSequenceBase animation) || animation.Notifies is not { } notifies)
                return cues;

            foreach (var notify in notifies)
            {
                if (notify.Notify is not { IsNull: false } index || index.Load() is not { } notifyObject) continue;
                if (notifyObject.GetOrDefault<FPackageIndex?>("Event") is not { IsNull: false } eventIndex) continue;
                if (EventPath(eventIndex) is not { } path) continue;
                cues.Add(new Cue(Math.Max(0, notify.LinkValue + notify.TriggerTimeOffset), path));
            }
        }
        catch (Exception)
        {
            // unreadable: no sounds for it
        }

        return cues.OrderBy(c => c.Time).ToList();
    }

    // sounds made to start with an effect (not stop / unequip / HUD warning / per-direction ones)
    private static readonly HashSet<string> StartSounds = new(StringComparer.OrdinalIgnoreCase)
        { "PlayOnStart", "AudioEvent", "AkAudioEvent" };

    // An effect's start sounds and voice lines, with their delays
    public static List<Cue> EffectSounds(IFileProvider provider, string effectFile, bool firstPerson)
    {
        var cues = new List<Cue>();
        try
        {
            if (!provider.TryLoadPackage(effectFile[..^".uasset".Length], out var package)) return cues;
            void Add(FPackageIndex index, double delay, bool voice)
            {
                if (index.ResolvedObject?.Class?.Name.Text != "AkAudioEvent" || EventPath(index) is not { } eventPath) return;
                var eventName = eventPath[(eventPath.LastIndexOf('/') + 1)..];
                // cancelling / stopping sounds aren't part of the animation playing out
                if (eventName.StartsWith("Stop_", StringComparison.OrdinalIgnoreCase) || eventName.Contains("Cancel", StringComparison.OrdinalIgnoreCase)) return;
                if (cues.All(c => c.EventPath != eventPath)) cues.Add(new Cue(Math.Max(0, delay), eventPath, voice));
            }

            foreach (var export in package.GetExports())
                foreach (var property in export.Properties)
                {
                    var name = property.Name.Text;
                    var wanted = StartSounds.Contains(name) ||
                                 name.Equals(firstPerson ? "PlayOnStart1P" : "PlayOnStart3PAlly", StringComparison.OrdinalIgnoreCase);
                    if (wanted && property.Tag is ObjectProperty { Value: { IsNull: false } index })
                        Add(index, 0, false);

                    // newer effects (EffectAudioComponent): a list of { AudioEvent, InitialDelaySeconds }
                    if (name.Equals("AudioEvents", StringComparison.OrdinalIgnoreCase) && property.Tag is ArrayProperty { Value: { } list })
                        foreach (var item in list.Properties)
                            if (item is StructProperty { Value.StructType: FStructFallback entry } &&
                                entry.GetOrDefault<FPackageIndex?>("AudioEvent") is { IsNull: false } audioEvent)
                                Add(audioEvent, entry.GetOrDefault<float>("InitialDelaySeconds"), false);

                    // the voice line said with the effect (EffectAbilityVOComponent): a row of the ability's VO table
                    if (name.Equals("Line", StringComparison.OrdinalIgnoreCase) && property.Tag is StructProperty { Value.StructType: FStructFallback line } &&
                        line.GetOrDefault<FPackageIndex?>("DataTable") is { IsNull: false } tableIndex &&
                        line.GetOrDefault<FName>("RowName") is { IsNone: false } rowName &&
                        tableIndex.Load() is UDataTable table && table.RowMap.FirstOrDefault(r => r.Key.Text == rowName.Text).Value is { } row &&
                        row.GetOrDefault<FPackageIndex?>("Event") is { IsNull: false } voiceEvent)
                        Add(voiceEvent, row.GetOrDefault<float>("InitialDelay"), true);
                }
        }
        catch (Exception)
        {
            // unreadable effect: no sounds from it
        }

        return cues;
    }

    private static bool IsEffect(string file) => file.EndsWith(".uasset") && FileName(file).StartsWith("FXC_", StringComparison.OrdinalIgnoreCase);
    private static bool IsMontage(string file) => file.EndsWith(".uasset") && file.Contains("Montage", StringComparison.OrdinalIgnoreCase);
    private static string FileName(string file) => file[(file.LastIndexOf('/') + 1)..].Replace(".uasset", "");
    private static string PackageOf(string file) => "/Game/" + file["ShooterGame/Content/".Length..^".uasset".Length];
    private static string GamePath(string objectPath) => objectPath.StartsWith("ShooterGame/Content/", StringComparison.OrdinalIgnoreCase)
        ? "/Game/" + objectPath["ShooterGame/Content/".Length..]
        : objectPath;
    private static string ObjectPathOf(string file)
    {
        var package = file[..^".uasset".Length];
        return $"{package}.{package[(package.LastIndexOf('/') + 1)..]}";
    }

    // words that say what something is ("FP_Sprinter_S0_X_Fire" -> fire; "FXC_Sprinter_X_Beam_Ability" -> beam), and
    // words used for the same moment ("Idle" of an ultimate = it's "Equipped")
    private static readonly HashSet<string> NotMeaning = new(StringComparer.OrdinalIgnoreCase)
    {
        "fp", "tp", "ab", "abtp", "abcs", "gn", "s0", "fxc", "montage", "cosmetic", "production", "ability", "abil", "gun",
        "add", "pose", "both", "rot", "lb", "ub", "1p", "3p", "play", "sfx", "temp", "tmp", "long", "new", "v2", "base", "main"
    };

    private static readonly Dictionary<string, string[]> SameMoment = new(StringComparer.OrdinalIgnoreCase)
    {
        ["idle"] = ["equipped", "idle"], ["fire"] = ["beam", "fire", "shoot"],
        ["activate"] = ["cast", "activate"], ["cast"] = ["cast", "activate"], ["equip"] = ["equip", "equipped"]
    };

    // what an animation does, as effects are named for it (not what it's of: "RadEater", "Heal" are in all of an
    // ability's effects; nor moving around: walk, jump, aim)
    private static readonly HashSet<string> Actions = new(StringComparer.OrdinalIgnoreCase)
    {
        "equip", "equipped", "unequip", "cast", "throw", "fire", "shoot", "beam", "activate", "reactivate", "deploy", "recall",
        "release", "place", "use", "commit", "dash", "attack", "slash", "stab", "swing", "summon", "detonate", "trigger",
        "plant", "spawn", "toss", "launch", "charge", "finish", "intro", "outro", "revive", "resurrect", "idle",
        "explode", "explosion", "pickup", "retrieve", "reequip", "overhand", "underhand"
    };

    private static readonly HashSet<string> Movement = new(StringComparer.OrdinalIgnoreCase)
    {
        "walk", "run", "jump", "land", "crouch", "aim", "aims", "aimoffset", "aimoffsets", "offsets", "offset", "pose", "idlepose",
        "standup", "stand", "sprint", "slide", "strafe", "turn", "look", "blendspace", "nav", "fall", "rot"
    };

    private static List<string> ActionWords(IEnumerable<string> words) =>
        words.Where(Actions.Contains).SelectMany(w => SameMoment.GetValueOrDefault(w) ?? [w]).Distinct().ToList();

    public static List<string> Words(string name, string? agent = null) =>
        name.Split('.')[0].Split('_', StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(part => Regex.Split(part, "(?<=[a-z])(?=[A-Z])") is { Length: > 1 } pieces ? pieces : [part])
            .Select(w => w.ToLowerInvariant()).Distinct()
            .Where(w => w.Length >= 3 && !NotMeaning.Contains(w) && !Regex.IsMatch(w, "^[qecx4]$|^[0-9]+$") &&
                        (agent is null || !agent.Contains(w, StringComparison.OrdinalIgnoreCase))) // the agent's codename, in pieces too
            .ToList();

    private static readonly Regex AgentOf = new(@"Characters/([^/]+)/", RegexOptions.IgnoreCase);

    // Steps 2-4 for one animation (or montage) at objectPath: (sound effects, voice lines)
    public static (List<Cue> Sounds, List<Cue> Voice) LinkedSounds(IFileProvider provider, Links links, string objectPath, bool firstPerson)
    {
        var path = GamePath(objectPath);
        var name = path[(path.LastIndexOf('/') + 1)..].Split('.')[0];
        var agent = AgentOf.Match(path) is { Success: true } m ? m.Groups[1].Value : null;
        var words = Words(name, agent);

        // what plays it: effects, through its montages too (an effect plays the montage, which plays the animation);
        // and other files (the ability or gun itself)
        var users = links.FilesUsing(path).ToList();
        var montages = users.Where(IsMontage).ToList();
        var players = users.Concat(montages.SelectMany(mf => links.FilesUsing(PackageOf(mf)))).Distinct().Where(f => !IsMontage(f)).ToList();
        var effects = players.Where(IsEffect).ToList();
        // several effects play it (an equip and a charge marker): the ones named for what the animation does
        var actions = ActionWords(words);
        var named = effects.Where(e => ActionWords(Words(FileName(e), agent)).Any(actions.Contains)).ToList();
        if (named.Count > 0) effects = named;

        // moving around while holding an ability (walk, jump, aim, ...) is played by the ability's held state, whose
        // effect also has its cast or throw: only the state's looping sound goes with those
        var moving = named.Count == 0 && words.Any(Movement.Contains) && !actions.Any(a => a is not ("idle" or "equipped"));
        // an effect without sounds of its own may inherit them (Jett's DaggerThrow1-5 from DaggerThrowParent)
        IEnumerable<Cue> WithParents(string effect)
        {
            var own = EffectSounds(provider, effect, firstPerson);
            return own.Count > 0 ? own : links.FilesUsedBy(effect).Where(IsEffect).Take(3).SelectMany(parent => EffectSounds(provider, parent, firstPerson));
        }

        var found = effects.SelectMany(e => WithParents(e)
            .Where(c => !moving || c.Voice || IsLoop(c))).ToList();
        var sounds = found.Where(c => !c.Voice).ToList();
        var voice = found.Where(c => c.Voice).ToList();

        // 3. effects started by the same ability / gun, named for the same moment
        if (sounds.Count == 0)
        {
            var owners = players.Where(f => !IsEffect(f))
                .Concat(effects.SelectMany(e => links.FilesUsing(PackageOf(e))).Where(f => !IsEffect(f) && !IsMontage(f)))
                .Where(f => agent is null || f.Contains($"/{agent}/", StringComparison.OrdinalIgnoreCase))
                .Distinct().ToList();
            // the same action (Fire ~ Beam), plus the words of its own effect for the same part ("Beam")
            var moment = actions.Concat(named.SelectMany(e => Words(FileName(e), agent))).Distinct().ToList();
            var siblings = moment.Count == 0 ? [] : owners.SelectMany(links.FilesUsedBy).Where(IsEffect).Distinct()
                .Where(e => !effects.Contains(e) && !Ending.IsMatch(FileName(e)) && ActionWords(Words(FileName(e), agent)).Concat(Words(FileName(e), agent)).Any(moment.Contains))
                .Take(6).ToList();
            var siblingSounds = siblings.SelectMany(e => EffectSounds(provider, e, firstPerson)).ToList();
            // (an effect beside it may end the ability: out of ammo, expiring)
            sounds = siblingSounds.Where(c => !c.Voice && !Ending.IsMatch(FileName(c.EventPath + ".uasset"))).ToList();
            if (voice.Count == 0) voice = siblingSounds.Where(c => c.Voice).ToList();
        }

        // 4. the agent's sounds named for what the animation is (two words or more: "wolf run")
        if (sounds.Count == 0 && agent != null && words.Count >= 2)
            sounds = AgentSounds(provider, agent)
                .Where(e => !e.Contains("/VO/", StringComparison.OrdinalIgnoreCase) && words.All(Words(FileName(e + ".uasset"), agent).Contains))
                .OrderBy(e => e.Length).Take(3).Select(e => new Cue(0, e)).ToList();

        // voice lines only with the first use (not re-equipping it after a gun, quick / fast equips)
        if (Regex.IsMatch(name, "(Re_?Equip|Quick_?Equip|Fast_?Equip)", RegexOptions.IgnoreCase)) voice = [];

        // the voice line an ability says when it's used ("Cast" in its VO table): equips, casts, activations
        else if (voice.Count == 0 && words.Any(w => w is "equip" or "cast" or "activate") &&
            Regex.Match(path, @"^/Game/(Characters/[^/]+/S0/Ability_[^/]+)/") is { Success: true } folder)
            voice = AbilityVoiceLine(provider, folder.Groups[1].Value, "Cast");

        sounds = sounds.Where(c => !c.EventPath.EndsWith("Play_Silence", StringComparison.OrdinalIgnoreCase)).ToList();
        return (Distinct(sounds), Distinct(voice));
    }

    private static bool IsLoop(Cue cue) => Regex.IsMatch(FileName(cue.EventPath + ".uasset"), @"(Loop|_LP|Idle)", RegexOptions.IgnoreCase);

    // the sounds of an ability ending (out of ammo, expiring, timing out) don't belong at the start of its animations
    private static readonly Regex Ending = new(@"(Ammo_?Out|Expire|Timeout|Time_Out|_End(_|$)|Deactivate|Destroyed|NoFuel|Stop)", RegexOptions.IgnoreCase);

    // a row of the ability folder's voice line table ("Characters/Wushu/S0/Ability_X" -> DataTable_Wushu_X_VOLines, "Cast")
    private static List<Cue> AbilityVoiceLine(IFileProvider provider, string abilityFolder, string row)
    {
        var prefix = $"ShooterGame/Content/{abilityFolder}/";
        var tableFile = provider.Files.Keys.FirstOrDefault(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                                                                 k.IndexOf('/', prefix.Length) < 0 && k.EndsWith("VOLines.uasset", StringComparison.OrdinalIgnoreCase));
        if (tableFile is null) return [];
        try
        {
            if (!provider.TryLoadPackageObject(ObjectPathOf(tableFile), out UDataTable table)) return [];
            var entry = table.RowMap.FirstOrDefault(r => r.Key.Text.Equals(row, StringComparison.OrdinalIgnoreCase)).Value;
            if (entry?.GetOrDefault<FPackageIndex?>("Event") is not { IsNull: false } voiceEvent || EventPath(voiceEvent) is not { } eventPath) return [];
            return [new Cue(Math.Max(0, entry.GetOrDefault<float>("InitialDelay")), eventPath, true)];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static List<Cue> Distinct(IEnumerable<Cue> cues) =>
        cues.GroupBy(c => c.EventPath, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).OrderBy(c => c.Time).ToList();

    // the agent's "Play_" sound events (from their Events_Char_<agent> folders)
    private static readonly Dictionary<string, List<string>> agentSounds = new(StringComparer.OrdinalIgnoreCase);
    private static List<string> AgentSounds(IFileProvider provider, string agent)
    {
        lock (agentSounds)
        {
            if (agentSounds.TryGetValue(agent, out var list)) return list;
            var marker = $"/Events_Char_{agent}";
            return agentSounds[agent] = provider.Files.Keys
                .Where(k => k.StartsWith(Services.GameSounds.EventsRoot, StringComparison.OrdinalIgnoreCase) && k.Contains(marker, StringComparison.OrdinalIgnoreCase) &&
                            k.EndsWith(".uasset") && FileName(k).StartsWith("Play_", StringComparison.OrdinalIgnoreCase))
                .Select(k => k[..^".uasset".Length]).ToList();
        }
    }

    // An animation's sounds (sources: its montage first, then itself; see SourcesOf): the first step that finds any,
    // plus the voice lines of its effects. links null: cues only.
    public static (List<Cue> Sounds, List<Cue> Voice) FindCues(IFileProvider provider, IEnumerable<string> sources, bool firstPerson, Links? links = null)
    {
        var sourceList = sources.ToList();
        var cues = sourceList.Select(source => Read(provider, source)).FirstOrDefault(c => c.Count > 0);
        if (links is null) return (cues ?? [], []);

        // montages playing it that the list doesn't pair with it (odd names like "…_Equip_MontageLongTMP")
        cues ??= sourceList.SelectMany(source => links.FilesUsing(GamePath(source)).Where(IsMontage).Select(ObjectPathOf))
            .Select(montage => Read(provider, montage)).FirstOrDefault(c => c.Count > 0);

        List<Cue> voice = [];
        foreach (var source in sourceList)
        {
            var (sounds, lines) = LinkedSounds(provider, links, source, firstPerson);
            if (voice.Count == 0) voice = lines;
            if (cues is null && sounds.Count > 0) cues = sounds;
            if (cues != null && voice.Count > 0) break;
        }

        return (cues ?? [], voice);
    }

    // a .wav at a moment of the animation; Loop: the game repeats it while the animation plays
    public record Placed(double Time, string Path, string Name, bool Loop = false);

    // The sounds to send with an animation, each as a .wav (one version: the 1st or 3rd person one to match,
    // English voice): effects and/or voice lines
    public static List<Placed> Prepare(IFileProvider provider, IEnumerable<string> sources, bool firstPerson,
        Links? links = null, bool effects = true, bool voiceLines = true)
    {
        var placed = new List<Placed>();
        var (sounds, voice) = FindCues(provider, sources, firstPerson, links);
        var cues = (effects ? sounds : []).Concat(voiceLines ? voice : []).ToList();

        var wavs = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var cue in cues)
        {
            if (!wavs.TryGetValue(cue.EventPath, out var wav))
            {
                try
                {
                    var variants = Services.GameSounds.Variants(provider, cue.EventPath);
                    wavs[cue.EventPath] = wav = Pick(variants, firstPerson) is { } variant ? Services.GameSounds.Wav(cue.EventPath, variant) : null;
                }
                catch (Exception ex)
                {
                    AppUtils.AppLog.Warning($"Sound {cue.EventPath[(cue.EventPath.LastIndexOf('/') + 1)..]} skipped: {ex.Message}");
                    wavs[cue.EventPath] = wav = null;
                }
            }

            if (wav is not null) placed.Add(new Placed(cue.Time, wav, cue.EventPath[(cue.EventPath.LastIndexOf('/') + 1)..], !cue.Voice && IsLoop(cue)));
        }

        return placed;
    }

    // where an animation's cues can be: its montage first (that's what the game plays), then the animation itself;
    // for a mix, the upper body's (an equip over a run sounds like the equip)
    public static List<string> SourcesOf(Views.Controls.AnimationItem item)
    {
        var parts = item.Kind == Views.Controls.EAnimationKind.FullBody ? [item.UpperHalf!, item.LowerHalf!] : new[] { item };
        return parts.SelectMany(p => new[] { p.Wrapper?.ObjectPath, p.ObjectPath }).OfType<string>().Distinct().ToList();
    }

    // 1st person arms, guns (held in 1st person) and 1st person ability props hear the 1st person versions
    public static bool IsFirstPerson(Views.Controls.AnimationItem item) =>
        Regex.IsMatch(item.Name, "^(FP|GN|AB|EQ)_", RegexOptions.IgnoreCase);

    // the game picks one version at random; a fixed one here (the first), from the right point of view and language
    public static Services.GameSounds.Variant? Pick(IReadOnlyList<Services.GameSounds.Variant> variants, bool firstPerson)
    {
        if (variants.Count == 0) return null;
        var language = variants.Where(v => v.Language is null || v.Language.Equals("en-US", StringComparison.OrdinalIgnoreCase)).ToList();
        if (language.Count == 0) language = variants.ToList();
        var mine = firstPerson ? new[] { "_1P", "_FP" } : new[] { "_3P", "_TP" };
        var other = firstPerson ? new[] { "_3P", "_TP" } : new[] { "_1P", "_FP" };
        bool Has(Services.GameSounds.Variant v, string[] marks) => marks.Any(m => v.Name.Contains(m, StringComparison.OrdinalIgnoreCase));
        var view = language.Where(v => Has(v, mine)).ToList();
        if (view.Count == 0) view = language.Where(v => !Has(v, other)).ToList();
        if (view.Count == 0) view = language;
        return view.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).First();
    }

    // "/Game/WwiseAudio/Events/.../Play_X.Play_X" -> "ShooterGame/Content/WwiseAudio/Events/.../Play_X"
    private static string? EventPath(FPackageIndex index)
    {
        var path = index.ResolvedObject?.GetPathName();
        if (string.IsNullOrEmpty(path)) return null;
        var dot = path.LastIndexOf('.');
        if (dot > 0) path = path[..dot];
        return path.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase) ? "ShooterGame/Content/" + path["/Game/".Length..] : path.TrimStart('/');
    }
}
