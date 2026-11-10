using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Meitou.Networking.VNext;
using Meitou.Networking.VNext.Transport;

namespace Meitou.Tests.Engine;

internal static class NativeFixture
{
    public static void Require()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows() || !Environment.Is64BitProcess,
            "Native game QUIC integration requires Windows x64.");
        Assert.SkipWhen(Environment.GetEnvironmentVariable("MEITOU_QUIC_NATIVE_DIRECTORY") is null ||
            Environment.GetEnvironmentVariable("MEITOU_QUIC_SHIM_SHA256") is null,
            "Supply the trusted native QUIC directory and shim SHA256 for game integration.");
    }
    public static QuicGameTransportOptions Options(X509Certificate2 certificate)
    {
        Require();
        string path = Path.Combine(Path.GetTempPath(), "Meitou.TestCa-" + certificate.GetCertHashString(HashAlgorithmName.SHA256) + ".pem");
        File.WriteAllText(path, certificate.ExportCertificatePem());
        return new(Environment.GetEnvironmentVariable("MEITOU_QUIC_NATIVE_DIRECTORY")!,
            Environment.GetEnvironmentVariable("MEITOU_QUIC_SHIM_SHA256")!, path, "localhost");
    }
    public static async Task<NativeGameConnection> Connect(IPEndPoint address, X509Certificate2 certificate, CancellationToken token)
    {
        var config = Options(certificate);
        var endpoint = new NativeQuicEndpoint(config.NativeDirectory, config.ShimSha256, false, config.CaFile);
        File.Delete(config.CaFile);
        endpoint.Connect(address.Address, checked((ushort)address.Port), "localhost");
        var connection = new NativeGameConnection(endpoint);
        try { await connection.Connected.WaitAsync(token); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
}
