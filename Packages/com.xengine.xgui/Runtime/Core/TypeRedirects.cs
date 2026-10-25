// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Runtime.CompilerServices;

using XEngine.Runtime;

namespace XEngine.Runtime.UI;

/// <summary>
/// Installs Echo type redirects when the XGUI Runtime assembly is loaded. Redirects are names-only
/// so unloading and reloading the package does not leave a package <see cref="Type"/> rooted in the
/// core registry.
/// </summary>
internal static class TypeRedirects
{
    [ModuleInitializer]
    [OnAssemblyLoad(Order = -1000)]
    internal static void Register()
    {
        Register("UIImage", "Image");
        Register("TextComponent", "Text");
        Register("UIButton", "Button");
        Register("UISlider", "Slider");
        Register("UIScrollbar", "Scrollbar");
        Register("UIScrollRect", "ScrollRect");
        Register("UIInputField", "InputField");
        Register("UIDropdown", "Dropdown");
        Register("RectMask", "RectMask2D");
        Register("UILayoutElement", "LayoutElement");
        Register("UILayoutGroup", "LayoutGroup");
        Register("UIHorizontalLayoutGroup", "HorizontalLayoutGroup");
        Register("UIVerticalLayoutGroup", "VerticalLayoutGroup");
        Register("UIGridLayoutGroup", "GridLayoutGroup");
        Register("UIContentSizeFitter", "ContentSizeFitter");

        // The Runtime layout compatibility classes above carried the package's own names until they
        // took the UI prefix every other compatibility type already had (two identical
        // XEngine.Runtime.UI.LayoutGroup names made CS0433 unavoidable for any assembly referencing
        // both). Scenes and prefabs authored before that rename still serialize the unprefixed name
        // in assembly XEngine.Runtime, which now matches no Runtime type at all, so map those
        // strings to the package the same way the prefixed names are mapped.
        Register("LayoutElement", "LayoutElement");
        Register("LayoutGroup", "LayoutGroup");
        Register("HorizontalLayoutGroup", "HorizontalLayoutGroup");
        Register("VerticalLayoutGroup", "VerticalLayoutGroup");
        Register("GridLayoutGroup", "GridLayoutGroup");
        Register("ContentSizeFitter", "ContentSizeFitter");
    }

    private static void Register(string legacyName, string packageName)
    {
        const string legacyAssembly = "XEngine.Runtime";
        const string packageAssembly = "XEngine.XGUI.Runtime";
        const string uiNamespace = "XEngine.Runtime.UI.";

        RuntimeTypeRegistry.RegisterAlias(
            uiNamespace + legacyName + ", " + legacyAssembly,
            uiNamespace + packageName + ", " + packageAssembly);
        RuntimeTypeRegistry.RegisterAlias(
            uiNamespace + legacyName,
            uiNamespace + packageName + ", " + packageAssembly);
        RuntimeTypeRegistry.RegisterAlias(
            legacyName,
            uiNamespace + packageName + ", " + packageAssembly);
    }
}
