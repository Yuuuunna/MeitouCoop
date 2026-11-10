using System.Buffers.Binary;
using Meitou.Networking.VNext;
using static Meitou.Tests.NetworkingNext.MotionDatagramCodecTests;

namespace Meitou.Tests.NetworkingNext;

public class StateFenceReferenceTests
{
    sealed class Grants : IStateViewAuthorization
    {
        public bool ViewAllowed = true;
        public ulong View = 2;
        public readonly HashSet<(ulong, uint)> Denied = [];
        public bool IsViewAuthorized(ulong session, ulong view) => ViewAllowed && session == 1 && view == View;
        public bool IsEntityAuthorized(ulong session, ulong view, ulong entity, uint generation) =>
            IsViewAuthorized(session, view) && !Denied.Contains((entity, generation));
    }
    static readonly ViewFence Fence = new(1, 2);
    static readonly BaselineFence Capture = new(10, 100, 20);
    static readonly ReadyFence Ready = new(10, 20);
    static StateFenceReference Staging(Grants? grants = null, int maximumHistory = 4096)
    {
        var model = new StateFenceReference(1, grants ?? new(), maximumLifecycleHistory: maximumHistory);
        Assert.Equal(FenceResult.Applied, model.BeginAuthorizedView(2, Capture));
        return model;
    }
    static StateFenceReference Active(Grants? grants = null, params MotionRecord[] records)
    {
        var model = Staging(grants);
        model.StageBaseline(Fence, Capture, records.Length == 0 ? [Motion()] : records);
        model.CommitView(Fence, Ready);
        Assert.True(model.TryGetReady(out var ready));
        Assert.Equal(Ready, ready);
        Assert.Equal(FenceResult.Applied, model.ActivateAuthorizedView(Fence, ready));
        return model;
    }
    static MotionDatagram Packet(ulong sequence, params MotionRecord[] records) => new(Header with { PacketSequence = sequence }, records);
    static MotionRecord Current(StateFenceReference model, ulong entity = 5)
    {
        Assert.True(model.TryGetMotion(entity, out var state));
        return state;
    }

    [Fact]
    public void LostFinalDatagramThenIdleConvergesFromReliableCheckpointOrAbsoluteRefresh()
    {
        foreach (bool reliable in new[] { true, false })
        {
            var model = Active();
            model.ApplyDatagram(Packet(1, Motion(revision: 8, x: 8)));
            var stopped = Motion(revision: 9, x: 9) with { Flags = MotionFlags.None, Velocity = default };
            // The final transition datagram is lost; the world produces no further gameplay input.
            if (reliable) model.ApplyReliableCheckpoint(Fence, stopped);
            else model.ApplyDatagram(Packet(3, stopped)); // Authority's idle absolute refresh.
            Assert.Equal(stopped, Current(model));
            Assert.Equal(FenceResult.Duplicate, model.ApplyReliableCheckpoint(Fence, stopped));
            model.ApplyDatagram(Packet(2, Motion(revision: 8, x: 8)));
            Assert.Equal(stopped, Current(model));
        }
    }

    [Fact]
    public void ReorderedPacketsDoNotUseAGlobalDeltaOrSequenceBaseline()
    {
        var model = Active(null, Motion(5), Motion(8));
        model.ApplyDatagram(Packet(100, Motion(5, revision: 9, x: 9)));
        model.ApplyDatagram(Packet(2, Motion(5, revision: 8, x: 8), Motion(8, revision: 10, x: 10)));
        Assert.Equal(9u, Current(model, 5).Revision);
        Assert.Equal(10u, Current(model, 8).Revision);
    }

    [Fact]
    public void CommitRequiresStagedBaselineAndAllLifecycleThroughWatermarkThenExplicitActive()
    {
        var model = Staging();
        var ready = Ready with { LifecycleWatermark = 22 };
        model.CommitView(Fence, ready);
        Assert.False(model.TryGetReady(out _));
        model.StageBaseline(Fence, Capture, [Motion()]);
        model.ApplyLifecycle(Fence, new(21, MotionLifecycleKind.Spawn, 8, 1, Motion(8, 1, 1)));
        Assert.False(model.TryGetReady(out _));
        Assert.Equal(FenceResult.Ignored, model.ActivateAuthorizedView(Fence, ready));
        Assert.Equal(FenceResult.Ignored, model.ApplyDatagram(Packet(9, Motion(revision: 99))));
        Assert.Equal(FenceResult.Ready, model.ApplyLifecycle(Fence, new(22, MotionLifecycleKind.Despawn, 8, 1)));
        Assert.True(model.TryGetReady(out var actual));
        Assert.Equal(ready, actual);
        Assert.False(model.TryGetMotion(5, out _));
        model.ActivateAuthorizedView(Fence, ready);
        Assert.Equal(7u, Current(model).Revision);
        Assert.False(model.TryGetMotion(8, out _));
    }

    [Fact]
    public void RevocationAndNewViewFenceOldBaselineAndDatagramsImmediately()
    {
        var grants = new Grants();
        var model = Active(grants);
        model.WithdrawView(Fence);
        Assert.False(model.TryGetMotion(5, out _));
        Assert.Equal(FenceResult.Ignored, model.StageBaseline(Fence, Capture, [Motion()]));
        Assert.Equal(FenceResult.Ignored, model.ActivateAuthorizedView(Fence, Ready));
        grants.View = 3;
        Assert.Equal(FenceResult.Applied, model.BeginAuthorizedView(3, Capture with { Id = 11 }));
        Assert.Equal(FenceResult.Ignored, model.StageBaseline(Fence, Capture, [Motion()]));
        Assert.Equal(FenceResult.Ignored, model.ApplyDatagram(Packet(99, Motion(revision: 99))));
        Assert.False(model.TryGetMotion(5, out _));
    }

    [Fact]
    public void NewerReliableRevisionSurvivesDelayedAndRepeatedBaselineCompletion()
    {
        var model = Staging();
        model.ApplyLifecycle(Fence, new(21, MotionLifecycleKind.Spawn, 8, 1, Motion(8, 1, 1)));
        var latest = Motion(8, 1, 3, 30);
        model.ApplyReliableCheckpoint(Fence, latest);
        model.StageBaseline(Fence, Capture, [Motion()]);
        var ready = Ready with { LifecycleWatermark = 21 };
        model.CommitView(Fence, ready);
        model.ActivateAuthorizedView(Fence, ready);
        Assert.Equal(latest, Current(model, 8));
        model.ApplyReliableCheckpoint(Fence, Motion(revision: 8, x: 80));
        model.StageBaseline(Fence, Capture, [Motion()]);
        Assert.Equal(8u, Current(model).Revision);
        Assert.Equal(latest, Current(model, 8));
    }

    [Fact]
    public void DespawnAndReentryRequireNewGenerationAndLateOldMotionCannotRecreateEntity()
    {
        var model = Active();
        model.ApplyLifecycle(Fence, new(21, MotionLifecycleKind.Despawn, 5, 6));
        model.ApplyDatagram(Packet(200, Motion(revision: 99)));
        Assert.False(model.TryGetMotion(5, out _));
        Assert.Throws<InvalidDataException>(() => model.ApplyLifecycle(Fence, new(22, MotionLifecycleKind.Spawn, 5, 6, Motion())));
        var entered = Motion(generation: 7, revision: 1, x: 100);
        model.ApplyLifecycle(Fence, new(22, MotionLifecycleKind.Spawn, 5, 7, entered));
        model.ApplyDatagram(Packet(201, Motion(revision: 1000)));
        model.ApplyReliableCheckpoint(Fence, Motion(revision: 1001));
        Assert.Equal(entered, Current(model));
        model.StageBaseline(Fence, Capture, [Motion()]);
        Assert.Equal(entered, Current(model));
    }

    [Fact]
    public void ConflictingSameRevisionRejectsWholeBatchWithoutApplyingEarlierRecords()
    {
        var model = Active(null, Motion(5), Motion(8));
        var packet = Packet(1, Motion(revision: 8), Motion(8, x: 99));
        Assert.Throws<InvalidDataException>(() => model.ApplyDatagram(packet));
        Assert.Equal(Motion(), Current(model));
        Assert.Equal(Motion(8), Current(model, 8));
        Assert.Throws<InvalidDataException>(() => model.ApplyReliableCheckpoint(Fence, Motion(x: 99)));
        Assert.Equal(Motion(), Current(model));
        Assert.Throws<InvalidDataException>(() => model.ApplyDatagram(Packet(2, Motion(revision: 8), Motion(x: 99))));
        Assert.Equal(Motion(), Current(model));
    }

    [Fact]
    public void InvalidWireInputCannotPartiallyMutateAnActiveView()
    {
        var model = Active();
        byte[] good = MotionDatagramCodec.Encode(Packet(1, Motion(revision: 8)));
        foreach (int offset in new[] { 4, 7, 48, 50, 52, 54, 62, 66, 98 })
        {
            byte[] bad = (byte[])good.Clone();
            bad[offset] = offset is 54 or 62 or 66 ? (byte)0 : (byte)255;
            Assert.Throws<InvalidDataException>(() => model.ApplyDatagram(bad));
            Assert.Equal(Motion(), Current(model));
        }
        foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            byte[] bad = (byte[])good.Clone();
            BinaryPrimitives.WriteSingleLittleEndian(bad.AsSpan(70), invalid);
            Assert.Throws<InvalidDataException>(() => model.ApplyDatagram(bad));
            Assert.Equal(Motion(), Current(model));
        }
        Assert.Throws<InvalidDataException>(() => model.ApplyDatagram(good, 91));
        Assert.Equal(Motion(), Current(model));
    }

    [Fact]
    public void ExternalAuthorizationIsRequiredAndRecheckedBeforeExposingStagedOutput()
    {
        var grants = new Grants { ViewAllowed = false };
        var model = new StateFenceReference(1, grants);
        Assert.Equal(FenceResult.Ignored, model.BeginAuthorizedView(2, Capture));
        Assert.Equal(FenceResult.Ignored, model.ApplyDatagram(Packet(1, Motion())));
        grants.ViewAllowed = true;
        model.BeginAuthorizedView(2, Capture);
        model.StageBaseline(Fence, Capture, [Motion()]);
        grants.Denied.Add((5, 6));
        model.CommitView(Fence, Ready);
        model.ActivateAuthorizedView(Fence, Ready);
        Assert.False(model.TryGetMotion(5, out _));
        Assert.Equal(FenceResult.Duplicate, model.ApplyReliableCheckpoint(Fence, Motion(revision: 99)));
        grants.ViewAllowed = false;
        Assert.Equal(ReferenceViewPhase.Withdrawn, model.Phase);
        Assert.Equal(FenceResult.Ignored, model.StageBaseline(Fence, Capture, [Motion()]));
    }

    [Fact]
    public void PacketsCannotRebindAuthenticatedSessionOrGrantANewerView()
    {
        var model = Active();
        Assert.Equal(FenceResult.Ignored, model.ApplyDatagram(new(Header with { SessionEpoch = 999 }, [Motion(revision: 100)])));
        Assert.Equal(FenceResult.Ignored, model.ApplyDatagram(new(Header with { ViewEpoch = 999 }, [Motion(revision: 100)])));
        Assert.Equal(Motion(), Current(model));
    }

    [Fact]
    public void EntityRevocationPurgesStagingAndOldBaselineOrPolicyReboundCannotResurrectIt()
    {
        foreach (bool active in new[] { true, false })
        {
            var grants = new Grants();
            var model = active ? Active(grants) : Staging(grants);
            if (!active) model.StageBaseline(Fence, Capture, [Motion()]);
            grants.Denied.Add((5, 6));
            Assert.Equal(FenceResult.Duplicate, model.StageBaseline(Fence, Capture, [Motion()]));
            grants.Denied.Clear(); // A policy rebound cannot reuse a revoked interest generation.
            model.StageBaseline(Fence, Capture, [Motion()]);
            if (!active) { model.CommitView(Fence, Ready); model.ActivateAuthorizedView(Fence, Ready); }
            Assert.False(model.TryGetMotion(5, out _));
            model.ApplyReliableCheckpoint(Fence, Motion(revision: 999));
            Assert.False(model.TryGetMotion(5, out _));
            var entered = Motion(generation: 7, revision: 1);
            model.ApplyLifecycle(Fence, new(21, MotionLifecycleKind.Spawn, 5, 7, entered));
            Assert.Equal(entered, Current(model));
        }
    }

    [Fact]
    public void OrderedLifecycleDeduplicatesAndOverflowAbandonsInsteadOfDroppingDespawn()
    {
        var model = Staging(maximumHistory: 1);
        model.StageBaseline(Fence, Capture, [Motion()]);
        var change = new MotionLifecycleChange(21, MotionLifecycleKind.Spawn, 8, 1, Motion(8, 1, 1));
        model.ApplyLifecycle(Fence, change);
        Assert.Equal(FenceResult.Duplicate, model.ApplyLifecycle(Fence, change));
        Assert.Throws<InvalidDataException>(() => model.ApplyLifecycle(Fence, change with { InitialMotion = Motion(8, 1, 1, 99) }));
        Assert.Throws<InvalidDataException>(() => model.ApplyLifecycle(Fence, new(23, MotionLifecycleKind.Despawn, 5, 6)));
        Assert.Equal(FenceResult.Abandoned, model.ApplyLifecycle(Fence, new(22, MotionLifecycleKind.Despawn, 5, 6)));
        Assert.Equal(ReferenceViewPhase.Abandoned, model.Phase);
        Assert.False(model.TryGetReady(out _));
        Assert.Equal(FenceResult.Ignored, model.ActivateAuthorizedView(Fence, Ready));
        Assert.False(model.TryGetMotion(5, out _));
    }

    [Fact]
    public void UnknownEntitiesAndGenerationsAreDroppedWithoutBeingBuffered()
    {
        var model = Active();
        model.ApplyDatagram(Packet(1, Motion(8, 1, 99), Motion(generation: 7, revision: 99)));
        model.ApplyReliableCheckpoint(Fence, Motion(8, 1, 100));
        model.ApplyLifecycle(Fence, new(21, MotionLifecycleKind.Spawn, 8, 1, Motion(8, 1, 1)));
        Assert.Equal(1u, Current(model, 8).Revision);
        Assert.Equal(7u, Current(model).Revision);
    }

    [Fact]
    public void RevisionAndInterestGenerationCannotWrapAndResetRequiresAuthorizedNewView()
    {
        var grants = new Grants();
        var final = Motion(generation: uint.MaxValue, revision: uint.MaxValue);
        var model = Active(grants, final);
        model.ApplyDatagram(Packet(1, final with { Revision = 1 }));
        Assert.Equal(final, Current(model));
        model.ApplyLifecycle(Fence, new(21, MotionLifecycleKind.Despawn, 5, uint.MaxValue));
        Assert.Throws<InvalidDataException>(() => model.ApplyLifecycle(Fence, new(22, MotionLifecycleKind.Spawn, 5, 1, Motion(generation: 1, revision: 1))));
        grants.View = 3;
        var nextFence = new ViewFence(1, 3);
        var nextCapture = new BaselineFence(11, 200, 0);
        var nextReady = new ReadyFence(11, 0);
        model.BeginAuthorizedView(3, nextCapture);
        model.StageBaseline(nextFence, nextCapture, [Motion(generation: 1, revision: 1)]);
        model.CommitView(nextFence, nextReady);
        model.ActivateAuthorizedView(nextFence, nextReady);
        Assert.Equal(1u, Current(model).InterestGeneration);
    }

    [Fact]
    public void EntryBudgetRejectsOversizedBaselineAndAbandonsLifecycleGrowth()
    {
        var model = new StateFenceReference(1, new Grants(), maximumEntries: 1);
        model.BeginAuthorizedView(2, Capture);
        model.StageBaseline(Fence, Capture, [Motion()]);
        Assert.Throws<InvalidDataException>(() => model.StageBaseline(Fence, Capture, [Motion(), Motion(8)]));
        model.CommitView(Fence, Ready);
        model.ActivateAuthorizedView(Fence, Ready);
        Assert.Equal(Motion(), Current(model));
        Assert.Equal(FenceResult.Abandoned, model.ApplyLifecycle(Fence, new(21, MotionLifecycleKind.Spawn, 8, 1, Motion(8, 1, 1))));
        Assert.Equal(ReferenceViewPhase.Abandoned, model.Phase);
        Assert.False(model.TryGetMotion(5, out _));
    }
}
