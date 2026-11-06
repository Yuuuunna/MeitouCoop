using Meitou.Data.World;
using Meitou.Engine;

namespace Meitou.Game;

sealed partial class GameHost
{
    void UpdateFoliageOwnership()
    {
        if (scene.Objects?.Levels is not { } levels) return;
        if (session is RemotePlayerSession)
        {
            levels.SetFoliageOwnership(session.HasWorldView
                ? session.CurrentSnapshot.FoliageOwnedTowns.Select(t => (t.X, t.Z)) : []);
            return;
        }
        if (simulationContent?.Levels is { } authority && !ReferenceEquals(levels, authority))
            levels.SetFoliageOwnership(authority.FoliageOwnership);
    }
}
