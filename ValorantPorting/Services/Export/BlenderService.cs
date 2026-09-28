using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json;
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

    public static void Send(ExportData data, BlenderExportSettings settings)
    {
        var export = new BlenderExport
        {
            Data = data,
            Settings = settings,
            AssetsRoot = App.AssetsFolder.FullName.Replace("\\", "/")
        };

        SendMessage(JsonConvert.SerializeObject(export));
    }

    // Asks the Blender add-on to apply a .psa to the currently selected armature.
    // upperPsaPath: the upper-body half of an upper/lower body pair; Blender merges it with psaPath (the lower body)
    public static void SendAnimation(string name, string psaPath, string? upperPsaPath = null)
    {
        SendMessage(JsonConvert.SerializeObject(new
        {
            AssetsRoot = App.AssetsFolder.FullName.Replace("\\", "/"),
            Data = new
            {
                Name = name, Type = "Animation", AnimationPath = psaPath.Replace("\\", "/"),
                UpperAnimationPath = upperPsaPath?.Replace("\\", "/")
            }
        }));
    }

    // Asks the Blender add-on to import a map exported as USD.
    public static void SendMap(string name, string usdPath, string? materialsPath)
    {
        SendMessage(JsonConvert.SerializeObject(new
        {
            AssetsRoot = App.AssetsFolder.FullName.Replace("\\", "/"),
            Data = new { Name = name, Type = "Map", MapPath = usdPath.Replace("\\", "/"), MaterialsPath = materialsPath?.Replace("\\", "/") }
        }));
    }

    private static void SendMessage(string message)
    {
        var messageBytes = Encoding.ASCII.GetBytes(message);
        SendSpliced(Client, messageBytes, Globals.BUFFER_SIZE);
        Client.Send(Encoding.ASCII.GetBytes("MessageFinished"));
    }
}