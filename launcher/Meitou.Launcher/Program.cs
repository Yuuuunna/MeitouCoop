using Avalonia;

namespace Meitou.Launcher;

/// <summary>
/// The launcher's entry point.
/// </summary>
static class Program
{
    /// <summary>
    /// Whether this launch explicitly avoids GPU rendering and automatic game hardware probes.
    /// </summary>
    internal static bool SoftwareRendering { get; private set; }

    /// <summary>
    /// Runs Velopack's hooks, loads the native libraries and starts the Avalonia app; with <see cref="WorkshopSyncChild.Argument"/> it only
    /// does a Workshop sync, without a window.
    /// </summary>
    [STAThread]
    public static void Main(string[] args)
    {
        if (args is [WorkshopSyncChild.Argument])
        {
            WorkshopSyncChild.Run();
            return;
        }

        // First: Velopack runs its install, update and uninstall hooks here and may exit (docs/launcher.md, "Self-update").
        Velopack.VelopackApp.Build().Run();
        NativeLibraries.Load();
        BuildApp(args).StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Allows CPU-only desktops to bypass graphics driver initialization; this does not change the game's renderer.
    /// </summary>
    static AppBuilder BuildApp(string[] args)
    {
        var builder = AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
        SoftwareRendering = args.Contains("--software-rendering", StringComparer.Ordinal)
            || Environment.GetEnvironmentVariable("MEITOU_LAUNCHER_SOFTWARE_RENDERING") == "1";
        if (!SoftwareRendering) return builder;

        return builder
            .With(new Win32PlatformOptions
            {
                RenderingMode = [Win32RenderingMode.Software],
                CompositionMode = [Win32CompositionMode.RedirectionSurface],
            })
            .With(new X11PlatformOptions { RenderingMode = [X11RenderingMode.Software] })
            .With(new AvaloniaNativePlatformOptions { RenderingMode = [AvaloniaNativeRenderingMode.Software] });
    }
}
