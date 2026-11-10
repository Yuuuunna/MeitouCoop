using Meitou.Networking.VNext;

namespace Meitou.Tests.NetworkingNext;

public class WireBudgetAcceptanceTests
{
    static WireCounterSample[] Capture(long sentPerSecond, long receivedPerSecond = 0) =>
        Enumerable.Range(0, 13).Select(i => new WireCounterSample(TimeSpan.FromSeconds(i * 10),
            sentPerSecond * i * 10, receivedPerSecond * i * 10, 100L * i * 10,
            receivedPerSecond == 0 ? 0 : 10L * i * 10, 2000L * i * 10,
            receivedPerSecond == 0 ? 0 : 200L * i * 10)).ToArray();

    [Fact]
    public void PayloadUnderBudgetFailsWhenAckTrafficAndHeadersExceedBudget()
    {
        var result = WireBudgetAcceptance.Evaluate(Capture(59_000, 1000), true);
        Assert.False(result.Passed);
        Assert.Equal(504_640, result.MeanBitsPerSecond);
        Assert.Equal(12, result.WindowBitsPerSecond.Count);
    }

    [Fact]
    public void ExactBudgetPassesButIncompleteCaptureNeverDoes()
    {
        var samples = Capture(59_700);
        Assert.Equal(500_000, WireBudgetAcceptance.Evaluate(samples, true).MeanBitsPerSecond);
        Assert.True(WireBudgetAcceptance.Evaluate(samples, true).Passed);
        Assert.False(WireBudgetAcceptance.Evaluate(samples, false).Passed);
        Assert.False(WireBudgetAcceptance.Evaluate(samples[..^1], true).Passed);
    }

    [Fact]
    public void ServerEgressGateDoesNotCountIngressAsUplink()
    {
        var samples = Capture(3_100_000, 100_000);
        Assert.True(WireBudgetAcceptance.Evaluate(samples, true, serverEgressOnly: true).Passed);
        Assert.False(WireBudgetAcceptance.Evaluate(samples, true).Passed);
    }

    [Fact]
    public void MissingWindowBoundaryAndResetCannotProducePassingEvidence()
    {
        var samples = Capture(1000);
        Assert.False(WireBudgetAcceptance.Evaluate(samples.Where((_, i) => i != 3).ToArray(), true).Passed);
        samples[5] = samples[5] with { SentUdpBytes = 0 };
        Assert.Throws<ArgumentException>(() => WireBudgetAcceptance.Evaluate(samples, true));
    }
}
