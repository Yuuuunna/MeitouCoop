using System.Numerics;
using System.Text.Json;
using Meitou.Engine;
using Meitou.Simulation;

namespace Meitou.Game;

sealed partial class GameHost
{
    readonly bool networkAcceptance = Environment.GetEnvironmentVariable("MEITOU_REMOTE_ACCEPTANCE") == "1";
    double acceptanceSeconds, acceptanceNextReport, acceptanceNextMove = 35, acceptanceStillSince, acceptanceFirstMoveAt;
    Vector3? acceptanceLastPosition, acceptanceOrigin;
    CharacterId acceptanceCharacter = CharacterId.None;
    int acceptanceMoves;
    int acceptancePictures;
    string? acceptancePicture;

    // Opt-in driver for a COPY of an acceptance world. Uses the actual input binding, network session,
    // server movement and renderer; it never writes local simulation state or invents presentation events.
    void RunOnlineAcceptance(double seconds, long frames, bool covered)
    {
        if (!networkAcceptance || session is not RemotePlayerSession remote) return;
        if (remote.Connection.State.Disconnection is { } failure)
            throw new IOException("The graphical network acceptance connection failed.", failure);
        if (covered || !remote.CanControl) return;
        acceptanceSeconds += seconds;
        var current = remote.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == acceptanceCharacter);
        if (current is null)
        {
            var role = remote.Connection.State.Admission.State.Roles.FirstOrDefault(r => r.Available &&
                (g.OnlineRole is null || r.Entity.Value == g.OnlineRole.Value));
            if (role is null) return;
            acceptanceCharacter = role.Character;
            current = remote.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == acceptanceCharacter);
            if (current is null) return;
            acceptanceOrigin = current.Position;
            remote.Submit(new SelectCommand([acceptanceCharacter]) { Tick = remote.CommandTick });
        }
        if (acceptanceLastPosition is not { } previous || Vector3.DistanceSquared(previous, current.Position) > .0001f)
            acceptanceStillSince = acceptanceSeconds;
        acceptanceLastPosition = current.Position;
        string? pictureDirectory = Environment.GetEnvironmentVariable("MEITOU_REMOTE_ACCEPTANCE_PICTURES");
        if (pictureDirectory is not null && acceptanceMoves > 0 && acceptancePictures < 12 &&
            acceptanceSeconds >= acceptanceFirstMoveAt + acceptancePictures * .5)
        {
            Directory.CreateDirectory(pictureDirectory);
            acceptancePicture = Path.Combine(Path.GetFullPath(pictureDirectory), $"frame-{acceptancePictures++:D2}.png");
            screenshotRequested = true;
        }
        if (acceptanceSeconds >= acceptanceNextMove && acceptanceSeconds - acceptanceStillSince >= 1 && remote.PendingCommands == 0)
        {
            var origin = acceptanceOrigin!.Value;
            var target = origin + new Vector3((acceptanceMoves % 2 == 0 ? 1 : -1) * 4, 0, 0);
            target.Y = gpu.Terrain.HeightAt(target.X, target.Z);
            remote.Submit(new MoveOrder([acceptanceCharacter], target) { Tick = remote.CommandTick });
            if (acceptanceMoves == 0) acceptanceFirstMoveAt = acceptanceSeconds;
            acceptanceMoves++;
            acceptanceNextMove = acceptanceSeconds + 5;
            acceptanceStillSince = acceptanceSeconds;
        }
        if (acceptanceSeconds < acceptanceNextReport) return;
        acceptanceNextReport = Math.Floor(acceptanceSeconds / 10 + 1) * 10;
        var statistics = remote.Connection.TransportStatistics;
        Console.WriteLine("network-acceptance " + JsonSerializer.Serialize(new
        {
            schema = 1, seconds = acceptanceSeconds, frames, characters = remote.CurrentSnapshot.Characters.Count,
            moves = acceptanceMoves, receivedTick = remote.CommandTick, presentationTick = remote.PresentationTick,
            statistics.SentUdpPayloadBytes, statistics.ReceivedUdpPayloadBytes,
            statistics.SentQuicPackets, statistics.ReceivedQuicPackets, statistics.RttMicroseconds,
            statistics.NativeOutstandingBytes, statistics.QueuedReceiveBytes,
            accounting = "UDP payload counters; QUIC packet counts are not exact UDP/IP wire accounting",
        }));
    }
}
