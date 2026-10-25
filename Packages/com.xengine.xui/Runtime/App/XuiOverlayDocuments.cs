// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.XUI.Core;

namespace XEngine.XUI;

/// <summary>
/// The built-in overlay screens — message box, toast, loading — as <c>.xui</c> source.
/// </summary>
/// <remarks>
/// <para>
/// These are documents rather than hand-built hierarchies for two reasons. They are then made of
/// exactly the same twelve tags everything else is, so there is no second UI-construction path to keep
/// working. And an application can replace any of them by registering a document under the same screen
/// name, which is how a project reskins its dialogs without forking the package.
/// </para>
/// <para>
/// They are embedded text rather than assets so an overlay works before any asset database exists —
/// including in a test, and including when the thing being reported is that asset loading failed.
/// </para>
/// </remarks>
public static class XuiOverlayDocuments
{
    public const string MessageBoxScreen = "XuiMessageBox";
    public const string ToastScreen = "XuiToastStack";
    public const string LoadingScreen = "XuiLoading";

    /// <summary>
    /// A modal dialog: scrim, panel, title, message, and a row the code fills with buttons.
    /// </summary>
    public const string MessageBox = """
        <?xml version="1.0" encoding="utf-8"?>
        <XUI version="1">
          <Theme name="default">
            <Color name="scrim" value="#000000B0"/>
            <Color name="panel" value="#1E2433"/>
            <Color name="text" value="#E8EAF0"/>
            <Color name="muted" value="#9AA3B8"/>
          </Theme>

          <Screen name="XuiMessageBox" reference="1920x1080" layer="PopUp">
            <Image id="XImgScrim" anchor="stretch" color="scrim"/>
            <VStack id="XVsPanel" anchor="center" size="520x_" color="panel" spacing="16" padding="28">
              <Text id="XTxtTitle" size="_x36" font-size="26" align="center" color="text">Title</Text>
              <Text id="XTxtMessage" size="_x60" font-size="18" align="center" color="muted">Message</Text>
              <HStack id="XHsButtons" size="_x44" spacing="12" expand="true"/>
            </VStack>
          </Screen>
        </XUI>
        """;

    /// <summary>
    /// A bottom-anchored column the toast code appends rows to. Nothing is authored inside it because
    /// its contents are entirely transient.
    /// </summary>
    public const string Toast = """
        <?xml version="1.0" encoding="utf-8"?>
        <XUI version="1">
          <Screen name="XuiToastStack" reference="1920x1080" layer="Other">
            <VStack id="XVsToasts" anchor="bottom" size="640x_" pos="0,120" spacing="8"/>
          </Screen>
        </XUI>
        """;

    /// <summary>A blocking scrim with a label. Deliberately plain — it exists to stop input.</summary>
    public const string Loading = """
        <?xml version="1.0" encoding="utf-8"?>
        <XUI version="1">
          <Theme name="default">
            <Color name="scrim" value="#000000C0"/>
            <Color name="text" value="#E8EAF0"/>
          </Theme>

          <Screen name="XuiLoading" reference="1920x1080" layer="Other">
            <Image id="XImgScrim" anchor="stretch" color="scrim"/>
            <Text id="XTxtLabel" anchor="center" size="600x40" font-size="20" align="center" color="text">Loading…</Text>
          </Screen>
        </XUI>
        """;

    /// <summary>
    /// Parses one of the built-in documents. Cached: an overlay opens often, and its source never
    /// changes at runtime.
    /// </summary>
    internal static XuiDocument Parse(string screenName)
    {
        return screenName switch
        {
            MessageBoxScreen => _messageBox ??= XuiDocumentParser.Parse(MessageBox, "XuiMessageBox (built-in)"),
            ToastScreen => _toast ??= XuiDocumentParser.Parse(Toast, "XuiToastStack (built-in)"),
            LoadingScreen => _loading ??= XuiDocumentParser.Parse(Loading, "XuiLoading (built-in)"),
            _ => throw new ArgumentException("Not a built-in overlay screen: " + screenName),
        };
    }

    private static XuiDocument? _messageBox;
    private static XuiDocument? _toast;
    private static XuiDocument? _loading;
}
