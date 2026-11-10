using Meitou.Networking.VNext;

namespace Meitou.Tests.NetworkingNext;

public class ReliableLaneFramerTests
{
    [Fact]
    public void FragmentedAndCoalescedCallbacksYieldIdenticalFrames()
    {
        var encoder = new ReliableLaneFramer(ScheduledLane.Control);
        byte[] bytes = [.. encoder.Encode([1, 2]), .. encoder.Encode([3]), .. encoder.Encode([4, 5, 6])];
        foreach (int chunk in new[] { 1, 3, 7, bytes.Length })
        {
            var reader = new ReliableLaneFramer(ScheduledLane.Control);
            var frames = new List<byte[]>();
            for (int i = 0; i < bytes.Length; i += chunk)
                reader.Feed(bytes.AsSpan(i, Math.Min(chunk, bytes.Length - i)), b => frames.Add(b.ToArray()));
            reader.Complete();
            Assert.Equal(3, frames.Count);
            Assert.Equal(new byte[] { 1, 2 }, frames[0]);
            Assert.Equal(new byte[] { 3 }, frames[1]);
            Assert.Equal(new byte[] { 4, 5, 6 }, frames[2]);
        }
    }

    [Fact]
    public void InvalidLengthFaultsBeforeBodyAllocationAndCannotResume()
    {
        foreach (byte[] prefix in new byte[][] { [0, 0, 0, 0], [255, 255, 255, 255], [0, 64, 0, 0] })
        {
            var reader = new ReliableLaneFramer(ScheduledLane.Control);
            Assert.Throws<InvalidDataException>(() => reader.Feed(prefix, _ => Assert.Fail("Invalid frame emitted.")));
            Assert.Throws<InvalidOperationException>(() => reader.Feed([1], _ => { }));
        }
    }

    [Fact]
    public void TruncationAndReceiverFailureCannotMasqueradeAsCleanCompletion()
    {
        var reader = new ReliableLaneFramer(ScheduledLane.Bulk);
        reader.Feed([2, 0, 0, 0, 1], _ => Assert.Fail("Partial frame emitted."));
        Assert.Throws<InvalidDataException>(reader.Complete);
        var failing = new ReliableLaneFramer(ScheduledLane.Lifecycle);
        Assert.Throws<IOException>(() => failing.Feed(failing.Encode([1]), _ => throw new IOException()));
        Assert.Throws<InvalidOperationException>(failing.Complete);
    }
}
