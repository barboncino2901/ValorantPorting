using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Animation;
using ValorantPorting.Views.Controls;

namespace ValorantPorting.Export;

// Tidies the animation list. Riot's montages are little playlists: about 4,000 of them only play one animation that is
// already in the list, ~550 play an upper body + lower body pair (covered by the list's "(full body)" entries), and a
// few hundred play several animations in a row (character select intro -> idle), which stay as "(sequence)" entries.
public static class AnimationMontages
{
    // Wrappers: the hidden montage that plays each animation (its sound cues are on the montage)
    public record Result(HashSet<AnimationItem> Hidden, Dictionary<AnimationItem, List<AnimationItem>> Sequences,
        Dictionary<AnimationItem, AnimationItem> Wrappers);

    // "(full body)" entries for every _UB/_LB pair in the same folder
    public static List<AnimationItem> FullBodyPairs(IReadOnlyList<AnimationItem> items)
    {
        // "…/TP_Core_AK_S0_Equip_UB.TP_Core_AK_S0_Equip_UB" -> "…/tp_core_ak_s0_equip"
        static string PairKey(AnimationItem item) => Key(item.ObjectPath[..item.ObjectPath.LastIndexOf('.')])[..^3];
        var lower = new Dictionary<string, AnimationItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.Where(i => i.Name.EndsWith("_LB"))) lower.TryAdd(PairKey(item), item);
        var pairs = new List<AnimationItem>();
        foreach (var upper in items.Where(i => i.Name.EndsWith("_UB")))
            if (lower.TryGetValue(PairKey(upper), out var match))
                pairs.Add(AnimationItem.FullBody(upper, match));
        return pairs;
    }

    // Sorting opens ~5,600 montages (about 5 s), so the result is kept in .data and reused until the game files or the
    // app change. One file, replaced each time. gameUpdate: when the game files last changed (null: don't cache).
    public static Result ClassifyCached(IFileProvider provider, IReadOnlyList<AnimationItem> items, DateTime? gameUpdate)
    {
        var file = Path.Combine(App.DataFolder.FullName, "montage-sort-cache.json");
        var stamp = gameUpdate is { } changed ? $"{Services.UpdateService.CurrentVersion}|{changed.Ticks}|{items.Count}|empty-hidden" : null;
        var byPath = new Dictionary<string, AnimationItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.Where(i => i.Kind == EAnimationKind.Single)) byPath.TryAdd(item.ObjectPath, item);

        if (stamp != null && Read(file) is { } cached && cached.Stamp == stamp)
        {
            var hidden = cached.Hidden.Select(p => byPath.GetValueOrDefault(p)).ToList();
            var sequences = cached.Sequences.ToDictionary(s => byPath.GetValueOrDefault(s.Key)!, s => s.Value.Select(p => byPath.GetValueOrDefault(p)!).ToList());
            var wrappers = (cached.Wrappers ?? []).Select(w => (Clip: byPath.GetValueOrDefault(w.Key), Montage: byPath.GetValueOrDefault(w.Value)))
                .Where(w => w.Clip != null && w.Montage != null).ToDictionary(w => w.Clip!, w => w.Montage!);
            if (cached.Wrappers != null && hidden.All(h => h != null) && sequences.All(s => s.Key != null && s.Value.All(c => c != null)))
                return new Result(hidden.ToHashSet()!, sequences, wrappers);
        }

        var result = Classify(provider, items);
        if (stamp != null)
        {
            try
            {
                App.DataFolder.Create();
                File.WriteAllText(file, JsonConvert.SerializeObject(new CacheFile(stamp, result.Hidden.Select(h => h.ObjectPath).ToList(),
                    result.Sequences.ToDictionary(s => s.Key.ObjectPath, s => s.Value.Select(c => c.ObjectPath).ToList()),
                    result.Wrappers.ToDictionary(w => w.Key.ObjectPath, w => w.Value.ObjectPath))));
            }
            catch (Exception)
            {
                // no cache this time: sorted again on the next start
            }
        }

        return result;
    }

    private record CacheFile(string Stamp, List<string> Hidden, Dictionary<string, List<string>> Sequences,
        Dictionary<string, string>? Wrappers); // null in caches from before sounds: sorted again

    private static CacheFile? Read(string file)
    {
        try
        {
            return File.Exists(file) ? JsonConvert.DeserializeObject<CacheFile>(File.ReadAllText(file)) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static Result Classify(IFileProvider provider, IReadOnlyList<AnimationItem> items)
    {
        var byPath = new Dictionary<string, AnimationItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.Where(i => i.Kind == EAnimationKind.Single)) byPath.TryAdd(Key(item.ObjectPath), item);
        var pairedHalves = items.Where(i => i.Kind == EAnimationKind.FullBody)
            .SelectMany(i => new[] { i.UpperHalf!, i.LowerHalf! }).ToHashSet();

        var hidden = new HashSet<AnimationItem>();
        var sequences = new Dictionary<AnimationItem, List<AnimationItem>>();
        var wrappers = new Dictionary<AnimationItem, AnimationItem>();
        foreach (var montageItem in items.Where(i => i.Kind == EAnimationKind.Single && MontageName.IsMatch(i.Name)))
        {
            try
            {
                if (!provider.TryLoadPackageObject(montageItem.ObjectPath, out UAnimMontage montage)) continue;
                var tracks = montage.SlotAnimTracks
                    .Select(t => (t.AnimTrack?.AnimSegments ?? []).Select(s => s.AnimReference?.ResolvedObject?.GetPathName()).ToList())
                    .Where(t => t.Count > 0).ToList();
                // nothing left to play (Riot removed its animation from the files): not worth listing
                if (tracks.Count > 0 && tracks.SelectMany(t => t).All(p => p is null))
                {
                    hidden.Add(montageItem);
                    continue;
                }
                if (tracks.Count == 0 || tracks.SelectMany(t => t).Any(p => p is null)) continue;

                var clips = tracks.SelectMany(t => t).Select(p => byPath.GetValueOrDefault(Key(p!))).ToList();
                if (clips.Any(c => c is null)) continue; // plays something that isn't in the list: keep it as it is
                var distinct = clips.Distinct().ToList();

                // one animation, or an upper + lower body pair (plus a face track repeating the upper body)
                if (distinct.Count == 1 || distinct.All(pairedHalves.Contains) && distinct.Any(c => c!.Name.EndsWith("_UB")) &&
                    distinct.Any(c => c!.Name.EndsWith("_LB")) && distinct.Count == 2)
                {
                    hidden.Add(montageItem);
                    // "…_Montage" over "…_Montage2" when several play the same animation
                    foreach (var clip in distinct)
                        if (!wrappers.TryGetValue(clip!, out var known) || montageItem.Name.Length < known.Name.Length)
                            wrappers[clip!] = montageItem;
                    continue;
                }

                // several animations in a row on the main track; other tracks (a face track) only repeat them
                var main = tracks.OrderByDescending(t => t.Count).First().Select(p => byPath[Key(p!)]).ToList();
                if (main.Distinct().Count() > 1 && clips.All(main.Contains)) sequences[montageItem] = main;
            }
            catch (Exception)
            {
                // unreadable montage: keep it as it is
            }
        }

        return new Result(hidden, sequences, wrappers);
    }

    private static readonly System.Text.RegularExpressions.Regex MontageName =
        new(@"_Montage\d*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase); // "…_Montage", "…_Montage1"

    // "/Game/X/Y.Y" and "ShooterGame/Content/X/Y.Y" -> "x/y.y"
    private static string Key(string objectPath)
    {
        var path = objectPath.TrimStart('/');
        foreach (var prefix in new[] { "Game/", "ShooterGame/Content/" })
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) path = path[prefix.Length..];
        return path.ToLowerInvariant();
    }
}
