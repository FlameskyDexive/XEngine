using global::XEngine.Animation.Humanoid;
using XEngine.Editor.Inspector;
using XEngine.OrigamiUI;
using XEngine.Runtime;
using XEngine.Runtime.AssetImporting;
using XEngine.Runtime.Resources;
using Xunit;
using Surface = XEngine.Editor.Test.InspectorComponentHeaderTests.Fixture;

namespace XEngine.Editor.Test;

public sealed class GenericAvatarInspectorTests : EditorTestHarness
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void StandaloneAvatarExtensionImportsTypedDataAndReloads(bool generic)
    {
        using var root = new GameObject("Model");
        var child = new GameObject("Bip001"); child.SetParent(root);
        using var source = generic ? Avatar.BuildGeneric(root.Transform,"Bip001") : new Avatar();
        Assets.CreateAsset(source,"Descriptor.avatar");
        var entry = Assets.GetEntry("Descriptor.avatar")!;
        Assert.Equal("ScriptableObjectImporter",entry.ImporterType);
        var loaded=Assert.IsType<Avatar>(Assets.Get(entry.Guid));
        Assert.Equal(source.RigKind,loaded.RigKind);
        Assert.Equal(source.GenericSkeleton.Count,loaded.GenericSkeleton.Count);
        var result=Assets.ReimportWithResult(entry.Guid);
        Assert.True(result.Succeeded,result.Error);
        var reloaded=Assert.IsType<Avatar>(Assets.Get(entry.Guid));
        Assert.Equal(source.RigKind,reloaded.RigKind);
        Assert.Equal(entry.Guid,reloaded.AssetID);
    }

    [Theory]
    [InlineData(240f)] [InlineData(335f)] [InlineData(520f)]
    public void GenericDescriptorIsReadOnlyAndWrapsWithoutHumanoidControls(float width)
    {
        using var root = new GameObject("Model");
        var child = new GameObject("LongBip001ReferenceNode"); child.SetParent(root);
        using var avatar = Avatar.BuildGeneric(root.Transform,"LongBip001ReferenceNode");
        var entry = new AssetEntry { Guid=Guid.NewGuid(),Path="Descriptor.avatar" };
        var editor = new AvatarAssetEditor(); using var f = new Surface(width);
        var paper = f.Paper;
        for (int frame=0;frame<3;frame++)
        {
            paper.BeginFrame(1f/60); Origami.BeginFrame(paper,1f/60);
            using (paper.Column("host").Size(width,900).Enter()) editor.OnGUI(paper,"avatar",entry,avatar);
            Origami.EndFrame(paper); paper.EndFrame();
        }
        Assert.Contains(f.Elements(),e=>e.Paragraph=="Rig Type: Generic");
        var notes = f.Elements().Where(e=>e.Paragraph?.StartsWith("Root Path:")==true
            || e.Paragraph?.StartsWith("Read-only skeleton")==true).ToArray();
        Assert.Equal(2,notes.Length);
        Assert.All(notes,e=> { Assert.True(e.IsNotInteractable); Assert.Null(e.OnClick);
            Assert.Equal(XEngine.Scribe.TextWrapMode.Wrap,e.WrapMode); Assert.InRange(e.X+e.LayoutWidth,1,width); });
        Assert.Contains(f.Elements(),e=>e.Paragraph=="Reference Transforms: 2");
        Assert.Contains(f.Elements(),e=>e.Paragraph=="Validation: OK");
        Assert.DoesNotContain(f.Elements(),e=>e.Paragraph?.Contains("Auto Map")==true
            || e.Paragraph?.Contains("Save Asset")==true || e.Paragraph?.StartsWith("Legs ")==true);
        Assert.Null(avatar.Validate()); Assert.Equal(AvatarRigKind.Generic,avatar.RigKind);
    }

    private sealed class NullResolver : IModelTextureResolver
    {
        public AssetRef<Texture2D> ResolveExternal(string path)=>default;
        public AssetRef<Texture2D> ResolveEmbedded(string? name,byte[] bytes,string? mime)=>default;
    }

    [Theory]
    [InlineData("Anbi.FBX")] [InlineData("Anbi@Idle.FBX")]
    public void RealModelsKeepEveryNodeAcrossStrippingAndDescriptorRoundTrip(string file)
    {
        string path=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../ZonezeroTestProject/Assets/ZZZ/Arts/PlayerModel/Anbi",file));
        var importer=new ModelImporter();
        var off=importer.Import(new FileInfo(path),new ModelImporterSettings {TextureResolver=new NullResolver()});
        var on=importer.Import(new FileInfo(path),new ModelImporterSettings {TextureResolver=new NullResolver(),StripBones=true,SkinBoneCount=1});
        try
        {
            using var a=Avatar.BuildGeneric(off.RootGO!.Transform);
            using var b=Avatar.BuildGeneric(on.RootGO!.Transform);
            Assert.Null(a.Validate()); Assert.Null(b.Validate());
            Assert.True(a.GenericSkeleton.Count>100);
            Assert.Equal(a.GenericSkeleton.Count,b.GenericSkeleton.Count);
            for(int i=0;i<a.GenericSkeleton.Count;i++)
            {
                var x=a.GenericSkeleton[i]; var y=b.GenericSkeleton[i];
                Assert.Equal(x.Path,y.Path); Assert.Equal(x.ParentIndex,y.ParentIndex);
                Assert.Equal(x.LocalPosition,y.LocalPosition); Assert.Equal(x.LocalRotation,y.LocalRotation); Assert.Equal(x.LocalScale,y.LocalScale);
            }
            using var back=XEngine.Echo.Serializer.Deserialize<Avatar>(XEngine.Echo.Serializer.Serialize(typeof(object),a));
            Assert.Null(back.Validate()); Assert.Equal(a.GenericSkeleton.Count,back.GenericSkeleton.Count);
        }
        finally { Dispose(off); Dispose(on); }
    }

    private static void Dispose(ModelImportResult data)
    {
        data.RootGO?.Dispose();
        foreach(var mesh in data.Meshes)mesh.Dispose();
        foreach(var material in data.Materials)material.Dispose();
        foreach(var clip in data.Animations)clip.Dispose();
    }
}
