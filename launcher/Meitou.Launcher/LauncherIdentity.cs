namespace Meitou.Launcher;

/// <summary>
/// The Coop distribution's identity, independent of upstream installations and update feeds.
/// </summary>
static class LauncherIdentity
{
    /// <summary>
    /// The repository used for game releases, launcher updates and news.
    /// </summary>
    public const string Repository = "Yuuuunna/MeitouCoop";

    /// <summary>
    /// The branch containing the news folder.
    /// </summary>
    public const string NewsBranch = "master";

    /// <summary>
    /// A public static catalog updated after game and launcher releases, independent of the REST API's anonymous quota.
    /// </summary>
    public static string ReleaseCatalogUrl => $"https://raw.githubusercontent.com/{Repository}/launcher-feed/releases.json";

    /// <summary>
    /// The directory under the user's application data folder.
    /// </summary>
    public const string DataFolder = "MeitouCoop";

    /// <summary>
    /// The title shown by standard launchers.
    /// </summary>
    public const string Title = "Meitou Coop Launcher";
}
