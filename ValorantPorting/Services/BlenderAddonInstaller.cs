using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ValorantPorting.AppUtils;

namespace ValorantPorting.Services;

// Files built into the app: CUE4Parse-Natives.dll and the Blender add-on (zipped from ValorantPortingBlender at build
// time). The app puts them where they're needed, so updating the app updates everything:
// - the DLL next to the app (replaced when it differs),
// - the add-on in every Blender 5+ that has a Valorant Porting add-on installed, when that one is older
//   (%APPDATA%\Blender Foundation\Blender\<version>\scripts\addons\ValorantPortingBlender; the old one goes to the
//   Recycle Bin). Blender loads add-ons when it starts, so it has to be restarted afterwards.
public static class BlenderAddonInstaller
{
    private const string AddonFolderName = "ValorantPortingBlender";
    private static readonly Version MinimumBlender = new(5, 0); // what the add-on is made and tested for

    private static string AppFolder => Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
    // VP_BLENDER_FOLDER: another Blender user folder (testing)
    private static string BlenderUserFolder => Environment.GetEnvironmentVariable("VP_BLENDER_FOLDER") ??
                                               Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Blender Foundation", "Blender");

    private static byte[]? Resource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (stream is null) return null;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    // ---- CUE4Parse-Natives.dll

    // Puts the built-in DLL next to the app if it's missing or different (a newer app brings a newer one).
    public static void EnsureNatives()
    {
        try
        {
            if (Resource("CUE4Parse-Natives.dll") is not { } bytes) return; // a dev build without it
            var target = Path.Combine(AppFolder, "CUE4Parse-Natives.dll");
            if (File.Exists(target) && SHA256.HashData(File.ReadAllBytes(target)).SequenceEqual(SHA256.HashData(bytes))) return;
            File.WriteAllBytes(target, bytes);
        }
        catch (Exception ex)
        {
            AppLog.Warning($"Could not unpack CUE4Parse-Natives.dll next to the app ({ex.Message}); some animations may not export.");
        }
    }

    // ---- the Blender add-on

    private static byte[]? AddonZip => Resource("ValorantPortingBlender.zip");

    // the version in an add-on's bl_info: "version": (1, 10, 1)
    private static Version? VersionOf(string initPy)
    {
        var match = Regex.Match(initPy, @"""version""\s*:\s*\((\d+)\s*,\s*(\d+)\s*,\s*(\d+)\)");
        return match.Success ? new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value)) : null;
    }

    public static Version? BuiltInVersion
    {
        get
        {
            if (AddonZip is not { } zip) return null;
            using var archive = new ZipArchive(new MemoryStream(zip));
            var init = archive.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/') == $"{AddonFolderName}/__init__.py");
            if (init is null) return null;
            using var reader = new StreamReader(init.Open());
            return VersionOf(reader.ReadToEnd());
        }
    }

    public record Install(Version Blender, string AddonsFolder, Version? AddonVersion);

    // Blender 5+ versions of this user (their add-ons folder) and the Valorant Porting add-on in each, if any
    public static List<Install> FindBlenders()
    {
        var found = new List<Install>();
        if (!Directory.Exists(BlenderUserFolder)) return found;
        foreach (var folder in Directory.GetDirectories(BlenderUserFolder))
        {
            if (!Version.TryParse(Path.GetFileName(folder), out var blender) || blender < MinimumBlender) continue;
            var addons = Path.Combine(folder, "scripts", "addons");
            var init = Path.Combine(addons, AddonFolderName, "__init__.py");
            Version? addon = null;
            try { if (File.Exists(init)) addon = VersionOf(File.ReadAllText(init)) ?? new Version(0, 0, 0); } catch { }
            found.Add(new Install(blender, addons, addon));
        }

        return found.OrderBy(i => i.Blender).ToList();
    }

    // The Blender versions whose Valorant Porting add-on is older than the built-in one get the built-in one.
    // Returns the ones updated ("5.2"); errors are logged.
    public static List<string> UpdateInstalledAddons()
    {
        var updated = new List<string>();
        if (BuiltInVersion is not { } builtIn) return updated;
        foreach (var install in FindBlenders().Where(i => i.AddonVersion is { } v && v < builtIn))
        {
            try
            {
                InstallInto(install.AddonsFolder);
                updated.Add(install.Blender.ToString(2));
                AppLog.Information($"Blender {install.Blender.ToString(2)}: Valorant Porting add-on updated {install.AddonVersion!.ToString(3)} -> {builtIn.ToString(3)}.");
            }
            catch (Exception ex)
            {
                AppLog.Warning($"Could not update the Blender {install.Blender.ToString(2)} add-on ({ex.Message}). Install it by hand: " +
                               "Edit > Preferences > Add-ons > Install from Disk > the zip in the \"Blender Add-ons\" folder.");
            }
        }

        return updated;
    }

    // First install: into the newest Blender 5+ (it still has to be ticked in Blender's add-on list once).
    public static string? InstallIntoNewestBlender()
    {
        if (FindBlenders().LastOrDefault() is not { } newest) return null;
        InstallInto(newest.AddonsFolder);
        return newest.Blender.ToString(2);
    }

    // whether every Blender 5+ that has the add-on has the built-in version (or newer)
    public static bool InstalledAddonsUpToDate() =>
        BuiltInVersion is not { } builtIn || FindBlenders().All(i => i.AddonVersion is null || i.AddonVersion >= builtIn);

    private static void InstallInto(string addonsFolder)
    {
        var zip = AddonZip ?? throw new InvalidOperationException("this app has no built-in add-on");
        Directory.CreateDirectory(addonsFolder);
        var current = Path.Combine(addonsFolder, AddonFolderName);
        if (Directory.Exists(current))
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(current, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        using var archive = new ZipArchive(new MemoryStream(zip));
        archive.ExtractToDirectory(addonsFolder, overwriteFiles: true);
    }

    // The zip for installing by hand ("Install from Disk"), in the app's "Blender Add-ons" folder; older ones removed.
    public static void WriteManualZip()
    {
        try
        {
            if (AddonZip is not { } zip || BuiltInVersion is not { } version) return;
            var folder = Path.Combine(AppFolder, "Blender Add-ons");
            if (!Directory.Exists(folder)) folder = AppFolder;
            var target = Path.Combine(folder, $"1 - ValorantPortingBlender-{version.ToString(3)}.zip");
            foreach (var old in Directory.GetFiles(folder, "*ValorantPortingBlender*.zip").Where(f => !f.Equals(target, StringComparison.OrdinalIgnoreCase)))
                try { File.Delete(old); } catch { }
            if (!File.Exists(target) || !File.ReadAllBytes(target).SequenceEqual(zip)) File.WriteAllBytes(target, zip);
        }
        catch (Exception ex)
        {
            AppLog.Warning($"Could not write the add-on zip next to the app ({ex.Message}).");
        }
    }
}
