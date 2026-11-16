using System.Runtime.InteropServices;
using System.Text;

namespace Meitou.Game.Host;

/// <summary>
/// The optional flat friends API from the user's Steamworks library, including Kenshi's older SteamFriends015 accessor.
/// </summary>
sealed unsafe class SteamFriends
{
    /// <summary>
    /// Steam's value limit includes the terminating zero byte.
    /// </summary>
    const int ValueBytes = 256;

    /// <summary>
    /// The interface owned by Steamworks until shutdown.
    /// </summary>
    readonly nint _friends;

    /// <summary>
    /// Publishes one UTF-8 key/value pair; returns a one-byte C++ bool.
    /// </summary>
    readonly delegate* unmanaged[Cdecl]<nint, byte*, byte*, byte> _set;

    /// <summary>
    /// Removes every rich-presence key for this app.
    /// </summary>
    readonly delegate* unmanaged[Cdecl]<nint, void> _clear;

    /// <summary>
    /// Pumps Steam callbacks on the game thread.
    /// </summary>
    readonly delegate* unmanaged[Cdecl]<void> _callbacks;

    /// <summary>
    /// Binds the flat methods and their matching friends interface after Steamworks initialization.
    /// </summary>
    SteamFriends(nint library)
    {
        _set = (delegate* unmanaged[Cdecl]<nint, byte*, byte*, byte>)NativeLibrary.GetExport(library, "SteamAPI_ISteamFriends_SetRichPresence");
        _clear = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(library, "SteamAPI_ISteamFriends_ClearRichPresence");
        _callbacks = (delegate* unmanaged[Cdecl]<void>)NativeLibrary.GetExport(library, "SteamAPI_RunCallbacks");
        _friends = FriendsInterface(library);
        if (_friends == 0) throw new InvalidOperationException("Steam returned no friends interface.");
    }

    /// <summary>
    /// Enables presence when supported; an unavailable interface leaves overlay/play-time integration running.
    /// </summary>
    internal static SteamFriends? TryOpen(nint library)
    {
        try
        {
            var friends = new SteamFriends(library);
            Console.WriteLine("Steamworks: friends rich presence enabled.");
            return friends;
        }
        catch (Exception e) when (e is EntryPointNotFoundException or InvalidOperationException)
        {
            Console.WriteLine($"Steamworks: rich presence unavailable ({e.Message}).");
            return null;
        }
    }

    /// <summary>
    /// Uses a versioned accessor when exported, otherwise Kenshi's legacy flat SteamClient accessor.
    /// </summary>
    static nint FriendsInterface(nint library)
    {
        foreach (string version in new[] { "017", "016", "015" })
        {
            if (NativeLibrary.TryGetExport(library, "SteamAPI_SteamFriends_v" + version, out nint accessor))
                return ((delegate* unmanaged[Cdecl]<nint>)accessor)();
        }

        return LegacyFriendsInterface(library);
    }

    /// <summary>
    /// Retrieves SteamFriends015 using the initialized user's existing pipe; no new Steam connection is created.
    /// </summary>
    static nint LegacyFriendsInterface(nint library)
    {
        var client = (delegate* unmanaged[Cdecl]<nint>)NativeLibrary.GetExport(library, "SteamClient");
        var user = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(library, "SteamAPI_GetHSteamUser");
        var pipe = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(library, "SteamAPI_GetHSteamPipe");
        var friends = (delegate* unmanaged[Cdecl]<nint, int, int, byte*, nint>)
            NativeLibrary.GetExport(library, "SteamAPI_ISteamClient_GetISteamFriends");
        nint instance = client();
        if (instance == 0) return 0;

        fixed (byte* version = "SteamFriends015\0"u8)
            return friends(instance, user(), pipe(), version);
    }

    /// <summary>
    /// Shares a value without splitting a UTF-8 character at Steam's byte limit. An empty value removes the key.
    /// </summary>
    internal bool Set(string key, string value)
    {
        Span<byte> bytes = stackalloc byte[ValueBytes];
        Encoding.UTF8.GetEncoder().Convert(value.Replace('\0', ' ').AsSpan(), bytes[..^1], true, out _, out int written, out _);
        bytes[written] = 0;

        fixed (byte* keyBytes = Encoding.UTF8.GetBytes(key + '\0'))
        fixed (byte* valueBytes = bytes)
            return _set(_friends, keyBytes, valueBytes) != 0;
    }

    /// <summary>
    /// Processes callbacks on the game thread, including while the simulation is paused.
    /// </summary>
    internal void Pump() => _callbacks();

    /// <summary>
    /// Clears presence before leaving a world or shutting Steamworks down.
    /// </summary>
    internal void Clear() => _clear(_friends);
}
