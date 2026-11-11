using System.Numerics;

namespace Meitou.Data.Particles;

/// <summary>Cosmetic particle pools attached to authoritative roots. No local node scheduling or movement.</summary>
public sealed class PresentedEffectSet(ParticleLibrary library, EffectWorld world)
{
    Dictionary<string, PresentedGroup> _byId = new(StringComparer.Ordinal);
    public IReadOnlyList<EffectGroup> Groups { get; private set; } = [];

    public void Accept(WeatherEffectFrame frame, Func<string, EffectRecord?> resolve)
    {
        frame.Validate();
        var next = new Dictionary<string, PresentedGroup>(StringComparer.Ordinal);
        foreach (var node in frame.Nodes)
        {
            if (_byId.TryGetValue(node.Id, out var existing))
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
        _byId = next; Groups = next.Values.Cast<EffectGroup>().ToArray();
    }

    public void Clear() { _byId.Clear(); Groups = []; }

    sealed class PresentedGroup : EffectGroup
    {
        readonly EffectUnit _unit;
        ParticleEnvironment _environment;
        public (string Effect, string System, int Seed, bool Weather) Identity { get; }
        public override bool NodesAreAuthoritative => true;

        public PresentedGroup(EffectNodeSnapshot node, EffectRecord effect, PuSystemDef system, EffectWorld world)
            : base(effect, node.Weather ? new(effect, 1, 0, 0) : null, system, node.Seed, world)
        {
            Identity = (node.EffectId, node.System, node.Seed, node.Weather);
            _unit = new(effect, system, node.Seed); _units.Add(_unit);
        }
        public void Apply(EffectNodeSnapshot node)
        {
            _unit.Position = node.Position; _unit.Orientation = node.Orientation; _unit.Tint = node.Tint; _unit.Alpha = node.Alpha;
            _unit.Size = node.Size; _unit.LifeScale = node.LifeScale; _unit.Radius = node.Radius ?? float.PositiveInfinity;
            _unit.Life = node.Life ?? float.PositiveInfinity; _unit.Age = node.Age; _unit.Stopped = node.Stopped; _unit.StoppedFor = node.StoppedFor;
            _unit.Wrap = node.Wrap; _unit.WrapCentre = node.WrapCentre; _unit.WrapSize = node.WrapSize;
            _unit.WrapMaxHeight = node.WrapMaxHeight ?? float.PositiveInfinity; _unit.WrapAhead = node.WrapAhead;
            _environment = node.Environment;
            if (_unit.Simulation is { } sim) { sim.Orientation = node.Orientation; sim.Size = node.Size; sim.LifeScale = node.LifeScale; sim.EmissionStopped = node.Stopped; }
        }
        public override void Update(float dt, in EffectCamera camera, WeatherEffectInput weather)
        {
            _unit.DistanceToCamera = Vector3.Distance(_unit.Position, camera.Eye);
            _unit.Environment = _environment with { EmissionScale = _environment.EmissionScale * _world.Density };
            float reach = float.IsPositiveInfinity(_unit.Radius) ? 0 : _unit.Radius;
            bool near = _unit.DistanceToCamera - reach <= _world.ActiveRadius + Effect.MaximumViewDistance;
            if (_unit.Active && !near) _unit.Deactivate();
            if (!_unit.Active && near && !_unit.Stopped)
            {
                _unit.Activate(); _unit.Pending = Math.Min(Math.Min(_unit.Age, _unit.LongestLife), 30);
            }
            if (_unit.Active) _unit.Pending += dt;
        }
        public override void BeginWarm(float seconds, in EffectCamera camera, WeatherEffectInput weather) => Update(0, camera, weather);
    }
}
