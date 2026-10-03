using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.Assets.Exports.Wwise;
using CUE4Parse.UE4.Wwise;
using ValorantPorting.AppUtils;

namespace ValorantPorting.Services;

// The game's sound effects and voice lines (Wwise): the list of every sound, and one sound's audio as .wav.
// Kept light on purpose: the list is only names from the file index, the sound system starts the first time a sound
// is played or exported (and then only reads the sound banks that sound needs), and audio is converted one sound at a
// time, kept as .wav files in the Assets folder so the next time is instant.
public static class GameSounds
{
    public const string EventsRoot = "ShooterGame/Content/WwiseAudio/Events/";

    public record Entry(string EventPath, string EventName, string Folder); // EventPath without ".uasset"

    // one audio file of a sound: a sound often has a few versions the game picks from at random, 1st and 3rd person
    // versions, or one per language (voice lines)
    public record Variant(string Name, string? Language, Func<byte[]> Data);

    private static readonly object WwiseLock = new();
    private static WwiseProvider? wwise;

    // the sound banks read so far stay in memory while sounds are being used (about 50 MB, up to ~150 MB after
    // hundreds of different sounds); after a while without any, it's all let go: starting again takes ~0.3 s
    private static readonly TimeSpan IdleRelease = TimeSpan.FromMinutes(2);
    private static readonly System.Threading.Timer IdleTimer = new(_ => Release(), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);

    public static void Release()
    {
        lock (WwiseLock)
        {
            if (wwise is null) return;
            wwise = null;
        }

        AppUtils.MemoryHelper.ReleaseAfterLoading("Sound system idle");
    }

    // every "Play_" event (Stop_/Set_/Mix_ ones only control other sounds)
    public static List<Entry> List(IFileProvider provider) =>
        provider.Files.Keys
            .Where(k => k.StartsWith(EventsRoot, StringComparison.OrdinalIgnoreCase) && k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
            .Select(k =>
            {
                var path = k[..^".uasset".Length];
                var slash = path.LastIndexOf('/');
                return new Entry(path, path[(slash + 1)..], path[EventsRoot.Length..Math.Max(EventsRoot.Length, slash)]);
            })
            .Where(e => e.EventName.StartsWith("Play_", StringComparison.OrdinalIgnoreCase))
            .ToList();

    public static List<Variant> Variants(IFileProvider provider, string eventPath)
    {
        lock (WwiseLock)
        {
            var timer = Stopwatch.StartNew();
            var starting = wwise is null;
            wwise ??= new WwiseProvider((AbstractVfsFileProvider)provider, "", loadBanksOnDemand: true);
            if (starting) AppLog.Information($"Sound system ready in {timer.ElapsedMilliseconds} ms.");
            IdleTimer.Change(IdleRelease, System.Threading.Timeout.InfiniteTimeSpan);

            var name = eventPath[(eventPath.LastIndexOf('/') + 1)..];
            var audioEvent = provider.LoadPackageObject<UAkAudioEvent>($"{eventPath}.{name}");
            var variants = new List<Variant>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sound in wwise.ExtractAudioEventSounds(audioEvent))
            {
                if (sound.Data is null) continue;
                var file = Path.GetFileName(sound.OutputPath.Replace('\\', '/'));
                var language = Regex.Match(file, @"\(([a-zA-Z]{2}-[a-zA-Z]{2})\)$") is { Success: true } m ? m.Groups[1].Value : null;
                var variantName = Regex.Replace(file, @"\s*\((SFX|[a-zA-Z]{2}-[a-zA-Z]{2})\)$", "");
                if (!seen.Add($"{variantName}|{language}")) continue;
                // voice lines: the game only installs the language it's played in, the others are empty
                if (language is not null && sound.GetData().Length == 0) continue;
                variants.Add(new Variant(variantName, language, sound.GetData));
            }

            return variants;
        }
    }

    // the variant as a .wav in the Assets folder (converted once, then reused); null if it can't be converted
    public static string? Wav(string eventPath, Variant variant)
    {
        var relative = eventPath.StartsWith(EventsRoot, StringComparison.OrdinalIgnoreCase) ? eventPath[EventsRoot.Length..] : eventPath;
        var fileName = Safe(variant.Name) + (variant.Language is { } language ? $" ({language})" : "") + ".wav";
        var wav = Path.Combine(App.AssetsFolder.FullName, "Sounds", relative.Replace('/', Path.DirectorySeparatorChar), fileName);
        if (File.Exists(wav) && new FileInfo(wav).Length > 44) return wav;

        if (Converter() is not { } converter) return null;
        Directory.CreateDirectory(Path.GetDirectoryName(wav)!);
        var wem = wav[..^4] + ".wem";
        try
        {
            File.WriteAllBytes(wem, variant.Data());
            var start = new ProcessStartInfo(converter)
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("-o");
            start.ArgumentList.Add(wav);
            start.ArgumentList.Add(wem);
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync();
            process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(30000)) process.Kill();
            if (process.ExitCode != 0 || !File.Exists(wav))
            {
                AppLog.Warning($"Could not convert the sound {variant.Name}: {error.Result.Trim()}");
                return null;
            }

            return wav;
        }
        catch (Exception ex)
        {
            AppLog.Warning($"Could not convert the sound {variant.Name}: {ex.Message}");
            return null;
        }
        finally
        {
            try { File.Delete(wem); } catch { /* left behind: harmless */ }
        }
    }

    // ---- the converter (vgmstream, built into the app and unpacked into .data the first time)

    private static string? converterPath;

    private static string? Converter()
    {
        if (converterPath is not null && File.Exists(converterPath)) return converterPath;
        if (Environment.GetEnvironmentVariable("VP_VGMSTREAM") is { Length: > 0 } dev && File.Exists(dev)) return converterPath = dev;

        try
        {
            using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("vgmstream.zip");
            if (resource is null)
            {
                AppLog.Warning("The sound converter (vgmstream) isn't in this build, so sounds can't be converted.");
                return null;
            }

            using var memory = new MemoryStream();
            resource.CopyTo(memory);
            var bytes = memory.ToArray();
            var folder = Path.Combine(App.DataFolder.FullName, "vgmstream");
            var exe = Path.Combine(folder, "vgmstream-cli.exe");
            var stampFile = Path.Combine(folder, "version.txt");
            var stamp = Convert.ToHexString(SHA256.HashData(bytes));
            if (!File.Exists(exe) || !File.Exists(stampFile) || File.ReadAllText(stampFile) != stamp)
            {
                Directory.CreateDirectory(folder);
                using (var archive = new ZipArchive(new MemoryStream(bytes)))
                    foreach (var entry in archive.Entries.Where(e => e.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                                                                     e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                                                                     e.Name.Equals("COPYING", StringComparison.OrdinalIgnoreCase)))
                        entry.ExtractToFile(Path.Combine(folder, entry.Name), overwrite: true);
                File.WriteAllText(stampFile, stamp);
            }

            return converterPath = exe;
        }
        catch (Exception ex)
        {
            AppLog.Warning($"Could not unpack the sound converter: {ex.Message}");
            return null;
        }
    }

    private static string Safe(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
}
