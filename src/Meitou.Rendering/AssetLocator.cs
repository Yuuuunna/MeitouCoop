using Meitou.Content;

namespace Meitou.Rendering;

/// <summary>Rendering's compatibility name for the shared CPU asset resolver.</summary>
public sealed class AssetLocator(GameInstall install) : Meitou.Data.GameAssets(install);
