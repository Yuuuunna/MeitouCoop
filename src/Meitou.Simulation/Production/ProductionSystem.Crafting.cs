using Meitou.Data.Gameplay.Construction;
using Meitou.Simulation.Items;

namespace Meitou.Simulation.Production;

/// <summary>
/// Crafting benches (docs/game/buildings-production.md "Crafting", docs/simulation.md "Production"): a queue per bench, worked by its operators; each craft binds the bench's input sections
/// to its ingredients, draws them as the work goes, and puts the finished item into the bench's output.
/// </summary>
public sealed partial class ProductionSystem
{
    /// <summary>How many crafts a bench's queue holds (engine choice; the original's limit is Unknown).</summary>
    public const int MaxCraftQueue = 20;

    HashSet<(CraftKind, string)>? everEnabled;

    /// <summary>Crafts some research unlocks; anything no research lists is craftable from the start (engine reading).</summary>
    HashSet<(CraftKind, string)> EverEnabled()
    {
        if (everEnabled is not null) return everEnabled;
        allResearch ??= ResearchData.LoadAll(db);
        return everEnabled = [.. allResearch.SelectMany(r => r.EnableCrafts.Select(e => (e.Kind, e.Id)))];
    }

    bool Unlocked(World world, CraftKind kind, string id, int faction) => ResearchFor(world, faction).UnlockedCrafts.Contains((kind, id)) || !EverEnabled().Contains((kind, id));

    /// <summary>Whether the player can queue an option now: its record (and a weapon's model) unlocked, and its level within the bench's tech level.</summary>
    public bool CanCraft(World world, CraftBench bench, CraftOption option) => CanCraft(world, bench, option, world.Player.Faction);

    public bool CanCraft(World world, CraftBench bench, CraftOption option, int faction) =>
        Unlocked(world, option.Kind, option.Item, faction) && (option.Model is null || Unlocked(world, CraftKind.Weapon, option.Model, faction)) && option.Level <= bench.TechLevel;

    CraftRecipe RecipeOf(Machine m, CraftOption option) => m.Data.Craft!.Recipe(db, constants, option, m.Data.Inputs);

    void QueueCraft(World world, QueueCraft order)
    {
        if (!MayControlMachine(world, order, order.Site) || order.Issuer is not null && (order.Count <= 0 || order.Count > MaxCraftQueue)) return;
        if (world.Machines.Find(order.Site) is not { Craft: { } state, Data.Craft: { } bench } m) return;
        if (Site(world, order.Site) is not { Owner: >= 0 } site || !world.MayManage(order, site.Owner, FactionPermission.ManageAssets)) return;
        if (bench.Options.FirstOrDefault(o => o.Key == order.Option) is not { } option || !CanCraft(world, bench, option, site.Owner)) return;
        for (int i = 0; i < Math.Max(1, order.Count) && state.Queue.Count < MaxCraftQueue; i++) state.Queue.Add(new CraftJob(RecipeOf(m, option)));
        Bind(world, m, state, state.Queue[0]);
        world.Machines.Touch();
    }

    void CancelCraft(World world, CancelCraft order)
    {
        if (!MayControlMachine(world, order, order.Site)) return;
        if (world.Machines.Find(order.Site) is not { Craft: { } state } m || order.Index < 0 || order.Index >= state.Queue.Count) return;
        state.Queue.RemoveAt(order.Index);
        if (state.Queue.Count > 0) Bind(world, m, state, state.Queue[0]);
        world.Machines.Touch();
    }

    /// <summary>Units of the first finished stack the output already holds of this option (it stops the bench at the option's store).</summary>
    static int Held(CraftState state, CraftOption option) => state.Done.Where(d => d.Record == option.Item).Sum(d => d.Quantity);

    /// <summary>
    /// Sets the input sections for the first craft (<b>Observed</b>, <c>CraftingBuilding::vfunc_182</c> "setProductionItem"): section k takes ingredient k at its per-item amount;
    /// sections it does not use draw nothing. Done when the first craft changes (queued, cancelled, finished). Items of another kind in a section fall on the ground (engine choice; the
    /// original hands them back to the bench).
    /// </summary>
    void Bind(World world, Machine m, CraftState state, CraftJob job)
    {
        if (state.Bound == job.Option.Key) return;
        for (int k = 0; k < m.Inputs.Length; k++)
        {
            var slot = m.Inputs[k];
            if (slot.Item != ItemOf(m, job, k) && slot.Count > 0 && items.Create(slot.Item, slot.Count) is { } back)
            {
                world.GroundItems.Drop(back, m.Position, 0);
                slot.Count = 0;
                slot.Level = 0;
            }
        }
        Apply(m, state, job);
    }

    static string ItemOf(Machine m, CraftJob job, int k) => k < job.Recipe.Inputs.Count ? job.Recipe.Inputs[k].Item : m.Inputs[k].Recipe.Item;

    /// <summary>Sets the sections' items and rates for a craft (also on load, before the saved section items are read back).</summary>
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
    /// One operator's work on a bench (<b>Observed</b>, <c>CraftingBuilding::vfunc_150</c>): <c>rate x s x option x P x dt / seconds</c> of the first craft, clamped to the inputs; the
    /// critical is rolled when the craft first gets work; at 0.999 the item is made.
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
        float amount = m.Data.Rate * WorkFactor(world, m) * s * Settings.ProductionOption * constants.ProductionSpeed * dt / MathF.Max(1e-3f, job.Seconds);
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
    /// A finished craft (<b>Observed</b>, <c>FUN_1402bcac0</c>): armour of the crafter's tier (one better on a critical), capped at the bench's tech level, and a robotic limb or crossbow of that grade as its level; a weapon of the queued model,
    /// or when the crafter's skill is below its level and there was no critical, of the best model of the bench at or below the skill (engine reading of the model pick); other items as
    /// they are. The craft leaves the queue, and with repeat on goes back to its end.
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
                made = items.Create(option.Item, 1, Math.Min(CraftBench.ArmourQuality(skill, critical), bench.TechLevel));   // the factory gives an armour piece its level from that quality
                break;
            case CraftKind.Robotics or CraftKind.Crossbow:
                made = items.Create(option.Item, 1);
                if (made is not null) made.Level = Math.Min(CraftBench.ArmourQuality(skill, critical), bench.TechLevel);
                break;
            case CraftKind.Weapon:
                var model = option;
                if (skill < option.Level && !critical)
                    model = bench.Options.Where(o => o.Kind == CraftKind.Weapon && o.Item == option.Item && o.Level <= skill).OrderByDescending(o => o.Level).FirstOrDefault()
                        ?? bench.Options.Where(o => o.Kind == CraftKind.Weapon && o.Item == option.Item).OrderBy(o => o.Level).First();
                made = items.Create(option.Item, 1);
                if (made is not null)
                {
                    made.Level = model.Level;
                    made.MaterialId = model.Model ?? "";
                    made.CompanyId = CraftBench.PlayerWeapons;
                }
                break;
            default:
                made = items.Create(option.Item, 1);
                break;
        }
        state.Queue.RemoveAt(0);
        if (made is not null)
        {
            made.Section = "out";
            if (state.Done.FirstOrDefault(d => d.Record == made.Record && d.Quality == made.Quality && d.MaterialId == made.MaterialId && d.Quantity < d.StackLimit) is { } stack) stack.Quantity++;
            else state.Done.Add(made);
        }
        if (state.Repeat) state.Queue.Add(new CraftJob(RecipeOf(m, option)));
        world.Machines.Touch();
    }

    IReadOnlyDictionary<string, IReadOnlyList<CraftOptionView>>? publishedCrafts;
    (int Faction, long Version)[] craftsResearch = [];
    int craftsBenches = -1;

    /// <summary>Every crafting bench's options (rebuilt when research or the set of benches changed).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<CraftOptionView>> CraftsView(World world)
    {
        int benches = 0;
        foreach (var m in world.Machines.All) if (m.Craft is not null) benches++;
        var versions = world.Factions.All.Select(f => (f.Index, f.Research.Version)).ToArray();
        if (publishedCrafts is not null && craftsResearch.SequenceEqual(versions) && craftsBenches == benches) return publishedCrafts;
        craftsResearch = versions;
        craftsBenches = benches;
        var map = new Dictionary<string, IReadOnlyList<CraftOptionView>>(StringComparer.Ordinal);
        foreach (var m in world.Machines.All) if (m.Craft is not null) map[m.Site] = CraftOptions(world, m.Site);
        return publishedCrafts = map;
    }

    /// <summary>What a bench can craft, for its Craft tab: every option with its recipe and whether it can be queued now.</summary>
    public IReadOnlyList<CraftOptionView> CraftOptions(World world, string site)
    {
        if (world.Machines.Find(site) is not { Data.Craft: { } bench } m) return [];
        return [.. bench.Options.Select(o =>
        {
            var recipe = RecipeOf(m, o);
            return new CraftOptionView(o.Key, recipe.Name, o.Kind, o.Level, recipe.Hours, [.. recipe.Inputs.Select(i => (items.Info(i.Item)?.Name ?? i.Item, i.PerItem))], CanCraft(world, bench, o, Site(world, m.Site)?.Owner ?? -1));
        })];
    }

    /// <summary>A queued craft read from a save (dropped when the bench no longer offers it).</summary>
    internal void RestoreCraft(Machine m, string item, string? model, float progress, byte critical)
    {
        string key = model is null ? item : item + "|" + model;
        if (m.Craft is not { } state || m.Data.Craft?.Options.FirstOrDefault(o => o.Key == key) is not { } option) return;
        state.Queue.Add(new CraftJob(RecipeOf(m, option)) { Progress = Math.Clamp(progress, 0, 1), Critical = critical });
        if (state.Queue.Count == 1) Apply(m, state, state.Queue[0]);
    }

    /// <summary>A finished item (at most <paramref name="count"/> units, 0: all) taken from a bench's output into a character's inventory (what does not fit stays).</summary>
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
}
