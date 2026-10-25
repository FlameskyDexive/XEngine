// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

using XEngine.Async;
using XEngine.Runtime;
using XEngine.Runtime.UI;
using XEngine.XUI.Core;

namespace XEngine.XUI;

/// <summary>
/// Shared plumbing for the built-in overlays: registering their documents and rebuilding one from
/// generated source.
/// </summary>
/// <remarks>
/// A dialog's button row and a toast stack both have a shape that depends on the call, and the obvious
/// answer — build those children in C# — would create a second UI-construction path that has to be
/// kept working alongside the assembler forever. Generating the document instead keeps exactly one
/// path: the overlays are made of the same twelve tags as everything else, and a project can restyle
/// them by registering its own document under the same screen name.
/// </remarks>
internal static class XuiOverlays
{
    /// <summary>
    /// Registers a generated document under a screen name and shows it, replacing whatever was there.
    /// </summary>
    internal static XuiOpenScreen Rebuild(string screenName, string documentSource)
    {
        // Close first: the screen is being rebuilt from new source, and reusing the old hierarchy would
        // show the previous dialog's buttons.
        Xui.Close(screenName);

        var asset = new XuiDocumentAsset { Source = documentSource, Name = screenName };
        if (asset.Document == null)
        {
            throw new InvalidOperationException(
                "The generated " + screenName + " document does not parse: " + asset.ParseError);
        }

        Table.Add(asset);
        return Xui.ShowScreen(screenName);
    }

    /// <summary>
    /// The table the overlays register into. It wraps whatever the application already set, so
    /// registering an overlay never drops the app's own documents.
    /// </summary>
    private static XuiDocumentTable Table
    {
        get
        {
            if (Xui.Documents is XuiDocumentTable existing) return existing;

            var table = new XuiDocumentTable();
            Xui.Documents = table;
            return table;
        }
    }

    /// <summary>Escapes text for an XML attribute or text node in a generated document.</summary>
    internal static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);
    }
}

/// <summary>One button in a message box.</summary>
public readonly struct XuiMessageBoxButton
{
    public XuiMessageBoxButton(string label, bool primary = false)
    {
        Label = label;
        Primary = primary;
    }

    public string Label { get; }

    /// <summary>Drawn in the accent colour. The affirmative action, if there is one.</summary>
    public bool Primary { get; }

    public static implicit operator XuiMessageBoxButton(string label) => new(label);
}

/// <summary>
/// Awaitable modal dialogs.
/// </summary>
/// <remarks>
/// <para>
/// The call site reads as a question with an answer:
/// <c>int choice = await Xui.MessageBox("Delete", "Are you sure?", "Cancel", "Delete");</c> — no
/// callback, no state machine spread across three methods.
/// </para>
/// <para>
/// Concurrent calls <b>queue</b>. Two systems asking a question at the same moment is normal, and
/// without a queue the second dialog would replace the first and its caller would wait forever.
/// </para>
/// </remarks>
public static partial class Xui
{
    private static readonly Queue<PendingModal> ModalQueue = new();
    private static PendingModal? _activeModal;

    /// <summary>True while a dialog is showing or queued.</summary>
    public static bool HasModal => _activeModal != null || ModalQueue.Count > 0;

    /// <summary>
    /// Shows a dialog and completes with the index of the button pressed.
    /// </summary>
    public static XTask<int> MessageBox(string title, string message, params XuiMessageBoxButton[] buttons)
    {
        if (buttons == null || buttons.Length == 0)
            buttons = new XuiMessageBoxButton[] { new("OK", primary: true) };

        var source = AutoResetXTaskCompletionSource<int>.Create();
        ModalQueue.Enqueue(new PendingModal(title, message, buttons, source));
        if (_activeModal == null) PumpModalQueue();
        return source.Task;
    }

    /// <summary>A two-button confirm. True when the primary (second) button was pressed.</summary>
    public static async XTask<bool> Confirm(
        string title, string message, string cancelLabel = "Cancel", string confirmLabel = "OK")
    {
        int choice = await MessageBox(
            title, message,
            new XuiMessageBoxButton(cancelLabel),
            new XuiMessageBoxButton(confirmLabel, primary: true));
        return choice == 1;
    }

    /// <summary>Dismisses the dialog on screen as if <paramref name="choice"/> had been pressed.</summary>
    public static bool DismissModal(int choice = -1)
    {
        if (_activeModal == null) return false;
        CompleteModal(choice);
        return true;
    }

    private static void PumpModalQueue()
    {
        if (_activeModal != null || ModalQueue.Count == 0)
        {
            if (_activeModal == null) Close(XuiOverlayDocuments.MessageBoxScreen);
            return;
        }

        PendingModal pending = ModalQueue.Dequeue();
        _activeModal = pending;

        XuiOpenScreen open = XuiOverlays.Rebuild(
            XuiOverlayDocuments.MessageBoxScreen,
            BuildMessageBoxDocument(pending.Title, pending.Message, pending.Buttons));

        for (int i = 0; i < pending.Buttons.Length; i++)
        {
            int choice = i;
            if (open.Instance.Get<Button>("XBtn" + i.ToString(CultureInfo.InvariantCulture))
                is { IsDisposed: false } button)
            {
                button.OnClick += () => CompleteModal(choice);
            }
        }
    }

    private static void CompleteModal(int choice)
    {
        PendingModal? pending = _activeModal;
        _activeModal = null;
        pending?.Source.TrySetResult(choice);

        // Show the next one, or take the dialog down when the queue is empty.
        PumpModalQueue();
    }

    private static string BuildMessageBoxDocument(
        string title, string message, XuiMessageBoxButton[] buttons)
    {
        var builder = new StringBuilder(XuiOverlayDocuments.MessageBox.Length + 256);
        string template = XuiOverlayDocuments.MessageBox;

        // The template carries an empty button row; the generated document fills it in.
        const string EmptyRow = """<HStack id="XHsButtons" size="_x44" spacing="12" expand="true"/>""";
        int rowIndex = template.IndexOf(EmptyRow, StringComparison.Ordinal);

        builder.Append(template, 0, rowIndex);
        builder.Append("""<HStack id="XHsButtons" size="_x44" spacing="12" expand="true">""");
        for (int i = 0; i < buttons.Length; i++)
        {
            builder.Append("\n            <Btn id=\"XBtn").Append(i.ToString(CultureInfo.InvariantCulture))
                .Append("\" size=\"_x44\" color=\"")
                .Append(buttons[i].Primary ? "#4C8DFF" : "#2A3346")
                .Append("\">").Append(XuiOverlays.Escape(buttons[i].Label)).Append("</Btn>");
        }
        builder.Append("\n          </HStack>");
        builder.Append(template, rowIndex + EmptyRow.Length, template.Length - rowIndex - EmptyRow.Length);

        return builder.ToString()
            .Replace(">Title</Text>", ">" + XuiOverlays.Escape(title) + "</Text>", StringComparison.Ordinal)
            .Replace(">Message</Text>", ">" + XuiOverlays.Escape(message) + "</Text>", StringComparison.Ordinal);
    }

    private sealed class PendingModal
    {
        public PendingModal(
            string title,
            string message,
            XuiMessageBoxButton[] buttons,
            AutoResetXTaskCompletionSource<int> source)
        {
            Title = title;
            Message = message;
            Buttons = buttons;
            Source = source;
        }

        public string Title { get; }
        public string Message { get; }
        public XuiMessageBoxButton[] Buttons { get; }
        public AutoResetXTaskCompletionSource<int> Source { get; }
    }
}
