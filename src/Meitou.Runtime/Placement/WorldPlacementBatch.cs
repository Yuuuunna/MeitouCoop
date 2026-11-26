using System.Numerics;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Construction;

namespace Meitou.Runtime.Placement;

/// <summary>
/// Reconstructs submitted wall lines from authoritative nodes and terrain, and distinguishes earlier queued ghosts from pre-existing sites.
/// </summary>
sealed class WorldPlacementBatch : IConstructionPlacementBatch
{
    /// <summary>
    /// The live authority and the command whose plans are being checked.
    /// </summary>
    readonly WorldPlacement _placement;
    /// <summary>
    /// The current world; never a client snapshot.
    /// </summary>
    readonly World _world;
    /// <summary>
    /// The already authorized actor and faction passed to the ordinary geometry check.
    /// </summary>
    readonly PlayerId? _actor;
    /// <summary>
    /// The command's faction, including research ownership.
    /// </summary>
    readonly int _faction;
    /// <summary>
    /// The original ordered plans and optional line descriptions.
    /// </summary>
    readonly PlaceBuildings _command;
    /// <summary>
    /// Each plan's line index, or -1 for an ordinary ghost.
    /// </summary>
    readonly int[] _lineOf;
    /// <summary>
    /// Whether all line descriptions have disjoint, ordered, in-bounds ranges and finite world coordinates.
    /// </summary>
    readonly bool _valid;
    /// <summary>
    /// Regenerated line geometry; null records a rejected line so later pieces cannot reinterpret it.
    /// </summary>
    readonly Dictionary<int, ResolvedLine?> _resolved = [];
    /// <summary>
    /// Only sites successfully created by this command, mapped to their original plan index.
    /// </summary>
    readonly Dictionary<string, int> _accepted = new(StringComparer.Ordinal);

    /// <summary>
    /// A verified line and the shared pass computed from its actual snapped joints.
    /// </summary>
    sealed record ResolvedLine(IReadOnlyList<WallPiece> Pieces, WallJointPass Pass);

    /// <summary>
    /// Starts an isolated geometry context; malformed descriptions do not fall back to broader collision exemptions.
    /// </summary>
    internal WorldPlacementBatch(WorldPlacement placement, World world, PlayerId? actor, int faction, PlaceBuildings command)
    {
        _placement = placement;
        _world = world;
        _actor = actor;
        _faction = faction;
        _command = command;
        _lineOf = Enumerable.Repeat(-1, command.Plans.Count).ToArray();
        _valid = IndexLines();
    }

    /// <summary>
    /// Checks a plan with the same live geometry and actor rules as an ordinary single-building command.
    /// </summary>
    public PlacementFailure Check(int index) => !_valid ? PlacementFailure.InvalidPlan
        : _placement.Check(_world, _actor, _faction, _command.Plans[index], this, index);

    /// <summary>
    /// A failed plan is never registered; successfully placed sites remain live geometry for the rest of the batch.
    /// </summary>
    public void Accepted(int index, BuildSite site) => _accepted.Add(site.Id, index);

    /// <summary>
    /// Line pieces use their once-regenerated line context rather than rebuilding ordinary ghost snap candidates for every piece.
    /// </summary>
    internal bool IsLine(int index) => _lineOf[index] >= 0;

    /// <summary>
    /// Applies verified wall-line passes and the preview's queued-ghost rules to an otherwise ordinary authoritative query.
    /// </summary>
    internal PlacementQuery? Query(PlacementQuery query, int index)
    {
        int lineIndex = _lineOf[index];
        if (lineIndex >= 0)
        {
            if (!_resolved.TryGetValue(lineIndex, out var resolved))
                _resolved[lineIndex] = resolved = ResolveLine(lineIndex, query.Services);
            if (resolved is null) return null;

            int piece = index - _command.WallLines[lineIndex].FirstPlan;
            query = query with
            {
                Ignore = (record, position, rotation) => _placement.Data(record) is { } other
                    && resolved.Pass.Passes(piece, other, position, rotation),
            };
        }

        var queued = new List<QueuedGhost>();
        foreach (var site in query.Sites)
        {
            if (!_accepted.TryGetValue(site.Id, out int previous) || lineIndex >= 0 && _lineOf[previous] == lineIndex) continue;
            if (_placement.Data(site.Record) is not { } data) continue;

            var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, PlacementCheck.YawOf(site.Rotation));
            var hull = query.Services.Footprint(site.Record)?.Place(site.Position, yaw);
            queued.Add(new QueuedGhost(data, site.Position, site.Rotation, site.Floor, hull, site.Parent, site.Outside));
        }
        return query with { Sites = query.Sites.Where(site => !_accepted.ContainsKey(site.Id)).ToArray(), Queued = queued };
    }

    /// <summary>
    /// Validates line ranges independently of the network decoder, including direct local command callers.
    /// </summary>
    bool IndexLines()
    {
        if (_command.WallLines is null || _command.WallLines.Count > _lineOf.Length) return false;

        int next = 0;
        for (int i = 0; i < _command.WallLines.Count; i++)
        {
            var line = _command.WallLines[i];
            if (line?.Intent is not { } intent || string.IsNullOrWhiteSpace(intent.Master) || line.FirstPlan < next || line.PieceCount <= 0
                || line.FirstPlan > _lineOf.Length - line.PieceCount || !Position(intent.Start) || !Position(intent.End)) return false;

            next = line.FirstPlan + line.PieceCount;
            for (int plan = line.FirstPlan; plan < next; plan++) _lineOf[plan] = i;
        }
        return true;
    }

    /// <summary>
    /// The same finite coordinate envelope required of ordinary placement commands.
    /// </summary>
    static bool Position(Vector3 point) => float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z)
        && MathF.Abs(point.X) <= WorldLayout.HalfWorldSize && MathF.Abs(point.Z) <= WorldLayout.HalfWorldSize
        && MathF.Abs(point.Y) <= WorldLayout.WorldSize;

    /// <summary>
    /// Recreates a line using the authority's record data, research, current snapped nodes and ground profile, then checks every submitted piece.
    /// </summary>
    ResolvedLine? ResolveLine(int index, PlacementServices services)
    {
        var line = _command.WallLines[index];
        var intent = line.Intent;
        if (_placement.Data(intent.Master) is not { LinksTogether: true, LinkType: 0 or 3 } master
            || _world.Factions.Find(_faction)?.Research.IsUnlocked(intent.Master) != true
            || master.LinkType == 3 && !intent.StartSnapped || !intent.StartSnapped && intent.Reversed) return null;

        var starts = intent.StartSnapped ? StartSnaps(index, services).Select(snap => (WallSnap?)snap) : [null];
        var ends = intent.EndSnapped
            ? WorldWallSnaps.At(_world, services, _placement.Data, intent.End, intent.Reversed ? 1 : 2).Select(snap => (WallSnap?)snap).ToArray()
            : [null];
        foreach (var start in starts)
            foreach (var end in ends)
            {
                var pieces = WallLine.Layout(master, _placement.Data, services.Ground, intent.Start, start, intent.End, end, intent.Reversed);
                if (!Matches(line, pieces)) continue;

                return new ResolvedLine(pieces, new WallJointPass(pieces, WallLine.Shortest(master, _placement.Data),
                    start?.Point, end?.Point));
            }
        return null;
    }

    /// <summary>
    /// Current real nodes and the original's chained-line continuation, derived only from a previously validated and fully accepted line.
    /// </summary>
    IEnumerable<WallSnap> StartSnaps(int index, PlacementServices services)
    {
        var intent = _command.WallLines[index].Intent;
        foreach (var snap in WorldWallSnaps.At(_world, services, _placement.Data, intent.Start))
            if (snap.Reverses == intent.Reversed) yield return snap;

        for (int previous = 0; previous < index; previous++)
        {
            var prior = _command.WallLines[previous];
            var old = prior.Intent;
            if (old.Master != intent.Master || old.Reversed != intent.Reversed || old.EndSnapped
                || !_resolved.TryGetValue(previous, out var resolved) || resolved is null || prior.PieceCount != resolved.Pieces.Count
                || Enumerable.Range(prior.FirstPlan, prior.PieceCount).Any(plan => !_accepted.ContainsValue(plan))) continue;

            var last = resolved.Pieces[^1];
            var (start, end) = WallLine.Ends(last.Data, last.Position, last.Rotation);
            var point = old.Reversed ? start : end;
            if (!WorldWallSnaps.Near(point, intent.Start)) continue;

            var flat = old.End - old.Start;
            flat.Y = 0;
            if (flat.LengthSquared() < 1e-10f) continue;

            var direction = Vector3.Normalize(flat);
            yield return new WallSnap(point, old.Reversed ? -direction : direction, old.Reversed ? 2 : 1);
        }
    }

    /// <summary>
    /// Exact records, outdoor placement and regenerated transforms are required; a client cannot reorder pieces or lend one line's pass to another.
    /// </summary>
    bool Matches(PlannedWallLine line, IReadOnlyList<WallPiece> pieces)
    {
        if (line.PieceCount > pieces.Count) return false;

        for (int i = 0; i < line.PieceCount; i++)
        {
            var plan = _command.Plans[line.FirstPlan + i];
            var piece = pieces[i];
            if (plan is null || plan.Record != piece.Data.Id || plan.Parent is not null || plan.Floor != 0 || plan.Outside
                || !WorldWallSnaps.Near(plan.Position, piece.Position) || !WorldWallSnaps.SameTurn(plan.Rotation, piece.Rotation)) return false;
        }
        return true;
    }
}
