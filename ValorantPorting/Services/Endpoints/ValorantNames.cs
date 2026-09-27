using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using ValorantPorting.AppUtils;
using ValorantPorting.Export;

namespace ValorantPorting.Services.Endpoints;

// Real names for Valorant's internal codenames (Wushu -> Jett, AK -> Vandal, Afterglow -> RGX 11z Pro, ability slots),
// used to make animation names readable. Comes from valorant-api.com (so new agents/skins are picked up automatically)
// with a local copy for offline use. Everything still works with raw codenames if neither is available.
public static class ValorantNames
{
    private const string AgentsUrl = "https://valorant-api.com/v1/agents?isPlayableCharacter=true";
    private const string WeaponsUrl = "https://valorant-api.com/v1/weapons";

    public static IReadOnlyDictionary<string, AnimationNamer.Agent> Agents { get; private set; } = new Dictionary<string, AnimationNamer.Agent>();
    public static IReadOnlyDictionary<string, string> Guns { get; private set; } = new Dictionary<string, string>();
    public static IReadOnlyDictionary<string, string> Skins { get; private set; } = new Dictionary<string, string>();

    private static readonly ManualResetEventSlim Ready = new(false);

    public static void StartLoading() => Task.Run(Load);

    public static void WaitUntilLoaded(TimeSpan timeout) => Ready.Wait(timeout);

    private static async Task Load()
    {
        var cacheFile = Path.Combine(App.DataFolder.FullName, "valorant-names.json");
        try
        {
            JObject? data = null;
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                var agents = JObject.Parse(await http.GetStringAsync(AgentsUrl));
                var weapons = JObject.Parse(await http.GetStringAsync(WeaponsUrl));
                data = new JObject { ["agents"] = agents["data"], ["weapons"] = weapons["data"] };
                await File.WriteAllTextAsync(cacheFile, data.ToString(Newtonsoft.Json.Formatting.None));
            }
            catch (Exception ex)
            {
                if (File.Exists(cacheFile))
                    data = JObject.Parse(await File.ReadAllTextAsync(cacheFile));
                else
                    AppLog.Warning($"Could not load agent/weapon names ({ex.Message}); animations will show internal codenames.");
            }

            if (data is not null) Parse(data);
        }
        catch (Exception ex)
        {
            AppLog.Warning($"Could not read agent/weapon names: {ex.Message}");
        }
        finally
        {
            Ready.Set();
        }
    }

    private static void Parse(JObject data)
    {
        var slotKeys = new Dictionary<string, string> { ["Ability1"] = "Q", ["Ability2"] = "E", ["Grenade"] = "C", ["Ultimate"] = "X" };

        var agents = new Dictionary<string, AnimationNamer.Agent>(StringComparer.OrdinalIgnoreCase);
        foreach (var agent in data["agents"]?.Children<JObject>() ?? [])
        {
            var codename = agent.Value<string>("developerName");
            var name = agent.Value<string>("displayName");
            if (string.IsNullOrEmpty(codename) || string.IsNullOrEmpty(name)) continue;

            var abilities = new Dictionary<string, string>();
            foreach (var ability in agent["abilities"]?.Children<JObject>() ?? [])
            {
                if (slotKeys.TryGetValue(ability.Value<string>("slot") ?? "", out var key))
                    abilities[key] = ability.Value<string>("displayName") ?? key;
            }

            agents[codename] = new AnimationNamer.Agent(name, abilities); // abilities keyed Q / E / C / X
        }

        var guns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var skins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var weapon in data["weapons"]?.Children<JObject>() ?? [])
        {
            var gunName = weapon.Value<string>("displayName") ?? "";
            var gunFolder = FolderOf(weapon.Value<string>("assetPath"));
            if (gunFolder is not null) guns[gunFolder] = gunName;

            foreach (var skin in weapon["skins"]?.Children<JObject>() ?? [])
            {
                var skinFolder = FolderOf(skin.Value<string>("assetPath"));
                var skinName = skin.Value<string>("displayName") ?? "";
                if (skinFolder is null || skinFolder.Equals(gunFolder, StringComparison.OrdinalIgnoreCase) ||
                    skinFolder.Equals("Standard", StringComparison.OrdinalIgnoreCase) || skinName.StartsWith("Random"))
                    continue;

                // "RGX 11z Pro Vandal" -> "RGX 11z Pro" (the skin line), melee names stay as they are
                if (skinName.EndsWith(" " + gunName, StringComparison.OrdinalIgnoreCase))
                    skinName = skinName[..^(gunName.Length + 1)];
                skins.TryAdd(skinFolder, skinName);
            }
        }

        Agents = agents;
        Guns = guns;
        Skins = skins;
    }

    // ".../Rifles/AK/AKPrimaryAsset" -> "AK"
    private static string? FolderOf(string? assetPath)
    {
        if (string.IsNullOrEmpty(assetPath)) return null;
        var parts = assetPath.Split('/');
        return parts.Length >= 2 ? parts[^2] : null;
    }
}
