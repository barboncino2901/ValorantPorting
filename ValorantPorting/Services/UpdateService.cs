using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using ValorantPorting.AppUtils;

namespace ValorantPorting.Services;

// Updates from this project's GitHub releases: finds a newer release, downloads the app (checked against GitHub's
// SHA-256 checksum), swaps it in and restarts. The new app brings CUE4Parse-Natives.dll and the Blender add-on built in
// and puts them in place itself (BlenderAddonInstaller). The replaced file is only kept until the new version has
// started; if it doesn't start, it's put back.
public static class UpdateService
{
    private const string Repository = "barboncino2901/ValorantPorting";
    private const string LatestReleaseApi = $"https://api.github.com/repos/{Repository}/releases/latest";
    public const string ReleasesPage = $"https://github.com/{Repository}/releases/latest";
    private const string AfterUpdateArgument = "--after-update";
    private static readonly string[] AppAssets = ["ValorantPorting.exe"];
    // left by updaters before 1.10.1, which also swapped the DLL
    private static readonly string[] LeftoverAssets = ["ValorantPorting.exe", "CUE4Parse-Natives.dll"];

    public record Asset(string Name, string Url, string? Sha256);
    public record Release(Version Version, string Tag, string PageUrl, string Notes, List<Asset> Assets);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ValorantPorting-Updater");
        return client;
    }

    public static Version CurrentVersion => Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0);

    // Local/dev builds aren't stamped with a release version (1.0.0.0): they don't look for updates on their own.
    public static bool IsReleaseBuild => CurrentVersion > new Version(1, 0, 0, 0);

    private static string AppFolder => Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
    private static string UpdateFolder => Path.Combine(App.DataFolder.FullName, "update");
    private static string StartedMarker => Path.Combine(App.DataFolder.FullName, "update-started.ok");

    // The latest release if it's newer than this version; null if not (or GitHub can't be reached).
    public static async Task<Release?> FindNewerReleaseAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await Http.SendAsync(request, new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);
            if (!response.IsSuccessStatusCode) return null;
            var json = JObject.Parse(await response.Content.ReadAsStringAsync());

            var tag = json.Value<string>("tag_name") ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
            if (Normalize(version) <= Normalize(CurrentVersion)) return null;

            var assets = json["assets"]?.Children<JObject>()
                .Select(a => new Asset(a.Value<string>("name") ?? "", a.Value<string>("browser_download_url") ?? "",
                    a.Value<string>("digest") is { } digest && digest.StartsWith("sha256:") ? digest["sha256:".Length..] : null))
                .ToList() ?? [];
            if (AppAssets.Any(name => assets.All(a => a.Name != name))) return null; // not a complete release

            return new Release(version, tag, json.Value<string>("html_url") ?? ReleasesPage, json.Value<string>("body") ?? "", assets);
        }
        catch (Exception)
        {
            return null; // offline, rate limited, ...: try again next start
        }
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    // Downloads and installs the release, starts it and waits until it has opened. Returns false (with everything as
    // it was) if something fails; on success the caller closes this instance.
    public static async Task<bool> InstallAsync(Release release, IProgress<string> status)
    {
        var replaced = new List<(string Target, string Backup)>();
        Process? started = null;
        try
        {
            Directory.CreateDirectory(UpdateFolder);
            var downloads = new Dictionary<string, string>();
            foreach (var asset in release.Assets.Where(a => AppAssets.Contains(a.Name)))
            {
                status.Report($"Downloading {asset.Name}...");
                var file = Path.Combine(UpdateFolder, asset.Name);
                await using (var source = await Http.GetStreamAsync(asset.Url))
                await using (var target = File.Create(file))
                    await source.CopyToAsync(target);
                if (asset.Sha256 != null && !Sha256Of(file).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"{asset.Name} didn't download correctly (checksum mismatch).");
                downloads[asset.Name] = file;
            }

            // A running program can't be overwritten on Windows, but it can be renamed: move the old files aside.
            status.Report("Installing...");
            foreach (var name in AppAssets)
            {
                var target = Path.Combine(AppFolder, name);
                var backup = target + ".old";
                if (File.Exists(target))
                {
                    File.Move(target, backup, overwrite: true);
                    replaced.Add((target, backup));
                }
                else replaced.Add((target, ""));

                File.Move(downloads[name], target, overwrite: true);
            }

            // start the new version; it confirms with a marker file once its window is up
            status.Report("Starting the new version...");
            File.Delete(StartedMarker);
            started = Process.Start(new ProcessStartInfo(Path.Combine(AppFolder, "ValorantPorting.exe"), AfterUpdateArgument)
                { WorkingDirectory = Directory.GetCurrentDirectory(), UseShellExecute = false });
            for (var waited = 0; waited < 60; waited++)
            {
                if (File.Exists(StartedMarker)) return true;
                if (started is null || started.HasExited) break;
                await Task.Delay(1000);
            }

            throw new InvalidOperationException("The new version didn't start.");
        }
        catch (Exception ex)
        {
            try { if (started is { HasExited: false }) started.Kill(); } catch { }
            foreach (var (target, backup) in replaced)
            {
                try
                {
                    if (backup.Length > 0) File.Move(backup, target, overwrite: true);
                    else File.Delete(target);
                }
                catch { }
            }

            AppLog.Error($"Update failed, nothing was changed: {ex.Message}");
            return false;
        }
        finally
        {
            try { Directory.Delete(UpdateFolder, recursive: true); } catch { }
        }
    }

    private static string Sha256Of(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static bool StartedAfterUpdate(string[] args) => args.Contains(AfterUpdateArgument);

    // Called once the main window is up: confirms the update to the old instance and removes the replaced files
    // (retrying until the old instance has closed and released its .exe).
    public static void ConfirmStarted()
    {
        try
        {
            App.DataFolder.Create();
            File.WriteAllText(StartedMarker, CurrentVersion.ToString());
        }
        catch { }

        Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 60; attempt++)
            {
                var left = LeftoverAssets.Select(name => Path.Combine(AppFolder, name + ".old")).Where(File.Exists).ToList();
                foreach (var file in left)
                    try { File.Delete(file); } catch { }
                if (left.All(f => !File.Exists(f))) break;
                await Task.Delay(1000);
            }

            try { File.Delete(StartedMarker); } catch { }
        });
    }
}
