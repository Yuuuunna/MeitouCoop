using System.Numerics;

namespace Meitou.Data.Particles;

/// <summary>Cosmetic particle pools attached to authoritative roots. No local node scheduling or movement.</summary>
public sealed class PresentedEffectSet(ParticleLibrary library, EffectWorld world)
{
    Dictionary<string, PresentedGroup> byId = new(StringComparer.Ordinal);
    public IReadOnlyList<EffectGroup> Groups { get; private set; } = [];

    public void Accept(WeatherEffectFrame frame, Func<string, EffectRecord?> resolve)
    {
        frame.Validate();
        var next = new Dictionary<string, PresentedGroup>(StringComparer.Ordinal);
        foreach (var node in frame.Nodes)
        {
            if (byId.TryGetValue(node.Id, out var existing))
            {
                if (existing.Identity != (node.EffectId, node.System, node.Seed, node.Weather)) throw new InvalidDataException("Effect node identity was reused.");
                next.Add(node.Id, existing);
            }
            else
            {
                var effect = resolve(node.EffectId);
                if (effect is null || effect.RecordId != node.EffectId || effect.ParticleSystem != node.System ||
                    effect.AffectType == World.WeatherAffecting.None || effect.EffectRadius <= 0 || node.Weather && effect.IsCameraGroup ||
                    library.FindSystem(node.System) is not { } system)
                    throw new InvalidDataException("Authoritative effect is absent from compatible content.");
                next.Add(node.Id, new PresentedGroup(node, effect, system, world));
            }
        }
        // All content and identities must validate before existing roots are changed.
        foreach (var node in frame.Nodes) next[node.Id].Apply(node);
        byId = next; Groups = next.Values.Cast<EffectGroup>().ToArray();
    }

    public void Clear() { byId.Clear(); Groups = []; }

    sealed class PresentedGroup : EffectGroup
    {
        readonly EffectUnit unit;
        ParticleEnvironment environment;
        public (string Effect, string System, int Seed, bool Weather) Identity { get; }
        public override bool NodesAreAuthoritative => true;

        public PresentedGroup(EffectNodeSnapshot node, EffectRecord effect, PuSystemDef system, EffectWorld world)
            : base(effect, node.Weather ? new(effect, 1, 0, 0) : null, system, node.Seed, world)
        {
            Identity = (node.EffectId, node.System, node.Seed, node.Weather);
            unit = new(effect, system, node.Seed); units.Add(unit);
        }
        public void Apply(EffectNodeSnapshot node)
        {
            unit.Position = node.Position; unit.Orientation = node.Orientation; unit.Tint = node.Tint; unit.Alpha = node.Alpha;
            unit.Size = node.Size; unit.LifeScale = node.LifeScale; unit.Radius = node.Radius ?? float.PositiveInfinity;
            unit.Life = node.Life ?? float.PositiveInfinity; unit.Age = node.Age; unit.Stopped = node.Stopped; unit.StoppedFor = node.StoppedFor;
            unit.Wrap = node.Wrap; unit.WrapCentre = node.WrapCentre; unit.WrapSize = node.WrapSize;
            unit.WrapMaxHeight = node.WrapMaxHeight ?? float.PositiveInfinity; unit.WrapAhead = node.WrapAhead;
            environment = node.Environment;
            if (unit.Simulation is { } sim) { sim.Orientation = node.Orientation; sim.Size = node.Size; sim.LifeScale = node.LifeScale; sim.EmissionStopped = node.Stopped; }
        }
        public override void Update(float dt, in EffectCamera camera, WeatherEffectInput weather)
        {
            unit.DistanceToCamera = Vector3.Distance(unit.Position, camera.Eye);
            unit.Environment = environment with { EmissionScale = environment.EmissionScale * world.Density };
            float reach = float.IsPositiveInfinity(unit.Radius) ? 0 : unit.Radius;
            bool near = unit.DistanceToCamera - reach <= world.ActiveRadius + Effect.MaximumViewDistance;
            if (unit.Active && !near) unit.Deactivate();
            if (!unit.Active && near && !unit.Stopped)
            {
                unit.Activate(); unit.Pending = Math.Min(Math.Min(unit.Age, unit.LongestLife), 30);
            }
            if (unit.Active) unit.Pending += dt;
        }
        public override void BeginWarm(float seconds, in EffectCamera camera, WeatherEffectInput weather) => Update(0, camera, weather);
    }
}
