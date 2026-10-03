using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json;
using ValorantPorting.AppUtils;
using ValorantPorting.Export;
using ValorantPorting.Export.Blender;
using ValorantPorting.Services.Export;

namespace ValorantPorting.Services;

public class BlenderService : SocketServiceBase
{
    private static readonly UdpClient Client = new();

    static BlenderService()
    {
        Client.Connect("localhost", Globals.BLENDER_PORT);
    }

    public static void Send(ExportData data, BlenderExportSettings settings) => SendMessage(JsonConvert.SerializeObject(ExportMessage(data, settings)));

    public static BlenderExport ExportMessage(ExportData data, BlenderExportSettings settings) => new()
    {
        Data = data,
        Settings = settings,
        AssetsRoot = App.AssetsFolder.FullName.Replace("\\", "/")
    };

    // A scene: several imports/animations in one message, done in order by the add-on (agent, gun in its hand, the
    // animations on each). One message, so none of them can be lost while Blender is busy with the first.
    public static void SendScene(string name, IReadOnlyList<object> steps)
    {
        SendMessage(JsonConvert.SerializeObject(new
        {
            AssetsRoot = App.AssetsFolder.FullName.Replace("\\", "/"),
            Data = new { Name = name, Type = "Scene", Steps = steps }
        }));
    }

    // Asks the Blender add-on to apply a .psa to the currently selected armature.
    // upperPsaPath: the upper-body half of an upper/lower body pair; Blender merges it with psaPath (the lower body)
    // repeat: how many times looping animations (runs, idles) play in a row; lowerLoops/upperLoops: which ones loop
    // sequencePaths: all clips of a montage that plays several in a row (psaPath is the first), joined in Blender
    // sounds: the game's sounds for it, each placed at its moment (the add-on adds them to the Video Sequencer)
    public static void SendAnimation(string name, string psaPath, string? upperPsaPath = null, int repeat = 1,
        bool lowerLoops = false, bool upperLoops = false, IReadOnlyList<string>? sequencePaths = null,
        IReadOnlyList<AnimationSounds.Placed>? sounds = null) =>
        SendMessage(JsonConvert.SerializeObject(AnimationMessage(name, psaPath, upperPsaPath, repeat, lowerLoops, upperLoops, sequencePaths, sounds: sounds)));

    // sceneTarget: in a scene, which armature it goes on ("agent:TP", "agent:FP", "agent:CS" or "gun")
    public static object AnimationMessage(string name, string psaPath, string? upperPsaPath = null, int repeat = 1,
        bool lowerLoops = false, bool upperLoops = false, IReadOnlyList<string>? sequencePaths = null, string? sceneTarget = null,
        IReadOnlyList<AnimationSounds.Placed>? sounds = null) => new
    {
        AssetsRoot = App.AssetsFolder.FullName.Replace("\\", "/"),
        SceneTarget = sceneTarget,
        Data = new
        {
            Name = name, Type = "Animation", AnimationPath = psaPath.Replace("\\", "/"),
            UpperAnimationPath = upperPsaPath?.Replace("\\", "/"),
            Repeat = repeat, LowerLoops = lowerLoops, UpperLoops = upperLoops,
            SequencePaths = sequencePaths?.Select(p => p.Replace("\\", "/")).ToList(),
            Sounds = sounds?.Select(s => new { Path = s.Path.Replace("\\", "/"), s.Time, s.Name }).ToList()
        }
    };

    // Asks the Blender add-on to import a map exported as USD.
    public static void SendMap(string name, string usdPath, string? materialsPath)
    {
        SendMessage(JsonConvert.SerializeObject(new
        {
            AssetsRoot = App.AssetsFolder.FullName.Replace("\\", "/"),
            Data = new { Name = name, Type = "Map", MapPath = usdPath.Replace("\\", "/"), MaterialsPath = materialsPath?.Replace("\\", "/") }
        }));
    }

    // a sound from the Sounds tab, added at Blender's current frame
    public static void SendSound(string name, IReadOnlyList<string> wavPaths)
    {
        SendMessage(JsonConvert.SerializeObject(new
        {
            AssetsRoot = App.AssetsFolder.FullName.Replace("\\", "/"),
            Data = new { Name = name, Type = "Sound", Paths = wavPaths.Select(p => p.Replace("\\", "/")).ToList() }
        }));
    }

    private static void SendMessage(string message)
    {
        if (DateTime.Now - BlenderSelectionListener.LastHeard > TimeSpan.FromSeconds(10))
            AppLog.Warning("No sign of the Blender add-on: is Blender open with the Valorant Porting add-on enabled? " +
                           "If you just updated the app, install the add-on from the \"Blender Add-ons\" folder again and restart Blender.");

        var messageBytes = Encoding.ASCII.GetBytes(message);
        SendSpliced(Client, messageBytes, Globals.BUFFER_SIZE);
        Client.Send(Encoding.ASCII.GetBytes("MessageFinished"));
    }
}