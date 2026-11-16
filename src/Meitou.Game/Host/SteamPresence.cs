using System.Diagnostics;
using System.Numerics;
using Meitou.Networking;

namespace Meitou.Game.Host;

/// <summary>
/// Publishes the current world's Steam group and localized View Game Info status. No subtitle template or join command is advertised.
/// </summary>
sealed class SteamPresence(SteamFriends friends)
{
    /// <summary>
    /// Last status poll in monotonic time; publication is checked at most once a second.
    /// </summary>
    long _polled;

    /// <summary>
    /// The last values Steam accepted, null before publication or after reset.
    /// </summary>
    string? _status, _group;

    /// <summary>
    /// Whether the current failed publication has already been logged.
    /// </summary>
    bool _failureLogged;

    /// <summary>
    /// Pumps callbacks each frame and publishes only changed status/group values once per real-time second.
    /// </summary>
    internal void Update(GameHost host)
    {
        friends.Pump();
        long now = Stopwatch.GetTimestamp();
        if (_polled != 0 && Stopwatch.GetElapsedTime(_polled, now).TotalSeconds < 1) return;
        _polled = now;

        var (status, group) = Describe(host);
        bool accepted = Publish("steam_player_group", group, ref _group);
        accepted &= Publish("status", status, ref _status);
        if (!accepted && !_failureLogged) Console.WriteLine("Steamworks: rich presence update refused; retrying.");
        _failureLogged = !accepted;
    }

    /// <summary>
    /// Removes the previous world's group and location immediately, including during transitions or shutdown.
    /// </summary>
    internal void Reset()
    {
        friends.Clear();
        _status = null;
        _group = null;
        _polled = 0;
        _failureLogged = false;
    }

    /// <summary>
    /// Retains a value only when Steam accepts it, so a transient refusal is retried on the next poll.
    /// </summary>
    bool Publish(string key, string value, ref string? previous)
    {
        if (value == previous) return true;
        if (!friends.Set(key, value)) return false;

        previous = value;
        Console.WriteLine($"Steamworks: {key} = {value}");
        return true;
    }

    /// <summary>
    /// Groups only admitted, usable online sessions by server world incarnation, shared across players and address aliases.
    /// </summary>
    static (string Status, string Group) Describe(GameHost host)
    {
        if (host.TitleScreen.TitleVisible) return (Loc.Tr("Meitou Coop | Main menu"), "");
        if (host.OnlineConnection?.State is { } remote) return DescribeOnline(host, remote);
        if (host.Covered || !host.Session.HasWorldView) return (Loc.Tr("Meitou Coop | Loading"), "");

        return (WithLocation(Loc.Tr("Meitou Coop | Single player"), host), "");
    }

    /// <summary>
    /// Withdraws the group when disconnected or no longer admitted; no player count is inferred from characters or visible roles.
    /// </summary>
    static (string Status, string Group) DescribeOnline(GameHost host, RemoteSessionState remote)
    {
        if (remote.Disconnection is not null) return (Loc.Tr("Meitou Coop | Disconnected"), "");
        if (!remote.CanControl || !host.Session.HasWorldView) return (Loc.Tr("Meitou Coop | Synchronizing"), "");

        string status = host.Covered ? Loc.Tr("Meitou Coop | Loading") : WithLocation(Loc.Tr("Meitou Coop | Multiplayer"), host);
        return (status, "meitou:" + remote.Admission.Identity.World.ToString("N"));
    }

    /// <summary>
    /// Uses the selected character's position, falling back to the camera when no character is selected.
    /// </summary>
    static string WithLocation(string status, GameHost host)
    {
        var at = host.Player.SelectedPosition ?? host.Session.Camera.Current.Target;
        string? town = TownAt(host, at);
        string? region = host.Player.BiomeBanner.RegionAt?.Invoke(at.X, at.Z) is { } here ? here.Name : null;
        if (region == "NONE") region = null;

        string? location = town;
        if (!string.IsNullOrWhiteSpace(region)) location = location is null ? region : location + " / " + region;
        return string.IsNullOrWhiteSpace(location) ? status : status + " | " + location;
    }

    /// <summary>
    /// Finds the closest enclosing outpost or placed town, using the same horizontal radius as the world's town data.
    /// </summary>
    static string? TownAt(GameHost host, Vector3 at)
    {
        string? name = null;
        float nearest = float.PositiveInfinity;
        foreach (var town in host.Session.CurrentSnapshot.Outposts)
            Consider(town.Name, town.Position, town.Radius, at, ref name, ref nearest);

        foreach (var site in host.PopData?.Sites ?? [])
            Consider(site.Town.Name, site.Position, site.Town.Radius, at, ref name, ref nearest);

        return name;
    }

    /// <summary>
    /// Keeps only named towns containing the point; terrain height does not affect membership.
    /// </summary>
    static void Consider(string candidate, Vector3 centre, float radius, Vector3 at, ref string? name, ref float nearest)
    {
        if (string.IsNullOrWhiteSpace(candidate) || radius <= 0) return;
        float dx = centre.X - at.X, dz = centre.Z - at.Z;
        float distance = dx * dx + dz * dz;
        if (distance > radius * radius || distance >= nearest) return;

        name = candidate;
        nearest = distance;
    }
}
