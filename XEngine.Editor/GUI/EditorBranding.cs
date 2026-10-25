// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;
using System.Reflection;

using XEngine.Runtime.Resources;
using XEngine.Vector;
using XEngine.Vector.Spatial;

namespace XEngine.Editor.GUI;

/// <summary>Shared raster branding used by the editor's launcher and startup sequence.</summary>
internal static class EditorBranding
{
    private const string LogoResourceName = "XEngine.Editor.Resources.xengine.png";
    // Visible mark extents in xengine.png. Removing the empty square canvas keeps the logo
    // centered at small launcher sizes without modifying the shared source image.
    private const float MarkLeft = 0.068f;
    private const float MarkTop = 0.145f;
    private const float MarkWidth = 0.870f;
    private const float MarkHeight = 0.669f;
    private const float MarkAspectRatio = MarkWidth / MarkHeight;
    private static Texture2D? s_logo;
    private static bool s_logoFailed;
    private static DateTime s_logoFailedAtUtc;

    internal static Texture2D? Logo
    {
        get
        {
            if (s_logo != null)
                return s_logo;
            if (s_logoFailed)
            {
                // Retry periodically so the logo returns after a device restore, without
                // hammering a broken device every frame.
                if ((DateTime.UtcNow - s_logoFailedAtUtc).TotalSeconds < 5)
                    return null;
            }

            try
            {
                using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(LogoResourceName);
                if (stream == null)
                    return null;

                s_logo = Texture2D.LoadFromStream(stream);
                s_logoFailed = false;
                return s_logo;
            }
            catch (Exception ex)
            {
                // GPU ops (texture upload) can fail transiently — e.g. after a Vulkan device loss
                // during project open the launcher still paints its header. Degrade to "no logo
                // this frame" instead of killing the process; retry on the 5s cadence above.
                Runtime.Debug.LogWarning($"[Branding] logo upload skipped: {ex.Message}");
                s_logoFailed = true;
                s_logoFailedAtUtc = DateTime.UtcNow;
                return null;
            }
        }
    }

    internal static void Draw(XEngine.Quill.Canvas canvas, Rect rect, byte alpha = 255)
    {
        Texture2D? logo = Logo;
        if (logo == null)
            return;

        float availableWidth = (float)rect.Size.X;
        float availableHeight = (float)rect.Size.Y;
        float width = MathF.Min(availableWidth, availableHeight * MarkAspectRatio);
        float height = width / MarkAspectRatio;
        float x = (float)rect.Min.X + (availableWidth - width) * 0.5f;
        float y = (float)rect.Min.Y + (availableHeight - height) * 0.5f;

        canvas.SetBrushTexture(logo);
        // Textures use a bottom-left V origin. The source image was authored top-left, so map the
        // cropped mark with a negative V scale; this keeps the upper and lower mark proportions
        // symmetric in the launcher instead of sampling the crop upside down.
        canvas.SetBrushTextureTransform(
            Transform2D.CreateTranslation(
                x - width * MarkLeft / MarkWidth,
                y + height * (1f - MarkTop) / MarkHeight) *
            Transform2D.CreateScale(width / MarkWidth, -height / MarkHeight));
        canvas.RectFilled(x, y, width, height, Color32.FromArgb(alpha, 255, 255, 255));
        canvas.ClearBrushTexture();
    }
}
