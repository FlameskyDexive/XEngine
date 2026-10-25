// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

using XEngine.OrigamiUI;
using XEngine.Quill;
using XEngine.Runtime.Resources;
using XEngine.Vector;
using XEngine.Vector.Spatial;

using Color = System.Drawing.Color;

namespace XEngine.Editor.GUI;

/// <summary>An <see cref="IOrigamiIcon"/> backed by an embedded PNG (Unity editor chrome dumps).
/// The texture is loaded once and never mutated, so the icon is a stable paint input: its draw
/// depends only on the rectangle, the tint colour and the texture identity.</summary>
internal sealed class EditorPngIcon : IStableOrigamiIcon
{
    private const string ResourcePrefix = "XEngine.Editor.Resources.Icons.";
    private static readonly Dictionary<string, EditorPngIcon?> Cache = new(StringComparer.Ordinal);

    private readonly Texture2D _texture;

    public object? PaintDependency => _texture;

    private EditorPngIcon(Texture2D texture) => _texture = texture;

    internal static IOrigamiIcon? TryLoad(string resourceRelativeName)
    {
        if (string.IsNullOrEmpty(resourceRelativeName)) return null;
        if (Cache.TryGetValue(resourceRelativeName, out EditorPngIcon? cached))
            return cached;

        string resourceName = ResourcePrefix + resourceRelativeName;
        EditorPngIcon? icon = null;
        try
        {
            using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                Texture2D texture = Texture2D.LoadFromStream(stream);
                icon = new EditorPngIcon(texture);
            }
        }
        catch
        {
            icon = null;
        }

        Cache[resourceRelativeName] = icon;
        return icon;
    }

    public void Draw(Canvas canvas, Rect rect, Color color, float strokeWidth = 1.5f)
    {
        if (_texture.IsDisposed) return;

        float x = (float)rect.Min.X;
        float y = (float)rect.Min.Y;
        float w = (float)rect.Size.X;
        float h = (float)rect.Size.Y;
        if (w <= 0f || h <= 0f) return;

        canvas.SetBrushTexture(_texture);
        canvas.SetBrushTextureTransform(
            Transform2D.CreateTranslation(x, y + h) *
            Transform2D.CreateScale(w, -h));
        canvas.RectFilled(x, y, w, h, Color32.FromArgb(color.A, color.R, color.G, color.B));
        canvas.ClearBrushTexture();
    }
}
