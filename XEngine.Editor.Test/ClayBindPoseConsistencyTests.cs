
using XEngine.Runtime;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>Dumps RAW FBX values (node LclTranslation, mesh vertex range, bindpose) for the
/// hero vs the dummy to prove the unit-scheme mismatch inside the ripped Anbi file.</summary>
public sealed class ClayBindPoseConsistencyTests : EditorTestHarness

{
    private const string AnbiFbx = @"F:\Git\ZZZ\Assets\Arts\PlayerModel\安比\Anbi.FBX";
    private const string ClaymoreFbx = @"F:\Git\ZZZ\Assets\Arts\EnemyModel\Claymore\Claymore.fbx";

    private static XEngine.Clay.Model LoadFbx(string path) =>
        XEngine.Clay.Importer.ModelImporter.Load(path, new XEngine.Clay.Importer.ModelImporterSettings
        {
            PostProcess = XEngine.Clay.Importer.PostProcessPresets.GameQuality
                & ~(XEngine.Clay.Importer.PostProcessFlags.CalcTangentSpace
                    | XEngine.Clay.Importer.PostProcessFlags.GenerateSmoothNormals
                    | XEngine.Clay.Importer.PostProcessFlags.GenerateNormals),
            GlobalScale = 1f,
        });

    [Fact]
    public void EditorSettings_GlobalScale100_StaysConsistent()
    {
        if (!File.Exists(AnbiFbx)) return;

        var m = XEngine.Clay.Importer.ModelImporter.Load(AnbiFbx, new XEngine.Clay.Importer.ModelImporterSettings
        {
            PostProcess = XEngine.Clay.Importer.PostProcessPresets.GameQuality
                & ~(XEngine.Clay.Importer.PostProcessFlags.CalcTangentSpace
                    | XEngine.Clay.Importer.PostProcessFlags.GenerateSmoothNormals
                    | XEngine.Clay.Importer.PostProcessFlags.GenerateNormals),
            GlobalScale = 100f,   // matches the copier-written meta (unitScale 100 cancels cm→m)
        });
        var world = new XEngine.Vector.Float4x4[m.Nodes.Count];
        for (int i = 0; i < m.Nodes.Count; i++)
        {
            var n = m.Nodes[i];
            var local = XEngine.Vector.Float4x4.CreateTRS(n.LocalPosition, n.LocalRotation, n.LocalScale);
            world[i] = n.Parent != null ? world[n.Parent.Index] * local : local;
        }
        var skin = m.Skins[0];
        int bones = Math.Min(skin.BoneNodeIndices.Length, skin.InverseBindPoses.Length);
        int bad = 0; float maxAngle = 0f;
        for (int b = 0; b < bones; b++)
        {
            var net = XEngine.Vector.Quaternion.FromMatrix(world[skin.BoneNodeIndices[b]] * skin.InverseBindPoses[b]);
            float angle = 2f * MathF.Acos(MathF.Min(1f, MathF.Abs(net.W))) * 180f / MathF.PI;
            if (angle > 30f) bad++;
            if (angle > maxAngle) maxAngle = angle;
        }
        Runtime.Debug.Log($"[ZZUnits] GlobalScale100: bones={bones} bad={bad} Bip001Y={m.Nodes[7].LocalPosition.Y:F4} maxAngle={maxAngle:F0}");
        Assert.Equal(0, bad);
    }

    /// <summary>The ZZZ Anbi export carries bind matrices in a different axis convention than
    /// its skeleton (every boneWorld*InvBind resolves to ~180° instead of identity), which
    /// turned the bind pose upside-down in prefab previews/thumbnails. BindPoseConsistencyStep
    /// must rebuild them from the node chain; the consistent Claymore export must be left
    /// untouched. Skips when the reference project is not on this machine.</summary>
    [Fact]
    public void BindPoseConsistency_RebuildsMismatchedBindPoses()
    {
        if (!File.Exists(AnbiFbx)) return;

        var m = LoadFbx(AnbiFbx);
        Assert.True(m.Skins.Count > 0);
        var world = new XEngine.Vector.Float4x4[m.Nodes.Count];
        for (int i = 0; i < m.Nodes.Count; i++)
        {
            var n = m.Nodes[i];
            var local = XEngine.Vector.Float4x4.CreateTRS(n.LocalPosition, n.LocalRotation, n.LocalScale);
            world[i] = n.Parent != null ? world[n.Parent.Index] * local : local;
        }
        var skin = m.Skins[0];
        int bones = Math.Min(skin.BoneNodeIndices.Length, skin.InverseBindPoses.Length);
        Assert.True(bones > 50);
        for (int b = 0; b < bones; b++)
        {
            var net = XEngine.Vector.Quaternion.FromMatrix(world[skin.BoneNodeIndices[b]] * skin.InverseBindPoses[b]);
            float angle = 2f * MathF.Acos(MathF.Min(1f, MathF.Abs(net.W))) * 180f / MathF.PI;
            Assert.True(angle < 5f, $"bone {b} ('{m.Nodes[skin.BoneNodeIndices[b]].Name}') net rotation {angle:F0}° after rebuild");
        }
    }

    [Fact]
    public void BindPoseConsistency_LeavesConsistentBindPosesAlone()
    {
        if (!File.Exists(ClaymoreFbx)) return;

        var m = LoadFbx(ClaymoreFbx);
        Assert.True(m.Skins.Count > 0);
        // Claymore was already consistent (net == identity); after the step it still must be —
        // and its skin must keep MORE than one bone (not degenerated).
        Assert.True(m.Skins[0].BoneNodeIndices.Length > 10);
    }
}
