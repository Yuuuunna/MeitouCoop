using Meitou.Content;
namespace Meitou.Data.Ogre;

/// <summary>Compatibility name for the shared CPU asset resolver.</summary>
public sealed class AssetLocator(GameInstall install) : Meitou.Data.GameAssets(install);
