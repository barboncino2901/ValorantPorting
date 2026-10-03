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
        var cues = new List<Cue>();
        var path = objectPath.StartsWith("ShooterGame/Content/", StringComparison.OrdinalIgnoreCase)
            ? "/Game/" + objectPath["ShooterGame/Content/".Length..]
            : objectPath;
        foreach (var file in filesUsing(path))
        {
            var fileName = file[(file.LastIndexOf('/') + 1)..];
            if (!fileName.StartsWith("FXC_", StringComparison.OrdinalIgnoreCase) || !file.EndsWith(".uasset")) continue;
            try
            {
                if (!provider.TryLoadPackage(file[..^".uasset".Length], out var package)) continue;
                foreach (var export in package.GetExports())
                    foreach (var property in export.Properties)
                    {
                        var name = property.Name.Text;
                        var wanted = StartSounds.Contains(name) || name.Equals(firstPerson ? "PlayOnStart1P" : "PlayOnStart3PAlly", StringComparison.OrdinalIgnoreCase);
                        if (!wanted || property.Tag is not CUE4Parse.UE4.Assets.Objects.Properties.ObjectProperty { Value: { IsNull: false } index }) continue;
                        if (index.ResolvedObject?.Class?.Name.Text != "AkAudioEvent" || EventPath(index) is not { } eventPath) continue;
                        if (cues.All(c => c.EventPath != eventPath)) cues.Add(new Cue(0, eventPath));
                    }
            }
            catch (Exception)
            {
                // unreadable effect: no sounds from it
            }
        }

        return cues;
    }

    public record Placed(double Time, string Path, string Name); // a .wav at a moment of the animation

    // The sounds to send with an animation: the cues of the first source that has any (its montage, then the
    // animation itself), each as a .wav (one version of it: the 1st or 3rd person one to match, English voice).
    // filesUsing: which files use a file (see AbilityResolver.FilesUsing), to find the effects that play the animation
    public static List<Placed> Prepare(IFileProvider provider, IEnumerable<string> sources, bool firstPerson,
        Func<string, IReadOnlyList<string>>? filesUsing = null)
    {
        var placed = new List<Placed>();
        var sourceList = sources.ToList();
        var cues = sourceList.Select(source => Read(provider, source)).FirstOrDefault(c => c.Count > 0);
        if (cues is null && filesUsing != null)
            cues = sourceList.Select(source => EffectCues(provider, filesUsing, source, firstPerson)).FirstOrDefault(c => c.Count > 0);
        if (cues is null) return placed;

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
