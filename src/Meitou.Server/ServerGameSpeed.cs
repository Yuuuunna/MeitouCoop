using System.Globalization;

namespace Meitou.Server;

/// <summary>
/// Keeps the operator's selected game speed while the empty-server policy temporarily pauses the runtime.
/// </summary>
sealed class ServerGameSpeed
{
    /// <summary>
    /// Console syntax for the original game's pause and three speed buttons (docs/game/game-loop.md, Speed and pause).
    /// </summary>
    public const string Usage = "speed [0|1|2|5] (0 pauses; no value shows the current speed; aliases: 倍速, 游戏倍速)";

    /// <summary>
    /// Serializes speed changes and player activity checks with simulation work.
    /// </summary>
    readonly ServerWorldLoop _loop;

    /// <summary>
    /// Whether the absence of Active players temporarily overrides the selected speed.
    /// </summary>
    readonly bool _pauseWhenEmpty;

    /// <summary>
    /// The operator's selection, accessed only on the world owner and reset to normal speed on process startup.
    /// </summary>
    double _requestedSpeed = 1;

    /// <summary>
    /// Creates speed controls for one dedicated world owner.
    /// </summary>
    public ServerGameSpeed(ServerWorldLoop loop, bool pauseWhenEmpty)
    {
        _loop = loop;
        _pauseWhenEmpty = pauseWhenEmpty;
    }

    /// <summary>
    /// Reconciles the selected speed with the current player activity without forgetting an operator pause or fast speed.
    /// </summary>
    public Task UpdateAsync(CancellationToken stopping) => _loop.InvokeAsync(Apply, stopping);

    /// <summary>
    /// Queries or selects one original speed; invalid input leaves the selection unchanged.
    /// </summary>
    public Task<string> ExecuteAsync(string? argument, CancellationToken stopping)
    {
        double? requested = null;
        if (argument is not null)
        {
            if (!double.TryParse(argument, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                || value is not (0 or 1 or 2 or 5))
                return Task.FromResult(Usage);

            requested = value;
        }

        return _loop.InvokeAsync(() => Select(requested), stopping);
    }

    /// <summary>
    /// Applies a selection and reports both the operator's choice and the effective runtime speed on the world owner.
    /// </summary>
    string Select(double? requested)
    {
        if (requested is { } value) _requestedSpeed = value;

        Apply();
        string reason = _requestedSpeed > 0 && _loop.Runtime.Speed == 0 ? " (paused: no active players)" : "";
        return FormattableString.Invariant($"Game speed: {_requestedSpeed}x; effective: {_loop.Runtime.Speed}x{reason}.");
    }

    /// <summary>
    /// Runs only on the world owner; automatic resume restores the selection rather than forcing normal speed.
    /// </summary>
    void Apply()
    {
        var world = _loop.Runtime.World;
        bool empty = _pauseWhenEmpty && !world.Players.All.Any(player => world.Authority.IsActive(player.Id));
        _loop.Runtime.Speed = empty ? 0 : _requestedSpeed;
    }
}
