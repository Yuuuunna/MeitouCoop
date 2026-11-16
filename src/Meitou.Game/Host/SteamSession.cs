using System.Runtime.InteropServices;
using Meitou.Content;
using Meitou.Localisation;

namespace Meitou.Game.Host;

/// <summary>
/// Optional Steamworks lifetime for the Steam copy of Kenshi. No Steam binaries are shipped with Meitou.
/// </summary>
sealed unsafe class SteamSession : IDisposable
{
    /// <summary>
    /// Kenshi's Steam App ID.
    /// </summary>
    const uint KenshiAppId = 233860;

    /// <summary>
    /// The install's Steamworks library; zero after shutdown.
    /// </summary>
    nint _library;

    /// <summary>
    /// The library's shutdown function.
    /// </summary>
    readonly delegate* unmanaged[Cdecl]<void> _shutdown;

    /// <summary>
    /// Optional gameplay presence, released before the library is unloaded.
    /// </summary>
    readonly SteamPresence? _presence;

    /// <summary>
    /// Takes an initialized library and binds optional rich presence.
    /// </summary>
    SteamSession(nint library, delegate* unmanaged[Cdecl]<void> shutdown)
    {
        _library = library;
        _shutdown = shutdown;
        if (SteamFriends.TryOpen(library) is { } friends)
        {
            _presence = new(friends);
            _presence.Reset();
        }
    }

    /// <summary>
    /// Starts Steamworks from the Windows install; logs and skips unavailable libraries, conflicting app IDs or failed initialization.
    /// </summary>
    public static SteamSession? TryStart(GameInstall install)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("Steamworks: skipped (currently supported on Windows only).");
            return null;
        }

        string path = GameFiles.Resolve(Path.Combine(install.Root, "steam_api64.dll"));
        if (!File.Exists(path))
        {
            Console.WriteLine("Steamworks: skipped (steam_api64.dll is missing from the Kenshi install).");
            return null;
        }

        return AppIdAllowed() ? StartLibrary(path) : null;
    }

    /// <summary>
    /// Refuses a working-directory or process app ID belonging to another game.
    /// </summary>
    static bool AppIdAllowed()
    {
        try
        {
            string? fileAppId = File.Exists("steam_appid.txt") ? File.ReadAllText("steam_appid.txt").Trim() : null;
            string? processAppId = Environment.GetEnvironmentVariable("SteamAppId");
            if ((fileAppId is null || fileAppId == KenshiAppId.ToString()) && (processAppId is null || processAppId == KenshiAppId.ToString()))
                return true;

            Console.WriteLine("Steamworks: skipped (a different Steam App ID is configured).");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Steamworks: could not read steam_appid.txt ({e.Message}).");
        }

        return false;
    }

    /// <summary>
    /// Transfers a successfully initialized library into the session; every failed attempt frees its library.
    /// </summary>
    static SteamSession? StartLibrary(string path)
    {
        nint library = 0;
        bool initialized = false;
        delegate* unmanaged[Cdecl]<void> shutdown = null;
        try
        {
            library = NativeLibrary.Load(path);
            shutdown = (delegate* unmanaged[Cdecl]<void>)NativeLibrary.GetExport(library, "SteamAPI_Shutdown");
            if (!Initialize(library)) return null;
            initialized = true;

            GameLanguage.SteamClientLanguage = ReadGameLanguage(library);
            var session = new SteamSession(library, shutdown);
            library = 0;
            Console.WriteLine($"Steamworks: initialized for Kenshi (233860), language {GameLanguage.SteamClientLanguage ?? "unknown"}.");
            return session;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or FileLoadException)
        {
            Console.WriteLine($"Steamworks: could not load Kenshi's Steam API ({e.Message}).");
            return null;
        }
        finally
        {
            if (library != 0)
            {
                if (initialized) shutdown();
                NativeLibrary.Free(library);
            }
        }
    }

    /// <summary>
    /// Uses detailed initialization on recent Steamworks DLLs, otherwise the one-byte C++ bool returned by Kenshi's older API.
    /// </summary>
    static bool Initialize(nint library)
    {
        if (NativeLibrary.TryGetExport(library, "SteamAPI_InitFlat", out nint initFlat))
        {
            byte* error = stackalloc byte[1024];
            int result = ((delegate* unmanaged[Cdecl]<byte*, int>)initFlat)(error);
            if (result == 0) return true;

            string? message = Marshal.PtrToStringUTF8((nint)error);
            string reason = string.IsNullOrEmpty(message) ? $"SteamAPI_InitFlat returned {result}" : message;
            Console.WriteLine($"Steamworks: {reason}");
            return false;
        }

        var init = (delegate* unmanaged[Cdecl]<byte>)NativeLibrary.GetExport(library, "SteamAPI_Init");
        if (init() != 0) return true;

        Console.WriteLine("Steamworks: SteamAPI_Init failed (this Kenshi Steam API has no detailed init error).");
        return false;
    }

    /// <summary>
    /// Updates optional Steam presence on the game thread while the session is alive.
    /// </summary>
    internal void Update(GameHost host)
    {
        if (_library != 0) _presence?.Update(host);
    }

    /// <summary>
    /// Removes the outgoing world's presence before replacing or disposing it.
    /// </summary>
    internal void LeaveWorld()
    {
        if (_library != 0) _presence?.Reset();
    }

    /// <summary>
    /// The language the Steam client runs Kenshi in (<c>ISteamApps::GetCurrentGameLanguage</c>, which the original asks at start to override its
    /// setting, docs/formats/localisation.md); null when the library lacks the exports.
    /// </summary>
    static string? ReadGameLanguage(nint library)
    {
        if (!NativeLibrary.TryGetExport(library, "SteamAPI_ISteamApps_GetCurrentGameLanguage", out nint current)) return null;
        nint apps = SteamApps(library);
        if (apps == 0) return null;

        byte* language = ((delegate* unmanaged[Cdecl]<nint, byte*>)current)(apps);
        return language is null ? null : Marshal.PtrToStringUTF8((nint)language);
    }

    /// <summary>
    /// The <c>ISteamApps</c> interface of version 008 (the original's): the newer libraries' accessor, else through <c>SteamClient</c> as Kenshi's own
    /// library offers it; 0 when neither is there.
    /// </summary>
    static nint SteamApps(nint library)
    {
        if (NativeLibrary.TryGetExport(library, "SteamAPI_SteamApps_v008", out nint accessor)) return ((delegate* unmanaged[Cdecl]<nint>)accessor)();
        if (!(NativeLibrary.TryGetExport(library, "SteamClient", out nint client)
            && NativeLibrary.TryGetExport(library, "SteamAPI_GetHSteamUser", out nint user)
            && NativeLibrary.TryGetExport(library, "SteamAPI_GetHSteamPipe", out nint pipe)
            && NativeLibrary.TryGetExport(library, "SteamAPI_ISteamClient_GetISteamApps", out nint getApps)))
        {
            return 0;
        }

        nint steamClient = ((delegate* unmanaged[Cdecl]<nint>)client)();
        int steamUser = ((delegate* unmanaged[Cdecl]<int>)user)(), steamPipe = ((delegate* unmanaged[Cdecl]<int>)pipe)();
        if (steamClient == 0 || steamPipe == 0) return 0;

        // A UTF-8 literal is stored with a terminating zero after its bytes.
        fixed (byte* version = "STEAMAPPS_INTERFACE_VERSION008"u8)
            return ((delegate* unmanaged[Cdecl]<nint, int, int, byte*, nint>)getApps)(steamClient, steamUser, steamPipe, version);
    }

    /// <summary>
    /// Clears presence, shuts Steamworks down and frees the library; once only.
    /// </summary>
    public void Dispose()
    {
        if (_library == 0) return;

        LeaveWorld();
        _shutdown();
        NativeLibrary.Free(_library);
        _library = 0;
    }
}
