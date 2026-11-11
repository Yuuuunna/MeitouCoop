using System.Text.Json;
using Meitou.Diagnostics;

namespace Meitou.Networking;

/// <summary>
/// Reads only correlation headers when tracing is enabled; never persists payloads.
/// </summary>
public static class LatencyFrameTrace
{
    public static void Command(WireFrame frame, ReplicationIdentity identity, LatencyStage stage)
    {
        if (LatencyTrace.Sink is not { } sink || frame.Kind != WireMessage.Command)
            return;
        try
        {
            using var json = JsonDocument.Parse(frame.Payload);
            ulong request = json.RootElement.GetProperty("Request").GetUInt64();
            LatencyTrace.Emit(sink, new(stage, identity.World, identity.Player.Value, identity.Generation, request));
        }
        catch (Exception)
        { /* malformed diagnostics cannot change protocol behavior */
        }
    }
}