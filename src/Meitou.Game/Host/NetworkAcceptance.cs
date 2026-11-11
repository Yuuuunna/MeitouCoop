using System.Numerics;
using System.Text.Json;
using Meitou.Simulation;

namespace Meitou.Game.Host;

/// <summary>
/// The opt-in graphical network acceptance driver for a disposable copy of a server world.
/// </summary>
sealed class NetworkAcceptance(GameHost host)
{
    /// <summary>
    /// The environment explicitly enabled this driver.
    /// </summary>
    readonly bool _networkAcceptance = Environment.GetEnvironmentVariable("MEITOU_REMOTE_ACCEPTANCE") == "1";
    /// <summary>
    /// The run, report, move, arrival and initial movement times.
    /// </summary>
    double _acceptanceSeconds, _acceptanceNextReport, _acceptanceNextMove = 35, _acceptanceStillSince, _acceptanceFirstMoveAt;
    /// <summary>
    /// The last shown position and the movement origin.
    /// </summary>
    Vector3? _acceptanceLastPosition, _acceptanceOrigin;
    /// <summary>
    /// The admitted character driven by this run.
    /// </summary>
    CharacterId _acceptanceCharacter = CharacterId.None;
    /// <summary>
    /// Movement commands submitted.
    /// </summary>
    int _acceptanceMoves;
    /// <summary>
    /// Pictures requested.
    /// </summary>
    int _acceptancePictures;
    /// <summary>
    /// The next screenshot output, consumed by the frame loop.
    /// </summary>
    internal string? AcceptancePicture;

    /// <summary>
    /// Drives only the admitted remote character, with real commands and renderer output.
    /// </summary>
    internal void Frame(double seconds, long frames, bool covered)
    {
        if (!_networkAcceptance || host.Session is not RemotePlayerSession remote) return;
        if (remote.Connection.State.Disconnection is { } failure)
            throw new IOException("The graphical network acceptance connection failed.", failure);
        if (covered || !remote.CanControl) return;
        _acceptanceSeconds += seconds;
        if (EntryCharacter(remote) is not { } current) return;
        if (_acceptanceLastPosition is not { } previous || Vector3.DistanceSquared(previous, current.Position) > .0001f)
            _acceptanceStillSince = _acceptanceSeconds;
        _acceptanceLastPosition = current.Position;
        CaptureFrame();
        MoveWhenReady(remote);
        Report(remote, frames);
    }

    /// <summary>
    /// Selects the requested admitted role and records the movement origin once it is visible.
    /// </summary>
    CharacterSnapshot? EntryCharacter(RemotePlayerSession remote)
    {
        var current = remote.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == _acceptanceCharacter);
        if (current is not null) return current;
        var role = remote.Connection.State.Admission.State.Roles.FirstOrDefault(r => r.Available
            && (host.GameOptions.OnlineRole is null || r.Entity.Value == host.GameOptions.OnlineRole.Value));
        if (role is null) return null;
        _acceptanceCharacter = role.Character;
        current = remote.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == _acceptanceCharacter);
        if (current is null) return null;
        _acceptanceOrigin = current.Position;
        remote.Submit(new SelectCommand([_acceptanceCharacter]) { Tick = remote.CommandTick });
        return current;
    }

    /// <summary>
    /// Requests successive pictures after the first move when an output directory was supplied.
    /// </summary>
    void CaptureFrame()
    {
        string? directory = Environment.GetEnvironmentVariable("MEITOU_REMOTE_ACCEPTANCE_PICTURES");
        if (directory is null || _acceptanceMoves == 0 || _acceptancePictures >= 12
            || _acceptanceSeconds < _acceptanceFirstMoveAt + _acceptancePictures * .5) return;
        Directory.CreateDirectory(directory);
        AcceptancePicture = Path.Combine(Path.GetFullPath(directory), $"frame-{_acceptancePictures++:D2}.png");
        host.ScreenshotRequested = true;
    }

    /// <summary>
    /// Alternates a short walk after each authoritative arrival, with no command still pending.
    /// </summary>
    void MoveWhenReady(RemotePlayerSession remote)
    {
        if (_acceptanceSeconds < _acceptanceNextMove || _acceptanceSeconds - _acceptanceStillSince < 1 || remote.PendingCommands != 0) return;
        var target = _acceptanceOrigin!.Value + new Vector3((_acceptanceMoves % 2 == 0 ? 1 : -1) * 4, 0, 0);
        target.Y = host.Gpu.Terrain.HeightAt(target.X, target.Z);
        remote.Submit(new MoveOrder([_acceptanceCharacter], target) { Tick = remote.CommandTick });
        if (_acceptanceMoves == 0) _acceptanceFirstMoveAt = _acceptanceSeconds;
        _acceptanceMoves++;
        _acceptanceNextMove = _acceptanceSeconds + 5;
        _acceptanceStillSince = _acceptanceSeconds;
    }

    /// <summary>
    /// Logs transport accounting and received versus presented ticks every ten seconds.
    /// </summary>
    void Report(RemotePlayerSession remote, long frames)
    {
        if (_acceptanceSeconds < _acceptanceNextReport) return;
        _acceptanceNextReport = Math.Floor(_acceptanceSeconds / 10 + 1) * 10;
        var statistics = remote.Connection.TransportStatistics;
        Console.WriteLine("network-acceptance " + JsonSerializer.Serialize(new
        {
            schema = 1, seconds = _acceptanceSeconds, frames, characters = remote.CurrentSnapshot.Characters.Count,
            moves = _acceptanceMoves, receivedTick = remote.CommandTick, presentationTick = remote.PresentationTick,
            statistics.SentUdpPayloadBytes, statistics.ReceivedUdpPayloadBytes,
            statistics.SentQuicPackets, statistics.ReceivedQuicPackets, statistics.RttMicroseconds,
            statistics.NativeOutstandingBytes, statistics.QueuedReceiveBytes,
            accounting = "UDP payload counters; QUIC packet counts are not exact UDP/IP wire accounting",
        }));
    }
}
