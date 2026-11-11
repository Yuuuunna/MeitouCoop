using Meitou.Data.Gameplay;
using Meitou.Data.World;
using Meitou.Game.Rendering.Display;
using Meitou.Networking;
using Meitou.Simulation;
using Meitou.Simulation.Population;

namespace Meitou.Game.Host;

/// <summary>
/// Online admission and presentation lifecycle; remote play never creates a local world.
/// </summary>
sealed class OnlineGame(GameHost host)
{
    /// <summary>
    /// A connection-loss notice was already shown.
    /// </summary>
    bool _disconnectShown;

    /// <summary>
    /// Ends the loading cover and reports a lost connection once.
    /// </summary>
    internal void ReportOnlineDisconnection()
    {
        if (_disconnectShown || host.Session is not RemotePlayerSession remote || remote.Connection.State.Disconnection is null) return;
        _disconnectShown = true;
        host.Warming = false;
        host.SkyArrival = null;
        host.PresentationHour = null;
        if (host.Gpu.Characters is { } characters) characters.LoadingFocus = null;
        host.Loading?.Finish();
        host.Player.Notice("Connection lost. Return to the main menu and reconnect. See log.txt for details.", true);
    }

    /// <summary>
    /// Loads compatible presentation content and a remote session without a local world.
    /// </summary>
    internal void BootOnline(VulkanDisplay display, bool interactive, RemoteGameSession connection)
    {
        host.CreateRenderer(display, interactive);
        var db = host.Scene.Database ?? throw new InvalidDataException("Online presentation requires compatible game content.");
        var levels = host.Scene.Objects?.Levels ?? WorldLevelData.Load(host.Install, includeInteriors: true);
        // Static tooltip/editor/map content only. No World, navigation owner or simulation systems exist on this client.
        var layouts = host.Scene.Objects?.Layouts ?? new BuildingLayouts(db, levels.Interiors);
        levels.ConfigurePreparedLayouts(layouts);
        var buildings = TownBuildings.Build(db, levels, layouts, (x, z) => host.Gpu.Terrain.HeightAt((float)x, (float)z));
        host.PopData = PopulationData.Create(db, levels.Towns(), new GeneratedAppearances(db, host.Install.Root), buildings: buildings);
        host.AnimationLibrary = AnimationLibrary.FromDatabase(db);
        var constants = GameConstants.FromDatabase(db);
        var clock = GameHost.GameClockFor(constants, host.WorldOptions.Hour, host.WorldOptions.Day ?? 0);
        host.Session = new RemotePlayerSession(connection, host.Gpu.Terrain.HeightAt, clock: clock, tickRate: host.Config.TickRate);
        host.Modding.StartMods(db, host.PopData);
        host.Modding.StartDeferredWarmup();
        host.Session.Advance(0);
        if (!host.Session.HasWorldView) throw new IOException("The server withdrew the world while scene content loaded. Reconnect to prepare entry again.");
        host.BootPlayer(display, interactive);
    }

    /// <summary>
    /// Copies faction ownership into the scene's foliage view without mutating authority.
    /// </summary>
    internal void UpdateFoliageOwnership()
    {
        if (host.Scene.Objects?.Levels is not { } levels) return;
        if (host.Session.IsRemote)
        {
            levels.SetFoliageOwnership(host.Session.HasWorldView
                ? host.Session.CurrentSnapshot.FoliageOwnedTowns.Select(t => (t.X, t.Z)) : []);
            return;
        }
        if (host.SimulationContent?.Levels is { } authority && !ReferenceEquals(levels, authority))
            levels.SetFoliageOwnership(authority.FoliageOwnership);
    }
}
