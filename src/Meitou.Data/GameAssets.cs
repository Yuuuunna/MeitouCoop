using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Ogre;

namespace Meitou.Data;

/// <summary>
/// Finds art files the way the viewer needs them: an existing path, a path relative to the install (FCS stores
/// <c>.\data\...</c>), then the bare file name through the <c>resources.cfg</c> and enabled mod folders (Ogre's lookup, not
/// recursive), then the bare name anywhere under data/ (for files outside those folders).
/// </summary>
public class GameAssets
{
    readonly GameInstall install;
    readonly Lazy<OgreScriptResources> everything;

    public GameAssets(GameInstall install)
    {
        this.install = install;
        Configured = OgreScriptResources.ForInstall(install, LoadOrder.FromInstall(install));
        everything = new(() => OgreScriptResources.FromDirectory(install.DataDirectory));
    }

    public OgreScriptResources Configured { get; }

    /// <summary>Full path of <paramref name="name"/>, or null. <paramref name="how"/> says which rule found it.</summary>
    public string? Find(string name, out string how)
    {
        how = "";
        if (string.IsNullOrWhiteSpace(name)) return null;
        name = name.Trim().Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(name) && GameFiles.Exists(name)) { how = "path"; return Path.GetFullPath(GameFiles.Resolve(name)); }
        if (!Path.IsPathRooted(name))
        {
            var underInstall = GameFiles.Resolve(Path.GetFullPath(Path.Combine(install.Root, name)));
            if (File.Exists(underInstall)) { how = "install path"; return underInstall; }
            var local = GameFiles.Resolve(name);
            if (File.Exists(local)) { how = "path"; return Path.GetFullPath(local); }
        }
        var bare = Path.GetFileName(name);
        if (Configured.Find(bare) is { } configured) { how = "resources.cfg"; return configured; }
        if (everything.Value.Find(bare) is { } anywhere) { how = "data/ (outside resources.cfg)"; return anywhere; }
        return null;
    }

    public string? Find(string name) => Find(name, out _);

    /// <summary>Mesh lookup uses configured resource folders before an FCS relative path.</summary>
    public string? FindMesh(string fcsPath) => Configured.Find(Path.GetFileName(fcsPath.Replace('\\', '/'))) ?? Find(fcsPath);

    public Characters.CharacterRigs CreateRigs(GameDatabase db) => new(db, FindMesh, Find);

    public string Relative(string path) => Path.GetRelativePath(install.Root, path);
}
