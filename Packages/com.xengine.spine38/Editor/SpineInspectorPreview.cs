using System;

using XEngine.Editor.Core;
using XEngine.Editor.GUI;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Spine38;
using XEngine.Vector;

using Color = System.Drawing.Color;

namespace XEngine.Spine38.Editor;

/// <summary>Shared inspector preview surface for SkeletonDataAsset and scene components.</summary>
internal sealed class SpineInspectorPreview : IDisposable
{
    public const float DefaultHeight = 220f;

    private PreviewRenderer? _preview;
    private Guid _assetId;
    private string _animationName = "";
    private string _skinName = "";

    public void Draw(Paper paper, string id, SkeletonDataAsset? asset, string? animationName, string? skinName,
        float width = 0f, float height = DefaultHeight)
    {
        using (paper.Column($"{id}_wrap").Width(UnitValue.Stretch()).Height(height).Margin(0, 0, 8, 8).Enter())
        {
            if (asset == null || Graphics.IsHeadless)
            {
                Origami.Label(paper, $"{id}_empty", "Assign a Skeleton Data Asset to preview.").Show();
                return;
            }

            float w = width > 8f ? width : 360f;
            _preview ??= new PreviewRenderer((int)Math.Max(8, w), (int)Math.Max(8, height));
            string anim = animationName ?? "";
            string skin = skinName ?? "";
            if (_assetId != asset.AssetID || !string.Equals(_animationName, anim, StringComparison.Ordinal)
                || !string.Equals(_skinName, skin, StringComparison.Ordinal))
            {
                SpinePreview.Apply(_preview, asset, string.IsNullOrEmpty(anim) ? null : anim,
                    string.IsNullOrEmpty(skin) ? null : skin);
                _assetId = asset.AssetID;
                _animationName = anim;
                _skinName = skin;
            }
            else
            {
                SpinePreview.Tick(_preview, EditDelta());
            }

            EditorApplication.Instance?.FrameScheduler.RegisterAnimation(Time.UnscaledTotalTime + 0.25);
            _preview.Resize((int)Math.Max(8, w), (int)Math.Max(8, height));
            _preview.MarkPreviewDirty();
            _preview.Render();

            Texture? tex = _preview.Result?.MainTexture;
            paper.Box($"{id}_rt")
                .Width(UnitValue.Stretch())
                .Height(height)
                .BackgroundColor(Color.FromArgb(255, 38, 38, 42))
                .Rounded(4)
                .OnPostLayout((handle, rect) => paper.Draw(ref handle, (canvas, r) =>
                {
                    float rx = (float)r.Min.X;
                    float ry = (float)r.Min.Y;
                    float rw = (float)r.Size.X;
                    float rh = (float)r.Size.Y;
                    if (tex == null || rw < 2f || rh < 2f)
                        return;
                    canvas.SetBrushTexture(tex);
                    canvas.SetBrushTextureTransform(
                        XEngine.Vector.Spatial.Transform2D.CreateTranslation(rx, ry + rh) *
                        XEngine.Vector.Spatial.Transform2D.CreateScale(rw, -rh));
                    canvas.RoundedRectFilled(rx, ry, rw, rh, 4, 4, 4, 4, new Color32(255, 255, 255, 255));
                    canvas.ClearBrushTexture();
                }));
        }
    }

    public void Dispose()
    {
        _preview?.Dispose();
        _preview = null;
        _assetId = Guid.Empty;
    }

    private static float EditDelta()
    {
        float dt = (float)Time.UnscaledDeltaTime;
        return dt > 0f ? Math.Min(dt, 0.05f) : 1f / 60f;
    }
}
