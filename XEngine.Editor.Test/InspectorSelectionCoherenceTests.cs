using System.Reflection;
using XEngine.Editor.Core;
using XEngine.Editor.GUI.Panels;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.Events;
using XEngine.Quill;
using XEngine.Rosetta;
using XEngine.Runtime;
using XEngine.Scribe;
using XEngine.Vector;
using Xunit;

namespace XEngine.Editor.Test;

public sealed class InspectorSelectionCoherenceTests : EditorTestHarness
{
    private readonly InspectorPanel _panel = new();
    private readonly Paper _paper = new(new Renderer(), 420, 720, new FontAtlasSettings { AtlasSize = 32 });
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void ClearSelectionEmptiesUnlockedInspectorAndFolderCannotReviveIt()
    {
        WithFont(() =>
        {
            using var go = new GameObject("ClearSelectionTarget");
            Selection.Select(go); Frame(); AssertShows(go.Name);
            Selection.Clear(); Frame(); AssertEmpty(); Assert.DoesNotContain(go.Name, Text());
            Selection.Select(new ContentItem { IsFolder = true }); Frame(); AssertEmpty();
        });
    }

    [Fact]
    public void InitialDrawRemembersExistingTargetForFolderNavigation()
    {
        WithFont(() =>
        {
            using var go = new GameObject("InitiallySelectedTarget");
            Selection.Select(go); Frame(); AssertShows(go.Name);
            Selection.Select(new ContentItem { IsFolder = true }); Frame(); AssertShows(go.Name);
        });
    }

    [Fact]
    public void LockPinsSelectionAcrossChangeAndClearButUnlockFollowsEmptySelection()
    {
        WithFont(() =>
        {
            using var pinned = new GameObject("PinnedInspectorTarget");
            using var other = new GameObject("OtherInspectorTarget");
            Selection.Select(pinned); Frame(); ToggleLock();
            Selection.Select(other); Frame(); AssertShows(pinned.Name); Assert.DoesNotContain(other.Name, Text());
            Selection.Clear(); Frame(); AssertShows(pinned.Name);
            ToggleLock(); Frame(); AssertEmpty(); Assert.DoesNotContain(pinned.Name, Text());
        });
    }

    [Fact]
    public void ReloadCleanupResubscribesAndCloseReleasesSelectionCallback()
    {
        WithFont(() =>
        {
            using var before = new GameObject("BeforeReloadTarget");
            using var after = new GameObject("AfterReloadTarget");
            Selection.Select(before); Frame(); _panel.OnScriptReloadCleanup();
            Selection.Select(after); Frame(); AssertShows(after.Name);
            Selection.Select(new ContentItem { IsFolder = true }); Frame(); AssertShows(after.Name);
            Selection.Clear(); Frame(); AssertEmpty();
            _panel.OnClosed(); Selection.Select(before);
            Assert.Null(typeof(InspectorPanel).GetField("_lastInspectable", PrivateInstance)!.GetValue(_panel));
        });
    }

    private void WithFont(Action body)
    {
        var previousFont = EditorTheme.DefaultFont;
        var previousSelection = Selection.Selected.ToArray();
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var font = new FontFile(Path.Combine(root, "XEngine.Editor", "Resources", "Geist-Regular.ttf"));
        EditorTheme.DefaultFont = font;
        using var themeScope = Origami.PushTheme(EditorTheme.BuildOrigamiTheme());
        try { body(); }
        finally
        {
            _panel.OnClosed(); _paper.Canvas.Dispose();
            Selection.Clear();
            foreach (var selected in previousSelection) Selection.AddToSelection(selected);
            EditorTheme.DefaultFont = previousFont;
        }
    }

    private void Frame()
    {
        _paper.BeginFrame(1f / 60); Origami.BeginFrame(_paper, 1f / 60);
        using (_paper.Box("host").Size(420, 720).Enter()) _panel.OnGUI(_paper, 420, 720);
        Origami.EndFrame(_paper); _paper.EndFrame();
    }
    private List<string> Text()
    {
        var result = new List<string>();
        for (int i = 0; i < _paper.ElementCount; i++)
        {
            ref var data = ref _paper.GetElementData(i);
            if (data.Paragraph != null) result.Add(data.Paragraph);
            var handle = _paper.FindElementByID(data.ID);
            // Unfocused text fields draw their bound value without creating storage. A focus-loss
            // event materializes that real input value without changing the model or typing text.
            if (data.Cursor == PaperCursor.Text && data.IsHookedToParent)
                data.OnFocusChange?.Invoke(new FocusEvent(handle, false));
            var input = _paper.GetElementStorage(handle, "TextInputState", default(ElementBuilder.TextInputState));
            if (input.Value != null) result.Add(input.Value);
        }
        return result;
    }
    private void AssertEmpty() => Assert.Contains(Loc.Get("inspector.nothing_selected"), Text());
    private void AssertShows(string name) => Assert.Contains(name, Text());
    private void ToggleLock() => typeof(InspectorPanel).GetMethod("ToggleLock", PrivateInstance)!.Invoke(_panel, null);

    private sealed class Renderer : ICanvasRenderer
    {
        public void Dispose() { }
        public object CreateTexture(uint width, uint height) => new Int2((int)width, (int)height);
        public Int2 GetTextureSize(object texture) => (Int2)texture;
        public void SetTextureData(object texture, IntRect bounds, byte[] data) { }
        public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> calls) { }
    }
}
