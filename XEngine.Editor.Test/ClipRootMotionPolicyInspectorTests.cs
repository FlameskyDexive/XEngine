using XEngine.Echo;
using XEngine.Editor.Inspector;
using XEngine.OrigamiUI;
using XEngine.Runtime;
using Xunit;
using Surface = XEngine.Editor.Test.InspectorComponentHeaderTests.Fixture;

namespace XEngine.Editor.Test;

public sealed class ClipRootMotionPolicyInspectorTests : EditorTestHarness
{
    [Theory]
    [InlineData(0, "Legacy Automatic")]
    [InlineData(1, "Explicit Bone")]
    [InlineData(2, "Disabled")]
    [InlineData(77, "Unknown (77)")]
    [InlineData(1, "Explicit Model Root", ".")]
    public void InspectorReportsActualPolicyReadOnlyWithoutMutatingAsset(int policy, string caption, string path = "Model/Bip001")
    {
        using var clip = new AnimationClip { Name="Policy", Duration=1, RootMotionPolicy=(ClipRootMotionPolicy)policy,
            RootBonePath=path };
        string before=Serializer.Serialize(typeof(object),clip).WriteToString();
        var entry=new AssetEntry { Guid=Guid.NewGuid(), Path="Policy.anim" };
        var editor=new AnimationClipAssetEditor();
        using var f=new Surface(240);
        for(int i=0;i<3;i++)
        {
            f.Paper.BeginFrame(1f/60); Origami.BeginFrame(f.Paper,1f/60);
            using(f.Paper.Column("host").Size(240,900).Enter())editor.OnGUI(f.Paper,"clip",entry,clip);
            Origami.EndFrame(f.Paper); f.Paper.EndFrame();
        }
        var note=Assert.Single(f.Elements(),e=>e.Paragraph?.StartsWith("Root Motion:")==true);
        Assert.Contains(caption,note.Paragraph);
        Assert.True(note.IsNotInteractable); Assert.Null(note.OnClick);
        Assert.Equal(XEngine.Scribe.TextWrapMode.Wrap,note.WrapMode);
        Assert.InRange(note.X+note.LayoutWidth,1,240);
        Assert.DoesNotContain(f.Elements(),e=>e.Paragraph?.Contains("Save Asset")==true);
        Assert.Equal(before,Serializer.Serialize(typeof(object),clip).WriteToString());
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(77)]
    [InlineData(1, ".")]
    public void StandaloneImportAndReimportPreserveRootPolicyAndGuid(int policy, string path = "Model/Bip001")
    {
        using var clip=new AnimationClip {Name="Policy",Duration=1,RootMotionPolicy=(ClipRootMotionPolicy)policy,
            RootBonePath=path};
        Assets.CreateAsset(clip,"Policy.anim");
        var entry=Assets.GetEntry("Policy.anim")!;
        var loaded=Assert.IsType<AnimationClip>(Assets.Get(entry.Guid));
        Assert.Equal(clip.RootMotionPolicy,loaded.RootMotionPolicy);
        Assert.Equal(clip.RootBonePath,loaded.RootBonePath);
        var result=Assets.ReimportWithResult(entry.Guid);
        Assert.True(result.Succeeded,result.Error);
        var reloaded=Assert.IsType<AnimationClip>(Assets.Get(entry.Guid));
        Assert.Equal(entry.Guid,reloaded.AssetID);
        Assert.Equal(clip.RootMotionPolicy,reloaded.RootMotionPolicy);
        Assert.Equal(clip.RootBonePath,reloaded.RootBonePath);
    }
}
