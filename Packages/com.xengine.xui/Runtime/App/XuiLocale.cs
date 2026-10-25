// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Runtime.UI;
using XEngine.XUI.Core;

namespace XEngine.XUI;

/// <summary>Which piece of text on an element a <see cref="XuiLocalizedText"/> owns.</summary>
public enum XuiTextSlot
{
    /// <summary>The element's own text.</summary>
    Label,

    /// <summary>An <c>InputField</c>'s placeholder.</summary>
    Placeholder,

    /// <summary>An <c>InputField</c>'s initial value.</summary>
    Value,
}

/// <summary>
/// Remembers the source string a piece of text came from, so it can be re-translated in place.
/// </summary>
/// <remarks>
/// Without this, switching locale would mean rebuilding every open screen — which throws away scroll
/// positions, input focus, and anything a controller wrote into a label after it was built. The
/// component is added only where the assembler actually had something to translate, so a screen with no
/// translatable text carries none at all.
/// </remarks>
[AddComponentMenu("UI/XUI/Localized Text")]
public sealed class XuiLocalizedText : MonoBehaviour
{
    /// <summary>The untranslated string, exactly as the document spelled it.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Which slot on this element the source string fills.</summary>
    public XuiTextSlot Slot { get; set; } = XuiTextSlot.Label;

    /// <summary>Re-reads the active catalog and writes the result into the target.</summary>
    public void Apply()
    {
        if (Source.Length == 0) return;
        string translated = Xui.Tr(Source);

        switch (Slot)
        {
            case XuiTextSlot.Placeholder:
                if (GameObject.GetComponent<InputField>() is { IsDisposed: false } withPlaceholder
                    && withPlaceholder.Placeholder is { IsDisposed: false } placeholder)
                {
                    placeholder.Text = translated;
                }
                break;

            case XuiTextSlot.Value:
                if (GameObject.GetComponent<InputField>() is { IsDisposed: false } field)
                    field.Text = translated;
                break;

            default:
                if (GameObject.GetComponent<TextComponent>() is { IsDisposed: false } text)
                    text.Text = translated;
                break;
        }
    }
}

public static partial class Xui
{
    private static readonly Dictionary<string, XuiCatalog> Catalogs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, AssetRef<FontAsset>> LocaleFonts = new(StringComparer.OrdinalIgnoreCase);
    private static XuiCatalog? _active;
    private static string _locale = string.Empty;

    /// <summary>
    /// Raised after the locale changes and every open screen has been re-translated. For the text a
    /// controller writes itself — a formatted score, a name from a server — which no component owns.
    /// </summary>
    public static event Action<string>? LocaleChanged;

    /// <summary>
    /// The active locale tag. Empty means untranslated: every string shows as authored.
    /// </summary>
    /// <remarks>
    /// Setting it re-translates the open screens in place rather than rebuilding them, so scroll
    /// position, focus, and anything a controller wrote after the build all survive a language switch.
    /// </remarks>
    public static string Locale
    {
        get => _locale;
        set
        {
            string next = value ?? string.Empty;
            if (string.Equals(_locale, next, StringComparison.OrdinalIgnoreCase)) return;

            _locale = next;
            _active = next.Length == 0
                ? null
                : Catalogs.TryGetValue(next, out XuiCatalog? catalog) ? catalog : null;

            RetranslateOpenScreens();
            LocaleChanged?.Invoke(_locale);
        }
    }

    /// <summary>Locales a catalog has been registered for.</summary>
    public static IReadOnlyCollection<string> AvailableLocales => Catalogs.Keys;

    /// <summary>Entries in the active catalog; zero when nothing is loaded for the locale.</summary>
    public static int TranslationCount => _active?.Count ?? 0;

    /// <summary>
    /// Registers a catalog. Registering the active locale's catalog again re-applies it, which is how a
    /// re-imported <c>.po</c> reaches an open screen.
    /// </summary>
    public static void RegisterCatalog(XuiCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (catalog.Locale.Length == 0) return;

        Catalogs[catalog.Locale] = catalog;
        if (string.Equals(catalog.Locale, _locale, StringComparison.OrdinalIgnoreCase))
        {
            _active = catalog;
            RetranslateOpenScreens();
        }
    }

    /// <summary>
    /// Binds a font to a locale. Every text element switches to it while that locale is active.
    /// </summary>
    /// <remarks>
    /// Whole-asset substitution, not per-glyph fallback. XGUI's text has Bold / Italic / Mono slots but
    /// no fallback chain by character range, so a CJK locale swaps the font outright. Saying so is
    /// better than implying a glyph-level fallback that does not exist and would surface as tofu in
    /// exactly the locale nobody on the team reads.
    /// </remarks>
    public static void RegisterLocaleFont(string locale, AssetRef<FontAsset> font)
    {
        if (string.IsNullOrEmpty(locale)) return;
        LocaleFonts[locale] = font;
        if (string.Equals(locale, _locale, StringComparison.OrdinalIgnoreCase)) RetranslateOpenScreens();
    }

    /// <summary>The font bound to the active locale, if any.</summary>
    public static AssetRef<FontAsset>? ActiveLocaleFont
        => _locale.Length > 0 && LocaleFonts.TryGetValue(_locale, out AssetRef<FontAsset> font) ? font : null;

    /// <summary>
    /// Translates a string in the active locale, falling back to the string itself.
    /// </summary>
    /// <remarks>
    /// The source string <em>is</em> the key. A screen therefore reads correctly with no catalog at all,
    /// which is what makes an untranslated build shippable and a missing entry a cosmetic problem rather
    /// than a screen of <c>MENU_TITLE_1</c>.
    /// </remarks>
    public static string Tr(string source) => _active?.Translate(source) ?? source;

    /// <summary>Translates with a context, for one source string that needs two translations.</summary>
    public static string Tr(string source, string context) => _active?.Translate(source, context) ?? source;

    /// <summary>
    /// Re-applies the active locale to one screen. Called when a screen is built and on every switch.
    /// </summary>
    public static int Retranslate(XuiScreenInstance instance)
        => instance?.Root is { IsDisposed: false } root ? Retranslate(root) : 0;

    private static int Retranslate(GameObject node)
    {
        int applied = 0;
        if (node.GetComponent<XuiLocalizedText>() is { IsDisposed: false } localized)
        {
            localized.Apply();
            applied++;
        }

        ApplyLocaleFont(node);

        IReadOnlyList<GameObject> children = node.Children;
        for (int i = 0; i < children.Count; i++) applied += Retranslate(children[i]);
        return applied;
    }

    private static void ApplyLocaleFont(GameObject node)
    {
        if (ActiveLocaleFont is not { } font) return;
        if (node.GetComponent<TextComponent>() is { IsDisposed: false } text) text.Font = font;
    }

    private static void RetranslateOpenScreens()
    {
        foreach (XuiOpenScreen open in Open.Values) Retranslate(open.Instance);
    }
}
