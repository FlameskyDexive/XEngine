using System.Reflection;
using XEngine.Editor.Core;
using XEngine.Editor.GUI.Panels;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Quill;
using XEngine.Runtime;
using XEngine.Vector;
using Xunit;

namespace XEngine.Editor.Test;

public sealed class HierarchyIndexedKeyboardTests
{
    [Fact]
    public void RecycledTreeNodesDropSceneReferencesAndThePoolIsCapped()
    {
        // A pooled node that still points at its GameObject pins a removed subtree in memory
        // (100k objects stayed live after removal + forced GC); the pool must not size to the peak.
        var panel = new HierarchyPanel();
        static FieldInfo Field(string name) => typeof(HierarchyPanel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        var nodes = (List<TreeNode>)Field("_treeNodes").GetValue(panel)!;
        var pool = (List<TreeNode>)Field("_treeNodePool").GetValue(panel)!;
        var objects = new List<GameObject>();
        for (int i = 0; i < 5000; i++)
        {
            var go = new GameObject("LD " + i); objects.Add(go);
            nodes.Add(new TreeNode { Id = go.Identifier.ToString(), Label = go.Name, UserData = go, Depth = 0 });
        }
        try
        {
            typeof(HierarchyPanel).GetMethod("RecycleTreeNodes", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, null);
            Assert.Empty(nodes);
            Assert.Equal(4096, pool.Count);
            Assert.All(pool, node => { Assert.Null(node.UserData); Assert.Equal("", node.Id); Assert.Equal("", node.Label); });
        }
        finally { foreach (var go in objects) go.Dispose(); }
    }

    [Fact]
    public void IndexedKeyboardMatchesLegacySelectionAndFoldTrace()
    {
        using var a = new GameObject("A"); using var b = new GameObject("B");
        using var c = new GameObject("C"); using var d = new GameObject("D"); using var e = new GameObject("E");
        b.SetParent(a); c.SetParent(b); d.SetParent(a);
        var nodes = new List<TreeNode> {
            Node(a,0,true), Node(b,1,true), Node(c,2,false), Node(d,1,false), Node(e,0,false)
        };
        var flat = new List<object> { a,b,c,d,e };
        var actions = new (PaperKey Key, bool Shift)[] {
            (PaperKey.Down,false),(PaperKey.Right,false),(PaperKey.Right,false),
            (PaperKey.Left,false),(PaperKey.Left,false),(PaperKey.Down,false),
            (PaperKey.Up,false),(PaperKey.Right,false),(PaperKey.Down,true),
            (PaperKey.Up,true),(PaperKey.Left,false),(PaperKey.Left,false),
            (PaperKey.Down,false),(PaperKey.Up,false),(PaperKey.Right,false)
        };
        try { Assert.Equal(Trace(false), Trace(true)); }
        finally { Selection.Clear(); }

        List<string> Trace(bool indexed)
        {
            Selection.Clear(); var panel = new HierarchyPanel();
            var paper = new Paper(new Renderer(), 200,200,new FontAtlasSettings { AtlasSize=32 });
            var stored = (Dictionary<string,bool>)Field("_expandStore").GetValue(panel)!;
            var resolved = (Dictionary<string,bool>)Field("_expandState").GetValue(panel)!;
            if (indexed) Field("_rowIndex").SetValue(panel,new HierarchyRowIndex(nodes,stored,resolved));
            var trace = new List<string>();
            try
            {
                foreach (var action in actions)
                {
                    if (!indexed)
                        foreach(var node in nodes) resolved[node.Id] = node.HasChildren && (stored.TryGetValue(node.Id,out bool state) ? state : node.DefaultExpanded);
                    paper.ClearInput(); paper.BeginFrame(1f/60);
                    paper.SetKeyState(PaperKey.LeftShift,action.Shift); paper.SetKeyState(action.Key,true);
                    typeof(HierarchyPanel).GetMethod("HandleArrowNavigation",BindingFlags.Instance|BindingFlags.NonPublic)!
                        .Invoke(panel,[paper,nodes,flat,true]);
                    var names = new List<string>();
                    foreach(var selected in Selection.GetSelected<GameObject>()) names.Add(selected.Name);
                    names.Sort(StringComparer.Ordinal);
                    trace.Add($"{Selection.GetActiveAs<GameObject>()?.Name}|{string.Join(',',names)}|{Raw(a)}|{Raw(b)}");
                }
                Assert.Contains(trace, static line => line.StartsWith("C|",StringComparison.Ordinal));
                Assert.Contains(trace, static line => line.Contains("B,C",StringComparison.Ordinal));
                return trace;
            }
            finally { panel.OnClosed(); paper.Canvas.Dispose(); }
            bool Raw(GameObject go) => !stored.TryGetValue(go.Identifier.ToString(),out bool state) || state;
        }
        static TreeNode Node(GameObject go,int depth,bool children) => new() { Id=go.Identifier.ToString(),UserData=go,Depth=depth,HasChildren=children,DefaultExpanded=true };
        static FieldInfo Field(string name) => typeof(HierarchyPanel).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!;
    }
    private sealed class Renderer : ICanvasRenderer
    {
        public object CreateTexture(uint width,uint height) => new Int2((int)width,(int)height);
        public Int2 GetTextureSize(object texture) => (Int2)texture;
        public void SetTextureData(object texture,IntRect bounds,byte[] data) { }
        public void RenderCalls(Canvas canvas,IReadOnlyList<DrawCall> calls) { }
        public void Dispose() { }
    }
}
