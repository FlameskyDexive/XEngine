// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

using XEngine.Runtime;
using XEngine.Runtime.UI;
using XEngine.Vector;
using XEngine.Vector.Geometry;

namespace XEngine.XUI;

/// <summary>Drives toast expiry. Attached to the toast screen so it lives and dies with it.</summary>
public sealed class XuiToastTicker : MonoBehaviour
{
    public override void Update() => Xui.TickToasts(Time.DeltaTime);
}

/// <summary>
/// Transient messages that stack and expire on their own.
/// </summary>
/// <remarks>
/// A toast is fire-and-forget by definition — the caller says something happened and moves on. So the
/// API takes no handle and returns nothing to dispose; the stack owns every toast's lifetime, and an
/// abandoned one cannot leak because expiry is time-based rather than caller-driven.
/// </remarks>
public static partial class Xui
{
    private const int MaxToasts = 5;

    private static readonly List<ActiveToast> Toasts = new();

    /// <summary>Seconds a toast stays up when the caller does not say.</summary>
    public static float DefaultToastSeconds { get; set; } = 3f;

    /// <summary>How many toasts are showing.</summary>
    public static int ToastCount => Toasts.Count;

    /// <summary>Shows a transient message at the bottom of the screen.</summary>
    public static void Toast(string message, float? seconds = null)
    {
        if (string.IsNullOrEmpty(message)) return;

        Toasts.Add(new ActiveToast(message, seconds ?? DefaultToastSeconds));

        // The oldest goes when the stack is full: a burst of messages should not push the screen off
        // the top, and the newest is the one the user is waiting to see.
        while (Toasts.Count > MaxToasts) Toasts.RemoveAt(0);

        RebuildToasts();
    }

    /// <summary>Removes every toast immediately.</summary>
    public static void ClearToasts()
    {
        if (Toasts.Count == 0) return;
        Toasts.Clear();
        Close(XuiOverlayDocuments.ToastScreen);
    }

    /// <summary>
    /// Ages the stack. Called by <see cref="XuiToastTicker"/> every frame; exposed so a test can
    /// advance time without waiting for one.
    /// </summary>
    public static void TickToasts(float deltaSeconds)
    {
        if (Toasts.Count == 0 || deltaSeconds <= 0f) return;

        bool expired = false;
        for (int i = Toasts.Count - 1; i >= 0; i--)
        {
            ActiveToast toast = Toasts[i];
            toast.Remaining -= deltaSeconds;
            if (toast.Remaining > 0f) continue;
            Toasts.RemoveAt(i);
            expired = true;
        }

        if (!expired) return;
        if (Toasts.Count == 0) Close(XuiOverlayDocuments.ToastScreen);
        else RebuildToasts();
    }

    private static void RebuildToasts()
    {
        XuiOpenScreen open = XuiOverlays.Rebuild(
            XuiOverlayDocuments.ToastScreen, BuildToastDocument());

        // The ticker rides on the screen root, so it is destroyed with the stack and cannot outlive it.
        if (open.Instance.Root is { IsDisposed: false } root
            && root.GetComponent<XuiToastTicker>() is null)
        {
            root.AddComponent<XuiToastTicker>();
        }
    }

    private static string BuildToastDocument()
    {
        var builder = new StringBuilder(512);
        builder.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<XUI version=\"1\">\n");
        builder.Append("  <Screen name=\"").Append(XuiOverlayDocuments.ToastScreen)
            .Append("\" reference=\"1920x1080\" layer=\"Other\">\n");
        builder.Append("    <VStack id=\"XVsToasts\" anchor=\"bottom\" size=\"640x_\" pos=\"0,120\" spacing=\"8\">\n");

        for (int i = 0; i < Toasts.Count; i++)
        {
            builder.Append("      <HStack id=\"XHsToast").Append(i.ToString(CultureInfo.InvariantCulture))
                .Append("\" size=\"_x44\" color=\"#1E2433E0\" padding=\"12\">\n");
            builder.Append("        <Text id=\"XTxtToast").Append(i.ToString(CultureInfo.InvariantCulture))
                .Append("\" size=\"_x20\" font-size=\"16\" align=\"center\" color=\"#E8EAF0\">")
                .Append(XuiOverlays.Escape(Toasts[i].Message)).Append("</Text>\n");
            builder.Append("      </HStack>\n");
        }

        builder.Append("    </VStack>\n  </Screen>\n</XUI>\n");
        return builder.ToString();
    }

    private sealed class ActiveToast
    {
        public ActiveToast(string message, float seconds)
        {
            Message = message;
            Remaining = seconds;
        }

        public string Message { get; }
        public float Remaining { get; set; }
    }

    // ------------------------------------------------------------------
    //  Loading
    // ------------------------------------------------------------------

    private static int _loadingDepth;

    /// <summary>True while a blocking loading overlay is up.</summary>
    public static bool IsLoading => _loadingDepth > 0;

    /// <summary>
    /// Shows a blocking overlay. Reference-counted: two systems loading at once produce one overlay,
    /// and it comes down when the last of them finishes rather than the first.
    /// </summary>
    public static void ShowLoading(string? label = null)
    {
        _loadingDepth++;
        if (_loadingDepth > 1 && label == null) return;

        XuiOverlays.Rebuild(XuiOverlayDocuments.LoadingScreen, BuildLoadingDocument(label));
    }

    /// <summary>Releases one <see cref="ShowLoading"/>. The overlay closes at zero.</summary>
    public static void HideLoading()
    {
        if (_loadingDepth == 0) return;
        _loadingDepth--;
        if (_loadingDepth == 0) Close(XuiOverlayDocuments.LoadingScreen);
    }

    /// <summary>Drops the overlay whatever the count. For a failure path that lost track.</summary>
    public static void ResetLoading()
    {
        _loadingDepth = 0;
        Close(XuiOverlayDocuments.LoadingScreen);
    }

    private static string BuildLoadingDocument(string? label)
    {
        string text = XuiOverlays.Escape(string.IsNullOrEmpty(label) ? "Loading…" : label);
        return XuiOverlayDocuments.Loading.Replace(
            ">Loading…</Text>", ">" + text + "</Text>", StringComparison.Ordinal);
    }
}
