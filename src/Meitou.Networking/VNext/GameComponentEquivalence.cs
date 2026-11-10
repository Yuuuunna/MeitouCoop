using System.Globalization;
using Meitou.Data.Characters;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation;
using Meitou.Simulation.Crime;

namespace Meitou.Networking.VNext;

/// <summary>Visual and UI-precision equivalence checks against the last transmitted component value.
/// Baseline/spawn always transmits bit-exact values; these predicates only suppress sub-pixel / sub-digit
/// floating-point drift on subsequent delta publications.</summary>
internal static class GameComponentEquivalence
{
    const float MphFactor = 0.22369362f;
    const float KmhFactor = 0.36f;

    static int BarStep(float value) => (int)MathF.Round(Math.Clamp(value, 0f, 1f) * 1024f);

    public static bool SameBuild(BodyBuild old, BodyBuild cur) =>
        (old.Starvation <= 0f) == (cur.Starvation <= 0f) &&
        (old.Starvation >= 1f) == (cur.Starvation >= 1f) &&
        (old.Starvation > 0.25f) == (cur.Starvation > 0.25f) &&
        (old.Starvation > old.Definition) == (cur.Starvation > cur.Definition) &&
        (old.Definition <= 0.33f) == (cur.Definition <= 0.33f) &&
        MathF.Abs(old.Muscle - cur.Muscle) <= 0.002f &&
        MathF.Abs(old.ShapeMuscle - cur.ShapeMuscle) <= 0.002f &&
        MathF.Abs(old.Definition - cur.Definition) <= 0.005f &&
        MathF.Abs(old.Starvation - cur.Starvation) <= 0.005f &&
        MathF.Abs(old.ShapeStarvation - cur.ShapeStarvation) <= 0.005f;

    public static bool SameBody(BodyStatus old, BodyStatus cur, bool detailed)
    {
        if (old.Care != cur.Care ||
            old.Unconscious != cur.Unconscious ||
            old.Dead != cur.Dead ||
            old.NeedsAid != cur.NeedsAid ||
            old.MedicJob != cur.MedicJob ||
            old.RescueJob != cur.RescueJob ||
            old.NeedsSplint != cur.NeedsSplint ||
            old.PlayingDead != cur.PlayingDead ||
            old.StayingLow != cur.StayingLow ||
            old.NeedsRobotAid != cur.NeedsRobotAid ||
            old.NeedsOrganicAid != cur.NeedsOrganicAid ||
            old.NeedsRepairs != cur.NeedsRepairs ||
            old.BeingEaten != cur.BeingEaten ||
            old.Coma != cur.Coma ||
            old.Crawling != cur.Crawling ||
            old.RightArmDisabled != cur.RightArmDisabled)
            return false;

        if ((old.Blood <= 0f) != (cur.Blood <= 0f) ||
            (old.Blood >= 1f) != (cur.Blood >= 1f) ||
            (old.WorstPart < 0f) != (cur.WorstPart < 0f) ||
            (old.WorstPart < 0.7f) != (cur.WorstPart < 0.7f) ||
            (old.WorstPart < 0.9f) != (cur.WorstPart < 0.9f) ||
            (old.WorstPart >= 1f) != (cur.WorstPart >= 1f) ||
            (old.Hunger <= 0f) != (cur.Hunger <= 0f) ||
            (old.Hunger < 1f) != (cur.Hunger < 1f) ||
            (old.Hunger <= 1f) != (cur.Hunger <= 1f) ||
            (old.Hunger < 1.25f) != (cur.Hunger < 1.25f) ||
            (old.Hunger < 2f) != (cur.Hunger < 2f) ||
            (old.Hunger <= 2f) != (cur.Hunger <= 2f) ||
            (old.KoTimer > 0f) != (cur.KoTimer > 0f) ||
            (int)old.KoTimer != (int)cur.KoTimer ||
            (cur.KoTimer <= 0f && MathF.Abs(old.KoTimer - cur.KoTimer) > 0.001f))
            return false;

        if (!detailed)
        {
            return MathF.Abs(old.Blood - cur.Blood) <= 0.01f &&
                   MathF.Abs(old.WorstPart - cur.WorstPart) <= 0.01f &&
                   MathF.Abs(old.Hunger - cur.Hunger) <= 0.025f;
        }

        return BarStep(old.Blood) == BarStep(cur.Blood) &&
               (int)(old.Blood * 100f) == (int)(cur.Blood * 100f) &&
               MathF.Abs(old.Blood - cur.Blood) <= 0.002f &&
               (int)(old.WorstPart * 100f) == (int)(cur.WorstPart * 100f) &&
               MathF.Abs(old.WorstPart - cur.WorstPart) <= 0.002f &&
               (int)(old.Hunger * 100f) == (int)(cur.Hunger * 100f) &&
               BarStep(old.Hunger - 2f) == BarStep(cur.Hunger - 2f) &&
               BarStep(old.Hunger - 1f) == BarStep(cur.Hunger - 1f) &&
               BarStep(old.Hunger) == BarStep(cur.Hunger);
    }

    public static bool SamePrison((double BaseGameHours, PrisonView Value)? old, PrisonView? cur, double currentGameHours)
    {
        if (old is null || cur is null) return old is null && cur is null;
        var (oldBase, oldVal) = old.Value;
        var curVal = cur.Value;
        if (oldVal.Locked != curVal.Locked ||
            BitConverter.SingleToInt32Bits(oldVal.Sentence) != BitConverter.SingleToInt32Bits(curVal.Sentence) ||
            oldVal.CageOwner != curVal.CageOwner ||
            oldVal.LockLevel != curVal.LockLevel ||
            oldVal.CageId != curVal.CageId)
            return false;
        double projected = currentGameHours == oldBase ? oldVal.HoursLeft : oldVal.HoursLeft - (currentGameHours - oldBase);
        return (projected > 0.0) == (curVal.HoursLeft > 0.0) &&
               (int)projected == (int)curVal.HoursLeft &&
               Math.Abs(projected - curVal.HoursLeft) <= 1e-4;
    }

    public static bool SameBounties((double BaseGameHours, BountyLine[]? Values) old, IReadOnlyList<BountyLine>? cur, double currentGameHours)
    {
        if (old.Values is null || cur is null) return old.Values is null && cur is null;
        if (old.Values.Length != cur.Count) return false;
        if (cur.Count == 0) return true;
        double elapsed = currentGameHours == old.BaseGameHours ? 0.0 : currentGameHours - old.BaseGameHours;
        for (int i = 0; i < cur.Count; i++)
        {
            var a = old.Values[i];
            var b = cur[i];
            if (a.Faction != b.Faction || a.Amount != b.Amount || a.Crimes != b.Crimes || a.Local != b.Local)
                return false;
            double projected = elapsed == 0.0 ? a.HoursLeft : a.HoursLeft - elapsed;
            if ((projected > 0.0) != (b.HoursLeft > 0.0) ||
                (int)projected != (int)b.HoursLeft ||
                Math.Abs(projected - b.HoursLeft) > 1e-4)
                return false;
        }
        return true;
    }

    public static bool SameHealthParts(HealthPartView[] old, float? oldKoPoint, IReadOnlyList<HealthPartView> cur, float? curKoPoint)
    {
        if (old.Length != cur.Count) return false;
        for (int i = 0; i < old.Length; i++)
        {
            var a = old[i];
            var b = cur[i];
            if (a.Id != b.Id || a.Name != b.Name || a.Robotic != b.Robotic || a.Limb != b.Limb ||
                a.Vital != b.Vital || a.Type != b.Type || MathF.Abs(a.Maximum - b.Maximum) > 1e-4f)
                return false;
            float maxA = Math.Max(a.Maximum, 1e-3f), maxB = Math.Max(b.Maximum, 1e-3f);
            float healthA = a.Flesh - a.Stun, healthB = b.Flesh - b.Stun;
            if ((int)MathF.Floor(healthA + 0.1f) != (int)MathF.Floor(healthB + 0.1f))
                return false;
            float greenA = healthA / maxA, greenB = healthB / maxB;
            if (BarStep(Math.Max(greenA, 0f)) != BarStep(Math.Max(greenB, 0f)) ||
                BarStep(Math.Max((healthA + a.Bandage) / maxA, 0f)) != BarStep(Math.Max((healthB + b.Bandage) / maxB, 0f)) ||
                BarStep(Math.Max((maxA - a.Stun) / maxA, 0f)) != BarStep(Math.Max((maxB - b.Stun) / maxB, 0f)) ||
                BarStep(Math.Clamp(greenA + a.Rig * 0.01f, 0f, 1f)) != BarStep(Math.Clamp(greenB + b.Rig * 0.01f, 0f, 1f)))
                return false;

            float leftA = a.Flesh + a.Bandage - a.Stun, leftB = b.Flesh + b.Bandage - b.Stun;
            float sumA = a.Flesh + a.Bandage, sumB = b.Flesh + b.Bandage;
            if ((leftA <= -a.Maximum) != (leftB <= -b.Maximum) ||
                (sumA < 0f) != (sumB < 0f) ||
                (oldKoPoint.HasValue && sumA >= oldKoPoint.Value) != (curKoPoint.HasValue && sumB >= curKoPoint.Value) ||
                (a.Maximum - sumA > 20f) != (b.Maximum - sumB > 20f) ||
                (a.Maximum * 0.95f - a.Stun <= sumA) != (b.Maximum * 0.95f - b.Stun <= sumB) ||
                (a.Bandage + a.Stun <= 5f) != (b.Bandage + b.Stun <= 5f) ||
                (leftA > 0f && leftA < 60f) != (leftB > 0f && leftB < 60f))
                return false;
        }
        return true;
    }

    public static bool SameItemGrid(InventoryView? old, InventoryView? cur)
    {
        if (old is null || cur is null) return old is null && cur is null;
        if (old.Revision != cur.Revision ||
            old.Width != cur.Width || old.Height != cur.Height ||
            old.BagWidth != cur.BagWidth || old.BagHeight != cur.BagHeight ||
            MathF.Abs(old.TotalWeight - cur.TotalWeight) > 0.001f ||
            old.CarryCapacity.HasValue != cur.CarryCapacity.HasValue)
            return false;
        if (old.CarryCapacity is { } oldCap && cur.CarryCapacity is { } curCap &&
            ((int)oldCap != (int)curCap || MathF.Abs(oldCap - curCap) > 0.25f))
            return false;
        int oldBurdenPct = (int)((1f - Math.Clamp(old.Burden, 0f, 1f)) * 100f);
        int curBurdenPct = (int)((1f - Math.Clamp(cur.Burden, 0f, 1f)) * 100f);
        if (oldBurdenPct != curBurdenPct ||
            (old.Burden >= 1f) != (cur.Burden >= 1f) ||
            MathF.Abs(old.Burden - cur.Burden) > 0.005f)
            return false;
        return old.Items.SequenceEqual(cur.Items);
    }

    public static bool SameHud(HudView? old, HudView? cur)
    {
        if (old is null || cur is null) return old is null && cur is null;
        if (old.Robot != cur.Robot ||
            (old.Fed > 0f) != (cur.Fed > 0f) ||
            (old.BleedRate > 1f) != (cur.BleedRate > 1f) ||
            (old.BleedRate < 1f) != (cur.BleedRate < 1f) ||
            MathF.Round(old.BleedRate * 100f) != MathF.Round(cur.BleedRate * 100f))
            return false;

        float capA = Math.Max(old.BloodCapacity, 1e-3f), capB = Math.Max(cur.BloodCapacity, 1e-3f);
        if (MathF.Abs(old.BloodCapacity - cur.BloodCapacity) > 0.01f ||
            (int)MathF.Floor(old.Blood + 0.1f) != (int)MathF.Floor(cur.Blood + 0.1f) ||
            BarStep(Math.Max(old.Blood / capA, 0f)) != BarStep(Math.Max(cur.Blood / capB, 0f)))
            return false;

        int bleedArrowsA = Math.Min(100, (int)MathF.Ceiling(MathF.Abs(old.Bleeding) / 0.05f));
        int bleedArrowsB = Math.Min(100, (int)MathF.Ceiling(MathF.Abs(cur.Bleeding) / 0.05f));
        if (bleedArrowsA != bleedArrowsB || MathF.Sign(old.Bleeding) != MathF.Sign(cur.Bleeding))
            return false;

        int encA = (int)((1f - Math.Clamp(old.Encumbrance, 0f, 1f)) * 100f);
        int encB = (int)((1f - Math.Clamp(cur.Encumbrance, 0f, 1f)) * 100f);
        if (encA != encB || MathF.Abs(old.Encumbrance - cur.Encumbrance) > 0.005f)
            return false;

        if (old.KoPoint.HasValue != cur.KoPoint.HasValue ||
            (old.KoPoint is { } koA && cur.KoPoint is { } koB && ((int)koA != (int)koB || MathF.Abs(koA - koB) > 0.05f)))
            return false;

        if (!SameStatLines(old.Stats, cur.Stats) ||
            !SameExtended(old.Extended, cur.Extended) ||
            !SameExtendedTips(old.ExtendedTips, cur.ExtendedTips) ||
            !SameSkill(old.Skill, cur.Skill) ||
            !SameStatReadouts(old.StatReadouts, cur.StatReadouts))
            return false;

        return true;
    }

    static bool SameStatLines(IReadOnlyList<HudStatLine> a, IReadOnlyList<HudStatLine> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            var x = a[i];
            var y = b[i];
            if (x.Label != y.Label || x.Text != y.Text ||
                (int)x.Value != (int)y.Value || (int)x.Base != (int)y.Base ||
                !x.Tip.SequenceEqual(y.Tip))
                return false;
        }
        return true;
    }

    static bool SameSpeedUnits(float a, float b) =>
        (int)(a * MphFactor) == (int)(b * MphFactor) &&
        (int)(a * KmhFactor) == (int)(b * KmhFactor) &&
        (int)MathF.Round(a * MphFactor) == (int)MathF.Round(b * MphFactor) &&
        (int)MathF.Round(a * KmhFactor) == (int)MathF.Round(b * KmhFactor);

    static bool SameExtended(ExtendedStats? old, ExtendedStats? cur)
    {
        if (old is null || cur is null) return old is null && cur is null;
        var a = old.Value;
        var b = cur.Value;
        if (a.Resting != b.Resting || a.Fighting != b.Fighting || a.Fists != b.Fists || a.Idle != b.Idle ||
            a.WorkHungerRate.HasValue != b.WorkHungerRate.HasValue ||
            (a.WorkHungerRate is { } wa && b.WorkHungerRate is { } wb && MathF.Round(wa * 100f) != MathF.Round(wb * 100f)))
            return false;
        return (int)(a.AthleticsXp * 100f) == (int)(b.AthleticsXp * 100f) &&
               (int)(a.StrengthXp * 100f) == (int)(b.StrengthXp * 100f) &&
               (int)a.ToughnessXp == (int)b.ToughnessXp &&
               (int)a.KoPoint == (int)b.KoPoint &&
               MathF.Abs(a.KoPoint - b.KoPoint) <= 0.05f &&
               SameSpeedUnits(a.RunSpeed, b.RunSpeed) &&
               SameSpeedUnits(a.RunValue, b.RunValue) &&
               (int)MathF.Round((a.RunSpeed - a.RunValue) * MphFactor) == (int)MathF.Round((b.RunSpeed - b.RunValue) * MphFactor) &&
               (int)MathF.Round((a.RunSpeed - a.RunValue) * KmhFactor) == (int)MathF.Round((b.RunSpeed - b.RunValue) * KmhFactor) &&
               (int)(a.HungerRate * 100f) == (int)(b.HungerRate * 100f);
    }

    static bool SameExtendedTips(ExtendedTips? a, ExtendedTips? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if ((int)a.StoredAthletics != (int)b.StoredAthletics ||
            (int)((1f - a.LegFactor) * 100f) != (int)((1f - b.LegFactor) * 100f) ||
            (int)((a.RoboticLegs - 1f) * 100f) != (int)((b.RoboticLegs - 1f) * 100f) ||
            (a.Gear > 1f) != (b.Gear > 1f) ||
            (a.Gear < 1f) != (b.Gear < 1f) ||
            (int)(MathF.Abs(a.Gear - 1f) * 100f) != (int)(MathF.Abs(b.Gear - 1f) * 100f) ||
            a.SneakCap.HasValue != b.SneakCap.HasValue ||
            (a.SneakCap is { } sa && b.SneakCap is { } sb && !SameSpeedUnits(sa, sb)))
            return false;
        return a.Athletics.SequenceEqual(b.Athletics) &&
               a.Strength.SequenceEqual(b.Strength) &&
               a.Toughness.SequenceEqual(b.Toughness) &&
               a.Hunger.SequenceEqual(b.Hunger);
    }

    static bool SameSkill(CurrentSkillView? old, CurrentSkillView? cur)
    {
        if (old is null || cur is null) return old is null && cur is null;
        var a = old.Value;
        var b = cur.Value;
        if (a.Stat != b.Stat || a.Label != b.Label || a.Penalised != b.Penalised || a.Speed != b.Speed ||
            a.Base.HasValue != b.Base.HasValue || a.Damage.HasValue != b.Damage.HasValue ||
            a.Terms.HasValue != b.Terms.HasValue || a.Accuracy.HasValue != b.Accuracy.HasValue ||
            a.Stealth.HasValue != b.Stealth.HasValue || a.Athletics.HasValue != b.Athletics.HasValue ||
            a.Swim.HasValue != b.Swim.HasValue)
            return false;

        bool valueSame = a.Speed ? SameSpeedUnits(a.Value, b.Value)
            : a.Accuracy.HasValue ? MathF.Round(a.Value * 100f) == MathF.Round(b.Value * 100f)
            : (int)a.Value == (int)b.Value;
        if (!valueSame) return false;
        if (a.Base is { } ba && b.Base is { } bb && (int)ba != (int)bb) return false;
        if (a.Damage is { } da && b.Damage is { } db && (int)da != (int)db) return false;
        if (a.Terms is { } ta && b.Terms is { } tb &&
            ((int)ta.Base != (int)tb.Base || ta.Injuries != tb.Injuries || ta.Hunger != tb.Hunger ||
             ta.Weather != tb.Weather || ta.Darkness != tb.Darkness || ta.Robotics != tb.Robotics ||
             ta.HasRobotics != tb.HasRobotics || ta.Gear != tb.Gear || ta.MissingArm != tb.MissingArm ||
             ta.ArmourBonus != tb.ArmourBonus || (int)ta.Total != (int)tb.Total))
            return false;
        if (a.Accuracy is { } aa && b.Accuracy is { } ab &&
            (MathF.Round(aa.Weapon * 100f) != MathF.Round(ab.Weapon * 100f) ||
             MathF.Round(aa.Skill) != MathF.Round(ab.Skill) ||
             MathF.Round(aa.Deviation * 100f) != MathF.Round(ab.Deviation * 100f)))
            return false;
        if (a.Stealth is { } sa && b.Stealth is { } sb &&
            ((int)sa.Skill != (int)sb.Skill || !SameSpeedUnits(sa.MaxSpeed, sb.MaxSpeed) ||
             (int)(sa.XpRate * 100f) != (int)(sb.XpRate * 100f) || sa.Carrying != sb.Carrying ||
             sa.Crawling != sb.Crawling || sa.Encumbrance != sb.Encumbrance || sa.Injuries != sb.Injuries ||
             sa.Gear != sb.Gear || sa.Robotics != sb.Robotics || (int)sa.Total != (int)sb.Total))
            return false;
        if (a.Athletics is { } ata && b.Athletics is { } atb &&
            ((int)ata.Skill != (int)atb.Skill || !SameSpeedUnits(ata.MaxSpeed, atb.MaxSpeed) ||
             ata.LegInjury != atb.LegInjury || ata.Encumbrance != atb.Encumbrance ||
             (ata.Gear > 1f) != (atb.Gear > 1f) || (ata.Gear < 1f) != (atb.Gear < 1f) ||
             (int)(MathF.Abs(ata.Gear - 1f) * 100f) != (int)(MathF.Abs(atb.Gear - 1f) * 100f) ||
             ata.Robotics != atb.Robotics || !SameSpeedUnits(ata.TotalSpeed, atb.TotalSpeed)))
            return false;
        if (a.Swim is { } swa && b.Swim is { } swb &&
            ((int)swa.Skill != (int)swb.Skill || !SameSpeedUnits(swa.MaxSpeed, swb.MaxSpeed) ||
             swa.Injuries != swb.Injuries || swa.Hunger != swb.Hunger || swa.Encumbrance != swb.Encumbrance ||
             swa.Robotics != swb.Robotics || swa.BrokenArms != swb.BrokenArms || !SameSpeedUnits(swa.Speed, swb.Speed)))
            return false;

        return true;
    }

    static bool SameStatReadouts(
        IReadOnlyDictionary<StatsEnumerated, IReadOnlyList<StatReadout>>? a,
        IReadOnlyDictionary<StatsEnumerated, IReadOnlyList<StatReadout>>? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a.Count != b.Count) return false;
        foreach (var (stat, rowsA) in a)
        {
            if (!b.TryGetValue(stat, out var rowsB) || rowsA.Count != rowsB.Count)
                return false;
            for (int i = 0; i < rowsA.Count; i++)
            {
                if (!SameStatReadout(rowsA[i], rowsB[i])) return false;
            }
        }
        return true;
    }

    static bool SameStatReadout(StatReadout a, StatReadout b)
    {
        if (a == b) return true;
        if (a.Label != b.Label || a.Main != b.Main) return false;
        if (a.Label == "Dodge XP rate:" && a.Value.EndsWith('%') && b.Value.EndsWith('%') &&
            TryParsePercent(a.Value, out double pa) && TryParsePercent(b.Value, out double pb))
            return (int)pa == (int)pb && Math.Abs(pa - pb) <= 0.1;
        return false;
    }

    static bool TryParsePercent(string text, out double value)
    {
        var span = text.AsSpan(0, text.Length - 1).Trim();
        if (double.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return true;
        return double.TryParse(span, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
    }
}
