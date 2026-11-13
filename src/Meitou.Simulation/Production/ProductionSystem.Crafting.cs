using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Perception;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Construction;
using Meitou.Simulation.Items;

namespace Meitou.Simulation.Production;
/// <summary>
/// Crafting benches (docs/game/buildings-production.md "Crafting", docs/simulation.md "Production"): a queue per bench, worked by its operators; each
/// craft binds the bench's input sections to its ingredients, draws them as the work goes, and puts the finished item into the bench's output.
/// </summary>
public sealed partial class ProductionSystem
{
    /// <summary>
    /// How many crafts a bench's queue holds (engine choice; the original's limit is Unknown).
    /// </summary>
    public const int MaxCraftQueue = 20;

    /// <summary>
    /// The cache of <see cref="EverEnabled"/>.
    /// </summary>
    HashSet<(CraftKind, string)>? _everEnabled;

    /// <summary>
    /// Crafts some research unlocks; anything no research lists is craftable from the start (engine reading).
    /// </summary>
    HashSet<(CraftKind, string)> EverEnabled()
    {
        if (_everEnabled is not null) return _everEnabled;
        _allResearch ??= ResearchData.LoadAll(_db);
        return _everEnabled = [.. _allResearch.SelectMany(r => r.EnableCrafts.Select(e => (e.Kind, e.Id)))];
    }

    /// <summary>
    /// Whether the player can queue an option now: its record (and a weapon's model) unlocked, and its level within the bench's tech level.
    /// </summary>
    public bool CanCraft(World world, CraftBench bench, CraftOption option) => CanCraft(world, bench, option, world.Player.Faction);

    /// <summary>
    /// The recipe of a bench option on the machine.
    /// </summary>
    CraftRecipe RecipeOf(Machine m, CraftOption option) => m.Data.Craft!.Recipe(_db, _constants, option, m.Data.Inputs);
    /// <summary>
    /// Queues a craft (its count, up to <see cref = "MaxCraftQueue"/>) when the bench offers it and the player can craft it, and binds the sections to
    /// the first job.
    /// </summary>
    void QueueCraft(World world, QueueCraft order)
    {
        if (!MayControlMachine(world, order, order.Site) || order.Issuer is not null && (order.Count <= 0 || order.Count > MaxCraftQueue))
            return;
        if (world.Machines.Find(order.Site)is not { Craft: { } state, Data.Craft: { } bench } m)
            return;
        if (Site(world, order.Site)is not { Owner: >= 0 } site || !world.MayManage(order, site.Owner, FactionPermission.ManageAssets))
            return;
        if (bench.Options.FirstOrDefault(o => o.Key == order.Option)is not { } option || !CanCraft(world, bench, option, site.Owner))
            return;
        for (int i = 0; i < Math.Max(1, order.Count) && state.Queue.Count < MaxCraftQueue; i++)
            state.Queue.Add(new CraftJob(RecipeOf(m, option)));
        Bind(world, m, state, state.Queue[0]);
        world.Machines.Touch();
    }

    /// <summary>
    /// Takes a craft off a bench's queue and binds the sections to the new first job.
    /// </summary>
    void CancelCraft(World world, CancelCraft order)
    {
        if (!MayControlMachine(world, order, order.Site))
            return;
        if (world.Machines.Find(order.Site)is not { Craft: { } state } m || order.Index < 0 || order.Index >= state.Queue.Count)
            return;
        state.Queue.RemoveAt(order.Index);
        if (state.Queue.Count > 0)
            Bind(world, m, state, state.Queue[0]);
        world.Machines.Touch();
    }

    /// <summary>
    /// Units of the first finished stack the output already holds of this option (it stops the bench at the option's store).
    /// </summary>
    static int Held(CraftState state, CraftOption option) => state.Done.Where(d => d.Record == option.Item).Sum(d => d.Quantity);

    /// <summary>
    /// Sets the input sections for the first craft (<b>Observed</b>, <c>CraftingBuilding::vfunc_182</c> "setProductionItem"): section k takes
    /// ingredient k at its per-item amount; sections it does not use draw nothing. Done when the first craft changes (queued, cancelled, finished).
    /// Items of another kind in a section fall on the ground (engine choice; the original hands them back to the bench). Each section's level moves
    /// with its item (<b>Verified</b>, <c>FUN_1402bb470</c>): all sections store their level under their old item first, then a section taking an
    /// item gets the level stored for it (0 when none), so switching to another recipe and back returns the partly drawn ingredient.
    /// </summary>
    void Bind(World world, Machine m, CraftState state, CraftJob job)
    {
        if (state.Bound == job.Option.Key) return;
        foreach (var held in m.Inputs) Remember(state, held.Item, held.Level);
        for (int k = 0; k < m.Inputs.Length; k++)
        {
            var slot = m.Inputs[k];
            if (slot.Item != ItemOf(m, job, k) && slot.Count > 0 && _items.Create(slot.Item, slot.Count) is { } back)
            {
                world.GroundItems.Drop(back, m.Position, 0);
                slot.Count = 0;
                slot.Level = 0;
            }
        }

        Apply(m, state, job);
        for (int k = 0; k < job.Recipe.Inputs.Count && k < m.Inputs.Length; k++)
        {
            if (state.Partial.Remove(m.Inputs[k].Item, out float level)) m.Inputs[k].Level = level;
            else m.Inputs[k].Level = 0;
        }
    }

    /// <summary>
    /// Stores a section's level under its item (a level of 0 forgets it).
    /// </summary>
    static void Remember(CraftState state, string item, float level)
    {
        if (level > 0) state.Partial[item] = level;
        else state.Partial.Remove(item);
    }

    /// <summary>
    /// A remembered level read from a save (an item that is not in the game is dropped).
    /// </summary>
    internal void RestorePartial(Machine m, string item, float level)
    {
        if (m.Craft is { } state && level > 0 && _items.Info(item) is not null) state.Partial[item] = level;
    }

    /// <summary>
    /// The item of section <paramref name="k"/> for a craft: the recipe's ingredient, else the section's own item.
    /// </summary>
    static string ItemOf(Machine m, CraftJob job, int k) => k < job.Recipe.Inputs.Count ? job.Recipe.Inputs[k].Item : m.Inputs[k].Recipe.Item;

    /// <summary>
    /// Sets the sections' items and rates for a craft (also on load, before the saved section items are read back).
    /// </summary>
    static void Apply(Machine m, CraftState state, CraftJob job)
    {
        state.Bound = job.Option.Key;
        for (int k = 0; k < m.Inputs.Length; k++)
        {
            m.Inputs[k].Item = ItemOf(m, job, k);
            m.Inputs[k].Rate = k < job.Recipe.Inputs.Count ? job.Recipe.Inputs[k].PerItem : 0;
        }
    }

    /// <summary>
    /// One operator's work on a bench (<b>Observed</b>, <c>CraftingBuilding::vfunc_150</c>): <c>rate x s x option x P x dt / seconds</c> of the first
    /// craft, clamped to the inputs; the critical is rolled when the craft first gets work; at 0.999 the item is made.
    /// </summary>
    bool CraftStep(World world, Machine m, float skill, float s, float dt)
    {
        var state = m.Craft!;
        if (state.Queue.Count == 0)
        {
            m.Status = 0;
            return false;
        }

        var job = state.Queue[0];
        if (Held(state, job.Option) >= job.Option.Store)
        {
            m.Status = 1;
            return false;
        }

        Bind(world, m, state, job);
        if (job.Critical == 2)
        {
            float chance = CraftBench.CriticalChance(Math.Max(0, skill), m.Data.Stat, job.Option.Level);
            float roll = Rng.Float(Rng.Hash(world.Seed, Rng.StableHash(m.Site), RngPurpose.Craft, (ulong)(uint)state.Rolls++));
            job.Critical = (byte)(roll < chance ? 1 : 0);
        }

        float amount = m.Data.Rate * WorkFactor(world, m) * s * Settings.ProductionOption * _constants.ProductionSpeed * dt
            / MathF.Max(1e-3f, job.Seconds);
        foreach (var input in m.Inputs)
            if (input.Rate > 0 && input.Level + input.Count < input.Rate * amount) amount = (input.Level + input.Count) / input.Rate;
        amount = MathF.Min(amount, 1 - job.Progress);
        if (amount <= 1e-9f)
        {
            m.Status = 0;
            return false;
        }

        m.Status = 2;
        foreach (var input in m.Inputs)
        {
            if (input.Rate <= 0) continue;
            float need = input.Rate * amount;
            while (input.Level < need && input.Count > 0) { input.Count--; input.Level += 1; }
            input.Level = MathF.Max(0, input.Level - need);
        }

        job.Progress += amount;
        if (job.Progress >= 0.999f) Finish(world, m, state, job, skill);
        return true;
    }

    /// <summary>
    /// A finished craft (<b>Observed</b>, <c>FUN_1402bcac0</c>): armour of the crafter's tier (one better on a critical), capped at the bench's tech
    /// level, and a robotic limb or crossbow of that grade as its level; a weapon of the queued model, or when the crafter's skill is below its level
    /// and there was no critical, of the best model of the bench at or below the skill (engine reading of the model pick); other items as they are.
    /// The craft leaves the queue, and with repeat on goes back to its end.
    /// </summary>
    void Finish(World world, Machine m, CraftState state, CraftJob job, float skill)
    {
        var option = job.Option;
        var bench = m.Data.Craft!;
        bool critical = job.Critical == 1;
        ItemInstance? made;
        switch (option.Kind)
        {
            case CraftKind.Armour:
                // the factory gives an armour piece its level from that quality
                made = _items.Create(option.Item, 1, Math.Min(CraftBench.ArmourQuality(skill, critical), bench.TechLevel));
                break;
            case CraftKind.Robotics or CraftKind.Crossbow:
                made = _items.Create(option.Item, 1);
                if (made is not null) made.Level = Math.Min(CraftBench.ArmourQuality(skill, critical), bench.TechLevel);
                break;
            case CraftKind.Weapon:
                var model = WeaponModel(bench, option, skill, critical);
                made = _items.Create(option.Item, 1);
                if (made is not null)
                {
                    made.Level = model.Level;
                    made.MaterialId = model.Model ?? "";
                    made.CompanyId = CraftBench.PlayerWeapons;
                }

                break;
            default:
                made = _items.Create(option.Item, 1);
                break;
        }

        state.Queue.RemoveAt(0);
        if (made is not null)
        {
            made.Section = "out";
            if (state.Done.FirstOrDefault(d => d.Record == made.Record && d.Quality == made.Quality && d.MaterialId == made.MaterialId
                && d.Quantity < d.StackLimit) is { } stack)
                stack.Quantity++;
            else state.Done.Add(made);
        }

        if (state.Repeat) state.Queue.Add(new CraftJob(RecipeOf(m, option)));
        world.Machines.Touch();
    }

    /// <summary>
    /// The model a weapon craft comes out as: the queued one, or below the crafter's skill with no critical the best model of the bench at or below
    /// the skill (else its lowest).
    /// </summary>
    static CraftOption WeaponModel(CraftBench bench, CraftOption option, float skill, bool critical) =>
        skill >= option.Level || critical
            ? option
            : bench.Options.Where(o => o.Kind == CraftKind.Weapon && o.Item == option.Item && o.Level <= skill)
                .OrderByDescending(o => o.Level)
                .FirstOrDefault()
                ?? bench.Options.Where(o => o.Kind == CraftKind.Weapon && o.Item == option.Item).OrderBy(o => o.Level).First();

    /// <summary>
    /// The cache of the smiths' craft predictions (null: made again).
    /// </summary>
    IReadOnlyDictionary<string, CraftSmithView>? _publishedSmiths;
    /// <summary>
    /// The machine registry version <see cref="_publishedSmiths"/> was made at (-1: never).
    /// </summary>
    long _smithsVersion = -1;

    /// <summary>
    /// The Craft tab's Current Smith per bench (<c>FUN_1402bcf60</c>): the bench's last operator (<see cref="Machine.Smith"/>) while it is alive with
    /// stats, his effective level of the bench's stat and the penalty of its multiplier, and for each queued craft what that smith would make
    /// (<see cref="CraftPrediction"/>). Rebuilt when the machines changed.
    /// </summary>
    public IReadOnlyDictionary<string, CraftSmithView> SmithsView(World world)
    {
        if (_publishedSmiths is not null && _smithsVersion == world.Machines.Version) return _publishedSmiths;
        _smithsVersion = world.Machines.Version;
        var map = new Dictionary<string, CraftSmithView>(StringComparer.Ordinal);
        foreach (var m in world.Machines.All)
        {
            if (m.Craft is not { } state || m.Data.Craft is not { } bench || m.Data.Stat <= 0 || !world.Characters.IsAlive(m.Smith)
                || world.Characters.Cold(m.Smith.Slot) is not { Stats: { } stats, Medical: { } medical } cold) continue;
            var (level, penalty) = World.EffectiveSkill(cold, medical, stats, (StatsEnumerated)m.Data.Stat);

            // FUN_1402b5900 reads the first queued craft whichever one is hovered.
            int critical = state.Queue.Count == 0
                ? 0
                : Math.Clamp((int)(CraftBench.CriticalChance(level, m.Data.Stat, state.Queue[0].Option.Level) * 100), 0, 100);
            var entries = new List<CraftPrediction>(state.Queue.Count);
            foreach (var job in state.Queue)
            {
                var option = job.Option;
                var none = new CraftPrediction(-1, null, false, 0, -1);
                switch (option.Kind)
                {
                    case CraftKind.Armour or CraftKind.Crossbow:
                        entries.Add(none with { Quality = CraftBench.ArmourQuality(level, false), Critical = critical });
                        break;
                    case CraftKind.Weapon:
                        var model = WeaponModel(bench, option, level, false);
                        entries.Add(none with
                        {
                            Model = model.Model is { } id ? _db.Find(id)?.Name ?? id : "",
                            ModelChanged = model.Model != option.Model,
                            MaterialCost = _db.Find(option.Item)?.GetInt("material cost", 4) ?? 4,
                            Critical = critical,
                        });
                        break;
                    default:
                        entries.Add(none);
                        break;
                }
            }

            map[m.Site] = new CraftSmithView(cold.Name, (int)level, penalty, entries);
        }

        return _publishedSmiths = map;
    }

    /// <summary>
    /// The cache of the benches' craft options (null: made again).
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<CraftOptionView>>? _publishedCrafts;
    /// <summary>
    /// The research count <see cref = "_publishedCrafts"/> was made at (-1: never).
    /// </summary>
    (int Faction, long Version)[] _craftsResearch = [];
    /// <summary>
    /// The bench count <see cref="_publishedCrafts"/> was made at (-1: never).
    /// </summary>
    int _craftsBenches = -1;
    /// <summary>
    /// Every crafting bench's options (rebuilt when research or the set of benches changed).
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<CraftOptionView>> CraftsView(World world)
    {
        int benches = 0;
        foreach (var m in world.Machines.All)
            if (m.Craft is not null)
                benches++;
        var versions = world.Factions.All.Select(f => (f.Index, f.Research.Version)).ToArray();
        if (_publishedCrafts is not null && _craftsResearch.SequenceEqual(versions) && _craftsBenches == benches)
            return _publishedCrafts;
        _craftsResearch = versions;
        _craftsBenches = benches;
        var map = new Dictionary<string, IReadOnlyList<CraftOptionView>>(StringComparer.Ordinal);
        foreach (var m in world.Machines.All)
            if (m.Craft is not null)
                map[m.Site] = CraftOptions(world, m.Site);
        return _publishedCrafts = map;
    }

    /// <summary>
    /// What a bench can craft, for its Craft tab: every option with its recipe and whether it can be queued now.
    /// </summary>
    public IReadOnlyList<CraftOptionView> CraftOptions(World world, string site)
    {
        if (world.Machines.Find(site)is not { Data.Craft: { } bench } m)
            return[];
        return[..bench.Options.Select(o =>
        {
            var recipe = RecipeOf(m, o);
            return new CraftOptionView(o.Key, recipe.Name, o.Kind, o.Level, recipe.Hours, [..recipe.Inputs.Select(i => (_items.Info(i.Item)?.Name ?? i.Item, i.PerItem))], CanCraft(world, bench, o, Site(world, m.Site)?.Owner ?? -1));
        })];
    }

    /// <summary>
    /// A queued craft read from a save (dropped when the bench no longer offers it).
    /// </summary>
    internal void RestoreCraft(Machine m, string item, string? model, float progress, byte critical)
    {
        string key = model is null ? item : item + "|" + model;
        if (m.Craft is not { } state || m.Data.Craft?.Options.FirstOrDefault(o => o.Key == key) is not { } option) return;
        state.Queue.Add(new CraftJob(RecipeOf(m, option)) { Progress = Math.Clamp(progress, 0, 1), Critical = critical });
        if (state.Queue.Count == 1) Apply(m, state, state.Queue[0]);
    }

    /// <summary>
    /// A finished item (at most <paramref name="count"/> units, 0: all) taken from a bench's output into a character's inventory (what does not fit
    /// stays).
    /// </summary>
    void TakeCrafted(World world, Machine m, CraftState state, Inventory root, int index, int count = 0)
    {
        if (index < 0 || index >= state.Done.Count) return;
        var item = state.Done[index];
        if (count > 0 && count < item.Quantity)
        {
            var unit = item.WithQuantity(count);
            item.Quantity -= count;
            if (!root.Add(unit)) item.Quantity += unit.Quantity;
            world.Machines.Touch();
            return;
        }

        state.Done.RemoveAt(index);
        if (!root.Add(item) && item.Quantity > 0) state.Done.Insert(index, item);
        world.Machines.Touch();
    }

    bool Unlocked(World world, CraftKind kind, string id, int faction) => ResearchFor(world, faction).UnlockedCrafts.Contains((kind, id)) || !EverEnabled().Contains((kind, id));
    public bool CanCraft(World world, CraftBench bench, CraftOption option, int faction) => Unlocked(world, option.Kind, option.Item, faction) && (option.Model is null || Unlocked(world, CraftKind.Weapon, option.Model, faction)) && option.Level <= bench.TechLevel;
    /// <summary>Where the camera looks (the last <see cref = "FocusCommand"/>; null: not told, as in headless runs): research costs are counted round it. Derived from the commands, not saved or hashed.</summary>
    readonly Dictionary<PlayerId, Vector3> _focuses = [];
    PlayerId _researchViewer = PlayerId.Local;
    /// <summary>
    /// Operating <see cref = "TaskRequest.Site"/> (rows 87, 97, 146, the jobs 152 and 234, the implicit sitting 275), a haul trip to the storage <see cref = "TaskRequest.Site"/> (124, the implicit
    /// ditching 242), or prospecting where the character stands (150).
    /// </summary>
    bool ITaskHandler.Runs(World world, int slot, RunningTask running) => running.Row == TaskType.FeedCorpseIntoMachine ? _errands.ContainsKey(world.Characters.IdOf(slot)) && (CharacterTask)world.Characters.Next[slot].Task is CharacterTask.Idle or CharacterTask.GoTo : running.Runs((CharacterTask)world.Characters.Next[slot].Task);
    bool ITaskHandler.Done(World world, int slot, CharacterTask task) => world.Characters.Cold(slot)!.Running is { Row: TaskType.FeedCorpseIntoMachine } ? !_errands.ContainsKey(world.Characters.IdOf(slot)) : world.Characters.Next[slot].Task != (byte)task;
    readonly Dictionary<int, int> _researchBenches = [];
    readonly Dictionary<int, int> _researchContributions = [];
    // ------------------------------------------------------------------ research
    ResearchState ResearchFor(World world, int faction) => faction < 0 ? world.Research : world.Factions.GetOrCreate(faction).Research;
    ResearchState ResearchFor(World world, Machine machine) => ResearchFor(world, Site(world, machine.Site)?.Owner ?? -1);
    int _publishedResearchFaction = int.MinValue;
    public bool Learn(World world, string id, int faction, PlayerId? recipient = null)
    {
        if (Research(id)is not { } data)
            return false;
        if (!Met(world, data, faction))
        {
            world.Post(recipient ?? PlayerId.Local, Loc.Tr("Failed to learn from blueprint: Technology requirements not met"));
            return false;
        }

        if (ResearchFor(world, faction).IsDone(id))
        {
            world.Post(recipient ?? PlayerId.Local, Loc.Tr("Research already known"));
            return false;
        }

        ResearchFor(world, faction).Complete(_db, id);
        _publishedResearch = null;
        world.Post(recipient ?? PlayerId.Local, Loc.Tr("Research complete: {1}", data.Name));
        return true;
    }

    public bool Learn(World world, ItemInstance blueprint, int faction, PlayerId? recipient = null) => _items.ResearchOf(blueprint)is { } research && Learn(world, research.StringId, faction, recipient);
    public bool Available(World world, ResearchData r, int faction) => !r.BlueprintOnly && !ResearchFor(world, faction).Exhausted(_db, r.Id) && !ResearchFor(world, faction).IsQueued(r.Id) && r.LevelAt(ResearchFor(world, faction).Times(r.Id)) <= ResearchFor(world, faction).TechLevel && Met(world, r.Requirements, faction, lenient: true);
    bool Met(World world, ResearchData r, int faction) => Met(world, r.Requirements, faction, false);
    bool Met(World world, IEnumerable<string> requirements, int faction, bool lenient)
    {
        foreach (var req in requirements)
        {
            var record = _db.Find(req);
            if (record is null)
                continue;
            if (record.Type == FcsRecordType.Research ? ResearchFor(world, faction).IsDone(req) : ResearchFor(world, faction).IsUnlocked(req))
                continue;
            if (!lenient || !Met(world, record.GetReferences("requirements").Select(link => link.TargetStringId), faction, false))
                return false;
        }

        return true;
    }

    Meitou.Data.World.ZoneCoordinate? FocusZone(PlayerId viewer) => _focuses.TryGetValue(viewer, out var at) ? Meitou.Data.World.WorldLayout.ZoneOf(at.X, at.Z) : null;
    /// <summary>The items of the player's research benches (where costs are paid from; engine: every bench, the original takes the benches near the player).</summary>
    IEnumerable<Inventory> Benches(World world, int faction, PlayerId? viewer = null)
    {
        var near = FocusZone(viewer ?? PlayerId.Local);
        foreach (var m in world.Machines.All)
        {
            if (!m.Data.IsResearch || m.Storage is not { } storage || Site(world, m.Site)is not { Complete: true } site || site.Owner != faction)
                continue;
            if (near is { } zone && Meitou.Data.World.WorldLayout.ZoneOf(m.Position.X, m.Position.Z)is var at && (Math.Abs(at.X - zone.X) > 1 || Math.Abs(at.Y - zone.Y) > 1))
                continue;
            yield return storage;
        }
    }

    int CountInBenches(World world, string item, int faction, PlayerId? viewer = null)
    {
        int n = 0;
        foreach (var s in Benches(world, faction, viewer))
            foreach (var i in s.Items)
                if (i.Record == item)
                    n += i.Quantity;
        return n;
    }

    bool Affordable(World world, ResearchData r, int faction, PlayerId? viewer = null)
    {
        if (ResearchFor(world, faction).Paid.Contains(r.Id, StringComparer.Ordinal))
            return true;
        foreach (var(item, count)in r.CostAt(ResearchFor(world, faction).Times(r.Id)))
            if (CountInBenches(world, item, faction, viewer) < count)
                return false;
        return true;
    }

    void Queue(World world, string id, int faction, PlayerId? recipient)
    {
        if (Research(id)is not { } r || !Available(world, r, faction))
            return;
        int level = r.LevelAt(ResearchFor(world, faction).Times(id));
        if (level > ResearchFor(world, faction).BenchLevel)
        {
            world.Post(recipient ?? PlayerId.Local, ResearchFor(world, faction).BenchLevel == 0 ? "Research needs a research bench." : $"{r.Name} needs a level {level} research bench.");
            return;
        }

        if (!Affordable(world, r, faction, recipient))
        {
            world.Post(recipient ?? PlayerId.Local, $"Not enough materials in the research benches for {r.Name}.");
            return;
        }

        if (!ResearchFor(world, faction).Paid.Contains(id, StringComparer.Ordinal))
            foreach (var(item, count)in r.CostAt(ResearchFor(world, faction).Times(id)))
            {
                int left = count;
                foreach (var s in Benches(world, faction, recipient))
                    foreach (var i in s.Items.Where(i => i.Record == item).ToList())
                    {
                        int take = Math.Min(left, i.Quantity);
                        i.Quantity -= take;
                        if (i.Quantity == 0)
                            s.Items.Remove(i);
                        left -= take;
                        if (left == 0)
                            break;
                    }
            }

        ResearchFor(world, faction).Enqueue(id);
        world.Machines.Touch();
    }

    public ResearchSnapshot ResearchView(World world, int faction) => ResearchView(world, faction, PlayerId.Local);
    public ResearchSnapshot ResearchView(World world, int faction, PlayerId viewer)
    {
        var zone = FocusZone(viewer);
        var state = ResearchFor(world, faction);
        if (_publishedResearch is not null && _publishedResearchFaction == faction && _researchViewer == viewer && _researchVersion == state.Version && _machinesVersion == world.Machines.Version && _researchZone == zone)
            return _publishedResearch;
        var lists = (state.Completed.Count, state.Queue.Count, state.Paid.Count, state.BenchLevel);
        if (_publishedResearch is not null && _publishedResearchFaction == faction && _researchViewer == viewer && _researchLists == lists && (ulong)(world.Tick - _researchBuilt) < ResearchRefreshTicks)
            return _publishedResearch;
        _researchLists = lists;
        _researchBuilt = world.Tick;
        _researchZone = zone;
        _researchViewer = viewer;
        _publishedResearchFaction = faction;
        _researchVersion = ResearchFor(world, faction).Version;
        _machinesVersion = world.Machines.Version;
        _allResearch ??= ResearchData.LoadAll(_db);
        var queue = state.Queue.Select(q => (q.Id, Research(q.Id)?.Name ?? q.Id, Research(q.Id)is { } d ? Math.Clamp(q.Seconds / (d.Hours(_constants, Settings.ResearchOption, state.Times(q.Id)) * (MachineData.DaySeconds / 24)), 0, 1) : 0)).ToArray();
        var available = _allResearch.Where(r => Available(world, r, faction)).Select(r => new ResearchOption(r.Id, r.Name, r.Category, r.LevelAt(state.Times(r.Id)), r.Hours(_constants, Settings.ResearchOption, state.Times(r.Id)), Affordable(world, r, faction, viewer), r.LevelAt(state.Times(r.Id)) > state.BenchLevel, state.Partial.TryGetValue(r.Id, out float s) ? Math.Clamp(s / (r.Hours(_constants, Settings.ResearchOption, state.Times(r.Id)) * (MachineData.DaySeconds / 24)), 0, 1) : 0) { Description = r.Description, Paid = state.Paid.Contains(r.Id, StringComparer.Ordinal), Cost = state.Paid.Contains(r.Id, StringComparer.Ordinal) ? [] : [..r.CostAt(state.Times(r.Id)).Select(c => (_items.Info(c.Item)?.Name ?? c.Item, c.Count, CountInBenches(world, c.Item, faction, viewer) >= c.Count))], // The description lists the RESEARCH requirements only (FUN_140495e00 looks them up as type 21).
        Requirements = [..r.Requirements.Select(_db.Find).Where(q => q is { Type: FcsRecordType.Research }).Select(q => (q!.Name, state.IsDone(q.StringId), state.IsQueued(q.StringId)))], Unlocks = [..r.Unlocks.Select(u => (u.Field, _db.Find(u.Id)?.Name)).Where(u => u.Name is not null).Select(u => (u.Field, u.Name!))], }).ToArray();
        return _publishedResearch = new ResearchSnapshot(state.TechLevel, state.BenchLevel, queue, state.Rate, available)
        {
            Finished = state.Completed.Count,
            Known = state.Completed.ToHashSet(StringComparer.Ordinal),
            RequirementsMet = _allResearch.Where(r => Met(world, r, faction)).Select(r => r.Id).ToHashSet(StringComparer.Ordinal),
        };
    }

    // ------------------------------------------------------------------ items in and out
    bool Near(World world, CharacterId who, BuildSite site, out CharacterCold cold, PlayerId? actor = null)
    {
        cold = null!;
        var table = world.Characters;
        if (!table.TryResolveNext(who, out int slot) || table.Cold(slot)is not { IsPlayer: true } c || c.Inventory is null)
            return false;
        if (actor is not null && (c.Medical?.Incapacitated != false || c.IsImprisoned || !c.CarriedBy.IsNone))
            return false;
        cold = c;
        if (GapTo(site, table.Next[slot].Position) <= Settings.TransferReach)
            return true;
        world.Post(actor ?? PlayerId.Local, "Out of trading range.");
        return false;
    }

    static bool PutAt(Inventory owner, ItemInstance item, int count, Inventory storage, int x, int y)
    {
        int n = count == 0 ? item.Quantity : Math.Min(count, item.Quantity);
        if (n <= 0 || item.Width <= 0 || item.Height <= 0 || x < 0 || y < 0 || x > storage.Width - item.Width || y > storage.Height - item.Height || n > storage.LimitOf(item) || !storage.InRegion(x, y, item.Width, item.Height) || !storage.IsFree("main", x, y, item.Width, item.Height) || !owner.Items.Contains(item) || n < item.Quantity && item.Contents is not null)
            return false;
        var unit = n == item.Quantity ? item : item.WithQuantity(n);
        if (ReferenceEquals(unit, item))
            owner.Items.Remove(item);
        else
        {
            item.Quantity -= n;
            if (unit.MaxCharges > 0)
                unit.Charges = unit.MaxCharges;
        }

        unit.Section = "main";
        unit.X = x;
        unit.Y = y;
        unit.Death = false;
        storage.Items.Add(unit);
        return true;
    }

    static bool ContainerRevisionsMatch(World world, StorageTransferCommand command, CharacterCold actor, Locks.Fixture container) => world.InventoryMatches(command, actor.Inventory!) && (command.Issuer is null && command.StorageRevision == 0 || command.StorageRevision != 0 && world.InventoryRevision(PlayerSystem.GridOf(world, container)) == command.StorageRevision);
    internal int StorageOwner(World world, string site) => world.Fixtures.Find(site)?.Owner ?? Site(world, site)?.Owner ?? -1;
    internal BuildSite? SaveWorkSite(World world, string key) => Site(world, key);
    public ulong WorkSiteRevision(World world, string id) => Site(world, id)is { } site ? world.BuildingRevision(site) : 0;
    static bool MayWork(World world, CharacterCold worker, BuildSite site) => world.MayUseAssets(worker, site.Owner) && (!world.Players.RequiresExplicitControl || !worker.IsPlayer || WorkSiteOpen(world, site));
    static bool WorkSiteOpen(World world, BuildSite site) => !site.Destroyed && !site.Upgrading && world.Fixtures.Find(site.Id)?.Lock is not { EffectivelyLocked: true } && world.Machines.Find(site.Id)is not { Closed: true };
    static bool MayStartWork(World world, SiteWorkCommand command, BuildSite site)
    {
        if (command.SiteRevision != 0 && world.BuildingRevision(site) != command.SiteRevision)
            return false;
        if (command.Issuer is not { } issuer)
            return true;
        return command.SiteRevision != 0 && WorkSiteOpen(world, site) && (site.Owner < 0 || command.FactionGrantRevision != 0 && world.Players.Find(issuer)is { } player && player.MembershipRevision(site.Owner) == command.FactionGrantRevision && player.HasPermission(site.Owner, FactionPermission.ManageAssets));
    }

    bool MayControlMachine(World world, MachineControlCommand command, string id)
    {
        if (Site(world, id)is not { Complete: true, Owner: >= 0 } site || world.Machines.Find(id)is not { } machine)
            return false;
        if (command.ControlRevision != 0 && world.MachineControlRevision(machine) != command.ControlRevision)
            return false;
        return command.Issuer is not { } issuer || command.ControlRevision != 0 && command.FactionGrantRevision != 0 && world.Players.Find(issuer)is { } player && player.MembershipRevision(site.Owner) == command.FactionGrantRevision && player.HasPermission(site.Owner, FactionPermission.ManageAssets);
    }

    static bool MayTransferMachine(World world, StorageTransferCommand command, BuildSite site)
    {
        if (command.Issuer is not { } issuer)
            return command.StorageRevision == 0 || world.Machines.Find(site.Id)is { } local && world.MachineRevision(local) == command.StorageRevision;
        if (world.Machines.Find(site.Id)is not { Closed: false } machine || world.Fixtures.Find(site.Id)?.Lock is { EffectivelyLocked: true } || command.StorageRevision == 0 || world.MachineRevision(machine) != command.StorageRevision)
            return false;
        return site.Owner < 0 || world.Players.Find(issuer)is { } player && player.HasPermission(site.Owner, FactionPermission.ManageAssets) && command.FactionGrantRevision != 0 && player.MembershipRevision(site.Owner) == command.FactionGrantRevision;
    }

    void StartDisposal(World world, int slot, OrderEntry order)
    {
        if (!((IOrderOwner)this).CanRun(world, slot, order).CanRun || Site(world, order.Site!)is not { } site || world.Machines.Find(site.Id)is not { } machine)
            return;
        var id = world.Characters.IdOf(slot);
        var errand = new Errand
        {
            Kind = ErrandKind.Corpse,
            Body = order.Target,
            Command = order.SourceCommand as DisposeCorpseOrder,
            Ordered = true,
            Control = world.Players.ControlOf(id)
        };
        BindDisposal(world, errand, site, machine);
        _errands[id] = errand;
        TaskStart.Stand(world.Characters, slot);
        StepErrand(world, slot, world.Characters.Cold(slot)!, errand);
    }

    void BindDisposal(World world, Errand errand, BuildSite site, Machine machine)
    {
        errand.Site = site;
        errand.Machine = machine;
        errand.Owner = site.Owner;
        errand.SiteRevision = world.BuildingRevision(site);
        errand.MembershipRevision = errand.Control is { } grant ? world.Players.Find(grant.Player)?.MembershipRevision(site.Owner) ?? 0 : 0;
    }

    public void CloseCorpseErrands(World world, PlayerId player)
    {
        foreach (var(actor, errand)in _errands.ToArray())
        {
            if (errand.Control?.Player != player)
                continue;
            CancelErrand(world, actor, errand);
        }
    }

    void CancelErrand(World world, CharacterId actor, Errand errand)
    {
        var table = world.Characters;
        if (table.TryResolveNext(actor, out int slot))
        {
            var cold = table.Cold(slot)!;
            if (errand.Ordered && cold.Running is { Row: TaskType.FeedCorpseIntoMachine })
            {
                TaskStart.End(world, slot, TaskEnd.Stopped);
                return;
            }

            if (table.Next[slot].Task == (byte)CharacterTask.PickUp && cold.CarryTarget == errand.Body)
                Carry!.CancelInstruction(world, slot);
            else if (errand.Walking && table.Next[slot].Task == (byte)CharacterTask.GoTo)
                MovementSystem.Stop(table, slot);
        }

        _errands.Remove(actor);
    }

    void ValidateErrands(World world)
    {
        foreach (var(actor, errand)in _errands.ToArray())
        {
            var table = world.Characters;
            if (!table.TryResolveNext(actor, out int slot))
            {
                _errands.Remove(actor);
                continue;
            }

            var cold = table.Cold(slot)!;
            bool valid = !world.Players.RequiresExplicitControl || errand.Control is { } grant && world.Players.Validate(grant) && (errand.Command is null || world.MayOrder(errand.Command, actor));
            if (valid && errand.Site is { } site)
            {
                valid = ReferenceEquals(Site(world, site.Id), site) && ReferenceEquals(world.Machines.Find(site.Id), errand.Machine) && site.Owner == errand.Owner && site.Complete && MayWork(world, cold, site) && world.BuildingRevision(site) == errand.SiteRevision;
                if (valid && errand.Control is { } control)
                    valid = world.Players.Find(control.Player)is { } player && player.HasPermission(site.Owner, FactionPermission.ManageAssets) && player.MembershipRevision(site.Owner) == errand.MembershipRevision;
            }

            if (valid && errand.Kind != ErrandKind.Corpse && !world.MayUseAssets(cold, cold.Faction))
                valid = false;
            if (valid && (!cold.Carrying.IsNone && cold.Carrying != errand.Body || cold.Medical is not { Incapacitated: false } || !cold.CarriedBy.IsNone))
                valid = false;
            if (valid)
                continue;
            // Losing authority cancels work; the existing carried entity and its items remain linked.
            CancelErrand(world, actor, errand);
        }
    }

    /// <summary>The player's town nearest to a point by centre, if its squared distance is within <paramref name = "reachSquared"/>.</summary>
    static PlayerTown? NearestPlayerTown(World world, Vector3 at, float reachSquared, int faction)
    {
        PlayerTown? best = null;
        float bestD = reachSquared;
        foreach (var town in world.Buildings.Towns)
        {
            float d = Vector3.DistanceSquared(town.Position, at);
            if (town.Owner == faction && d <= bestD)
                (best, bestD) = (town, d);
        }

        return best;
    }

    /// <summary>The player's town (of the player's faction) a point lies in: the nearest by centre within its radius, or null.</summary>
    static PlayerTown? PlayerTownAt(World world, Vector3 at, int faction)
    {
        PlayerTown? best = null;
        float bestD = float.MaxValue;
        foreach (var town in world.Buildings.Towns)
        {
            float d = Gap(town.Position, at);
            if (town.Owner == faction && d <= town.Radius && d < bestD)
                (best, bestD) = (town, d);
        }

        return best;
    }

    public bool InPlayerBuilding(World world, Vector3 at, int faction) => EnclosedAt(world, at, faction)is not null;
    /// <summary>The player's complete building with doors whose footprint holds a point (the nearest by origin), or null.</summary>
    BuildSite? EnclosedAt(World world, Vector3 at, int faction)
    {
        BuildSite? best = null;
        float bestGap = float.MaxValue;
        var sites = world.Buildings.All;
        for (int i = 0; i < sites.Count; i++) // indexed: asked per character in the publish, a boxed enumerator each time
        {
            var site = sites[i];
            float radius = Settings.Footprint?.Invoke(site.Record) ?? 0;
            if (!site.Complete || site.Owner != faction || radius <= 0)
                continue;
            float gap = Gap(site.Position, at);
            if (gap > radius || gap >= bestGap || !world.Fixtures.OfBuilding(site.Id).Any(f => f.Kind == FixtureKind.Door))
                continue;
            (best, bestGap) = (site, gap);
        }

        return best;
    }

    static bool MayEditJobs(World world, JobListCommand command, CharacterId character) => world.MayOrder(command, character) && (command.Issuer is null && command.JobsRevision == 0 || command.JobsRevision != 0 && world.JobsRevision(character) == command.JobsRevision);
    /// <summary>The player's complete building nearest <paramref name = "at"/> within <paramref name = "range"/> whose machine passes <paramref name = "ok"/> (ties: the lower site id).</summary>
    string? NearestSite(World world, Vector3 at, float range, Func<BuildSite, Machine, bool> ok, int faction)
    {
        string? best = null;
        float bestGap = float.MaxValue;
        foreach (var m in world.Machines.All)
        {
            if (Site(world, m.Site)is not { Complete: true } site || site.Owner != faction || world.Players.RequiresExplicitControl && !WorkSiteOpen(world, site))
                continue;
            float gap = Gap(site.Position, at);
            if (gap > range || gap > bestGap || (gap == bestGap && string.CompareOrdinal(m.Site, best) > 0) || !ok(site, m))
                continue;
            best = m.Site;
            bestGap = gap;
        }

        return best;
    }

    /// <summary>
    /// Saved faction claims awaiting a natural site's first resolution.
    /// </summary>
    readonly Dictionary<string, int> _naturalClaims = new(StringComparer.Ordinal);

    /// <summary>
    /// Every known rock owner, including saved machines whose foliage zone has not been placed yet.
    /// </summary>
    internal IEnumerable<(string Id, int Owner)> NaturalClaims => _naturalClaims
        .Concat(_restoredRocks.Where(pair => pair.Value.Owner >= 0).Select(pair => new KeyValuePair<string, int>(pair.Key, pair.Value.Owner)))
        .Concat(_naturalSites.Where(pair => pair.Value is { Owner: >= 0 }).Select(pair => new KeyValuePair<string, int>(pair.Key, pair.Value!.Owner)))
        .GroupBy(pair => pair.Key, StringComparer.Ordinal)
        .OrderBy(group => group.Key, StringComparer.Ordinal)
        .Select(group => (group.Key, group.Last().Value));

    /// <summary>
    /// Restores the stable faction claim before any saved machine or queued order resolves the rock.
    /// </summary>
    internal void RestoreNaturalClaim(World world, string id, int owner)
    {
        if (owner >= 0 && IsNatural(id)) _naturalClaims.TryAdd(id, owner);
    }

    /// <summary>
    /// Applies the checkpoint's owner when the resource first becomes available.
    /// </summary>
    void ApplyNaturalClaim(World world, BuildSite site)
    {
        if (!_naturalClaims.TryGetValue(site.Id, out int owner) || !world.IsPlayerFaction(owner)) return;

        site.Owner = owner;
        _naturalClaims.Remove(site.Id);
    }

    /// <summary>
    /// The natural site after its rock source has placed the zone, retaining the saved owner, player town and work state.
    /// </summary>
    BuildSite? Natural(World world, string id)
    {
        if (!IsNatural(id)) return null;
        if (_naturalSites.TryGetValue(id, out var known))
        {
            if (known is not null) ApplyNaturalClaim(world, known);
            return known;
        }
        if (ParseNatural(id) is not { } site) return _naturalSites[id] = null;

        // An unloaded foliage zone is not a missing rock and must not discard its saved machine.
        if (_nodes is not null)
        {
            var at = site.Position;
            if (_nodes.Near(0, at.X, at.Z, 1).FirstOrDefault(n => n.Record == site.Record) is not { Record.Length: > 0 } rock) return null;
            _naturalBounds[id] = new RockBounds(rock.Centre ?? rock.Position, rock.Radius, rock.Shape, rock.Transform);
        }

        if (_restoredRocks.Remove(id, out var restored)) site = restored;
        ApplyNaturalClaim(world, site);
        return _naturalSites[id] = site;
    }

    /// <summary>
    /// The store of each battery moves by flow / rate x dt seconds, between 0 and the capacity (<c>FarmBuilding::vfunc_27</c> on batteries); the town's charge totals follow.
    /// </summary>
    void ChargeBatteries(World world, (int Town, int Owner) town, List<Machine> grid, float dt)
    {
        float charge = 0, capacity = 0;
        foreach (var b in grid)
        {
            if (!b.Data.IsBattery)
                continue;
            float rate = Rated(world, b);
            b.Store = Capacity(world, b);
            if (rate > 0)
                b.Charge = Math.Clamp(b.Charge + b.Granted / rate * dt, 0, b.Store);
            (charge, capacity) = (charge + b.Charge, capacity + b.Store);
        }

        if (_townPower.TryGetValue(town, out var power))
            _townPower[town] = power with
            {
                Charge = charge,
                Capacity = capacity
            };
    }

    /// <summary>
    /// One town's frame (<b>Observed</b>, <c>Town::vfunc_87</c>): generator output is shared among the consumers in ascending demand, each taking at most an equal share of what is
    /// left (<c>FUN_140927350</c> sorts); when batteries were in use last tick, consumers left short that get battery power share the batteries' output. With nothing left over the
    /// batteries drain by what was drawn; with a surplus they charge from it (each at most its rate) and the grid leaves battery mode.
    /// </summary>
    static (int Town, int Owner) GridKey(World world, int town, int owner) => (town, world.Players.RequiresExplicitControl ? owner : -1);
    void Share(World world, (int Town, int Owner) town, List<Machine> grid)
    {
        bool mode = _batteryMode.GetValueOrDefault(town);
        float supply = 0, batteries = 0, demandTotal = 0;
        var consumers = new List<Machine>();
        foreach (var m in grid)
        {
            m.Granted = 0;
            if (m.Data.IsGenerator)
                supply += m.Demand = Output(world, m);
            else if (m.Data.IsBattery)
            {
                m.Demand = m.PowerOn && m.Charge > 0 ? m.Health * Rated(world, m) : 0;
                batteries += m.Demand;
            }
            else
            {
                // A farm wants its whole draw whoever works it and whether or not it is switched on (FarmBuilding slot 163, FUN_1400df330: |power output + increase|).
                float share = m.Data.Farm is null && m.Data.PowerOperators > 0 ? (float)m.LastOperators / m.Data.PowerOperators : 1;
                m.Demand = (m.Data.Farm is not null || m.PowerOn) && m.Active ? MathF.Abs(Rated(world, m)) * share : 0;
                demandTotal += m.Demand;
                consumers.Add(m);
            }
        }

        float generated = supply;
        _townSupply[town] = (generated > 0, batteries > 0);
        consumers.Sort((a, b) => a.Demand != b.Demand ? a.Demand.CompareTo(b.Demand) : string.CompareOrdinal(a.Site, b.Site));
        int left = consumers.Count, short_ = 0;
        foreach (var c in consumers)
        {
            float share = supply / Math.Max(1, left--);
            float give = MathF.Min(share, c.Demand);
            supply -= give;
            c.Granted = give;
            if (give < c.Demand && c.GetsBattery)
                short_++;
        }

        float batteryLeft = batteries;
        if (mode && short_ > 0)
            foreach (var c in consumers)
            {
                if (!c.GetsBattery || c.Granted >= c.Demand)
                    continue;
                float give = MathF.Min(c.Demand - c.Granted, batteryLeft / Math.Max(1, short_--));
                batteryLeft -= give;
                c.Granted += give;
            }

        float charged = 0, drained = 0;
        if (supply <= 0)
        {
            float used = drained = batteries - batteryLeft;
            foreach (var b in grid)
                if (b.Data.IsBattery)
                    b.Granted = used > 0 && batteries > 0 ? -used * b.Demand / batteries : 0;
            _batteryMode[town] = true;
        }
        else
        {
            _batteryMode[town] = false;
            charged = ChargeFromSurplus(world, grid, supply, batteries: grid.Count(b => b.Data.IsBattery));
        }

        foreach (var m in grid)
        {
            if (m.Data.IsConsumer)
                m.Power = m.Demand > 0 ? m.Granted / m.Demand : 1;
            else if (m.Data.IsGenerator)
            {
                // The load (GeneratorBuilding::vfunc_167): output / rated x (charging + consumer demand) / all generators' output, at most 1.
                float rated = Rated(world, m);
                m.Usage = generated > 0 && rated > 0 ? MathF.Min(1, m.Demand / rated * (charged + demandTotal) / generated) : 0;
            }
        }

        _townPower[town] = new(generated, demandTotal, charged, drained, 0, 0);
    }

    /// <summary>
    /// The window's content for a finished prospecting (<b>Verified</b>/<b>Observed</b> per docs/game/ui-screens.md "Prospecting"), read from the ground at the prospector's feet; the wind is
    /// the spot's weather region's at this moment (<c>FUN_140785f50</c>, read once when the window fills).
    /// </summary>
    void PublishProspect(World world, PlayerState recipient, CharacterCold prospector, System.Numerics.Vector3 at)
    {
        float skill = prospector.Stats?[StatsEnumerated.Science] ?? 0;
        var environment = new List<ProspectLine>(3);
        var(green, arid, swamp) = _resources.Coverage(at.X, at.Z);
        if (green > 0)
            environment.Add(new("Green", green));
        if (arid > 0)
            environment.Add(new("Arid", arid));
        if (swamp > 0)
            environment.Add(new("Swamp", swamp));
        var found = new ProspectLine[ProspectResources.Length];
        for (int k = 0; k < found.Length; k++)
            found[k] = new(ProspectResources[k].Name, _resources.Yield(ProspectResources[k].Resource, at));
        recipient.Prospected = new ProspectReport(checked((recipient.Prospected?.Serial ?? 0) + 1), at, prospector.Name, (int)skill, _resources.ZoneName(at.X, at.Z) ?? "", (int)at.Y, (int)((skill * 0.01f * 6000 + 2000) * 0.2f), environment, found, SampleProspectMap(world, at, skill * 0.01f), world.Weather?.At(at.X, at.Z)?.WindSpeed ?? 0);
    }

    /// <summary>Lazily creates the machine of a registered town placement with its resolved owner.</summary>
    public Machine? TownWork(World world, TownBuilding building)
    {
        if (Data(building.BuildingId)is null)
            return null;
        var site = world.Buildings.Find(building.Key) ?? world.Fixtures.GetTownMachineSite(world, building);
        return site is { Complete: true, Destroyed: false } && site.Record == building.BuildingId ? Ensure(world, site) : null;
    }

    void PrepareTownWorkers(World world)
    {
        var table = world.Characters;
        for (int i = 0; i < table.HighWater; i++)
            if (table.Next[i].Alive && table.Cold(i)is { Ai: { Behaviour: Ai.AiBehaviour.Work } mind } cold && (mind.WorkOperate ?? mind.CollectFrom ?? mind.FillTo)is { } building && Able(cold) && Ai.AiSystem.Holds(table.Next[i], cold) && AtTownWork(table.Next[i], world.Buildings.Find(building.Key)?.Position ?? building.Position))
                TownWork(world, building);
    }

    bool AtTownWork(in CharacterHot n, Vector3 position)
    {
        float dx = n.Position.X - position.X, dz = n.Position.Z - position.Z;
        return dx * dx + dz * dz <= Ai.AiSystem.WorkReach * Ai.AiSystem.WorkReach * 1.5f * 1.5f && (CharacterTask)n.Task is CharacterTask.Idle or CharacterTask.Rest;
    }

    /// <summary>Room for one freshly produced unit in the character's main grid or worn backpack.</summary>
    internal static bool RoomForOutput(Inventory inventory, ItemInstance unit)
    {
        static bool Room(Inventory grid, ItemInstance item, string section)
        {
            int limit = grid.LimitOf(item);
            return limit > 1 && grid.Items.Any(other => other.Section is ("main" or "backpack_content") && other.Record == item.Record && other.Quality == item.Quality && other.StolenFrom == item.StolenFrom && other.Death == item.Death && other.Unique == item.Unique && other.Contents is null && other.Buyback is null && other.Quantity < limit) || grid.FindCell(section, item.Width, item.Height, out _, out _);
        }

        return Room(inventory, unit, "main") || inventory.Backpack?.Contents is { } bag && Room(bag, unit, "backpack_content");
    }
}
