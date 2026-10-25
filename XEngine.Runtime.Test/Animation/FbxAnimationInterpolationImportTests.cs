// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text;

using XEngine.Runtime.AssetImporting;

using Xunit;

namespace XEngine.Runtime.Test.Animation;

public sealed class FbxAnimationInterpolationImportTests
{
    [Theory]
    [InlineData(4, 0.25f, 0.5f, 0.75f)]
    [InlineData(0x408, 0.15625f, 0.5f, 0.84375f)]
    [InlineData(0x2, 0f, 0f, 0f)]
    [InlineData(0x102, 1f, 1f, 1f)]
    public void ImportedFbx_PreservesAuthoredInterpolationInRuntimeCurves(
        int flags, float atQuarter, float atHalf, float atThreeQuarters)
    {
        // The second segment holds at 1: recomputing smooth tangents in the Runtime bridge
        // changes the preceding linear ramp and creates overshoot around Constant/ConstantNext.
        string fbx = FormattableString.Invariant($$"""
            ; FBX 7.4.0 project file
            FBXHeaderExtension: { FBXVersion: 7400 }
            GlobalSettings: {
                Properties70: { P: "UnitScaleFactor", "double", "Number", "", 100 }
            }
            Objects: {
                Model: 1, "Model::Bone", "LimbNode" { }
                AnimationStack: 2, "AnimStack::Take", "" { }
                AnimationLayer: 3, "AnimLayer::Base", "" { }
                AnimationCurveNode: 4, "AnimCurveNode::T", "" { }
                AnimationCurve: 5, "AnimCurve::X", "" {
                    KeyTime: *3 { a: 0, 18474463200, 46186158000 }
                    KeyValueFloat: *3 { a: 0, 1, 1 }
                    KeyAttrFlags: *1 { a: {{flags}} }
                    KeyAttrDataFloat: *4 { a: 0, 0, 0, 0 }
                    KeyAttrRefCount: *1 { a: 3 }
                }
            }
            Connections: {
                C: "OO", 1, 0
                C: "OO", 3, 2
                C: "OO", 4, 3
                C: "OP", 4, 1, "Lcl Translation"
                C: "OP", 5, 4, "d|X"
            }
            """);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(fbx));
        ModelImportResult imported = new ModelImporter().Import(
            stream, "interpolation.fbx");
        try
        {
            AnimationClip clip = Assert.Single(imported.Animations);
            AnimationClip.AnimBone bone = Assert.Single(clip.Bones, b => b.BoneName.EndsWith("Bone"));
            AnimationCurve curve = Assert.IsType<AnimationCurve>(bone.PosX);
            Assert.Equal(0f, curve.Evaluate(0f), 5);
            Assert.Equal(atQuarter, curve.Evaluate(0.1f), 3);
            Assert.Equal(atHalf, curve.Evaluate(0.2f), 3);
            Assert.Equal(atThreeQuarters, curve.Evaluate(0.3f), 3);
            Assert.Equal(1f, curve.Evaluate(0.4f), 5);
            Assert.Equal(1f, curve.Evaluate(0.6f), 5);
            Assert.Equal(1f, curve.Evaluate(1f), 5);
        }
        finally
        {
            imported.RootGO?.Dispose();
            foreach (AnimationClip clip in imported.Animations)
                clip.Dispose();
        }
    }
}
