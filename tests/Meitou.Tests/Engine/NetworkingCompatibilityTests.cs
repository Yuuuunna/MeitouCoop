using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Meitou.Networking;
using Meitou.Tests.Save;

namespace Meitou.Tests.Engine;

public class NetworkingCompatibilityTests
{
    static string Hash(char value) => new(value, 64);
    static CompatibilityHello Hello() => new(CompatibilityHandshake.Protocol, "engine-build", Hash('A'), Hash('B'));

    [Fact]
    public void SameNamesDoNotHideContentChangesAndMergeOrderIsSignificant()
    {
        string root = SaveSamples.Temp("compatibility-files"); Directory.CreateDirectory(root);
        try
        {
            string a = Path.Combine(root, "a.mod"), b = Path.Combine(root, "b.mod");
            File.WriteAllText(a, "one"); File.WriteAllText(b, "two");
            ContentFile[] inputs = [new(ContentKind.Database, "a.mod", a), new(ContentKind.Database, "b.mod", b)];
            var first = ContentManifest.ReadFiles(inputs);
            Assert.NotEqual(first.Digest, ContentManifest.ReadFiles(inputs.Reverse()).Digest);
            File.WriteAllText(a, "six"); // Same path and size, different contents.
            Assert.NotEqual(first.Digest, ContentManifest.ReadFiles(inputs).Digest);
            Assert.Equal(first.Digest, new ContentManifest(first.Files.Select(f => f with { Key = f.Key.ToUpperInvariant(), Sha256 = f.Sha256.ToLowerInvariant() })).Digest);
            Assert.Throws<InvalidDataException>(() => new ContentManifest([first.Files[0], first.Files[0] with { Key = "A.MOD" }]));
            Assert.Throws<InvalidDataException>(() => new ContentManifest([first.Files[0] with { Key = "../a.mod" }]));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RuleInsertionOrderDoesNotChangeDigestButValuesDo()
    {
        var a = new Dictionary<string, string> { ["pause-empty"] = "true", ["max-players"] = "32" };
        var b = new Dictionary<string, string> { ["max-players"] = "32", ["pause-empty"] = "true" };
        Assert.Equal(ContentManifest.RulesDigest(a), ContentManifest.RulesDigest(b));
        b["max-players"] = "64";
        Assert.NotEqual(ContentManifest.RulesDigest(a), ContentManifest.RulesDigest(b));
    }

    [Theory]
    [InlineData(CompatibilityFailure.Protocol)]
    [InlineData(CompatibilityFailure.EngineBuild)]
    [InlineData(CompatibilityFailure.GameplayRules)]
    [InlineData(CompatibilityFailure.Content)]
    public void EachCompatibilityMismatchRejectsTheConnection(CompatibilityFailure failure)
    {
        var expected = Hello();
        var actual = failure switch
        {
            CompatibilityFailure.Protocol => expected with { Protocol = (ushort)(CompatibilityHandshake.Protocol + 1) },
            CompatibilityFailure.EngineBuild => expected with { EngineBuild = "different-build" },
            CompatibilityFailure.GameplayRules => expected with { RulesDigest = Hash('C') },
            _ => expected with { ContentDigest = Hash('D') },
        };
        var handshake = new CompatibilityHandshake(expected);
        Assert.Equal(failure, handshake.Receive(actual)); Assert.Equal(CompatibilityPhase.Rejected, handshake.Phase);
        Assert.Throws<InvalidDataException>(() => handshake.Receive(expected));
    }

    [Fact]
    public void MalformedUnknownAndDuplicateHelloFieldsAreRejected()
    {
        var valid = CompatibilityHandshake.Encode(Hello());
        Assert.Equal(Hello(), CompatibilityHandshake.Decode(valid));
        string json = Encoding.UTF8.GetString(valid);
        Assert.Throws<InvalidDataException>(() => CompatibilityHandshake.Decode(Encoding.UTF8.GetBytes(json.Insert(1, "\"PlayerId\":2,"))));
        Assert.Throws<InvalidDataException>(() => CompatibilityHandshake.Decode(Encoding.UTF8.GetBytes(json.Insert(1, "\"Protocol\":1,"))));
        Assert.Throws<InvalidDataException>(() => CompatibilityHandshake.Decode("{}"u8));
        Assert.Throws<InvalidDataException>(() => CompatibilityHandshake.Decode(new byte[CompatibilityHandshake.MaximumHelloBytes + 1]));
        var gate = new CompatibilityHandshake(Hello());
        Assert.Throws<InvalidDataException>(() => gate.Receive(new WireFrame(WireMessage.Compatible, valid)));
        Assert.Equal(CompatibilityPhase.Rejected, gate.Phase);
        var malformed = new CompatibilityHandshake(Hello());
        Assert.Throws<InvalidDataException>(() => malformed.Receive(new WireFrame(WireMessage.Hello, "{}"u8.ToArray())));
        Assert.Equal(CompatibilityPhase.Rejected, malformed.Phase);
    }

    sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], cancellationToken);
    }

    [Fact]
    public async Task FragmentedAndCoalescedFramesRetainTheirBoundaries()
    {
        var ct = TestContext.Current.CancellationToken;
        using var encoded = new MemoryStream();
        await WireFrame.WriteAsync(encoded, new WireFrame(WireMessage.Hello, CompatibilityHandshake.Encode(Hello())), ct);
        await WireFrame.WriteAsync(encoded, new WireFrame(WireMessage.Compatible, ReadOnlyMemory<byte>.Empty), ct);
        using var input = new FragmentedStream(encoded.ToArray());
        var first = await WireFrame.ReadAsync(input, ct);
        Assert.Equal(Hello(), CompatibilityHandshake.Decode(first!.Payload.Span));
        Assert.Equal(WireMessage.Compatible, (await WireFrame.ReadAsync(input, ct))!.Kind);
        Assert.Null(await WireFrame.ReadAsync(input, ct));
    }

    [Theory]
    [InlineData("magic")]
    [InlineData("format")]
    [InlineData("kind")]
    [InlineData("length")]
    [InlineData("header-truncation")]
    [InlineData("payload-truncation")]
    public async Task InvalidFramesNeverDecodeAsCompleteMessages(string defect)
    {
        var ct = TestContext.Current.CancellationToken;
        using var encoded = new MemoryStream();
        await WireFrame.WriteAsync(encoded, new WireFrame(WireMessage.Hello, new byte[] { 1, 2, 3 }), ct);
        byte[] bytes = encoded.ToArray();
        if (defect == "magic") bytes[0] = 0;
        if (defect == "format") bytes[4] = 9;
        if (defect == "kind") bytes[6] = 99;
        if (defect == "length") BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), uint.MaxValue);
        if (defect == "header-truncation") bytes = bytes[..5];
        if (defect == "payload-truncation") bytes = bytes[..^1];
        using var input = new FragmentedStream(bytes);
        if (defect.EndsWith("truncation", StringComparison.Ordinal)) await Assert.ThrowsAsync<EndOfStreamException>(async () => await WireFrame.ReadAsync(input, ct));
        else await Assert.ThrowsAsync<InvalidDataException>(async () => await WireFrame.ReadAsync(input, ct));
    }

    [Fact]
    public async Task CompatibilityHelloRoundTripsOverARealLoopbackSocket()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10)); var ct = timeout.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var accepting = listener.AcceptTcpClientAsync(ct);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, ct);
        using var server = await accepting;
        await WireFrame.WriteAsync(client.GetStream(), new WireFrame(WireMessage.Hello, CompatibilityHandshake.Encode(Hello())), ct);
        var incoming = await WireFrame.ReadAsync(server.GetStream(), ct);
        var gate = new CompatibilityHandshake(Hello());
        Assert.Equal(CompatibilityFailure.None, gate.Receive(incoming!));
        Assert.Equal(CompatibilityPhase.Compatible, gate.Phase);
        await WireFrame.WriteAsync(server.GetStream(), new WireFrame(WireMessage.Compatible, ReadOnlyMemory<byte>.Empty), ct);
        Assert.Equal(WireMessage.Compatible, (await WireFrame.ReadAsync(client.GetStream(), ct))!.Kind);
    }
}
