using Meitou.Data.Gameplay;
using Meitou.Data.World;
using Meitou.Engine;
using Meitou.Networking;
using Meitou.Rendering;
using Meitou.Rendering.Display;
using Meitou.Simulation;

namespace Meitou.Game;

sealed partial class GameHost
{
    bool onlineDisconnectShown;

    void ReportOnlineDisconnection()
    {
        if (onlineDisconnectShown || session is not RemotePlayerSession remote || remote.Connection.State.Disconnection is null) return;
        onlineDisconnectShown = true;
        warming = false;
        skyArrival = null; presentationHour = null;
        if (gpu.Characters is { } characters) characters.LoadingFocus = null;
        loading?.Finish();
        player.Notice("Connection lost. Return to the main menu and reconnect. See log.txt for details.", true);
    }

    void BootOnline(VulkanDisplay display, bool interactive, RemoteGameSession connection)
    {
        LoadingStep("online scene");
        gpu = WorldFrame.CreateGpu(display.Context, install, scene, assets, o, interactive, LoadingStep, AwaitLoading);
        if (gpu.Post is { } post) post.UpscalerFactory = Meitou.Rendering.Upscalers.VendorUpscalers.Factory(display.Context, streamline, ngx);
        (camera, render) = WorldFrame.Setup(scene, o);
        var db = scene.Database ?? throw new InvalidDataException("Online presentation requires compatible game content.");
        var levels = scene.Objects?.Levels ?? WorldLevelData.Load(install, includeInteriors: true);
        // Static tooltip/editor/map content only. No World, navigation owner or simulation systems exist on this client.
        popData = PopulationData.Create(db, levels.Towns(), new GeneratedAppearances(db, install.Root));
        var constants = GameConstants.FromDatabase(db);
        session = new RemotePlayerSession(connection, gpu.Terrain.HeightAt, clock: GameClockFor(constants, o.Hour, o.Day ?? 0), tickRate: config.TickRate);
        session.Advance(0);
        if (!session.HasWorldView) throw new IOException("The server withdrew the world while scene content loaded. Reconnect to prepare entry again.");
        BootPlayer(display, interactive);
    }
}
