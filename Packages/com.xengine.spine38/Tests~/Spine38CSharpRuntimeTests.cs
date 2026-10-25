using Spine;

using Xunit;

namespace XEngine.Spine38.Tests;

public sealed class Spine38CSharpRuntimeTests
{
    private const string MinimalWalkJson = """
        {
          "skeleton": { "hash": "m1", "spine": "3.8.55", "width": 64, "height": 64 },
          "bones": [
            { "name": "root" },
            { "name": "body", "parent": "root", "y": 20 }
          ],
          "slots": [
            { "name": "body", "bone": "body" }
          ],
          "animations": {
            "walk": {
              "bones": {
                "body": {
                  "translate": [
                    { "time": 0, "x": 0, "y": 0 },
                    { "time": 0.5, "x": 10, "y": 0 }
                  ]
                }
              }
            }
          }
        }
        """;

    private const string ConvertedExportJson = """
        {
          "skeleton": { "hash": "bad", "spine": "3.8.75", "width": 8, "height": 8 },
          "bones": [ { "name": "root" } ],
          "slots": [ { "name": "body", "bone": "root" } ]
        }
        """;

    [Fact]
    public void SkeletonJson_LoadsBonesAndWalkAnimation()
    {
        SkeletonData data = ReadMinimal();
        Assert.Equal("3.8.55", data.Version);
        Assert.NotNull(data.FindBone("root"));
        Assert.NotNull(data.FindBone("body"));
        Assert.NotNull(data.FindAnimation("walk"));
        Assert.True(data.FindAnimation("walk").Duration >= 0.5f);
    }

    [Fact]
    public void AnimationState_WalkMovesBodyBone()
    {
        SkeletonData data = ReadMinimal();
        var skeleton = new Skeleton(data);
        var state = new AnimationState(new AnimationStateData(data));
        state.SetAnimation(0, "walk", loop: false);

        Bone body = skeleton.FindBone("body")!;
        skeleton.UpdateWorldTransform();
        float startX = body.WorldX;

        state.Update(0.5f);
        state.Apply(skeleton);
        skeleton.UpdateWorldTransform();

        Assert.True(body.WorldX > startX + 5f, $"Expected body WorldX to move, start={startX} now={body.WorldX}");
    }

    [Fact]
    public void SkeletonJson_SpineboyEss_ParsesWalk()
    {
        string path = Path.Combine(PackageRoot(), "Samples~", "GettingStarted", "Skeletons", "Spineboy", "spineboy-ess.json");
        Assert.True(File.Exists(path), path);
        var json = new SkeletonJson(SpineNullAttachmentLoader.Instance);
        using var reader = new StreamReader(path);
        SkeletonData data = json.ReadSkeletonData(reader);
        Assert.StartsWith("3.8", data.Version, StringComparison.Ordinal);
        Assert.NotEqual("3.8.75", data.Version);
        Assert.NotNull(data.FindAnimation("walk"));
        Assert.NotNull(data.FindBone("root"));

        var skeleton = new Skeleton(data);
        var state = new AnimationState(new AnimationStateData(data));
        state.SetAnimation(0, "walk", loop: true);
        Bone hip = skeleton.FindBone("hip") ?? skeleton.FindBone("root")!;
        skeleton.UpdateWorldTransform();
        float startX = hip.WorldX;
        state.Update(0.35f);
        state.Apply(skeleton);
        skeleton.UpdateWorldTransform();
        Assert.NotEqual(startX, hip.WorldX);
    }

    [Fact]
    public void SkeletonJson_AcceptsValidatedExport3875()
    {
        var json = new SkeletonJson(SpineNullAttachmentLoader.Instance);
        using var reader = new StringReader(ConvertedExportJson);
        SkeletonData data = json.ReadSkeletonData(reader);
        Assert.Equal("3.8.75", data.Version);
        Assert.Equal("root", Assert.Single(data.Bones).Name);
        Assert.Equal("body", Assert.Single(data.Slots).Name);
    }

    [Fact]
    public void SkeletonBinary_AcceptsValidatedExport3875()
    {
        using var stream = new MemoryStream();
        void Text(string s) { byte[] bytes = System.Text.Encoding.UTF8.GetBytes(s); stream.WriteByte((byte)(bytes.Length + 1)); stream.Write(bytes); }
        void Float(float v) { Span<byte> bytes = stackalloc byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(bytes, v); stream.Write(bytes); }
        Text("compatibility-fixture"); Text("3.8.75");
        for (int i = 0; i < 4; i++) Float(0); // bounds
        stream.WriteByte(0); // nonessential
        stream.WriteByte(0); // string table
        stream.WriteByte(1); Text("root");
        Float(0); Float(12); Float(34); Float(1); Float(1); Float(0); Float(0); Float(0);
        stream.WriteByte(0); stream.WriteByte(0); // normal transform; skinRequired
        for (int i = 0; i < 8; i++) stream.WriteByte(0); // slots, IK, transform, path, default skin, skins, events, animations
        stream.Position = 0;
        var binary = new SkeletonBinary(SpineNullAttachmentLoader.Instance);
        SkeletonData data = binary.ReadSkeletonData(stream);
        Assert.Equal("3.8.75", data.Version);
        BoneData root = Assert.Single(data.Bones);
        Assert.Equal("root", root.Name);
        Assert.Equal(12f, root.X); Assert.Equal(34f, root.Y);
        Assert.Equal(stream.Length, stream.Position);
    }

    private static SkeletonData ReadMinimal()
    {
        var json = new SkeletonJson(SpineNullAttachmentLoader.Instance);
        using var reader = new StringReader(MinimalWalkJson);
        return json.ReadSkeletonData(reader);
    }

    internal static string PackageRoot()
        => Spine38TestPaths.PackageRoot();
}
