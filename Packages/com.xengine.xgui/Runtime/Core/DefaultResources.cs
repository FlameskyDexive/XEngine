using System;
using XEngine.Runtime;
using XEngine.Runtime.Resources;

namespace XEngine.Runtime.UI;

/// <summary>
/// Stable first-party XGUI default resource keys. Concrete files live under
/// <c>Packages/com.xengine.xgui/Runtime/Resources/</c> and keep fixed GUIDs so
/// authored scenes can reference the package sprite/font. Headless runtime
/// still falls back to the embedded UI panel and Geist font.
/// </summary>
public static class XguiDefaultResources
{
    public const string DefaultSprite = "Packages/com.xengine.xgui/Runtime/Resources/DefaultSprite.png";
    public const string DefaultFont = "Packages/com.xengine.xgui/Runtime/Resources/DefaultFont.ttf";

    public static readonly Guid DefaultSpriteTextureGuid = new("8c4e9e61-e1be-42b6-a9bb-0017f9d86432");
    public static readonly Guid DefaultSpriteGuid = new("f66704c9-cd9c-759c-c49d-9a0a4d2ed4b8");
    public static readonly Guid DefaultFontGuid = new("0f1e8543-ff95-4bff-8b06-a91312e69554");

    public static Guid ResolveDefaultSpriteGuid()
    {
        if (AssetDatabase.Current != null && AssetDatabase.Get(DefaultSpriteGuid) is Sprite)
            return DefaultSpriteGuid;
        return BuiltInAssets.GuidFor(XEngine.Runtime.Resources.DefaultSprite.UIPanel);
    }

    public static Guid ResolveDefaultFontGuid()
    {
        if (AssetDatabase.Current != null && AssetDatabase.Get(DefaultFontGuid) is FontAsset)
            return DefaultFontGuid;
        return BuiltInAssets.GuidFor(XEngine.Runtime.Resources.DefaultFont.Default);
    }
}
