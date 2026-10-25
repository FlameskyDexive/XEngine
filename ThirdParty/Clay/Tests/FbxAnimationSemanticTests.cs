// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Clay.Formats.Fbx;
using XEngine.Clay.Importer;
using XEngine.Clay.Internal.Intermediate;
using XEngine.Vector;

using Xunit.Abstractions;

namespace XEngine.Clay.Tests;

/// <summary>
/// Semantic regression coverage for production FBX animation data. These tests inspect the
/// authoring channels before Clay maps them to quaternion/float3 curves, so a missing-axis or
/// Euler-order regression cannot be hidden by later finite-value and skin-topology checks.
/// </summary>
public sealed class FbxAnimationSemanticTests(ITestOutputHelper output)
{
    [Fact]
    public void CubicCurve_ExpandsAttributeRunsAndUsesNextLeftSlope()
    {
        FbxAnimationMapper.ScalarCurve curve = ReadCurve(
            [0f, 2f, 4f], [0f, 1f, 2f], [0x408, 4], [0f, 0f, 0f, 0f, 17f, 19f, 0f, 0f], [2, 1]);
        // Zero slopes produce smoothstep, with slopes expressed per second across 2-second keys.
        Assert.Equal(0.15625f, curve.Sample(0.5f), 5);
        Assert.Equal(1.15625f, curve.Sample(2.5f), 5);
        Assert.Equal(1f, curve.Sample(2f));

        curve = ReadCurve([0f, 2f], [0f, 1f], [0xc08, 0xc08],
            [2f, -1f, 0f, 0f, 99f, 99f, 0f, 0f], [1, 1]);
        // The incoming tangent belongs to slot 1 of the previous record; the next record's
        // outgoing value 99 must never contribute to the segment ending at that key.
        Assert.Equal(1.25f, curve.Sample(1f), 5);
    }

    [Fact]
    public void WeightedCubicCurve_InvertsTimeBezierAndUnpacksBothWeights()
    {
        float packedWeights = BitConverter.UInt32BitsToSingle(1000u | (6000u << 16));
        FbxAnimationMapper.ScalarCurve curve = ReadCurve(
            [0f, 1f], [0f, 1f], [0x03000408], [0f, 0f, packedWeights, 0f], [2]);
        // At Bezier parameter 1/2, X = 0.3125 for handles 0.1/0.6, while Y = 0.5.
        Assert.Equal(0.5f, curve.Sample(0.3125f), 5);
        Assert.Equal(0f, curve.Sample(0f));
        Assert.Equal(1f, curve.Sample(1f));
    }

    [Theory]
    [InlineData(0x108, 0.59375f)]
    [InlineData(0x2108, 0.625f)]
    [InlineData(0x6108, 0.5f)]
    [InlineData(0x208, 0.59375f)]
    public void AutomaticAndTcbTangents_UseNeighborsAndTimeSpacing(int flag, float midpoint)
    {
        // Uneven time intervals distinguish time-dependent Auto from time-independent Auto;
        // progressive clamp zeroes the endpoint tangent. TCB(0,0,0) is a cardinal spline.
        FbxAnimationMapper.ScalarCurve curve = ReadCurve(
            [0f, 1f, 3f], [0f, 1f, 0f], [flag], [0f, 0f, 0f, 0f], [3]);
        Assert.Equal(midpoint, curve.Sample(0.5f), 5);
    }

    [Theory]
    [InlineData(0x2, 2f)]
    [InlineData(0x102, 8f)]
    public void ConstantCurve_PreservesExactKeysAndAuthoredHoldMode(int flag, float betweenKeys)
    {
        FbxAnimationMapper.ScalarCurve curve = ReadCurve([0f, 1f], [2f, 8f], [flag], [0f, 0f, 0f, 0f], [2]);
        Assert.Equal(2f, curve.Sample(0f));
        Assert.Equal(betweenKeys, curve.Sample(0.5f));
        Assert.Equal(8f, curve.Sample(1f));
    }

    [Theory]
    [InlineData(0x408, 0.15625f)]
    [InlineData(0x2, 0f)]
    [InlineData(0x102, 1f)]
    public void ImportedTransform_BakesInterpolationBetweenOriginalKeys(int flag, float quarterValue)
    {
        Model model = ImportSingleAxis("Lcl Translation", "d|X", 1f, flag);
        AnimationClip clip = Assert.Single(model.AnimationClips);
        AnimationBinding position = Assert.Single(clip.Bindings, b => b.Property == AnimatedProperty.Position);
        Assert.Equal(quarterValue, position.Curve.EvaluateFloat3(0.25f).X, 4);
        Assert.Equal(0f, position.Curve.EvaluateFloat3(0f).X);
        Assert.Equal(1f, position.Curve.EvaluateFloat3(1f).X);
        Assert.True(position.Curve.Count > 2);
    }

    [Fact]
    public void ImportedEulerRotation_PreservesFullTurnAndPivotArc()
    {
        Model model = ImportSingleAxis("Lcl Rotation", "d|Z", 360f, 4,
            "P: \"RotationPivot\", \"Vector3D\", \"Vector\", \"\", 1, 0, 0");
        AnimationClip clip = Assert.Single(model.AnimationClips);
        AnimationBinding position = Assert.Single(clip.Bindings, b => b.Property == AnimatedProperty.Position);
        AnimationBinding rotation = Assert.Single(clip.Bindings, b => b.Property == AnimatedProperty.Rotation);
        Float3 quarterPosition = position.Curve.EvaluateFloat3(0.25f);
        Assert.InRange(Float3.Length(quarterPosition - new Float3(1f, -1f, 0f)), 0f, 0.0001f);
        Quaternion quarterRotation = rotation.Curve.EvaluateQuaternion(0.25f);
        Assert.InRange(AngleDeg(quarterRotation, Quaternion.AxisAngle(new Float3(0f, 0f, 1f), MathF.PI / 2f)), 0f, 0.05f);
    }

    [Fact]
    public void ImportedCubic_StaysWithinBakeToleranceWithoutExcessKeys()
    {
        Model model = ImportSingleAxis("Lcl Translation", "d|X", 1f, 0x408);
        AnimationBinding binding = Assert.Single(model.AnimationClips[0].Bindings, b => b.Property == AnimatedProperty.Position);
        Assert.InRange(binding.Curve.Count, 3, 64);
        for (int i = 0; i <= 137; i++)
        {
            float t = i / 137f;
            Assert.InRange(MathF.Abs(binding.Curve.EvaluateFloat3(t).X - t * t * (3f - 2f * t)), 0f, 0.0011f);
        }
    }

    [Theory]
    [InlineData(0f, 2f, 0f, 1f)]
    [InlineData(-1f, 3f, 0.5f, 2f)]
    public void ShortOverrideLayer_PreservesEarlierLayerTimeRange(
        float baseStart, float baseEnd, float overrideStart, float overrideEnd)
    {
        string fbx = FormattableString.Invariant($$"""
            ; FBX 7.4.0 project file
            FBXHeaderExtension: { FBXVersion: 7400 }
            Objects: {
                Model: 1, "Model::Bone", "LimbNode" { }
                AnimationStack: 2, "AnimStack::Take", "" { }
                AnimationLayer: 3, "AnimLayer::Base", "" { }
                AnimationCurveNode: 4, "AnimCurveNode::BaseT", "" { }
                AnimationCurve: 5, "AnimCurve::BaseX", "" {
                    KeyTime: *2 { a: {{(long)(baseStart * 46186158000d)}}, {{(long)(baseEnd * 46186158000d)}} }
                    KeyValueFloat: *2 { a: 100, 200 }
                }
                AnimationLayer: 6, "AnimLayer::Override", "" { }
                AnimationCurveNode: 7, "AnimCurveNode::OverrideT", "" { }
                AnimationCurve: 8, "AnimCurve::OverrideX", "" {
                    KeyTime: *2 { a: {{(long)(overrideStart * 46186158000d)}}, {{(long)(overrideEnd * 46186158000d)}} }
                    KeyValueFloat: *2 { a: 10, 20 }
                }
            }
            Connections: {
                C: "OO", 1, 0
                C: "OO", 3, 2
                C: "OO", 4, 3
                C: "OP", 4, 1, "Lcl Translation"
                C: "OP", 5, 4, "d|X"
                C: "OO", 6, 2
                C: "OO", 7, 6
                C: "OP", 7, 1, "Lcl Translation"
                C: "OP", 8, 7, "d|X"
            }
            """);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(fbx));
        Model model = ModelImporter.Load(stream, "fbx", ModelImporterSettings.Raw);
        AnimationClip clip = Assert.Single(model.AnimationClips);
        Assert.Equal(baseStart, clip.StartTime);
        Assert.Equal(baseEnd, clip.EndTime);
        AnimationBinding position = Assert.Single(clip.Bindings, b => b.Property == AnimatedProperty.Position);
        Assert.Equal(10f, position.Curve.EvaluateFloat3(baseStart).X);
        Assert.Equal(15f, position.Curve.EvaluateFloat3((overrideStart + overrideEnd) * 0.5f).X);
        Assert.Equal(20f, position.Curve.EvaluateFloat3(baseEnd).X);
        foreach (AnimationBinding binding in clip.Bindings)
        {
            Assert.Equal(baseStart, binding.Curve.StartTime);
            Assert.Equal(baseEnd, binding.Curve.EndTime);
        }
    }

    [Theory]
    [InlineData(false, false, false, 1f, true)]
    [InlineData(true, false, false, 1f, false)]
    [InlineData(false, true, false, 1f, false)]
    [InlineData(false, false, false, 0f, false)]
    [InlineData(false, false, false, 2f, false)]
    [InlineData(false, false, true, 1f, true)]
    public void MissingParentBind_UsesOnlyConsistentStaticRigidChildren(
        bool animatedChild, bool conflictingChild, bool explicitParent, float scaleX, bool shouldHaveParent)
    {
        var syntheticRoot = new IntermediateNode { Name = "scene" };
        var parent = new IntermediateNode { Name = "motion", Parent = syntheticRoot };
        var child = new IntermediateNode { Name = "joint", Parent = parent };
        syntheticRoot.Children.Add(parent);
        parent.Children.Add(child);
        var stack = new FbxModelMapper.LocalTransformStack(
            new Float3(1f, 2f, 3f), new Float3(15f, 30f, 70f), new Float3(scaleX, 1f, 1f),
            Float3.Zero, new Float3(2f, -1f, 0f), Float3.Zero, Float3.Zero,
            Quaternion.Identity, Quaternion.Identity, 4);
        var mapping = new FbxModelMapper.ModelMapping
        {
            Root = syntheticRoot,
            NodesByFbxId = new() { [1] = parent, [2] = child },
            TransformStacks = new() { [2] = stack },
        };
        var scene = new IntermediateScene();
        scene.Nodes.AddRange([syntheticRoot, parent, child]);
        var document = new FbxDocument(new FbxNode { Name = "root" }, 7400, null);
        if (animatedChild)
        {
            var curveNode = new FbxObject
            {
                Id = 3, ObjectType = "AnimationCurveNode", Name = "T", Subtype = "",
                Node = new FbxNode { Name = "AnimationCurveNode" }, Properties = new FbxPropertyTable(),
            };
            document.Objects.Add(3, curveNode);
            document.ConnectionsByDestination[2] = [new FbxConnection { Type = "OP", Source = 3, Destination = 2, Property = "Lcl Translation" }];
            // Equal endpoints with nonzero cubic handles must still reject frame inference.
            var curveData = new FbxNode { Name = "AnimationCurve" };
            curveData.Children.Add(new FbxNode { Name = "KeyTime", Properties = { FbxProperty.FromLongArray([0, 46186158000]) } });
            curveData.Children.Add(new FbxNode { Name = "KeyValueFloat", Properties = { FbxProperty.FromDoubleArray([1, 1]) } });
            curveData.Children.Add(new FbxNode { Name = "KeyAttrFlags", Properties = { FbxProperty.FromIntArray([0x408]) } });
            curveData.Children.Add(new FbxNode { Name = "KeyAttrDataFloat", Properties = { FbxProperty.FromDoubleArray([10, -10, 0, 0]) } });
            curveData.Children.Add(new FbxNode { Name = "KeyAttrRefCount", Properties = { FbxProperty.FromIntArray([2]) } });
            document.Objects.Add(4, new FbxObject { Id = 4, ObjectType = "AnimationCurve", Name = "X", Subtype = "", Node = curveData, Properties = new FbxPropertyTable() });
            document.ConnectionsByDestination[3] = [new FbxConnection { Type = "OP", Source = 4, Destination = 3, Property = "d|X" }];
        }
        Float4x4 expected = Float4x4.CreateTRS(new Float3(10f, 20f, -5f),
            Quaternion.AxisAngle(new Float3(0f, 1f, 0f), 0.6f), Float3.One);
        var worlds = new Dictionary<IntermediateNode, Float4x4>
        {
            [child] = expected * stack.ComposeMatrix(stack.DefaultTranslation, stack.DefaultRotationDegrees, stack.DefaultScale),
        };
        if (explicitParent) worlds[parent] = Float4x4.Identity;
        if (conflictingChild)
        {
            var sibling = new IntermediateNode { Name = "otherJoint", Parent = parent };
            parent.Children.Add(sibling);
            scene.Nodes.Add(sibling);
            mapping.NodesByFbxId[5] = sibling;
            mapping.TransformStacks[5] = stack;
            worlds[sibling] = Float4x4.CreateTranslation(new Float3(1f, 0f, 0f)) * worlds[child];
        }
        FbxSkinMapper.CompleteBindPoseAncestors(document, mapping, scene, worlds);
        Assert.Equal(shouldHaveParent, worlds.ContainsKey(parent));
        if (shouldHaveParent)
        {
            Float4x4 wanted = explicitParent ? Float4x4.Identity : expected;
            Assert.InRange(Float4.Length(worlds[parent].c3 - wanted.c3), 0f, 0.00001f);
            Assert.InRange(Float4.Length(worlds[parent].c0 - wanted.c0), 0f, 0.00001f);
            Assert.InRange(Float4.Length(worlds[parent].c1 - wanted.c1), 0f, 0.00001f);
            Assert.InRange(Float4.Length(worlds[parent].c2 - wanted.c2), 0f, 0.00001f);
        }
        Assert.False(worlds.ContainsKey(syntheticRoot));
    }

    [Theory]
    [InlineData(0, false, false, false, true)]
    [InlineData(1, false, false, false, true)]
    [InlineData(2, false, false, false, true)]
    [InlineData(1, true, false, false, false)]
    [InlineData(1, false, true, false, false)]
    [InlineData(1, false, false, true, false)]
    public void MissingParentOrigin_RotatingAndScalingChildOnlySuppliesTranslation(
        int inheritType, bool pivot, bool movingTranslation, bool conflictingChild, bool expected)
    {
        string fbx = FormattableString.Invariant($$"""
            ; FBX 7.4.0 project file
            FBXHeaderExtension: { FBXVersion: 7400 }
            Objects: {
                Model: 2, "Model::joint", "LimbNode" {
                    Properties70: {
                        P: "InheritType", "enum", "", "", {{inheritType}}
                        P: "RotationPivot", "Vector3D", "Vector", "", {{(pivot ? 1 : 0)}}, 0, 0
                    }
                }
                AnimationCurveNode: 3, "AnimCurveNode::R", "" { }
                AnimationCurve: 4, "AnimCurve::Rx", "" {
                    KeyTime: *2 { a: 0, 46186158000 }
                    KeyValueFloat: *2 { a: 0, 90 }
                }
                AnimationCurveNode: 5, "AnimCurveNode::S", "" { }
                AnimationCurve: 6, "AnimCurve::Sx", "" {
                    KeyTime: *2 { a: 0, 46186158000 }
                    KeyValueFloat: *2 { a: 1, 2 }
                }
                AnimationCurveNode: 7, "AnimCurveNode::T", "" { }
                AnimationCurve: 8, "AnimCurve::Tx", "" {
                    KeyTime: *2 { a: 0, 46186158000 }
                    KeyValueFloat: *2 { a: 0, 0 }
                    KeyAttrFlags: *1 { a: 1032 }
                    KeyAttrDataFloat: *4 { a: {{(movingTranslation ? 1 : 0)}}, 0, 0, 0 }
                    KeyAttrRefCount: *1 { a: 2 }
                }
            }
            Connections: {
                C: "OP", 3, 2, "Lcl Rotation"
                C: "OP", 4, 3, "d|X"
                C: "OP", 5, 2, "Lcl Scaling"
                C: "OP", 6, 5, "d|X"
                C: "OP", 7, 2, "Lcl Translation"
                C: "OP", 8, 7, "d|X"
            }
            """);
        FbxAsciiReader reader = FbxAsciiReader.Create(System.Text.Encoding.UTF8.GetBytes(fbx));
        var document = new FbxDocument(reader.ReadRoot(), 7400, null);
        var root = new IntermediateNode { Name = "scene" };
        var grandparent = new IntermediateNode { Name = "rig", Parent = root };
        var parent = new IntermediateNode
        {
            Name = "motion", Parent = grandparent, LocalPosition = new Float3(7f, 8f, 9f),
            LocalRotation = Quaternion.AxisAngle(Float3.UnitY, 0.7f), LocalScale = new Float3(1f, 2f, 3f),
        };
        var child = new IntermediateNode { Name = "joint", Parent = parent };
        root.Children.Add(grandparent);
        grandparent.Children.Add(parent);
        parent.Children.Add(child);
        var stack = FbxModelMapper.CaptureTransformStack(document.Objects[2].Properties);
        var mapping = new FbxModelMapper.ModelMapping
        {
            Root = root, NodesByFbxId = new() { [1] = parent, [2] = child },
            TransformStacks = new() { [2] = stack },
        };
        var scene = new IntermediateScene();
        scene.Nodes.AddRange([root, grandparent, parent, child]);
        Float4x4 grandparentBind = Float4x4.CreateTRS(new Float3(30f, 20f, 10f),
            Quaternion.AxisAngle(Float3.UnitX, 0.4f), new Float3(2f, 3f, 4f));
        Float4x4 original = grandparentBind * Float4x4.CreateTRS(parent.LocalPosition, parent.LocalRotation, parent.LocalScale);
        Float3 recoveredOrigin = new(1f, 2f, 3f);
        var worlds = new Dictionary<IntermediateNode, Float4x4>
        {
            [grandparent] = grandparentBind,
            [child] = Float4x4.CreateTranslation(recoveredOrigin),
        };
        if (conflictingChild)
        {
            var sibling = new IntermediateNode { Name = "otherJoint", Parent = parent };
            parent.Children.Add(sibling);
            scene.Nodes.Add(sibling);
            mapping.NodesByFbxId[9] = sibling;
            mapping.TransformStacks[9] = stack;
            // The same animated channels make this sibling an origin-only witness as well.
            document.ConnectionsByDestination[9] = document.ConnectionsByDestination[2];
            worlds[sibling] = Float4x4.CreateTranslation(recoveredOrigin + Float3.UnitX);
        }
        Assert.False(FbxAnimationMapper.HasConstantDefaultTransform(document, 2, stack));
        Dictionary<IntermediateNode, Float3> origins = FbxSkinMapper.CompleteBindPoseAncestors(document, mapping, scene, worlds);
        Assert.Equal(expected, origins.ContainsKey(parent));
        Assert.False(worlds.ContainsKey(parent));
        if (expected)
        {
            Assert.Equal(recoveredOrigin, origins[parent]);
            Quaternion rotation = parent.LocalRotation;
            Float3 scale = parent.LocalScale;
            FbxSkinMapper.OverrideBindPoseFromCluster(scene, worlds, origins);
            Assert.Equal(rotation, parent.LocalRotation);
            Assert.Equal(scale, parent.LocalScale);
            Float4x4 actual = grandparentBind * Float4x4.CreateTRS(parent.LocalPosition, parent.LocalRotation, parent.LocalScale);
            Assert.InRange(Float3.Length(actual.c3.XYZ - recoveredOrigin), 0f, 0.00001f);
            Assert.Equal(original.c0, actual.c0);
            Assert.Equal(original.c1, actual.c1);
            Assert.Equal(original.c2, actual.c2);
        }
        Assert.Equal(grandparentBind, worlds[grandparent]);
        Assert.False(worlds.ContainsKey(root));
    }

    [Fact]
    public void MissingParentOrigin_PropagatesOnlyOriginEvidenceToAncestors()
    {
        var root = new IntermediateNode { Name = "scene" };
        var ancestor = new IntermediateNode { Name = "ancestor", Parent = root };
        var parent = new IntermediateNode { Name = "motion", Parent = ancestor };
        var child = new IntermediateNode { Name = "joint", Parent = parent };
        root.Children.Add(ancestor);
        ancestor.Children.Add(parent);
        parent.Children.Add(child);
        var unitStack = FbxModelMapper.CaptureTransformStack(new FbxPropertyTable());
        var scaledStack = new FbxModelMapper.LocalTransformStack(
            Float3.Zero, Float3.Zero, new Float3(2f, 1f, 1f),
            Float3.Zero, Float3.Zero, Float3.Zero, Float3.Zero,
            Quaternion.Identity, Quaternion.Identity, 0);
        var mapping = new FbxModelMapper.ModelMapping
        {
            Root = root, NodesByFbxId = new() { [1] = ancestor, [2] = parent, [3] = child },
            TransformStacks = new() { [1] = unitStack, [2] = unitStack, [3] = scaledStack },
        };
        var scene = new IntermediateScene();
        scene.Nodes.AddRange([root, ancestor, parent, child]);
        Float3 origin = new(1f, 2f, 3f);
        var worlds = new Dictionary<IntermediateNode, Float4x4>
        {
            [child] = Float4x4.CreateTRS(origin, Quaternion.Identity, new Float3(2f, 1f, 1f)),
        };
        var document = new FbxDocument(new FbxNode { Name = "root" }, 7400, null);
        Dictionary<IntermediateNode, Float3> origins = FbxSkinMapper.CompleteBindPoseAncestors(document, mapping, scene, worlds);
        Assert.Single(worlds);
        Assert.Equal(origin, origins[parent]);
        Assert.Equal(origin, origins[ancestor]);
        Assert.False(origins.ContainsKey(root));
    }

    [Theory]
    [InlineData(1f, -2f, 3f)]
    [InlineData(-1f, 2f, 3f)]
    [InlineData(-1f, -2f, 3f)]
    public void TransformStack_NonUniformAndNegativeScale_ReconstructsAuthoredMatrix(float x, float y, float z)
    {
        var stack = new FbxModelMapper.LocalTransformStack(
            Float3.Zero, Float3.Zero, Float3.One,
            new Float3(2f, 1f, -3f), new Float3(4f, -5f, 6f),
            new Float3(-2f, 3f, 4f), new Float3(7f, 8f, -9f),
            Quaternion.AxisAngle(new Float3(1f, 0f, 0f), 0.3f),
            Quaternion.AxisAngle(new Float3(0f, 1f, 0f), -0.7f), 5);
        Float3 translation = new(11f, -12f, 13f), rotation = new(43f, 71f, -29f), scale = new(x, y, z);
        Float4x4 expected = stack.ComposeMatrix(translation, rotation, scale);
        stack.Decompose(translation, rotation, scale, out Float3 p, out Quaternion r, out Float3 s);
        Float4x4 actual = Float4x4.CreateTRS(p, r, s);
        Assert.InRange(Float4.Length(expected.c0 - actual.c0), 0f, 0.00001f);
        Assert.InRange(Float4.Length(expected.c1 - actual.c1), 0f, 0.00001f);
        Assert.InRange(Float4.Length(expected.c2 - actual.c2), 0f, 0.00001f);
        Assert.InRange(Float4.Length(expected.c3 - actual.c3), 0f, 0.00001f);
    }

    private static FbxAnimationMapper.ScalarCurve ReadCurve(float[] times, float[] values,
        int[] flags, float[] data, int[] references)
    {
        var node = new FbxNode { Name = "AnimationCurve" };
        var ticks = new long[times.Length];
        for (int i = 0; i < ticks.Length; i++) ticks[i] = (long)(times[i] * 46186158000d);
        node.Children.Add(new FbxNode { Name = "KeyTime", Properties = { FbxProperty.FromLongArray(ticks) } });
        node.Children.Add(new FbxNode { Name = "KeyValueFloat", Properties = { new FbxProperty { Type = FbxPropertyType.ArrayFloat, FloatArrayValue = values } } });
        node.Children.Add(new FbxNode { Name = "KeyAttrFlags", Properties = { FbxProperty.FromIntArray(flags) } });
        node.Children.Add(new FbxNode { Name = "KeyAttrDataFloat", Properties = { new FbxProperty { Type = FbxPropertyType.ArrayFloat, FloatArrayValue = data } } });
        node.Children.Add(new FbxNode { Name = "KeyAttrRefCount", Properties = { FbxProperty.FromIntArray(references) } });
        return Assert.IsType<FbxAnimationMapper.ScalarCurve>(FbxAnimationMapper.ReadScalarCurve(new FbxObject
        {
            Id = 1, ObjectType = "AnimationCurve", Name = "test", Subtype = "", Node = node, Properties = new FbxPropertyTable(),
        }));
    }

    private static Model ImportSingleAxis(string property, string axis, float lastValue, int flag, string modelProperties = "")
    {
        string fbx = FormattableString.Invariant($$"""
            ; FBX 7.4.0 project file
            FBXHeaderExtension: { FBXVersion: 7400 }
            Objects: {
                Model: 1, "Model::Bone", "LimbNode" {
                    Properties70: { {{modelProperties}} }
                }
                AnimationStack: 2, "AnimStack::Take", "" { }
                AnimationLayer: 3, "AnimLayer::Base", "" { }
                AnimationCurveNode: 4, "AnimCurveNode::T", "" { }
                AnimationCurve: 5, "AnimCurve::X", "" {
                    KeyTime: *2 { a: 0, 46186158000 }
                    KeyValueFloat: *2 { a: 0, {{lastValue}} }
                    KeyAttrFlags: *1 { a: {{flag}} }
                    KeyAttrDataFloat: *4 { a: 0, 0, 0, 0 }
                    KeyAttrRefCount: *1 { a: 2 }
                }
            }
            Connections: {
                C: "OO", 1, 0
                C: "OO", 3, 2
                C: "OO", 4, 3
                C: "OP", 4, 1, "{{property}}"
                C: "OP", 5, 4, "{{axis}}"
            }
            """);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(fbx));
        return ModelImporter.Load(stream, "fbx", ModelImporterSettings.Raw);
    }

    public static TheoryData<string> ZoneZeroAnimationFbx() => new()
    {
        @"ZonezeroTestProject/Assets/ZZZ/Arts/PlayerModel/Anbi/Anbi@Idle.FBX",
        @"ZonezeroTestProject/Assets/ZZZ/Arts/PlayerModel/Anbi/Anbi@Run.FBX",
        @"ZonezeroTestProject/Assets/ZZZ/Arts/PlayerModel/Corlin/Corin@Idle.FBX",
        @"ZonezeroTestProject/Assets/ZZZ/Arts/PlayerModel/Corlin/Corin@Run.FBX",
        @"ZonezeroTestProject/Assets/ZZZ/Arts/PlayerModel/Nicole/Nostradamus@Idle.FBX",
        @"ZonezeroTestProject/Assets/ZZZ/Arts/PlayerModel/Nicole/Nostradamus@Run.FBX",
    };

    public static TheoryData<string, string> ZoneZeroModelAndAnimationFbx() => new()
    {
        {
            @"ZonezeroTestProject/Assets/ZZZ/Arts/PlayerModel/Anbi/Anbi.FBX",
            @"ZonezeroTestProject/Assets/ZZZ/Arts/PlayerModel/Anbi/Anbi@Idle.FBX"
        },
        {
            @"ZonezeroTestProject/Assets/ZZZ/Arts/PlayerModel/Anbi/Anbi.FBX",
            @"ZonezeroTestProject/Assets/ZZZ/Arts/PlayerModel/Anbi/Anbi@Run.FBX"
        },
        {
            @"ZonezeroTestProject/Assets/ZZZ/Arts/PlayerModel/Corlin/Corin.FBX",
            @"ZonezeroTestProject/Assets/ZZZ/Arts/PlayerModel/Corlin/Corin@Idle.FBX"
        },
        {
            @"ZonezeroTestProject/Assets/ZZZ/Arts/PlayerModel/Corlin/Corin.FBX",
            @"ZonezeroTestProject/Assets/ZZZ/Arts/PlayerModel/Corlin/Corin@Run.FBX"
        },
        {
            @"ZonezeroTestProject/Assets/ZZZ/Arts/PlayerModel/Nicole/Nostradamus.FBX",
            @"ZonezeroTestProject/Assets/ZZZ/Arts/PlayerModel/Nicole/Nostradamus@Idle.FBX"
        },
        {
            @"ZonezeroTestProject/Assets/ZZZ/Arts/PlayerModel/Nicole/Nostradamus.FBX",
            @"ZonezeroTestProject/Assets/ZZZ/Arts/PlayerModel/Nicole/Nostradamus@Run.FBX"
        },
    };

    public static TheoryData<string, string> ZoneZeroAnimationWithAnimatedAttachmentFbx() => new()
    {
        {
            @"ZonezeroTestProject/Assets/ZZZ/Arts/PlayerModel/Anbi/Anbi@Attack_Normal_4.FBX",
            "Anbi_Weapon_02"
        },
        {
            @"ZonezeroTestProject/Assets/ZZZ/Arts/PlayerModel/Anbi/Anbi@BigSkill_End.FBX",
            "Anbi_Weapon_02"
        },
    };

    [Theory]
    [MemberData(nameof(ZoneZeroAnimationFbx))]
    public void ProductionClips_UseXyzEulerAndCompleteTrsAxes(string relativePath)
    {
        string fullPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", relativePath));
        Assert.True(File.Exists(fullPath), $"missing test asset: {fullPath}");

        byte[] bytes = File.ReadAllBytes(fullPath);
        Assert.True(FbxBinaryReader.TryCreate(bytes, out FbxBinaryReader? reader, out string error), error);
        FbxNode root = reader!.ReadRoot();
        var doc = new FbxDocument(root, reader.Version, creator: null);

        int rotationNodes = 0;
        int nonXyzRotationNodes = 0;
        int sparseRotationNodes = 0;
        int sparseTranslationNodes = 0;
        int sparseScaleNodes = 0;
        var orderCounts = new int[7];
        var attributeCounts = new SortedDictionary<int, int>();
        int cubicSamples = 0;
        float worstCubicVsLinear = 0f;

        foreach (FbxObject curveNode in doc.Objects.Values)
        {
            if (curveNode.ObjectType != "AnimationCurveNode") continue;
            if (!TryResolveTarget(curveNode, doc, out FbxObject? target, out string property)) continue;

            int axisMask = ResolveAxisMask(curveNode, doc);
            if (property == "Lcl Rotation")
            {
                rotationNodes++;
                int order = target!.Properties.GetIntOr("RotationOrder", 0);
                if ((uint)order < (uint)orderCounts.Length) orderCounts[order]++;
                if (order != 0) nonXyzRotationNodes++;
                if (axisMask != 0b111) sparseRotationNodes++;
            }
            else if (property == "Lcl Translation" && axisMask != 0b111)
            {
                sparseTranslationNodes++;
            }
            else if (property == "Lcl Scaling" && axisMask != 0b111)
            {
                sparseScaleNodes++;
            }
        }

        foreach (FbxObject sourceCurve in doc.Objects.Values)
        {
            if (sourceCurve.ObjectType != "AnimationCurve") continue;
            FbxNode? flagsNode = sourceCurve.Node.FindChild("KeyAttrFlags");
            FbxNode? dataNode = sourceCurve.Node.FindChild("KeyAttrDataFloat");
            FbxNode? refsNode = sourceCurve.Node.FindChild("KeyAttrRefCount");
            if (flagsNode is null || dataNode is null || refsNode is null) continue;
            int[] flags = flagsNode.Properties[0].AsIntArray();
            int[] references = refsNode.Properties[0].AsIntArray();
            float[] data = dataNode.Properties[0].AsFloatArray();
            FbxAnimationMapper.ScalarCurve curve = Assert.IsType<FbxAnimationMapper.ScalarCurve>(FbxAnimationMapper.ReadScalarCurve(sourceCurve));
            int key = 0;
            for (int record = 0; record < flags.Length; record++)
            {
                attributeCounts.TryGetValue(flags[record], out int oldCount);
                attributeCounts[flags[record]] = oldCount + references[record];
                for (int repeat = 0; repeat < references[record] && key + 1 < curve.Times.Length; repeat++, key++)
                {
                    // Evaluate at a KNOWN Bezier parameter to test the sampler's inverse-time
                    // solution against a direct polynomial (production Max exports are weighted).
                    if ((flags[record] & 0x60e) != 0x408) continue;
                    float duration = curve.Times[key + 1] - curve.Times[key];
                    if (duration <= 0f) continue;
                    uint weights = BitConverter.SingleToUInt32Bits(data[record * 4 + 2]);
                    float right = (flags[record] & 0x01000000) != 0 ? (weights & 0xffff) / 10000f : 1f / 3f;
                    float left = (flags[record] & 0x02000000) != 0 ? (weights >> 16) / 10000f : 1f / 3f;
                    // Bernstein coefficients at u=1/4 are 27/64, 27/64, 9/64, 1/64.
                    float normalizedTime = (27f * right + 9f * (1f - left) + 1f) / 64f;
                    float time = curve.Times[key] + duration * normalizedTime;
                    float controlRight = curve.Values[key] + duration * right * data[record * 4];
                    float controlLeft = curve.Values[key + 1] - duration * left * data[record * 4 + 1];
                    float expected = (27f * curve.Values[key] + 27f * controlRight + 9f * controlLeft + curve.Values[key + 1]) / 64f;
                    float linear = curve.Values[key] * (1f - normalizedTime) + curve.Values[key + 1] * normalizedTime;
                    float timeRounding = MathF.Abs(time - curve.Times[key] - duration * normalizedTime);
                    float tolerance = 0.0001f * MathF.Max(1f, MathF.Abs(expected)) +
                        4f * timeRounding * MathF.Max(MathF.Abs(data[record * 4]), MathF.Abs(data[record * 4 + 1]));
                    Assert.InRange(MathF.Abs(curve.Sample(time) - expected), 0f, tolerance);
                    worstCubicVsLinear = MathF.Max(worstCubicVsLinear, MathF.Abs(expected - linear));
                    cubicSamples++;
                }
            }
        }

        output.WriteLine(
            $"{Path.GetFileName(fullPath)} rotations={rotationNodes} nonXYZ={nonXyzRotationNodes} " +
            $"sparseR={sparseRotationNodes} sparseT={sparseTranslationNodes} sparseS={sparseScaleNodes} " +
            $"orders=[{string.Join(',', orderCounts)}]");
        output.WriteLine($"cubicSamples={cubicSamples} worstCubicVsLinear={worstCubicVsLinear:0.######}");
        foreach ((int flag, int count) in attributeCounts)
            output.WriteLine($"keyFlags=0x{flag:X8} keys={count}");

        Assert.True(rotationNodes > 0, $"{relativePath}: no animated rotation curve nodes found");
        Assert.Equal(0, nonXyzRotationNodes);
        Assert.Equal(0, sparseRotationNodes);
        Assert.Equal(0, sparseTranslationNodes);
        Assert.Equal(0, sparseScaleNodes);
        Assert.True(cubicSamples > 0, $"{relativePath}: fixture no longer exercises authored cubic slopes");
        Assert.True(worstCubicVsLinear > 0.0001f, $"{relativePath}: cubic fixture no longer differs from linear sampling");
    }

    [Theory]
    [MemberData(nameof(ZoneZeroModelAndAnimationFbx))]
    public void ProductionClips_KeepReferencePoseDistinctFromAuthoredFirstFrame(string modelPath, string animationPath)
    {
        Model model = ModelImporter.Load(RepoPath(modelPath));
        Model animationModel = ModelImporter.Load(RepoPath(animationPath));
        Assert.NotEmpty(animationModel.AnimationClips);

        AnimationClip clip = animationModel.AnimationClips[0];
        int matched = 0;
        int sourceVsModel30 = 0;
        int firstVsSource30 = 0;
        float worstSourceVsModel = 0f;
        float worstFirstVsSource = 0f;
        float worstPositionDelta = 0f;
        float worstScaleDelta = 0f;
        string worstSourceNode = string.Empty;
        string worstFirstNode = string.Empty;
        string worstPositionNode = string.Empty;
        string worstScaleNode = string.Empty;

        foreach (AnimationBinding binding in clip.Bindings)
        {
            if (binding.Property != AnimatedProperty.Rotation) continue;
            ModelNode sourceNode = animationModel.Nodes[binding.NodeIndex];
            ModelNode? modelNode = FindNode(model, sourceNode.Name);
            if (modelNode is null) continue;

            matched++;
            float sourceVsModel = AngleDeg(sourceNode.LocalRotation, modelNode.LocalRotation);
            float firstVsSource = AngleDeg(
                binding.Curve.EvaluateQuaternion(clip.StartTime),
                sourceNode.LocalRotation);
            if (sourceVsModel > 30f) sourceVsModel30++;
            if (firstVsSource > 30f) firstVsSource30++;
            if (sourceVsModel > worstSourceVsModel)
            {
                worstSourceVsModel = sourceVsModel;
                worstSourceNode = sourceNode.Name;
            }
            if (firstVsSource > worstFirstVsSource)
            {
                worstFirstVsSource = firstVsSource;
                worstFirstNode = sourceNode.Name;
            }
        }

        for (int i = 0; i < animationModel.Nodes.Count; i++)
        {
            ModelNode sourceNode = animationModel.Nodes[i];
            ModelNode? modelNode = FindNode(model, sourceNode.Name);
            if (modelNode is null) continue;
            float positionDelta = Float3.Length(sourceNode.LocalPosition - modelNode.LocalPosition);
            float scaleDelta = Float3.Length(sourceNode.LocalScale - modelNode.LocalScale);
            if (positionDelta > worstPositionDelta)
            {
                worstPositionDelta = positionDelta;
                worstPositionNode = sourceNode.Name;
            }
            if (scaleDelta > worstScaleDelta)
            {
                worstScaleDelta = scaleDelta;
                worstScaleNode = sourceNode.Name;
            }
        }

        output.WriteLine(
            $"{Path.GetFileName(animationPath)} matchedR={matched} " +
            $"sourceBind!=modelBind>30={sourceVsModel30} worst={worstSourceVsModel:0.###}({worstSourceNode}) " +
            $"firstFrame!=sourceBind>30={firstVsSource30} worst={worstFirstVsSource:0.###}({worstFirstNode}) " +
            $"worstBindPositionDelta={worstPositionDelta:0.######}({worstPositionNode}) " +
            $"worstBindScaleDelta={worstScaleDelta:0.######}({worstScaleNode})");
        Assert.True(matched > 0, $"{animationPath}: no rotation bindings matched the model skeleton");
        Assert.True(sourceVsModel30 > 0,
            $"{animationPath}: fixture no longer exercises source/destination bone-frame retargeting");
        Assert.True(firstVsSource30 > 0,
            $"{animationPath}: fixture no longer proves that authored frame zero differs from the source reference pose");
        Assert.True(worstPositionDelta < 0.02f,
            $"{animationPath}: source and model rigs differ by more than 2 cm; same-rig retarget assumptions are invalid");
        Assert.True(worstScaleDelta < 0.0001f,
            $"{animationPath}: source and model rig scales diverged; same-rig retarget assumptions are invalid");
    }

    [Theory]
    [MemberData(nameof(ZoneZeroAnimationWithAnimatedAttachmentFbx))]
    public void ProductionClips_PreserveAuthoredTrsAndAttachmentMotion(
        string animationPath,
        string animatedAttachmentName)
    {
        string fullPath = RepoPath(animationPath);
        Model animationModel = ModelImporter.Load(fullPath);
        AnimationClip clip = Assert.Single(animationModel.AnimationClips);
        ModelNode pelvis = Assert.Single(animationModel.Nodes, n => n.Name == "Bip001 Pelvis");

        byte[] bytes = File.ReadAllBytes(fullPath);
        Assert.True(FbxBinaryReader.TryCreate(bytes, out FbxBinaryReader? reader, out string error), error);
        var document = new FbxDocument(reader!.ReadRoot(), reader.Version, creator: null);
        FbxObject pelvisObject = Assert.Single(
            document.Objects.Values,
            o => o.ObjectType == "Model" && o.Name == "Bip001 Pelvis");
        FbxModelMapper.LocalTransformStack stack = FbxModelMapper.CaptureTransformStack(pelvisObject.Properties);
        stack.Decompose(
            stack.DefaultTranslation,
            stack.DefaultRotationDegrees,
            stack.DefaultScale,
            out Float3 propertyPosition,
            out Quaternion propertyRotation,
            out Float3 propertyScale);

        AnimationBinding position = Assert.Single(
            clip.Bindings,
            b => b.NodeIndex == pelvis.Index && b.Property == AnimatedProperty.Position);
        AnimationBinding rotation = Assert.Single(
            clip.Bindings,
            b => b.NodeIndex == pelvis.Index && b.Property == AnimatedProperty.Rotation);
        AnimationBinding scale = Assert.Single(
            clip.Bindings,
            b => b.NodeIndex == pelvis.Index && b.Property == AnimatedProperty.Scale);

        // Bip001 has no explicit bind matrix in these exports. Its constant pelvis child does;
        // recovering that missing ancestor frame must remove the former clip-dependent pelvis
        // reference offset without modifying any authored samples.
        Assert.True(FbxAnimationMapper.HasConstantDefaultTransform(document, pelvisObject.Id, stack));
        float rawToBindPositionOffset = Float3.Length(propertyPosition - pelvis.LocalPosition);
        Assert.InRange(rawToBindPositionOffset, 0f, 0.00001f);

        // P/R/S are emitted from one decomposed matrix timeline. They must remain authored source
        // samples here; AnimationClipPlayable owns the only source-bind -> target-bind retarget.
        // Collapsing the first sample to the imported skin reference recreates the T-pose bug.
        Assert.True(position.Curve.Count > 1, $"{animationPath}: authored position was not sampled");
        Assert.Equal(position.Curve.Count, rotation.Curve.Count);
        Assert.Equal(position.Curve.Count, scale.Curve.Count);
        for (int i = 0; i < position.Curve.Count; i++)
        {
            Assert.Equal(position.Curve[i].Time, rotation.Curve[i].Time, precision: 6);
            Assert.Equal(position.Curve[i].Time, scale.Curve[i].Time, precision: 6);
        }

        Float3 firstPosition = position.Curve.EvaluateFloat3(clip.StartTime);
        Quaternion firstRotation = rotation.Curve.EvaluateQuaternion(clip.StartTime);
        Float3 firstScale = scale.Curve.EvaluateFloat3(clip.StartTime);
        float firstPositionOffset = Float3.Length(firstPosition - pelvis.LocalPosition);
        float firstRotationOffset = QuaternionDistance(firstRotation, pelvis.LocalRotation);
        float firstScaleOffset = Float3.Length(firstScale - pelvis.LocalScale);
        output.WriteLine(
            $"{Path.GetFileName(animationPath)} pelvis keys={position.Curve.Count} " +
            $"skinReference={pelvis.LocalPosition}/{pelvis.LocalRotation}/{pelvis.LocalScale} " +
            $"propertyReference={propertyPosition}/{propertyRotation}/{propertyScale} " +
            $"first={firstPosition}/{firstRotation}/{firstScale} " +
            $"offsets={firstPositionOffset:0.########}/" +
            $"{firstRotationOffset:0.########}quat/{firstScaleOffset:0.########}");
        Assert.InRange(firstPositionOffset, 0f, 0.00001f);
        ModelNode motionRoot = Assert.Single(animationModel.Nodes, n => n.Name == "Bip001");
        AnimationBinding rootPosition = Assert.Single(clip.Bindings,
            b => b.NodeIndex == motionRoot.Index && b.Property == AnimatedProperty.Position);
        Assert.True(Float3.Length(rootPosition.Curve.EvaluateFloat3(clip.StartTime) - motionRoot.LocalPosition) > 0.02f,
            $"{animationPath}: recovering the reference frame must not rebase authored motion-root keys");
        Model sourceRig = ModelImporter.Load(RepoPath("ZonezeroTestProject/Assets/ZZZ/Arts/PlayerModel/Anbi/Anbi.FBX"));
        ModelNode referenceRoot = Assert.Single(sourceRig.Nodes, n => n.Name == "Bip001");
        Assert.InRange(Float3.Length(motionRoot.LocalPosition - referenceRoot.LocalPosition), 0f, 0.00001f);
        Assert.InRange(AngleDeg(motionRoot.LocalRotation, referenceRoot.LocalRotation), 0f, 0.05f);

        // Weapon animation must retain its authored positional range or the attachment becomes
        // rigid even if the body pose itself looks plausible.
        ModelNode attachment = Assert.Single(
            animationModel.Nodes,
            n => n.Name == animatedAttachmentName);
        AnimationBinding attachmentPosition = Assert.Single(
            clip.Bindings,
            b => b.NodeIndex == attachment.Index && b.Property == AnimatedProperty.Position);
        float attachmentRange = PositionRange(attachmentPosition.Curve);
        output.WriteLine($"{animatedAttachmentName} positionRange={attachmentRange:0.########}");
        Assert.True(attachmentRange > 1e-4f,
            $"{animationPath}/{animatedAttachmentName}: bind correction pinned legitimate attachment motion");
    }

    [Theory]
    [InlineData("Corin@Attack_Normal_5.FBX")]
    [InlineData("Corin@Attack_Normal_5_End.FBX")]
    public void ProductionRotatingPelvis_RecoversOnlyRootOriginAndKeepsAuthoredMotion(string fileName)
    {
        string directory = "ZonezeroTestProject/Assets/ZZZ/Arts/PlayerModel/Corlin/";
        string path = RepoPath(directory + fileName);
        Model source = ModelImporter.Load(path, ModelImporterSettings.Raw);
        Model reference = ModelImporter.Load(RepoPath(directory + "Corin.FBX"), ModelImporterSettings.Raw);
        AnimationClip clip = Assert.Single(source.AnimationClips);
        ModelNode root = Assert.Single(source.Nodes, n => n.Name == "Bip001");
        ModelNode pelvis = Assert.Single(source.Nodes, n => n.Name == "Bip001 Pelvis");
        ModelNode referenceRoot = Assert.Single(reference.Nodes, n => n.Name == "Bip001");
        Assert.True(FbxBinaryReader.TryCreate(File.ReadAllBytes(path), out FbxBinaryReader? reader, out string error), error);
        var doc = new FbxDocument(reader!.ReadRoot(), reader.Version, null);
        FbxObject pelvisObject = Assert.Single(doc.Objects.Values, o => o.ObjectType == "Model" && o.Name == pelvis.Name);
        var pelvisStack = FbxModelMapper.CaptureTransformStack(pelvisObject.Properties);
        Assert.False(FbxAnimationMapper.HasConstantDefaultTransform(doc, pelvisObject.Id, pelvisStack));
        Assert.True(FbxAnimationMapper.HasConstantZeroLocalOrigin(doc, pelvisObject.Id, pelvisStack));
        Assert.InRange(Float3.Length(root.LocalPosition - referenceRoot.LocalPosition), 0f, 0.00001f);
        Assert.InRange(Float3.Length(pelvis.LocalPosition), 0f, 0.00001f);

        FbxObject rootObject = Assert.Single(doc.Objects.Values, o => o.ObjectType == "Model" && o.Name == root.Name);
        var rootStack = FbxModelMapper.CaptureTransformStack(rootObject.Properties);
        rootStack.Decompose(rootStack.DefaultTranslation, rootStack.DefaultRotationDegrees, rootStack.DefaultScale,
            out _, out Quaternion savedRootRotation, out Float3 savedRootScale);
        Assert.InRange(QuaternionDistance(root.LocalRotation, savedRootRotation), 0f, 0.000001f);
        Assert.InRange(Float3.Length(root.LocalScale - savedRootScale), 0f, 0.000001f);
        AnimationBinding rootPosition = Assert.Single(clip.Bindings,
            b => b.NodeIndex == root.Index && b.Property == AnimatedProperty.Position);
        Assert.True(Float3.Length(rootPosition.Curve.EvaluateFloat3(clip.StartTime) - root.LocalPosition) > 5f);

        var axes = new Dictionary<string, FbxAnimationMapper.ScalarCurve>();
        foreach (FbxConnection target in doc.ConnectionsByDestination[pelvisObject.Id])
        {
            if (target.Type != "OP" || target.Property != "Lcl Rotation") continue;
            foreach (FbxConnection axis in doc.ConnectionsByDestination[target.Source])
            {
                if (axis.Type != "OP" || axis.Property is null) continue;
                axes[axis.Property] = Assert.IsType<FbxAnimationMapper.ScalarCurve>(
                    FbxAnimationMapper.ReadScalarCurve(doc.Objects[axis.Source]));
            }
        }
        AnimationBinding rotation = Assert.Single(clip.Bindings,
            b => b.NodeIndex == pelvis.Index && b.Property == AnimatedProperty.Rotation);
        AnimationBinding position = Assert.Single(clip.Bindings,
            b => b.NodeIndex == pelvis.Index && b.Property == AnimatedProperty.Position);
        float rotationRange = 0f;
        Quaternion first = rotation.Curve.EvaluateQuaternion(clip.StartTime);
        for (int i = 0; i <= 113; i++)
        {
            float time = clip.StartTime + (clip.EndTime - clip.StartTime) * i / 113f;
            Float3 euler = new(axes["d|X"].Sample(time), axes["d|Y"].Sample(time), axes["d|Z"].Sample(time));
            pelvisStack.Decompose(Float3.Zero, euler, Float3.One, out _, out Quaternion authored, out _);
            Quaternion actual = rotation.Curve.EvaluateQuaternion(time);
            Assert.InRange(AngleDeg(actual, authored), 0f, 0.1f);
            Assert.InRange(Float3.Length(position.Curve.EvaluateFloat3(time)), 0f, 0.000001f);
            rotationRange = MathF.Max(rotationRange, AngleDeg(first, actual));
        }
        Assert.True(rotationRange > 0.05f, "The pelvis rotation must retain its authored variation.");
        ModelNode prop = Assert.Single(source.Nodes, n => n.Name == "Bip001 Prop1");
        AnimationBinding propPosition = Assert.Single(clip.Bindings,
            b => b.NodeIndex == prop.Index && b.Property == AnimatedProperty.Position);
        Assert.True(PositionRange(propPosition.Curve) > 0.01f, "The weapon must retain its authored motion.");
        output.WriteLine($"{fileName} rootBind={root.LocalPosition} pelvisBind={pelvis.LocalPosition} " +
            $"pelvisRotationRange={rotationRange:0.######} weaponRange={PositionRange(propPosition.Curve):0.######}");
    }

    private static bool TryResolveTarget(
        FbxObject curveNode,
        FbxDocument doc,
        out FbxObject? target,
        out string property)
    {
        target = null;
        property = string.Empty;
        if (!doc.ConnectionsBySource.TryGetValue(curveNode.Id, out List<FbxConnection>? connections))
            return false;

        foreach (FbxConnection connection in connections)
        {
            if (connection.Type != "OP") continue;
            if (!doc.Objects.TryGetValue(connection.Destination, out FbxObject? candidate)) continue;
            if (candidate.ObjectType != "Model") continue;
            target = candidate;
            property = connection.Property;
            return true;
        }

        return false;
    }

    private static int ResolveAxisMask(FbxObject curveNode, FbxDocument doc)
    {
        if (!doc.ConnectionsByDestination.TryGetValue(curveNode.Id, out List<FbxConnection>? connections))
            return 0;

        int mask = 0;
        foreach (FbxConnection connection in connections)
        {
            if (connection.Type != "OP") continue;
            if (!doc.Objects.TryGetValue(connection.Source, out FbxObject? curve)) continue;
            if (curve.ObjectType != "AnimationCurve") continue;
            mask |= connection.Property switch
            {
                "d|X" => 0b001,
                "d|Y" => 0b010,
                "d|Z" => 0b100,
                _ => 0,
            };
        }
        return mask;
    }

    private static string RepoPath(string relativePath) => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", relativePath));

    private static ModelNode? FindNode(Model model, string name)
    {
        for (int i = 0; i < model.Nodes.Count; i++)
            if (model.Nodes[i].Name == name)
                return model.Nodes[i];
        return null;
    }

    private static float PositionRange(AnimationCurve curve)
    {
        Float3 minimum = new(float.MaxValue, float.MaxValue, float.MaxValue);
        Float3 maximum = new(float.MinValue, float.MinValue, float.MinValue);
        for (int i = 0; i < curve.Count; i++)
        {
            Float3 value = curve.EvaluateFloat3(curve[i].Time);
            minimum = new Float3(
                MathF.Min(minimum.X, value.X),
                MathF.Min(minimum.Y, value.Y),
                MathF.Min(minimum.Z, value.Z));
            maximum = new Float3(
                MathF.Max(maximum.X, value.X),
                MathF.Max(maximum.Y, value.Y),
                MathF.Max(maximum.Z, value.Z));
        }
        return Float3.Length(maximum - minimum);
    }

    private static float AngleDeg(Quaternion a, Quaternion b)
    {
        float dot = MathF.Abs(a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W);
        dot = Math.Clamp(dot, 0f, 1f);
        return 2f * MathF.Acos(dot) * 180f / MathF.PI;
    }

    private static float QuaternionDistance(Quaternion a, Quaternion b)
    {
        Float4 av = new(a.X, a.Y, a.Z, a.W);
        Float4 bv = new(b.X, b.Y, b.Z, b.W);
        return MathF.Min(Float4.Length(av - bv), Float4.Length(av + bv));
    }
}
