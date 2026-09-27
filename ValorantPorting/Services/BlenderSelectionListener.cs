using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using ValorantPorting.AppUtils;

namespace ValorantPorting.Services;

// Receives "VP_SELECT|<tag>" messages from the Blender add-on whenever a Valorant armature is selected,
// so the Animations tab can follow the Blender selection. Local machine only.
public static class BlenderSelectionListener
{
    private const int Port = 24284;
    private static UdpClient? client;

    public static void Start(Action<string> onSelection)
    {
        if (client is not null) return;
        try
        {
            client = new UdpClient(new IPEndPoint(IPAddress.Loopback, Port));
        }
        catch (SocketException ex)
        {
            AppLog.Warning($"Could not listen for the Blender selection on port {Port} ({ex.Message}). The animation filter will not follow Blender.");
            return;
        }

        Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    var result = await client.ReceiveAsync();
                    var message = Encoding.UTF8.GetString(result.Buffer);
                    if (!message.StartsWith("VP_SELECT|")) continue;
                    var tag = message["VP_SELECT|".Length..];
                    Application.Current.Dispatcher.Invoke(() => onSelection(tag));
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception)
                {
                    // ignore malformed packets and keep listening
                }
            }
        });
    }
}
