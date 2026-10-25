using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace XHub.Infrastructure;

/// <summary>Renders a live window to PNG (acceptance evidence capture).</summary>
public static class WindowCapture
{
    public static void Save(Window window, string path)
    {
        double scaling = window.RenderScaling;
        var pixelSize = new PixelSize(
            (int)Math.Ceiling(window.Bounds.Width * scaling),
            (int)Math.Ceiling(window.Bounds.Height * scaling));
        using var bitmap = new RenderTargetBitmap(pixelSize, new Vector(96 * scaling, 96 * scaling));
        bitmap.Render(window);
        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        using var stream = File.Create(path);
        bitmap.Save(stream);
    }
}
