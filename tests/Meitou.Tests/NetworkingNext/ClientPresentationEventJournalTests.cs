using Meitou.Networking;
using Meitou.Networking.VNext;
using Meitou.Simulation;
using Meitou.Simulation.Factions;
using Meitou.Tests.Engine;
namespace Meitou.Tests.NetworkingNext;

public sealed class ClientPresentationEventJournalTests
{
    static PresentationEventBatch Speech(ulong id) => new(id, []) { Speech = [new(id, new(0, 1), new("speaker", "speech", false))] };
    [Fact]
    public void AcceptedSpeechSurvivesLatestStateReplacementUntilConsumerAcknowledges()
    {
        var journal = new ClientPresentationEventJournal(new WorldViewCodec(WorldViewCodecTests.Database()));
        journal.Register(); journal.Observe(Speech(10)); Assert.Empty(journal.Read().Speech);
        journal.Observe(Speech(11)); journal.Observe(new(11, []));
        Assert.Equal(11ul, Assert.Single(journal.Read().Speech).Id);
        journal.Acknowledge(11); journal.Observe(Speech(11)); Assert.Empty(journal.Read().Speech);
        journal.Observe(Speech(12)); journal.Fence(); journal.Observe(Speech(12)); Assert.Empty(journal.Read().Speech);
        journal.Observe(Speech(13)); Assert.Equal(13ul, Assert.Single(journal.Read().Speech).Id);
    }
    [Fact]
    public void RegisteredButUnstartedConsumerSkipsLoadingHistoryWithoutRetainingAnUnboundedQueue()
    {
        var journal = new ClientPresentationEventJournal(new WorldViewCodec(WorldViewCodecTests.Database()));
        journal.Register(); for (ulong id = 1; id <= 10_000; id++) journal.Observe(Speech(id));
        Assert.Empty(journal.Read().Speech);
        journal.Observe(Speech(10_001)); Assert.Equal(10_001ul, Assert.Single(journal.Read().Speech).Id);
    }
    [Fact]
    public void FullUiCaptureCannotConsumeCriticalSpeechAndLateUiCannotOverwriteConsumerJournal()
    {
        var db = WorldViewCodecTests.Database(); var codec = new WorldViewCodec(db);
        var source = WorldViewCodecTests.View(db);
        source = source with { Publication = 1, Events = PresentationEventBatch.Empty,
            Snapshot = source.Snapshot with { Characters = source.Snapshot.Characters.Select(c => c with { Animations = [] }).ToArray() } };
        var publisher = new GameStatePublisher(codec, _ => 42, 11, 22); var replica = new GameStateReplica(codec, 11, 22);
        var journal = new ClientPresentationEventJournal(codec); journal.Register();
        void Apply(GameStatePublication publication)
        { foreach (var frame in publication.Frames) replica.Apply(frame); journal.Observe(replica.View!.Events); }
        Apply(publisher.Capture(source)); Assert.Empty(journal.Read().Speech);
        var eventView = source with { Publication = 2, Events = Speech(1), Snapshot = source.Snapshot with {
            Characters = source.Snapshot.Characters.Select(c => c with { Name = "slow UI" }).ToArray() } };
        var slowUi = publisher.Capture(eventView);
        Assert.DoesNotContain((ushort)7, slowUi.ComponentBytes.Keys);
        foreach (var critical in publisher.CaptureCritical(eventView with { Publication = 3 })) Apply(critical);
        foreach (var critical in publisher.CaptureCritical(eventView with { Publication = 4, Events = new(1, []) })) Apply(critical);
        Apply(slowUi);
        Assert.Empty(replica.View!.Events.Speech); Assert.Equal("slow UI", replica.View.Snapshot.Characters[0].Name);
        Assert.Equal(1ul, Assert.Single(journal.Read().Speech).Id);
        journal.Acknowledge(1); Assert.Empty(journal.Read().Speech);
    }

}
