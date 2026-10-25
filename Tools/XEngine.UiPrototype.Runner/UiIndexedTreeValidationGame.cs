using System.Text.Json;
using XEngine.Editor.UI;
using XEngine.Editor.UI.Rendering;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Quill;
using XEngine.Runtime;
using XEngine.Runtime.GUI;
using XEngine.Runtime.Resources;
using XEngine.Scribe;
using XEngine.Vector;

namespace XEngine.UiPrototype;

internal sealed class UiIndexedTreeValidationGame(string destination, bool retained = false) : Game
{
    public override void OnGui(Scene? scene, Paper paper)
    {
        try { Validate(); }
        catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 2; }
        Quit();
    }
    private void Validate()
    {
        Directory.CreateDirectory(destination);
        using var renderer = new PaperRenderer(); renderer.Initialize(360, 240);
        var font = new FontFile(Path.Combine(AppContext.BaseDirectory, "Geist-Regular.ttf"));
        var theme = OrigamiTheme.CreateDefaults(); theme.Font = font; Origami.SetTheme(theme);
        var nodes = new List<TreeNode>(); int[] parents = new int[270];
        for (int i = 0; i < parents.Length; i++)
        {
            parents[i] = i % 9 == 0 ? -1 : i - i % 9;
            nodes.Add(new TreeNode { Id=i.ToString(), Label=$"Node {i}", Depth=i % 9 == 0 ? 0 : 1, HasChildren=i % 9 == 0, DefaultExpanded=true });
        }
        var reports = new List<object>();
        foreach (float dpi in new[] { 1f, 1.25f, 1.5f, 2f })
        {
            int width = (int)(360 * dpi), height = (int)(240 * dpi);
            renderer.UpdateProjection(width, height);
            using var target = new RenderTexture(width, height, true, [TextureImageFormat.Color4b]); renderer.SetRenderTarget(target.frameBuffer);
            var budget = new UiMemoryBudget(32 * 1024 * 1024);
            var reference = new Paper(new UiCanvasAtlasOwner(renderer, budget), 360, 240, new FontAtlasSettings { AtlasSize=512 });
            var candidate = new Paper(new UiCanvasAtlasOwner(renderer, budget), 360, 240, new FontAtlasSettings { AtlasSize=512 });
            var store = new Dictionary<string, bool>(); var index = new Index(parents);
            var persistentTree = Origami.Tree(candidate, "tree", 360, 240);
            // Production-style region: final visual-state comparison decides recapture, so fold
            // transition frames must be detected without explicit invalidation.
            var region = retained ? new UiPaperSubtreeRegion(candidate, budget, budget, compareVisualState: true) : null;
            try
            {
                foreach (var paper in new[] { reference, candidate })
                { paper.Canvas.BeginFrame(360, 240, dpi); paper.Canvas.DrawText("Node 0123456789", 0, 0, new Color32(255,255,255,255), theme.Metrics.FontSize, font); }
                Compare("top", 0);
                Compare("middle", 1280);
                Compare("bottom", 100000);
                store["0"] = false; index.SetExpanded("0", false); Compare("collapsed-top", 0);
                store["0"] = true; index.SetExpanded("0", true); Compare("reopened", 0);
                // Fold transitions: 0.05 s steps advance the 0.15 s lerp in thirds; every
                // intermediate frame must match the legacy animated wrapper pixel for pixel.
                store["9"] = false; index.SetExpanded("9", false); CompareAnimated("collapsing", 0, 4);
                store["9"] = true; index.SetExpanded("9", true); CompareAnimated("expanding", 0, 4);
                // Fold above the viewport while scrolled: rows below shift without gaps.
                Compare("scrolled", 640);
                store["9"] = false; index.SetExpanded("9", false); CompareAnimated("collapsing-above", 640, 4);
                store["9"] = true; index.SetExpanded("9", true); CompareAnimated("expanding-above", 640, 4);
            }
            finally { region?.Dispose(); persistentTree.Dispose(); reference.Canvas.Dispose(); candidate.Canvas.Dispose(); }
            if (budget.ReservedBytes != 0) throw new InvalidOperationException("Tree atlas admission leak.");

            void Compare(string stage, int scroll)
            {
                // Settle existing scrollbar/caret/child wrapper animation for the static reference.
                for (int frame = 0; frame < 12; frame++) { region?.Invalidate(); Draw(reference, null, scroll); Draw(candidate, index, scroll); }
                Draw(reference, null, scroll); Wait(); byte[] expected = Read(); uint fullElements = reference.CountOfAllElements;
                long captures = region?.CaptureCount ?? 0;
                Draw(candidate, index, scroll); Wait(); byte[] actual = Read();
                if (region != null && (captures == 0 || region.CaptureCount != captures)) throw new InvalidOperationException("Stable subtree recaptured.");
                if (region?.LastStats.BufferUploadBytes > 0) throw new InvalidOperationException("Stable subtree uploaded geometry.");
                Save(expected, $"dpi-{dpi}-{stage}-reference.png", width, height); Save(actual, $"dpi-{dpi}-{stage}-indexed.png", width, height);
                if (!expected.AsSpan().SequenceEqual(actual)) throw new InvalidOperationException($"Indexed tree pixels differ: {dpi}/{stage}.");
                if (candidate.CountOfAllElements >= 180) throw new InvalidOperationException("Indexed path built too many elements.");
                reports.Add(new { Dpi=dpi, Stage=stage, ReferenceElements=fullElements, IndexedElements=candidate.CountOfAllElements,
                    BindingCreations=persistentTree.BindingCreations, CachedRowIds=persistentTree.CachedRowIdCount,
                    SubtreeCaptures=region?.CaptureCount, SubtreeStats=region?.LastStats, PixelsEqual=true });
            }
            void CompareAnimated(string stage, int scroll, int frames)
            {
                for (int frame = 1; frame <= frames; frame++)
                {
                    long captures = region?.CaptureCount ?? 0;
                    Draw(reference, null, scroll, .05f); Wait(); byte[] expected = Read(); uint fullElements = reference.CountOfAllElements;
                    Draw(candidate, index, scroll, .05f); Wait(); byte[] actual = Read();
                    bool animating = persistentTree.AnimatingFoldNode >= 0;
                    if (region != null && animating && region.CaptureCount == captures) throw new InvalidOperationException("Animating subtree was not recaptured.");
                    Save(expected, $"dpi-{dpi}-{stage}-f{frame}-reference.png", width, height); Save(actual, $"dpi-{dpi}-{stage}-f{frame}-indexed.png", width, height);
                    // Rows, carets, zebra stripes and the animating clip must match exactly. The overlay
                    // scrollbar thumb is the one intentional deviation: the legacy ScrollView positions it
                    // from the previous frame's post-layout content height, while the indexed path passes the
                    // current height explicitly, so mid-transition the thumb leads legacy by one frame
                    // (sub-pixel anti-aliasing at its two ends). Bound that strip instead of masking it silently:
                    // only the thumb's end rows may differ, never the track or a whole thumb offset.
                    int barPx = (int)MathF.Ceiling(6f * dpi);
                    if (!EqualOutsideRightColumns(expected, actual, width, height, barPx, out int thumbDiffPixels, out int thumbMaxDelta))
                        throw new InvalidOperationException($"Indexed fold transition pixels differ: {dpi}/{stage} frame {frame}.");
                    if (thumbDiffPixels > 4 * barPx) throw new InvalidOperationException($"Scrollbar strip differs beyond the thumb ends: {dpi}/{stage} frame {frame} ({thumbDiffPixels} pixels, max delta {thumbMaxDelta}).");
                    if (frame == 1 && !animating) throw new InvalidOperationException($"Fold did not animate: {stage}.");
                    if (frame == frames && animating) throw new InvalidOperationException($"Fold still animating after {frames} frames: {stage}.");
                    if (candidate.CountOfAllElements >= 200) throw new InvalidOperationException("Indexed transition built too many elements.");
                    reports.Add(new { Dpi=dpi, Stage=$"{stage}-f{frame}", ReferenceElements=fullElements, IndexedElements=candidate.CountOfAllElements,
                        FoldProgress=persistentTree.FoldProgress, Animating=animating, SubtreeCaptures=region?.CaptureCount, PixelsEqual=true,
                        ScrollbarThumbDiffPixels=thumbDiffPixels, ScrollbarThumbMaxChannelDelta=thumbMaxDelta });
                }
            }
            void Draw(Paper paper, ITreeRowIndex? mapping, int scroll, float dt = .25f)
            {
                paper.BeginFrame(dt, dpi); Origami.ScrollTo("tree_scroll", new Float2(0, scroll));
                using (paper.Column("tree_region").Width(360).Height(240).Enter())
                {
                if (mapping != null) region?.Attach(paper.CurrentParent);
                (mapping == null ? Origami.Tree(paper, "tree", 360, 240) : persistentTree).Nodes(nodes).RowIndex(mapping).ExpandStateStore(store)
                    .ZebraStripes().IsSelected(static n => n.Id == "65").Show();
                }
                using var clear = Graphics.GetCommandBuffer("N4 tree clear"); clear.SetRenderTarget(target.frameBuffer);
                clear.SetViewport(0,0,(uint)width,(uint)height); clear.DisableScissor();
                clear.ClearRenderTarget(ClearFlags.Color | ClearFlags.Depth | ClearFlags.Stencil, new Color(0f,0f,0f,1f)); Graphics.Submit(clear);
                paper.EndFrame();
            }
            byte[] Read()
            {
                byte[] bytes = new byte[target.MainTexture.GetSize()]; target.MainTexture.GetData<byte>(bytes.AsMemory());
                if (Graphics.Device!.Backend == XEngine.Runtime.RHI.GraphicsBackend.OpenGL)
                    for(int y=0;y<height/2;y++) for(int x=0;x<width*4;x++)
                    {int a=y*width*4+x,b=(height-y-1)*width*4+x;(bytes[a],bytes[b])=(bytes[b],bytes[a]);}
                return bytes;
            }
        }
        File.WriteAllText(Path.Combine(destination,"validation.json"), JsonSerializer.Serialize(new {Backend=Graphics.Device!.Backend.ToString(), RetainedSubtree=retained, Results=reports,
            Scope="Static settled 270-node actual Origami tree: persistent indexed builder versus fresh legacy builder, scrolling and fold endpoints. No animation/input or real Hierarchy acceptance."}, new JsonSerializerOptions {WriteIndented=true}));
    }
    private sealed class Index(int[] parents) : ITreeRowIndex
    {
        private readonly UiTreeVisibilityIndex _index = new(parents);
        public int Count => _index.VisibleCount;
        public int GetNodeIndex(int rank) => _index.GetNodeIndex(rank);
        public bool IsExpanded(int nodeIndex) => _index.IsExpanded(nodeIndex);
        public void SetExpanded(string id, bool expanded)
        {
            int node = int.Parse(id); long revision = _index.Revision;
            _index.SetExpanded(node, expanded);
            if (_index.Revision != revision) { FoldRevision++; LastFoldNodeIndex = node; LastFoldExpanded = expanded; }
        }
        public int GetVisibleRank(int nodeIndex) => _index.GetVisibleRank(nodeIndex);
        public int GetVisibleDescendantCount(int nodeIndex) => _index.GetVisibleDescendantCount(nodeIndex);
        public long FoldRevision { get; private set; }
        public int LastFoldNodeIndex { get; private set; } = -1;
        public bool LastFoldExpanded { get; private set; }
        public void SetTransientVisible(int nodeIndex, bool visible) => _index.SetTransientVisible(nodeIndex, visible);
    }
    /// <summary>Exact RGBA equality left of the last <paramref name="rightColumns"/> pixels; reports how
    /// many pixels inside that strip differ and their largest per-channel delta.</summary>
    private static bool EqualOutsideRightColumns(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, int width, int height, int rightColumns,
        out int stripDiffPixels, out int stripMaxDelta)
    {
        stripDiffPixels = 0; stripMaxDelta = 0;
        int split = Math.Max(0, width - rightColumns) * 4, stride = width * 4;
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            if (!expected.Slice(row, split).SequenceEqual(actual.Slice(row, split))) return false;
            for (int x = split; x < stride; x += 4)
            {
                int delta = 0;
                for (int c = 0; c < 4; c++) delta = Math.Max(delta, Math.Abs(expected[row + x + c] - actual[row + x + c]));
                if (delta == 0) continue;
                stripDiffPixels++; stripMaxDelta = Math.Max(stripMaxDelta, delta);
            }
        }
        return true;
    }
    private static void Wait() { using var command=Graphics.GetCommandBuffer("N4 tree readback"); Graphics.SubmitAndWait(command); }
    private void Save(byte[] bytes,string name,int width,int height)
    { using var image=new ImageMagick.MagickImage(); image.ReadPixels(bytes,new ImageMagick.PixelReadSettings((uint)width,(uint)height,ImageMagick.StorageType.Char,ImageMagick.PixelMapping.RGBA)); image.Write(Path.Combine(destination,name),ImageMagick.MagickFormat.Png32); }
}
