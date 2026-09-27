using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.MappingsProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.AssetRegistry;
using CUE4Parse.UE4.AssetRegistry.Objects;
using CUE4Parse.UE4.Versions;
using ValorantPorting.AppUtils;
using ValorantPorting.Services.Endpoints;

namespace ValorantPorting.ViewModels;

public class CUE4ParseViewModel : ObservableObject
{
    public static readonly VersionContainer Version = new(EGame.GAME_Valorant);

    private static readonly string MappingsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Mappings");

    public readonly List<FAssetData> AssetDataBuffers = new();
    public readonly ValorantPortingFileProvider Provider;

    public FAssetRegistryState? AssetRegistry;
    
    public CUE4ParseViewModel(string directory, EInstallType installType)
    {
        if (installType is EInstallType.Local && !Directory.Exists(directory))
        {
            AppLog.Warning(
                "Installation Not Found, Valorant installation path does not exist or has not been set. Please go to settings to verify you've set the right path and restart. The program will not work properly on Local Installation mode if you do not set it.");
            return;
        }

        Provider = installType switch
        {
            EInstallType.Local => new ValorantPortingFileProvider(new DirectoryInfo(directory), SearchOption.AllDirectories, true, Version),
            EInstallType.Live => new ValorantPortingFileProvider(true, Version)
        };
    }

    private ApiEndpointViewModel _apiEndpointView => ApiEndpointView;

    public async Task Initialize()
    {
        if (Provider is null) return;

        var mappingsPath = UedbMappings.ResolveMappings(MappingsDir);
        if (mappingsPath is null)
        {
            AppLog.Warning($"No mappings found in \"{MappingsDir}\" and none could be downloaded. Valorant assets will fail to parse without them.");
        }
        else
        {
            AppLog.Information($"Using mappings: {Path.GetFileName(mappingsPath)}");
            Provider.MappingsContainer = new FileUsmapTypeMappingsProvider(mappingsPath);
        }

        var oodlePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, CUE4Parse.Compression.OodleHelper.OodleFileName);
        var legacyOodlePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, CUE4Parse.Compression.OodleHelper.OODLE_NAME_OLD);
        if (!File.Exists(oodlePath) && File.Exists(legacyOodlePath))
            oodlePath = legacyOodlePath; // reuse the Oodle DLL older versions of the app already downloaded
        if (!File.Exists(oodlePath))
        {
            AppLog.Information("Oodle DLL not found locally, downloading a known-good copy from GitHub...");
            using var oodleHttpClient = new System.Net.Http.HttpClient();
            var downloaded = CUE4Parse.Compression.OodleHelper.DownloadOodleDllFromOodleUEAsync(oodleHttpClient, oodlePath).GetAwaiter().GetResult();
            if (!downloaded)
            {
                AppLog.Warning("Automatic Oodle download from GitHub failed.");
            }
        }
        if (File.Exists(oodlePath))
        {
            CUE4Parse.Compression.OodleHelper.Initialize(oodlePath);
        }
        else
        {
            AppLog.Warning($"Oodle DLL could not be found or downloaded to \"{oodlePath}\". Compressed assets will fail to load.");
        }
        await InitializeProvider();
        await InitializeKeys();

        Provider.LoadVirtualPaths();

        Provider.TryCreateReader("ShooterGame/AssetRegistry.bin", out var assetArchive);
        if (assetArchive is not null)
        {
            AssetRegistry = new FAssetRegistryState(assetArchive);
            AssetDataBuffers.AddRange(AssetRegistry.PreallocatedAssetDataBuffers);
        }
        else
        {
            AppLog.Warning("AssetRegistry.bin could not be loaded, so the asset handler will not have registry data to initialize.");
        }
    }

    private async Task InitializeKeys()
    {
        await Provider.SubmitKeyAsync(Globals.ZERO_GUID, new FAesKey(UedbMappings.AesKey));
    }


    private async Task InitializeProvider()
    {
        switch (AppSettings.Current.InstallType)
        {
            case EInstallType.Local:
            {
                Provider.InitializeLocal();
                break;
            }
            case EInstallType.Live:
            {
                var manifestInfo = _apiEndpointView.ValorantApi.GetManifest(CancellationToken.None);
                if (manifestInfo == null)
                    throw new Exception(
                        "Could not load latest Valorant manifest, you may have to switch to your local installation.");
                for (var i = 0; i < manifestInfo.Paks.Length; i++)
                    Provider.Initialize(manifestInfo.Paks[i].GetFullName(), new[] { manifestInfo.GetPakStream(i) });
                break;
            }
        }
    }
}
