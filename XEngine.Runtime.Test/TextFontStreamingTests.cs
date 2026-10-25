// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Resources;
using XEngine.Runtime.UI;
using XEngine.Vector;
using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>
/// A font <see cref="AssetRef{T}"/> can resolve to null while its asset is still loading
/// (async streaming); the bake that runs meanwhile uses the embedded fallback font. These
/// tests pin the two behaviours that keep that window harmless: the text rebakes when the
/// real font arrives (instead of staying on the fallback forever - historically a CJK label
/// assigned a CJK font stayed blank for the session), and characters the fallback cannot map
/// bake as visible .notdef boxes rather than nothing.
/// </summary>
public sealed class TextFontStreamingTests : RuntimeTestBase
{
    /// <summary>Backend that cannot load anything until <see cref="Available"/> is set - the
    /// deterministic stand-in for an asset that is still streaming in.</summary>
    private sealed class GatedFontBackend : AssetBackendBase
    {
        public EngineObject? Available;

        protected override EngineObject? LoadFresh(Guid assetId)
        {
            if (Available == null || Available.AssetID != assetId) return null;
            SetLoaded(assetId, Available);
            return Available;
        }
    }

    [Fact]
    public void Update_RebakesWhenAssignedFontResolvesAfterFirstBake()
    {
        AssetBackendBase? prevBackend = AssetDatabase.Current;
        bool prevAsync = AssetLoadingConfig.AsyncEnabled;
        AssetLoadingConfig.AsyncEnabled = false; // deterministic: .Res resolves synchronously through the gate
        var backend = new GatedFontBackend();
        AssetDatabase.Current = backend;
        Guid id = Guid.NewGuid();
        FontAsset? streamed = null;
        try
        {
            TextComponent text = CreateCanvasText(out GameCanvas canvas);
            text.Text = "登录";
            text.Size = 32;
            text.Font = new AssetRef<FontAsset>(id); // not loadable yet - "still streaming"

            canvas.RebuildIfDirty(); // bakes against the embedded fallback font
            Assert.Same(FontAsset.LoadDefault(), text.ResolvedFont);

            // The fallback has no CJK glyphs; the bake must still produce visible geometry
            // (.notdef boxes), never an empty mesh.
            Assert.NotNull(text.CachedMesh);
            Assert.True(text.CachedMesh!.VertexCount > 0, "unmapped characters must bake as tofu, not vanish");

            text.Update();           // first Update always dirties once (atlas-version stamp)
            canvas.RebuildIfDirty(); // absorb it
            text.Update();           // still unresolved: no rebake churn while the load is pending
            Assert.Equal(UIDirtyFlags.None, text.DirtyFlags & UIDirtyFlags.Vertices);

            using var stream = EmbeddedResources.GetStream("Assets/Defaults/Geist-Regular.ttf");
            using var ms = new System.IO.MemoryStream();
            stream.CopyTo(ms);
            streamed = new FontAsset("streamed", ms.ToArray()) { AssetID = id };
            backend.Available = streamed;

            text.Update(); // the resolution transition is the rebake trigger
            Assert.NotEqual(UIDirtyFlags.None, text.DirtyFlags & UIDirtyFlags.Vertices);
            Assert.NotEqual(UIDirtyFlags.None, text.DirtyFlags & UIDirtyFlags.Layout);
            Assert.Same(streamed, text.ResolvedFont);

            canvas.RebuildIfDirty();
            text.Update(); // stable afterwards: same instance, no further churn
            Assert.Equal(UIDirtyFlags.None, text.DirtyFlags & UIDirtyFlags.Vertices);
        }
        finally
        {
            AssetDatabase.Current = prevBackend;
            AssetLoadingConfig.AsyncEnabled = prevAsync;
            AssetDatabase.Forget(id);
            streamed?.Dispose();
        }
    }

    [Fact]
    public void GenerateMesh_KeepsEdgeGlyphsWhoseSdfPaddingLeavesTheRect()
    {
        TextComponent text = CreateCanvasText(out GameCanvas canvas);
        text.Text = "Account";
        text.Size = 20;
        text.Alignment = TextAlignment.CenterLeft; // pen starts at the rect's left edge

        canvas.RebuildIfDirty();

        // With clipping active the capture emits 6 vertices per glyph (2 triangles). The first
        // glyph's SDF quad pokes a couple of pixels left of the rect (distance-field padding);
        // culling it used to render "Account" as "ccount".
        Assert.NotNull(text.CachedMesh);
        Assert.Equal("Account".Length * 6, text.CachedMesh!.VertexCount);
    }

    private TextComponent CreateCanvasText(out GameCanvas canvas)
    {
        Scene scene = CreateScene(enable: false);
        GameObject canvasObject = CreateGameObject("Canvas");
        canvas = canvasObject.AddComponent<GameCanvas>();
        GameObject textObject = CreateGameObject("Text");
        textObject.SetParent(canvasObject, worldPositionStays: false);
        textObject.EnsureRectTransform();
        TextComponent text = textObject.AddComponent<TextComponent>();

        RectTransform rt = textObject.RectTransform!;
        rt.AnchorMin = Float2.Zero;
        rt.AnchorMax = Float2.Zero;
        rt.Pivot = new Float2(0f, 1f);
        rt.AnchoredPosition = new Float2(20f, 400f);
        rt.SizeDelta = new Float2(400f, 80f);

        scene.Add(canvasObject);
        scene.Enable();
        GameCanvas.ScreenSizeOverride = new Float2(800f, 450f);
        canvas.RebuildIfDirty();
        return text;
    }
}
