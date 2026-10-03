using System;
using System.Collections.Generic;
using System.Linq;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Objects.UObject;
using System.Text.RegularExpressions;

namespace ValorantPorting.Export;

// The sounds an animation plays in game: Riot puts sound cues on the timeline of an animation (mostly on its montage),
// each one pointing at a Wwise event ("Play_Wp_AK47_Chg_Hndl_Back_FP") at a time in seconds. Only cues that name their
// sound are used: others (arm movement, footsteps) are picked by the game's code, which isn't in the files.
public static class AnimationSounds
{
    public record Cue(double Time, string EventPath); // EventPath: "ShooterGame/Content/WwiseAudio/Events/.../Play_X"

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

    // Effects that play an animation also start sounds with it: ability casts and equips have no sound cues of their
    // own, their effect ("FXC_Wushu_E_Dashing") plays the montage and its sounds when it starts. Only the sounds made
    // to start with the effect are taken (not stop / unequip / HUD warning / per-direction ones).
    private static readonly HashSet<string> StartSounds = new(StringComparer.OrdinalIgnoreCase)
        { "PlayOnStart", "AudioEvent", "AkAudioEvent" };

    public static List<Cue> EffectCues(IFileProvider provider, Func<string, IReadOnlyList<string>> filesUsing, string objectPath, bool firstPerson)
    {
        var path = objectPath.StartsWith("ShooterGame/Content/", StringComparison.OrdinalIgnoreCase)
            ? "/Game/" + objectPath["ShooterGame/Content/".Length..]
            : objectPath;
        static bool IsEffect(string file) => file.EndsWith(".uasset") && file[(file.LastIndexOf('/') + 1)..].StartsWith("FXC_", StringComparison.OrdinalIgnoreCase);
        static bool IsMontage(string file) => file.EndsWith(".uasset") && file.Contains("_Montage", StringComparison.OrdinalIgnoreCase);
        static string PackageOf(string file) => "/Game/" + file["ShooterGame/Content/".Length..^".uasset".Length];

        // effects playing the animation itself, else the montages playing it (an effect plays the montage, which
        // plays the animation)
        var users = filesUsing(path);
        var effects = users.Where(IsEffect).ToList();
        if (effects.Count == 0)
            effects = users.Where(IsMontage).SelectMany(m => filesUsing(PackageOf(m))).Where(IsEffect).Distinct().ToList();

        // several effects use it (an equip and a charge marker): the ones named for what the animation does
        var name = path[(path.LastIndexOf('/') + 1)..];
        var actions = Regex.Split(name.Split('.')[0], "_").Where(w => w.Length >= 4 && !Regex.IsMatch(w, "^(Montage|Cosmetic)$", RegexOptions.IgnoreCase)).ToList();
        var named = effects.Where(e => actions.Any(w => e[(e.LastIndexOf('/') + 1)..].Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
        if (named.Count > 0) effects = named;

        var cues = new List<Cue>();
        foreach (var file in effects)
        {
            try
            {
                if (!provider.TryLoadPackage(file[..^".uasset".Length], out var package)) continue;
                void Add(FPackageIndex index, double delay)
                {
                    if (index.ResolvedObject?.Class?.Name.Text != "AkAudioEvent" || EventPath(index) is not { } eventPath) return;
                    var eventName = eventPath[(eventPath.LastIndexOf('/') + 1)..];
                    // cancelling / stopping sounds aren't part of the animation playing out
                    if (eventName.StartsWith("Stop_", StringComparison.OrdinalIgnoreCase) || eventName.Contains("Cancel", StringComparison.OrdinalIgnoreCase)) return;
                    if (cues.All(c => c.EventPath != eventPath)) cues.Add(new Cue(Math.Max(0, delay), eventPath));
                }

                foreach (var export in package.GetExports())
                    foreach (var property in export.Properties)
                    {
                        var propertyName = property.Name.Text;
                        var wanted = StartSounds.Contains(propertyName) ||
                                     propertyName.Equals(firstPerson ? "PlayOnStart1P" : "PlayOnStart3PAlly", StringComparison.OrdinalIgnoreCase);
                        if (wanted && property.Tag is CUE4Parse.UE4.Assets.Objects.Properties.ObjectProperty { Value: { IsNull: false } index })
                            Add(index, 0);

                        // newer effects (EffectAudioComponent): a list of { AudioEvent, InitialDelaySeconds }
                        if (propertyName.Equals("AudioEvents", StringComparison.OrdinalIgnoreCase) &&
                            property.Tag is CUE4Parse.UE4.Assets.Objects.Properties.ArrayProperty { Value: { } list })
                            foreach (var item in list.Properties)
                                if (item is CUE4Parse.UE4.Assets.Objects.Properties.StructProperty { Value.StructType: CUE4Parse.UE4.Assets.Objects.FStructFallback entry } &&
                                    entry.GetOrDefault<FPackageIndex?>("AudioEvent") is { IsNull: false } audioEvent)
                                    Add(audioEvent, entry.GetOrDefault<float>("InitialDelaySeconds"));
                    }
            }
            catch (Exception)
            {
                // unreadable effect: no sounds from it
            }
        }

        return cues.OrderBy(c => c.Time).ToList();
    }

    // object paths of the montages that play this animation ("ShooterGame/Content/…" or "/Game/…" object path in)
    private static IEnumerable<string> MontagesUsing(Func<string, IReadOnlyList<string>> filesUsing, string objectPath)
    {
        var path = objectPath.StartsWith("ShooterGame/Content/", StringComparison.OrdinalIgnoreCase)
            ? "/Game/" + objectPath["ShooterGame/Content/".Length..]
            : objectPath;
        foreach (var file in filesUsing(path))
        {
            if (!file.EndsWith(".uasset") || !file.Contains("Montage", StringComparison.OrdinalIgnoreCase)) continue;
            var package = file[..^".uasset".Length];
            yield return $"{package}.{package[(package.LastIndexOf('/') + 1)..]}";
        }
    }

    public record Placed(double Time, string Path, string Name); // a .wav at a moment of the animation

    // The sounds to send with an animation: the cues of the first source that has any (its montage, then the
    // animation itself), each as a .wav (one version of it: the 1st or 3rd person one to match, English voice).
    // An animation's sounds, in this order: its own cues (or its montage's), cues of any other montage playing it,
    // the sounds of the effects that play it. Empty if none.
    public static List<Cue> FindCues(IFileProvider provider, IEnumerable<string> sources, bool firstPerson,
        Func<string, IReadOnlyList<string>>? filesUsing = null)
    {
        var sourceList = sources.ToList();
        var cues = sourceList.Select(source => Read(provider, source)).FirstOrDefault(c => c.Count > 0);
        // montages playing it that the list doesn't pair with it (odd names like "…_Equip_MontageLongTMP")
        if (cues is null && filesUsing != null)
            cues = sourceList.SelectMany(source => MontagesUsing(filesUsing, source))
                .Select(montage => Read(provider, montage)).FirstOrDefault(c => c.Count > 0);
        if (cues is null && filesUsing != null)
            cues = sourceList.Select(source => EffectCues(provider, filesUsing, source, firstPerson)).FirstOrDefault(c => c.Count > 0);
        return cues ?? [];
    }

    // filesUsing: which files use a file (see AbilityResolver.FilesUsing), to find the effects that play the animation
    public static List<Placed> Prepare(IFileProvider provider, IEnumerable<string> sources, bool firstPerson,
        Func<string, IReadOnlyList<string>>? filesUsing = null)
    {
        var placed = new List<Placed>();
        var cues = FindCues(provider, sources, firstPerson, filesUsing);
        if (cues.Count == 0) return placed;

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

            if (wav is not null) placed.Add(new Placed(cue.Time, wav, cue.EventPath[(cue.EventPath.LastIndexOf('/') + 1)..]));
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
