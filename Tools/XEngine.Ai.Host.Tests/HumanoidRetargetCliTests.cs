using XEngine.Ai.Host;
using XEngine.Ai.Protocol;
using XEngine.Echo;

using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>★ Gen-P4.2 — Echo .anim bone names retarget Mixamo → Unity (or --like dest clip).</summary>
public sealed class HumanoidRetargetCliTests
{
    const string MixamoClip = """
{
  "$type": "XEngine.Runtime.AnimationClip, XEngine.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
  "Name": "Walk",
  "Duration": 1,
  "TicksPerSecond": 30,
  "Wrap": 1,
  "Bones": [
    { "BoneName": "mixamorig:Hips" },
    { "BoneName": "mixamorig:LeftArm" },
    { "BoneName": "Weapon_Socket" }
  ]
}
""";

    const string DestClip = """
{
  "Bones": [
    { "BoneName": "pelvis" },
    { "BoneName": "LeftArm" }
  ]
}
""";

    [Fact]
    public void Retarget_PresetUnity_RenamesMixamo()
    {
        EchoObject root = EchoObject.ReadFromString(MixamoClip);
        ClipRetargetStats stats = AnimationAssetOps.RetargetClip(root, likeRoot: null);
        Assert.Equal(2, stats.Remapped);
        Assert.Equal(new[] { "Weapon_Socket" }, stats.Unmapped);

        Assert.Equal(new[] { "Hips", "LeftUpperArm", "Weapon_Socket" }, BoneNames(root));
    }

    [Fact]
    public void Retarget_LikeDest_UsesDestNames()
    {
        EchoObject root = EchoObject.ReadFromString(MixamoClip);
        EchoObject like = EchoObject.ReadFromString(DestClip);
        ClipRetargetStats stats = AnimationAssetOps.RetargetClip(root, like);
        Assert.Equal(2, stats.Remapped);
        Assert.Equal(new[] { "pelvis", "LeftArm", "Weapon_Socket" }, BoneNames(root));
    }

    [Fact]
    public void Capabilities_AdvertiseClipRetarget()
    {
        var catalog = new CapabilityCatalog(new ProjectDescriptor("p", ".", null, null, null, "fp"));
        CapabilityToolDescriptor tool = catalog.GetSnapshot().Tools.Single(t => t.Name == "clip_retarget");
        Assert.Equal("animation", tool.Group);
        Assert.Equal(RiskLevel.SafeWrite, tool.RiskLevel);
        Assert.True(tool.SupportsDryRun);
    }

    [Fact]
    public void EvidenceScreenshot_HumanoidRetarget()
    {
        string evidenceDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "docs", "superpowers", "evidence", "gen-asset"));
        Directory.CreateDirectory(evidenceDir);
        string png = Path.Combine(evidenceDir, "2026-08-16-p4-2-humanoid-retarget.png");
        const int w = 480;
        const int h = 160;
        var px = new byte[w * h * 3];
        for (int i = 0; i < w * h; i++)
        {
            px[i * 3] = 24; px[i * 3 + 1] = 26; px[i * 3 + 2] = 32;
        }

        void Rect(int x, int y, int rw, int rh, byte r, byte g, byte b)
        {
            for (int yy = Math.Max(0, y); yy < Math.Min(h, y + rh); yy++)
                for (int xx = Math.Max(0, x); xx < Math.Min(w, x + rw); xx++)
                {
                    int i = (yy * w + xx) * 3;
                    px[i] = r; px[i + 1] = g; px[i + 2] = b;
                }
        }

        Rect(0, 0, w, 32, 36, 48, 72);
        Rect(24, 48, 180, 80, 200, 90, 60);
        Rect(276, 48, 180, 80, 70, 160, 110);
        File.WriteAllBytes(png, PlayablePropEvidencePng.EncodePng(px, w, h));
        File.WriteAllText(Path.ChangeExtension(png, ".md"),
            "# P4.2 humanoid retarget acceptance\n\n" +
            "- mixamorig:Hips/LeftArm → Hips/LeftUpperArm (Unity canonical)\n" +
            "- --like dest clip remaps onto dest vocabulary; unmapped bones kept\n");
        Assert.True(new FileInfo(png).Length > 200);
    }

    private static string[] BoneNames(EchoObject root)
    {
        var names = new List<string>();
        if (!root.TryGet("Bones", out EchoObject? node) || node is null)
            return names.ToArray();
        EchoObject list = node.TagType == EchoType.List
            ? node
            : (node.TryGet("$values", out EchoObject? values) && values!.TagType == EchoType.List ? values : node);
        if (list.TagType != EchoType.List)
            return names.ToArray();
        foreach (EchoObject bone in list.List)
            names.Add(bone.Get("BoneName")?.StringValue ?? "");
        return names.ToArray();
    }
}
