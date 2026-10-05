using System;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CUE4Parse.UE4.Versions;
using Newtonsoft.Json;
using ValorantPorting.Services.Endpoints.Models;

namespace ValorantPorting.AppUtils;

public partial class AppSettings : ObservableObject
{
    public static AppSettings Current;

    public static readonly DirectoryInfo DirectoryPath =
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ValorantPorting"));

    public static readonly DirectoryInfo FilePath = new(Path.Combine(DirectoryPath.FullName, "AppSettings.json"));

    [ObservableProperty] private AesResponse aesResponse;

    [ObservableProperty] private string archivePath;

    [ObservableProperty] private ERichPresenceAccess discordRPC;

    [ObservableProperty] private EInstallType installType;

    [ObservableProperty] private ELanguage language;

    [ObservableProperty] private bool firstPersonCamera; // add a camera on the 1st person arms' "Camera" bone

    [ObservableProperty] private bool skipAnimationSounds; // don't send the game's sounds with animations (on by default)

    [ObservableProperty] private bool skipAnimationVoiceLines; // nor the voice lines said with them (on by default)

    [ObservableProperty] private bool layerAnimations; // a new animation goes on top of the model's current one (only the bones it moves)

    public static void Load()
    {
        if (File.Exists(FilePath.FullName))
            Current = JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(FilePath.FullName));

        Current ??= new AppSettings();
    }

    public static void Save()
    {
        File.WriteAllText(FilePath.FullName, JsonConvert.SerializeObject(Current, Formatting.Indented));
    }
}