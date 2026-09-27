using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using ValorantPorting.AppUtils;

namespace ValorantPorting.Services.Endpoints;

// Keeps the mappings (and AES key) in sync with the latest Valorant version published on uedb.dev,
// so the app keeps working after game patches without manual file swaps.
public static class UedbMappings
{
    private const string GameApiUrl = "https://uedb.dev/api/games/valorant";
    private const string CdnRoot = "https://data.uedb.dev/";
    private const string FallbackAesKey = "0x4BE71AF2459CF83899EC9DC2CB60E22AC4B3047E0211034BBABE9D174C069DD6";

    public static string AesKey { get; private set; } = FallbackAesKey;

    public static string? ResolveMappings(string mappingsDir)
    {
        Directory.CreateDirectory(mappingsDir);

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var game = JObject.Parse(http.GetStringAsync(GameApiUrl).GetAwaiter().GetResult());

            var latest = game["versions"]?.Children<JObject>()
                .Where(v => v["mappings"]?.HasValues == true)
                .OrderBy(v => v.Value<DateTime?>("updated") ?? DateTime.MinValue)
                .LastOrDefault();

            if (latest is not null)
            {
                var key = latest.Value<string>("mainAesKey");
                if (!string.IsNullOrWhiteSpace(key)) AesKey = key;

                var mappings = latest["mappings"]!.Children<JObject>().ToList();
                var mapping = mappings.FirstOrDefault(m => m.Value<string>("compressionType") == "ZStandard") ?? mappings[0];
                var cdnPath = mapping.Value<string>("cdnPath")!;
                var hash = mapping.Value<string>("fileHash");
                var version = latest.Value<string>("versionString");
                var target = Path.Combine(mappingsDir, Path.GetFileName(cdnPath));

                if (File.Exists(target) && HashMatches(File.ReadAllBytes(target), hash))
                {
                    AppLog.Information($"Mappings are up to date ({version}).");
                    return target;
                }

                AppLog.Information($"Downloading mappings for {version} from uedb.dev...");
                var bytes = http.GetByteArrayAsync(CdnRoot + cdnPath).GetAwaiter().GetResult();
                if (HashMatches(bytes, hash))
                {
                    File.WriteAllBytes(target, bytes);
                    AppLog.Information($"Mappings updated to {version}.");
                    return target;
                }

                AppLog.Warning($"Downloaded mappings for {version} failed the checksum check and were discarded.");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning($"Could not check uedb.dev for new mappings ({ex.Message}). Using the newest local mappings instead.");
        }

        var local = new DirectoryInfo(mappingsDir).GetFiles("*.usmap")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
        return local?.FullName;
    }

    private static bool HashMatches(byte[] data, string? expectedSha256) =>
        string.IsNullOrEmpty(expectedSha256) ||
        Convert.ToHexString(SHA256.HashData(data)).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
}
