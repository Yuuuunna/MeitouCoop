namespace Meitou.Connections;

/// <summary>
/// The address syntax shared by the launcher and the game's device-identity connection path.
/// </summary>
public static class ServerAddress
{
    /// <summary>
    /// Parses a host and optional port, accepting bracketed IPv6 and defaulting to port 27800.
    /// </summary>
    public static (string Host, int Port) Parse(string address)
    {
        address = address.Trim();
        if (address.Length == 0 || !Uri.TryCreate("https://" + address, UriKind.Absolute, out var uri)
            || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/"
            || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Enter a server as host:port (IPv6: [address]:port).");

        bool explicitPort = address.StartsWith('[') ? address.LastIndexOf(']') < address.Length - 1 : address.Contains(':');
        int port = explicitPort ? uri.Port : 27800;
        if (port is < 1 or > 65535) throw new ArgumentException("Invalid server port.");

        return (uri.IdnHost.Trim('[', ']').ToLowerInvariant(), port);
    }

    /// <summary>
    /// A canonical address suitable for display and the game's --connect argument.
    /// </summary>
    public static string Normalize(string address)
    {
        var (host, port) = Parse(address);
        return host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";
    }
}
