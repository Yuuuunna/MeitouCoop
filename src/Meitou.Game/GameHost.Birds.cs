using System.Numerics;
namespace Meitou.Game;
sealed partial class GameHost
{
    Meitou.Game.BirdFlocks? birds;
    Meitou.Data.Gameplay.BiomeMap? birdAreas;
    double birdClock = double.NaN;

    /// <summary>The birds fly on the display's real time (the original's <c>BirdManager</c> thread is kicked with the real dt, docs/game/birds.md); the flocks are those the camera's BIOME_GROUP lists.</summary>
    void UpdateBirds()
    {
        if (scene.Database is not { } db) { gpu.BirdPieces.Clear(); return; }
        if (birds is null)
        {
            var kinds = Meitou.Data.Gameplay.BirdFlockData.LoadAll(db);
            try { birdAreas = Meitou.Data.Gameplay.BiomeMap.Create(Meitou.Data.Gameplay.BiomeGroupData.LoadAll(db), Meitou.Data.World.WeatherAreas.Load(install)); }
            catch (Exception e) when (e is IOException or FormatException) { birdAreas = null; }
            birds = new Meitou.Game.BirdFlocks(kinds, birdHeights.HeightAt, GroupIdOfCell);
            birdStep = new Meitou.Game.BirdThread(() => birds.Update(birdDt, birdEye, birdFocus, birdPlanes));

        }
        // The step runs beside the frame: its flocks are shown one frame late (the original's thread is as loose), and a step still running when the next frame starts is left to finish.
        bool async = o.Screenshot is null && !IsDuel;
        if (birdStep!.Busy) return;
        if (birdStarted && async)
        {
            gpu.BirdPieces.Clear();
            birds.AddPieces(gpu.BirdPieces, mesh => gpu.Debris is { } birdMeshes && birdMeshes.Extent(mesh, null) is not null);   // a mesh is loaded when first asked for
        }
        UpdateBirdAttractors(db);
        double now = realTime.Elapsed.TotalSeconds;
        birdDt = double.IsNaN(birdClock) ? 0 : (float)(now - birdClock);
        birdClock = now;
        birdEye = camera.Eye;
        birdFocus = camera.Target;
        birdPlanes = BirdFrustum();
        birdHeights.Refresh(gpu.Terrain);
        birdStarted = true;
        if (async) birdStep.Start();
        else
        {
            birds.Update(birdDt, birdEye, birdFocus, birdPlanes);
            gpu.BirdPieces.Clear();
            birds.AddPieces(gpu.BirdPieces, mesh => gpu.Debris is { } birdMeshes && birdMeshes.Extent(mesh, null) is not null);
        }
    }

    readonly Meitou.Rendering.TerrainHeights birdHeights = new();
    Meitou.Game.BirdThread? birdStep;
    bool birdStarted;
    float birdDt;
    Vector3 birdEye, birdFocus;
    Vector4[]? birdPlanes;

    long birdBuildingsRevision = -1;
    object? birdAttractorsFor;
    Task<List<(string Key, Meitou.Data.GameRecord Record, Vector3 Position)>>? birdLevelSources;
    readonly List<Meitou.Game.BirdAttractor> birdBuildingAttractors = [], birdLevelAttractors = [];

    /// <summary>
    /// The <c>bird attractor</c> references of the objects the original registers them for (<c>FUN_140263700</c>, on a building's, map feature's or animal's creation):
    /// the player's standing buildings, the level's buildings and map features (read once off the main thread) and every animal, which carries its own as it moves.
    /// </summary>
    void UpdateBirdAttractors(Meitou.Data.GameDatabase db)
    {
        var snapshot = session.CurrentSnapshot;
        bool changed = false;
        if (!ReferenceEquals(birdAttractorsFor, session.WorldIdentity))
        {
            birdAttractorsFor = session.WorldIdentity;
            birdLevelAttractors.Clear();
            birdLevelSources = scene.Objects is { } objects ? Task.Run(objects.BirdAttractorSources) : null;
            birdBuildingsRevision = -1;
            changed = true;
        }
        if (birdLevelSources is { IsCompletedSuccessfully: true } read)
        {
            foreach (var (key, record, at) in read.Result) AddBirdAttractors(birdLevelAttractors, key, record, at);
            birdLevelSources = null;
            changed = true;
        }
        if (snapshot.BuildingsRevision != birdBuildingsRevision)
        {
            birdBuildingsRevision = snapshot.BuildingsRevision;
            birdBuildingAttractors.Clear();
            foreach (var building in snapshot.Buildings)
                if (building.Complete && db.Find(building.Record) is { } record) AddBirdAttractors(birdBuildingAttractors, building.Id, record, building.Position);
            changed = true;
        }
        var animals = new List<Meitou.Game.BirdAttractor>();
        foreach (var c in snapshot.Characters)
            if (c.Appearance?.Animal is { } animal) AddBirdAttractors(animals, c.Id, animal, c.Position);
        if (changed || animals.Count > 0 || birdHadAnimals) birds!.SetAttractors([.. birdBuildingAttractors, .. birdLevelAttractors, .. animals]);
        birdHadAnimals = animals.Count > 0;
    }

    bool birdHadAnimals;

    void AddBirdAttractors(List<Meitou.Game.BirdAttractor> into, object owner, Meitou.Data.GameRecord record, Vector3 at)
    {
        foreach (var link in record.GetReferences("bird attractor"))
            into.Add(new Meitou.Game.BirdAttractor((owner, link.TargetStringId), link.TargetStringId, at, link.Values.Value0, link.Values.Value1));
    }

    string? GroupIdOfCell(int cx, int cz) => birdAreas is { } areas && areas.GroupOf(cx, cz) is >= 0 and var g ? areas.Groups[g].Id : null;

    /// <summary>The view's six planes, normals inwards and normalised (Ogre's cached frustum planes, as the birds' distance test takes them).</summary>
    Vector4[] BirdFrustum()
    {
        var planes = Meitou.Rendering.WorldCamera.FrustumPlanes(camera.View * camera.Projection(birdAspect, camera.Near, camera.ViewDistance));
        for (int i = 0; i < planes.Length; i++) planes[i] /= new Vector3(planes[i].X, planes[i].Y, planes[i].Z).Length();
        return planes;
    }

    float birdAspect = 16f / 9;

}
