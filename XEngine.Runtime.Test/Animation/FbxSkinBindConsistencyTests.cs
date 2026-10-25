// Import regression: a freshly imported skinned FBX must be SELF-CONSISTENT — composing each
// bone's world matrix (rest hierarchy) with its stored BindPose must yield the IDENTITY (up to
// float error). A -90°X residue here means the FBX bake lost the pre-rotation, and every
// directly-instantiated model (FBX Inspector preview, newly built scenes) renders tipped/lying.
// Regression for: Anbi/Corin/Nostradamus models importing with a -90°X skin bake (2026-09-03).
using System;
using System.IO;

using Xunit;
using Xunit.Abstractions;

using XEngine.Runtime;
using XEngine.Runtime.AssetImporting;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime.Test.Animation;

public class FbxSkinBindConsistencyTests(ITestOutputHelper output)
{
    private sealed class NullResolver : IModelTextureResolver
    {
        public AssetRef<Texture2D> ResolveExternal(string sourcePath) => default;
        public AssetRef<Texture2D> ResolveEmbedded(string? name, byte[] encodedBytes, string? mimeType) => default;
    }

    public static TheoryData<string> ModelFbx() => new()
    {
        @"ZonezeroTestProject/Assets/ZZZ/Arts/PlayerModel/Anbi/Anbi.FBX",
        @"ZonezeroTestProject/Assets/ZZZ/Arts/PlayerModel/Corin/Corin.FBX",
        @"ZonezeroTestProject/Assets/ZZZ/Arts/PlayerModel/Nicole/Nostradamus.FBX",
        @"ZonezeroTestProject/Assets/ZZZ/Arts/EnemyModel/Claymore/Claymore.fbx",
    };

    public static TheoryData<string> AnimationFbx() => new()
    {
        @"ZonezeroTestProject/Assets/ZZZ/Arts/PlayerModel/Anbi/Anbi@Idle.FBX",
        @"ZonezeroTestProject/Assets/ZZZ/Arts/PlayerModel/Anbi/Anbi@Run.FBX",
        @"ZonezeroTestProject/Assets/ZZZ/Arts/PlayerModel/Corlin/Corin@Idle.FBX",
        @"ZonezeroTestProject/Assets/ZZZ/Arts/PlayerModel/Corlin/Corin@Run.FBX",
        @"ZonezeroTestProject/Assets/ZZZ/Arts/PlayerModel/Nicole/Nostradamus@Idle.FBX",
        @"ZonezeroTestProject/Assets/ZZZ/Arts/PlayerModel/Nicole/Nostradamus@Run.FBX",
    };

    [Theory]
    [MemberData(nameof(ModelFbx))]
    public void FreshImport_RestSkin_IsIdentityPerBone(string relativePath)
    {
        FileInfo fbx = new(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", relativePath)
            .Replace('\\', '/'));
        Assert.True(fbx.Exists, $"missing test asset: {fbx.FullName}");

        var settings = new ModelImporterSettings { TextureResolver = new NullResolver(), UnitScale = 100f };
        ModelImportResult result = new ModelImporter().Import(fbx, settings);
        Assert.NotNull(result.RootGO);

        foreach (SkinnedMeshRenderer smr in result.RootGO.GetComponentsInChildren<SkinnedMeshRenderer>(true, true))
        {
            Mesh? mesh = smr.SharedMesh.Res;
            Transform?[]? bones = smr.Bones;
            if (mesh == null || bones == null || mesh.BindPoses == null) continue;

            Float4x4 worldToLocal = smr.Transform.WorldToLocalMatrix;
            Float4x4 smrWorld = smr.Transform.LocalToWorldMatrix;
            float worstAngleDeg = 0f;
            int worstBone = -1;
            int disagree30 = 0;

            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] == null || i >= mesh.BindPoses.Length) continue;
                Float4x4 skin = worldToLocal * WorldOf(bones[i]!) * mesh.BindPoses[i];

                // Renderer-anchored contract: the REST skin must be the IDENTITY (raw authored
                // vertices render through the renderer's own transform), and animation deforms
                // relative to that rest.
                Float3 skinY = new(skin.c1.X, skin.c1.Y, skin.c1.Z);
                Float3 unitY = Float3.UnitY;
                float cos = Math.Clamp(Float3.Dot(Float3.Normalize(skinY), unitY), -1f, 1f);
                float angleDeg = MathF.Abs(MathF.Acos(cos) * 180f / MathF.PI);
                if (angleDeg > 30f) disagree30++;
                if (angleDeg > worstAngleDeg)
                {
                    worstAngleDeg = angleDeg;
                    worstBone = i;
                }
            }

            output.WriteLine($"'{smr.GameObject.Name}' bones={bones.Length} worstRestSkinYAngle={worstAngleDeg:0.###}° " +
                $"at bone[{worstBone}] disagree>30°={disagree30} ({100f * disagree30 / bones.Length:0.#}%)");
            Assert.True(worstAngleDeg < 1f,
                $"'{smr.GameObject.Name}': rest skin matrix is rotated {worstAngleDeg:0.##}° from identity " +
                $"(bone[{worstBone}], {disagree30}/{bones.Length} bones >30°) — the FBX skin bake lost the pre-rotation.");

            // Upright-rendered assertion: with identity rest skins, the rendered subject
            // (smrWorld * rawVerts) must stand along Y (these are standing characters — a
            // Z-up/lying bake shows up as the Z extent dominating). Skipped for packed-vertex
            // meshes whose raw verts collapse to a point (their placement lives on the bones).
            if (mesh.Vertices is { Length: > 0 } verts && mesh.isReadable)
            {
                float rx = 0f, ry = 0f, rz = 0f;
                foreach (Float3 p in verts)
                {
                    rx = MathF.Max(rx, MathF.Abs(p.X));
                    ry = MathF.Max(ry, MathF.Abs(p.Y));
                    rz = MathF.Max(rz, MathF.Abs(p.Z));
                }
                if (MathF.Max(rx, MathF.Max(ry, rz)) < 0.05f)
                {
                    output.WriteLine($"'{smr.GameObject.Name}' raw verts are packed (span < 0.05) — upright check skipped.");
                    continue;
                }

                float ex = 0f, ey = 0f, ez = 0f;
                foreach (Float3 p in verts)
                {
                    Float3 w = new(
                        smrWorld.c0.X * p.X + smrWorld.c1.X * p.Y + smrWorld.c2.X * p.Z + smrWorld.c3.X,
                        smrWorld.c0.Y * p.X + smrWorld.c1.Y * p.Y + smrWorld.c2.Y * p.Z + smrWorld.c3.Y,
                        smrWorld.c0.Z * p.X + smrWorld.c1.Z * p.Y + smrWorld.c2.Z * p.Z + smrWorld.c3.Z);
                    ex = MathF.Max(ex, MathF.Abs(w.X));
                    ey = MathF.Max(ey, MathF.Abs(w.Y));
                    ez = MathF.Max(ez, MathF.Abs(w.Z));
                }
                output.WriteLine($"'{smr.GameObject.Name}' renderedExtents(X,Y,Z)=({ex:0.##},{ey:0.##},{ez:0.##})");
                Assert.True(ey >= ex && ey >= ez,
                    $"'{smr.GameObject.Name}': rendered subject is not Y-up (extents X={ex:0.##} Y={ey:0.##} Z={ez:0.##}) — lying bake.");
            }
        }
    }

    [Theory]
    [MemberData(nameof(AnimationFbx))]
    public void FreshImport_AnimationBones_CarryStaticSourceReferencePose(string relativePath)
    {
        FileInfo fbx = new(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", relativePath)
            .Replace('\\', '/'));
        Assert.True(fbx.Exists, $"missing test asset: {fbx.FullName}");

        var settings = new ModelImporterSettings { TextureResolver = new NullResolver(), UnitScale = 100f };
        ModelImportResult result = new ModelImporter().Import(fbx, settings);
        Assert.NotNull(result.RootGO);
        Assert.NotEmpty(result.Animations);

        AnimationClip clip = result.Animations[0];
        Assert.NotEmpty(clip.Bones);
        foreach (AnimationClip.AnimBone bone in clip.Bones)
        {
            Assert.True(bone.HasSourceReferencePose, $"{relativePath}/{bone.BoneName}: source reference pose missing");
            int slash = bone.BoneName.LastIndexOf('/');
            if (slash >= 0)
            {
                string parentPath = bone.BoneName[..slash];
                AnimationClip.AnimBone? parent = clip.GetBone(parentPath);
                Assert.NotNull(parent);
                Assert.True(parent!.HasSourceReferencePose,
                    $"{relativePath}/{bone.BoneName}: parent '{parentPath}' has no source reference pose");
            }
            Transform? source = result.RootGO.Transform.Find(bone.BoneName);
            Assert.NotNull(source);
            Assert.True(Float3.Length(source!.LocalPosition - bone.SourcePosition) < 1e-5f,
                $"{relativePath}/{bone.BoneName}: source reference position does not match imported hierarchy");
            Assert.True(Quaternion.Angle(
                    Quaternion.NormalizeSafe(source.LocalRotation),
                    Quaternion.NormalizeSafe(bone.SourceRotation)) < 1e-4f,
                $"{relativePath}/{bone.BoneName}: source reference rotation does not match imported hierarchy");
            Assert.True(Float3.Length(source.LocalScale - bone.SourceScale) < 1e-5f,
                $"{relativePath}/{bone.BoneName}: source reference scale does not match imported hierarchy");
        }
    }

    private static Float4x4 WorldOf(Transform t)
    {
        Float4x4 local = Float4x4.CreateTRS(t.LocalPosition, t.LocalRotation, t.LocalScale);
        return t.Parent != null ? WorldOf(t.Parent) * local : local;
    }
}
