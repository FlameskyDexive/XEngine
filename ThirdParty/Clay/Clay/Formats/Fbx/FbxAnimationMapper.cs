// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Clay.Importer;
using XEngine.Clay.Internal.Intermediate;
using XEngine.Vector;

namespace XEngine.Clay.Formats.Fbx;

/// <summary>
/// Maps FBX <c>AnimationStack</c> / <c>AnimationLayer</c> / <c>AnimationCurveNode</c> /
/// <c>AnimationCurve</c> objects to <see cref="IntermediateAnimation"/>s.
/// </summary>
/// <remarks>
/// FBX animation graph:
/// <code>
///   AnimationStack (= "clip")
///     -&gt; AnimationLayer (one or more, usually one)
///       -&gt; AnimationCurveNode (one per target-property: Lcl Translation, Lcl Rotation,
///                                Lcl Scaling, DeformPercent for blend-shape weights)
///         -&gt; AnimationCurve (one per scalar channel: X, Y, Z)
/// </code>
/// Curve nodes connect to their target <c>Model</c> via OP with the property name being the FBX
/// channel name (e.g. "Lcl Translation"). Curves carry <c>KeyTime</c> (int64 in KTime ticks,
/// 1/46186158000 of a second) and <c>KeyValueFloat</c> (parallel float values).
/// </remarks>
internal static class FbxAnimationMapper
{
    /// <summary>FBX time unit: 1 second = 46186158000 KTime ticks.</summary>
    private const double KTimePerSecond = 46186158000.0;

    public static void MapAll(FbxDocument doc, FbxModelMapper.ModelMapping modelMapping, IntermediateScene scene, ImportContext ctx,
        Dictionary<long, FbxBlendShapeMapper.ChannelTarget> blendShapeChannels)
    {
        // Walk every AnimationStack. Each becomes one IntermediateAnimation.
        foreach (var stack in doc.Objects.Values)
        {
            if (stack.ObjectType != "AnimationStack") continue;
            var anim = BuildClip(
                stack,
                doc,
                modelMapping,
                blendShapeChannels,
                ctx);
            if (anim is null || anim.Bindings.Count == 0) continue;
            scene.Animations.Add(anim);
        }
    }

    private static IntermediateAnimation? BuildClip(
        FbxObject stack,
        FbxDocument doc,
        FbxModelMapper.ModelMapping modelMapping,
        Dictionary<long, FbxBlendShapeMapper.ChannelTarget> blendShapeChannels, ImportContext ctx)
    {
        var clip = new IntermediateAnimation { Name = string.IsNullOrEmpty(stack.Name) ? $"Take_{stack.Id}" : stack.Name };
        var transformCurves = new Dictionary<long, TransformCurveSet>();

        // Stack -> Layers (Layer is a destination of an OO connection from the Stack).
        var layers = new List<FbxObject>();
        if (doc.ConnectionsByDestination.TryGetValue(stack.Id, out var stackIn))
        {
            foreach (var c in stackIn)
            {
                if (c.Type != "OO") continue;
                if (!doc.Objects.TryGetValue(c.Source, out var src)) continue;
                if (src.ObjectType == "AnimationLayer")
                    layers.Add(src);
            }
        }
        if (layers.Count == 0) return null;

        // Collect all T/R/S curve nodes for a Model before emitting bindings. FBX pivots couple
        // animated rotation/scale into the decomposed local position, so processing each property
        // independently loses part of the transform stack (notably on skeletal pelvis nodes).
        foreach (var layer in layers)
        {
            if (!doc.ConnectionsByDestination.TryGetValue(layer.Id, out var layerIn)) continue;
            foreach (var c in layerIn)
            {
                if (c.Type != "OO") continue;
                if (!doc.Objects.TryGetValue(c.Source, out var src)) continue;
                if (src.ObjectType != "AnimationCurveNode") continue;
                CollectCurveNode(
                    src,
                    doc,
                    modelMapping,
                    blendShapeChannels,
                    clip,
                    transformCurves);
            }
        }

        foreach (TransformCurveSet curves in transformCurves.Values)
            EmitTransformBindings(curves, clip);

        // The clip's time range is measured at bake, from the curves themselves.
        _ = ctx;
        return clip;
    }

    private static void CollectCurveNode(
        FbxObject curveNode, FbxDocument doc, FbxModelMapper.ModelMapping modelMapping,
        Dictionary<long, FbxBlendShapeMapper.ChannelTarget> blendShapeChannels,
        IntermediateAnimation clip,
        Dictionary<long, TransformCurveSet> transformCurves)
    {
        // The curve node connects to its target via OP. The target is either a Model (transform
        // channels) or a BlendShapeChannel deformer (DeformPercent = blend-shape weight).
        string property = string.Empty;
        IntermediateNode? targetNode = null;
        long targetFbxId = 0;
        FbxObject? targetObj = null;
        if (doc.ConnectionsBySource.TryGetValue(curveNode.Id, out var curveOut))
        {
            foreach (var c in curveOut)
            {
                if (c.Type != "OP") continue;
                if (!doc.Objects.TryGetValue(c.Destination, out var dst)) continue;

                if (dst.ObjectType == "Model" && modelMapping.NodesByFbxId.TryGetValue(dst.Id, out targetNode))
                {
                    property = c.Property;
                    targetFbxId = dst.Id;
                    targetObj = dst;
                    break;
                }
                if (dst.ObjectType == "Deformer" && dst.Subtype == "BlendShapeChannel")
                {
                    property = c.Property;
                    targetObj = dst;
                    break;
                }
            }
        }
        if (targetObj is null) return;

        // Blend-shape weight channel (DeformPercent on a BlendShapeChannel).
        if (targetObj.ObjectType == "Deformer")
        {
            EmitBlendShapeWeightBinding(targetObj, curveNode, doc, modelMapping, blendShapeChannels, clip);
            return;
        }

        if (targetNode is null || property.Length == 0) return;

        if (property != "Lcl Translation" && property != "Lcl Rotation" && property != "Lcl Scaling")
            return; // unsupported Model property

        // Collect the (X/Y/Z) child curves via OP (Curve -> CurveNode with property "d|X" etc.).
        var curveX = ResolveScalarCurve(curveNode, doc, "d|X");
        var curveY = ResolveScalarCurve(curveNode, doc, "d|Y");
        var curveZ = ResolveScalarCurve(curveNode, doc, "d|Z");
        if (curveX is null && curveY is null && curveZ is null) return;

        if (!transformCurves.TryGetValue(targetFbxId, out TransformCurveSet? curves))
        {
            if (!modelMapping.TransformStacks.TryGetValue(
                    targetFbxId,
                    out FbxModelMapper.LocalTransformStack transformStack))
                return;
            curves = new TransformCurveSet(targetNode, transformStack);
            transformCurves.Add(targetFbxId, curves);
        }
        curves.Set(
            property,
            curveX,
            curveY,
            curveZ,
            ReadCurveNodeDefaults(curveNode, curves.DefaultFor(property)));
    }

    private static void EmitTransformBindings(TransformCurveSet curves, IntermediateAnimation clip)
    {
        // Always evaluate the complete FBX stack. Besides pivot-coupled nodes, this also keeps
        // negative-scale decomposition stable. The authored samples stay in the source rig's
        // local space; AnimationClipPlayable performs the one required source-bind -> target-bind
        // retarget at runtime. Rebasing here as well destroys the authored first pose.
        if (curves.HasTranslation || curves.HasRotation || curves.HasScale)
            EmitBakedTransformBindings(curves, clip);
    }

    private static Float3 ReadCurveNodeDefaults(FbxObject curveNode, Float3 fallback) => new(
        (float)curveNode.Properties.GetDoubleOr("d|X", fallback.X),
        (float)curveNode.Properties.GetDoubleOr("d|Y", fallback.Y),
        (float)curveNode.Properties.GetDoubleOr("d|Z", fallback.Z));

    /// <summary>Resolves the AnimationCurve attached to a CurveNode under a given property name.</summary>
    private static ScalarCurve? ResolveScalarCurve(FbxObject curveNode, FbxDocument doc, string propertyName)
    {
        if (!doc.ConnectionsByDestination.TryGetValue(curveNode.Id, out var inConns)) return null;
        foreach (var c in inConns)
        {
            if (c.Type != "OP" || c.Property != propertyName) continue;
            if (!doc.Objects.TryGetValue(c.Source, out var src)) continue;
            if (src.ObjectType != "AnimationCurve") continue;
            return ReadScalarCurve(src);
        }
        return null;
    }

    /// <summary>Resolves the first AnimationCurve attached to a CurveNode, regardless of property name.</summary>
    private static ScalarCurve? ResolveFirstScalarCurve(FbxObject curveNode, FbxDocument doc)
    {
        if (!doc.ConnectionsByDestination.TryGetValue(curveNode.Id, out var inConns)) return null;
        foreach (var c in inConns)
        {
            if (c.Type != "OP") continue;
            if (!doc.Objects.TryGetValue(c.Source, out var src)) continue;
            if (src.ObjectType != "AnimationCurve") continue;
            var curve = ReadScalarCurve(src);
            if (curve is not null) return curve;
        }
        return null;
    }

    internal static ScalarCurve? ReadScalarCurve(FbxObject animCurve)
    {
        var keyTimeNode = animCurve.Node.FindChild("KeyTime");
        var keyValueNode = animCurve.Node.FindChild("KeyValueFloat");
        if (keyTimeNode is null || keyValueNode is null) return null;
        if (keyTimeNode.Properties.Count == 0 || keyValueNode.Properties.Count == 0) return null;

        // KeyTime is i64 KTime ticks; KeyValueFloat is f32 per key.
        long[] times = keyTimeNode.Properties[0].AsLongArray();
        float[] values = keyValueNode.Properties[0].FloatArrayValue ?? keyValueNode.Properties[0].AsFloatArray();
        if (times.Length == 0 || values.Length == 0) return null;
        int len = Math.Min(times.Length, values.Length);
        float[] timesS = new float[len];
        for (int i = 0; i < len; i++) timesS[i] = (float)(times[i] / KTimePerSecond);
        float[] vals = new float[len];
        Array.Copy(values, vals, len);
        return new ScalarCurve(timesS, vals,
            ReadArrayProperty(animCurve.Node, "KeyAttrFlags")?.AsIntArray(),
            ReadArrayProperty(animCurve.Node, "KeyAttrDataFloat")?.AsFloatArray(),
            ReadArrayProperty(animCurve.Node, "KeyAttrRefCount")?.AsIntArray());
    }

    private static FbxProperty? ReadArrayProperty(FbxNode node, string name)
    {
        FbxNode? child = node.FindChild(name);
        return child is { Properties.Count: > 0 } ? child.Properties[0] : null;
    }

    /// <summary>Whether every authored local channel stays at its Model default in every take.</summary>
    internal static bool HasConstantDefaultTransform(FbxDocument doc, long modelId,
        FbxModelMapper.LocalTransformStack stack) =>
        HasConstantDefaultChannels(doc, modelId, stack, propertyFilter: null);

    /// <summary>A co-located child can rotate/scale freely without moving its parent's origin.</summary>
    internal static bool HasConstantZeroLocalOrigin(FbxDocument doc, long modelId,
        FbxModelMapper.LocalTransformStack stack) =>
        stack.HasUnpivotedOrigin && stack.DefaultTranslation == Float3.Zero &&
        HasConstantDefaultChannels(doc, modelId, stack, "Lcl Translation");

    private static bool HasConstantDefaultChannels(FbxDocument doc, long modelId,
        FbxModelMapper.LocalTransformStack stack, string? propertyFilter)
    {
        if (!doc.ConnectionsByDestination.TryGetValue(modelId, out var connections)) return true;
        foreach (FbxConnection connection in connections)
        {
            if (connection.Type != "OP" ||
                !doc.Objects.TryGetValue(connection.Source, out FbxObject? node) ||
                node.ObjectType != "AnimationCurveNode") continue;
            if (propertyFilter is not null && connection.Property != propertyFilter) continue;
            Float3 defaults;
            switch (connection.Property)
            {
                case "Lcl Translation": defaults = stack.DefaultTranslation; break;
                case "Lcl Rotation": defaults = stack.DefaultRotationDegrees; break;
                case "Lcl Scaling": defaults = stack.DefaultScale; break;
                default: continue;
            }
            Float3 nodeDefaults = ReadCurveNodeDefaults(node, defaults);
            for (int axis = 0; axis < 3; axis++)
            {
                string property = axis == 0 ? "d|X" : axis == 1 ? "d|Y" : "d|Z";
                float expected = axis == 0 ? defaults.X : axis == 1 ? defaults.Y : defaults.Z;
                float fallback = axis == 0 ? nodeDefaults.X : axis == 1 ? nodeDefaults.Y : nodeDefaults.Z;
                ScalarCurve? curve = ResolveScalarCurve(node, doc, property);
                if (curve is not null ? !curve.StaysAt(expected, 0.0001f) : MathF.Abs(fallback - expected) > 0.0001f)
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Emits a <see cref="AnimatedProperty.BlendShapeWeight"/> binding from a DeformPercent curve on a
    /// BlendShapeChannel. FBX DeformPercent is already 0-100, matching the blend-shape frame weights.
    /// </summary>
    private static void EmitBlendShapeWeightBinding(
        FbxObject channel, FbxObject curveNode, FbxDocument doc, FbxModelMapper.ModelMapping modelMapping,
        Dictionary<long, FbxBlendShapeMapper.ChannelTarget> blendShapeChannels, IntermediateAnimation clip)
    {
        if (!blendShapeChannels.TryGetValue(channel.Id, out var target)) return;

        IntermediateNode? node = ResolveNodeForGeometry(target.GeometryId, doc, modelMapping);
        if (node is null) return;

        ScalarCurve? curve = ResolveScalarCurve(curveNode, doc, "d|DeformPercent") ?? ResolveFirstScalarCurve(curveNode, doc);
        if (curve is null || curve.Times.Length == 0) return;

        var binding = new IntermediateAnimationBinding
        {
            TargetNode = node,
            Property = AnimatedProperty.BlendShapeWeight,
            SubIndex = target.BlendShapeIndex,
            Interpolation = CurveInterpolation.Linear,
            Dimension = 1,
        };
        var times = new SortedSet<float>();
        AddTimes(times, curve);
        foreach (float time in times)
        {
            binding.Times.Add(time);
            binding.Values.Add(curve.Sample(time));
        }
        clip.Bindings.Add(binding);
    }

    /// <summary>Finds the model node that references a geometry (Geometry --OO--&gt; Model).</summary>
    private static IntermediateNode? ResolveNodeForGeometry(long geometryId, FbxDocument doc, FbxModelMapper.ModelMapping modelMapping)
    {
        if (!doc.ConnectionsBySource.TryGetValue(geometryId, out var outConns)) return null;
        foreach (var c in outConns)
        {
            if (c.Type != "OO") continue;
            if (!doc.Objects.TryGetValue(c.Destination, out var dst)) continue;
            if (dst.ObjectType != "Model") continue;
            if (modelMapping.NodesByFbxId.TryGetValue(dst.Id, out var node)) return node;
        }
        return null;
    }

    private static void EmitBakedTransformBindings(
        TransformCurveSet curves,
        IntermediateAnimation clip)
    {
        float[] mergedTimes = MergeTimes(
            curves.TranslationX, curves.TranslationY, curves.TranslationZ,
            curves.RotationX, curves.RotationY, curves.RotationZ,
            curves.ScaleX, curves.ScaleY, curves.ScaleZ,
            curves.StartTime, curves.EndTime);
        if (mergedTimes.Length == 0) return;

        FbxModelMapper.LocalTransformStack stack = curves.TransformStack;
        var positionBinding = new IntermediateAnimationBinding
        {
            TargetNode = curves.Target,
            Property = AnimatedProperty.Position,
            Interpolation = CurveInterpolation.Linear,
            Dimension = 3,
        };
        var rotationBinding = new IntermediateAnimationBinding
        {
            TargetNode = curves.Target,
            Property = AnimatedProperty.Rotation,
            Interpolation = CurveInterpolation.Linear,
            Dimension = 4,
        };
        var scaleBinding = new IntermediateAnimationBinding
        {
            TargetNode = curves.Target,
            Property = AnimatedProperty.Scale,
            Interpolation = CurveInterpolation.Linear,
            Dimension = 3,
        };
        positionBinding.Times.AddRange(mergedTimes);
        rotationBinding.Times.AddRange(mergedTimes);
        scaleBinding.Times.AddRange(mergedTimes);
        positionBinding.Values.Capacity = mergedTimes.Length * 3;
        rotationBinding.Values.Capacity = mergedTimes.Length * 4;
        scaleBinding.Values.Capacity = mergedTimes.Length * 3;
        for (int i = 0; i < mergedTimes.Length; i++)
        {
            float time = mergedTimes[i];
            Float3 translation = Sample(
                curves.TranslationX,
                curves.TranslationY,
                curves.TranslationZ,
                curves.TranslationDefault,
                time);
            Float3 rotation = Sample(
                curves.RotationX,
                curves.RotationY,
                curves.RotationZ,
                curves.RotationDefault,
                time);
            Float3 scale = Sample(
                curves.ScaleX,
                curves.ScaleY,
                curves.ScaleZ,
                curves.ScaleDefault,
                time);
            stack.Decompose(
                translation,
                rotation,
                scale,
                out Float3 position,
                out Quaternion localRotation,
                out Float3 localScale);
            positionBinding.Values.Add(position.X);
            positionBinding.Values.Add(position.Y);
            positionBinding.Values.Add(position.Z);
            rotationBinding.Values.Add(localRotation.X);
            rotationBinding.Values.Add(localRotation.Y);
            rotationBinding.Values.Add(localRotation.Z);
            rotationBinding.Values.Add(localRotation.W);
            scaleBinding.Values.Add(localScale.X);
            scaleBinding.Values.Add(localScale.Y);
            scaleBinding.Values.Add(localScale.Z);
        }
        clip.Bindings.Add(positionBinding);
        clip.Bindings.Add(rotationBinding);
        clip.Bindings.Add(scaleBinding);
    }

    private static Float3 Sample(
        ScalarCurve? x,
        ScalarCurve? y,
        ScalarCurve? z,
        Float3 defaultValue,
        float time) => new(
            x?.Sample(time) ?? defaultValue.X,
            y?.Sample(time) ?? defaultValue.Y,
            z?.Sample(time) ?? defaultValue.Z);

    private static float[] MergeTimes(
        ScalarCurve? translationX,
        ScalarCurve? translationY,
        ScalarCurve? translationZ,
        ScalarCurve? rotationX,
        ScalarCurve? rotationY,
        ScalarCurve? rotationZ,
        ScalarCurve? scaleX,
        ScalarCurve? scaleY,
        ScalarCurve? scaleZ,
        float startTime,
        float endTime)
    {
        var set = new SortedSet<float>();
        // A later layer can replace a channel with a shorter curve. Keep the time range of
        // every channel seen for this node, and clamp the selected layer at those endpoints.
        set.Add(startTime);
        set.Add(endTime);
        AddTimes(set, translationX);
        AddTimes(set, translationY);
        AddTimes(set, translationZ);
        AddTimes(set, rotationX, rotation: true, tolerance: 0.02f);
        AddTimes(set, rotationY, rotation: true, tolerance: 0.02f);
        AddTimes(set, rotationZ, rotation: true, tolerance: 0.02f);
        AddTimes(set, scaleX, tolerance: 0.0001f);
        AddTimes(set, scaleY, tolerance: 0.0001f);
        AddTimes(set, scaleZ, tolerance: 0.0001f);
        float[] result = new float[set.Count];
        set.CopyTo(result);
        return result;
    }

    private static void AddTimes(SortedSet<float> destination, ScalarCurve? curve, bool rotation = false, float tolerance = 0.001f)
    {
        if (curve == null) return;
        for (int i = 0; i < curve.Times.Length; i++)
        {
            destination.Add(curve.Times[i]);
            if (i + 1 == curve.Times.Length) continue;
            float start = curve.Times[i], end = curve.Times[i + 1];
            if (end <= start) continue;
            if (curve.IsConstant(i))
            {
                // Linear output curves need a key on each side of a discontinuity. Adjacent
                // representable times preserve both Constant and ConstantNext at exact keys.
                float edge = curve.IsConstantNext(i) ? MathF.BitIncrement(start) : MathF.BitDecrement(end);
                if (edge > start && edge < end) destination.Add(edge);
                continue;
            }
            if (curve.IsCubic(i))
                AddCubicTimes(destination, curve, start, end, curve.Values[i], curve.Values[i + 1], tolerance, 0);
            if (rotation && curve.Values[i] != curve.Values[i + 1])
            {
                // Euler interpolation and pivot-induced translation cannot be represented by
                // one quaternion slerp. Bake at least 60 Hz and retain full turns on short keys.
                int divisions = Math.Max((int)MathF.Ceiling((end - start) * 60f),
                    (int)MathF.Ceiling(MathF.Abs(curve.Values[i + 1] - curve.Values[i]) / 10f));
                divisions = Math.Clamp(divisions, 1, 4096);
                for (int j = 1; j < divisions; j++)
                    destination.Add(start + (end - start) * ((float)j / divisions));
            }
        }
    }

    private static void AddCubicTimes(SortedSet<float> times, ScalarCurve curve,
        float start, float end, float first, float last, float tolerance, int depth)
    {
        float midpoint = start + (end - start) * 0.5f;
        if (midpoint <= start || midpoint >= end || depth >= 12) return;
        float middle = curve.Sample(midpoint);
        // Check quarters as well: an S-shaped cubic can match its chord at the midpoint.
        float quarter = curve.Sample(start + (end - start) * 0.25f);
        float threeQuarter = curve.Sample(start + (end - start) * 0.75f);
        if (MathF.Abs(middle - (first + last) * 0.5f) <= tolerance &&
            MathF.Abs(quarter - (first * 0.75f + last * 0.25f)) <= tolerance &&
            MathF.Abs(threeQuarter - (first * 0.25f + last * 0.75f)) <= tolerance)
            return;
        times.Add(midpoint);
        AddCubicTimes(times, curve, start, midpoint, first, middle, tolerance, depth + 1);
        AddCubicTimes(times, curve, midpoint, end, middle, last, tolerance, depth + 1);
    }

    private sealed class TransformCurveSet
    {
        internal readonly IntermediateNode Target;
        internal readonly FbxModelMapper.LocalTransformStack TransformStack;

        internal ScalarCurve? TranslationX;
        internal ScalarCurve? TranslationY;
        internal ScalarCurve? TranslationZ;
        internal ScalarCurve? RotationX;
        internal ScalarCurve? RotationY;
        internal ScalarCurve? RotationZ;
        internal ScalarCurve? ScaleX;
        internal ScalarCurve? ScaleY;
        internal ScalarCurve? ScaleZ;
        internal Float3 TranslationDefault;
        internal Float3 RotationDefault;
        internal Float3 ScaleDefault;
        internal float StartTime = float.PositiveInfinity;
        internal float EndTime = float.NegativeInfinity;

        internal bool HasTranslation => TranslationX != null || TranslationY != null || TranslationZ != null;
        internal bool HasRotation => RotationX != null || RotationY != null || RotationZ != null;
        internal bool HasScale => ScaleX != null || ScaleY != null || ScaleZ != null;

        internal TransformCurveSet(
            IntermediateNode target,
            FbxModelMapper.LocalTransformStack transformStack)
        {
            Target = target;
            TransformStack = transformStack;
            TranslationDefault = transformStack.DefaultTranslation;
            RotationDefault = transformStack.DefaultRotationDegrees;
            ScaleDefault = transformStack.DefaultScale;
        }

        internal Float3 DefaultFor(string property) => property switch
        {
            "Lcl Translation" => TransformStack.DefaultTranslation,
            "Lcl Rotation" => TransformStack.DefaultRotationDegrees,
            _ => TransformStack.DefaultScale,
        };

        internal void Set(
            string property,
            ScalarCurve? x,
            ScalarCurve? y,
            ScalarCurve? z,
            Float3 defaultValue)
        {
            IncludeTimeRange(x);
            IncludeTimeRange(y);
            IncludeTimeRange(z);
            if (property == "Lcl Translation")
            {
                TranslationX = x;
                TranslationY = y;
                TranslationZ = z;
                TranslationDefault = defaultValue;
            }
            else if (property == "Lcl Rotation")
            {
                RotationX = x;
                RotationY = y;
                RotationZ = z;
                RotationDefault = defaultValue;
            }
            else
            {
                ScaleX = x;
                ScaleY = y;
                ScaleZ = z;
                ScaleDefault = defaultValue;
            }
        }

        private void IncludeTimeRange(ScalarCurve? curve)
        {
            if (curve is null || curve.Times.Length == 0) return;
            StartTime = MathF.Min(StartTime, curve.Times[0]);
            EndTime = MathF.Max(EndTime, curve.Times[^1]);
        }
    }

    internal sealed class ScalarCurve
    {
        public readonly float[] Times;
        public readonly float[] Values;
        private readonly int[] _flags;
        private readonly float[] _rightSlope, _leftSlope, _rightWeight, _leftWeight;

        public ScalarCurve(float[] times, float[] values, int[]? flags = null,
            float[]? data = null, int[]? refCounts = null)
        {
            Times = times;
            Values = values;
            int count = times.Length;
            _flags = new int[count];
            _rightSlope = new float[count];
            _leftSlope = new float[count];
            _rightWeight = new float[count];
            _leftWeight = new float[count];
            Array.Fill(_rightWeight, 1f / 3f);
            Array.Fill(_leftWeight, 1f / 3f);
            if (flags is not { Length: > 0 }) return;

            // FBX serializes four float slots per attribute record, run-length encoded by
            // KeyAttrRefCount. Slot 1 is the NEXT key's incoming slope, not this key's slope.
            // Slot 2 contains two packed decimal fixed-point weights (low/high uint16 / 10000).
            // See Autodesk FbxAnimCurveDef and ufbx's animation-curve reader.
            int key = 0;
            var records = new int[count];
            for (int record = 0; record < flags.Length && key < count; record++)
            {
                int repeat = refCounts is null ? 1 : record < refCounts.Length ? refCounts[record] : 0;
                for (int r = 0; r < repeat && key < count; r++, key++)
                {
                    _flags[key] = flags[record];
                    records[key] = record * 4;
                    if (data is null || record * 4 + 3 >= data.Length) continue;
                    _rightSlope[key] = data[record * 4];
                    if (key + 1 < count) _leftSlope[key + 1] = data[record * 4 + 1];
                    uint packed = BitConverter.SingleToUInt32Bits(data[record * 4 + 2]);
                    if ((flags[record] & 0x01000000) != 0)
                        _rightWeight[key] = Math.Clamp((packed & 0xffff) * 0.0001f, 0f, 1f);
                    if (key + 1 < count && (flags[record] & 0x02000000) != 0)
                        _leftWeight[key + 1] = Math.Clamp((packed >> 16) * 0.0001f, 0f, 1f);
                }
            }
            for (int i = 0; i < count; i++)
            {
                if (!IsCubic(i))
                {
                    if (i + 1 < count)
                        _leftSlope[i + 1] = IsConstant(i) ? 0f : Secant(i, i + 1);
                    continue;
                }
                int offset = records[i];
                if ((_flags[i] & 0x200) != 0 && data is not null && offset + 2 < data.Length)
                {
                    float tension = data[offset], continuity = data[offset + 1], bias = data[offset + 2];
                    float previous = i > 0 ? Secant(i - 1, i) : 0f;
                    float next = i + 1 < count ? Secant(i, i + 1) : 0f;
                    float factor = (i == 0 || i + 1 == count ? 1f : 0.5f) * (1f - tension);
                    _leftSlope[i] = factor * ((1f + bias) * (1f - continuity) * previous + (1f - bias) * (1f + continuity) * next);
                    _rightSlope[i] = factor * ((1f + bias) * (1f + continuity) * previous + (1f - bias) * (1f - continuity) * next);
                }
                else if ((_flags[i] & 0x400) == 0)
                {
                    float incomingBias = -_leftSlope[i], outgoingBias = _rightSlope[i];
                    _leftSlope[i] = AutoSlope(i, incomingBias);
                    _rightSlope[i] = AutoSlope(i, outgoingBias);
                }
            }
        }

        internal bool IsConstant(int index) => (_flags[index] & 0x2) != 0;
        internal bool IsConstantNext(int index) => (_flags[index] & 0x100) != 0;
        internal bool IsCubic(int index) => !IsConstant(index) && (_flags[index] & 0x8) != 0;

        internal bool StaysAt(float value, float tolerance)
        {
            for (int i = 0; i < Values.Length; i++)
            {
                if (!float.IsFinite(Values[i]) || MathF.Abs(Values[i] - value) > tolerance) return false;
                if (i + 1 >= Values.Length || !IsCubic(i)) continue;
                float duration = Times[i + 1] - Times[i];
                float outgoing = Values[i] + duration * _rightWeight[i] * _rightSlope[i];
                float incoming = Values[i + 1] - duration * _leftWeight[i + 1] * _leftSlope[i + 1];
                // The Bezier convex hull proves constancy between keys, including overshoot
                // with equal endpoint values; checking only frame zero is insufficient.
                if (!float.IsFinite(outgoing) || !float.IsFinite(incoming) ||
                    MathF.Abs(outgoing - value) > tolerance || MathF.Abs(incoming - value) > tolerance)
                    return false;
            }
            return true;
        }

        private float Secant(int first, int last) => Times[last] > Times[first]
            ? (Values[last] - Values[first]) / (Times[last] - Times[first]) : 0f;

        private float AutoSlope(int index, float bias)
        {
            bool hasPrevious = index > 0 && Times[index] > Times[index - 1];
            bool hasNext = index + 1 < Times.Length && Times[index + 1] > Times[index];
            int flags = _flags[index];
            if (!hasPrevious && !hasNext) return 0f;
            if ((flags & 0x1000) != 0 &&
                ((hasPrevious && MathF.Abs(Values[index] - Values[index - 1]) <= 0.00001f) ||
                 (hasNext && MathF.Abs(Values[index + 1] - Values[index]) <= 0.00001f))) return 0f;
            if ((!hasPrevious || !hasNext) && (flags & 0x4000) != 0) return 0f;
            float previous = hasPrevious ? Secant(index - 1, index) : Secant(index, index + 1);
            float next = hasNext ? Secant(index, index + 1) : previous;
            float slope = hasPrevious && hasNext ? Secant(index - 1, index + 1) : previous;
            if ((flags & 0x2000) == 0)
            {
                if (hasPrevious && hasNext)
                {
                    float fraction = (Times[index] - Times[index - 1]) / (Times[index + 1] - Times[index - 1]);
                    slope = (slope + previous * (1f - fraction) + next * fraction) * 0.5f;
                }
                float weight = MathF.Abs(bias) / 100f;
                float target = bias > 0f ? next : previous;
                float difference = target - slope;
                slope += difference * weight;
                if (weight > 5f)
                    slope += (weight - 5f) * (weight - 5f) * 40f * MathF.Sign(MathF.Abs(difference) > 0.00001f ? difference : bias);
            }
            if ((flags & 0x4000) != 0)
            {
                float sign = MathF.Sign(slope);
                float limitLeft = _leftWeight[index] > 0f ? sign * previous / _leftWeight[index] : 0f;
                float limitRight = _rightWeight[index] > 0f ? sign * next / _rightWeight[index] : 0f;
                slope = sign * MathF.Min(MathF.Abs(slope), MathF.Max(0f, MathF.Min(limitLeft, limitRight)));
            }
            return slope;
        }

        /// <summary>Evaluate the authored interpolation, clamped outside the keyed time range.</summary>
        public float Sample(float t)
        {
            if (Times.Length == 0) return 0f;
            if (t <= Times[0]) return Values[0];
            if (t >= Times[^1]) return Values[^1];

            int lo = 0, hi = Times.Length - 1;
            while (lo + 1 < hi)
            {
                int mid = (lo + hi) >> 1;
                if (Times[mid] <= t) lo = mid;
                else hi = mid;
            }
            float t0 = Times[lo], t1 = Times[lo + 1];
            if (t == t0) return Values[lo];
            if (IsConstant(lo)) return IsConstantNext(lo) ? Values[lo + 1] : Values[lo];
            float u = (t1 - t0) > 1e-12f ? (t - t0) / (t1 - t0) : 0f;
            if (IsCubic(lo))
            {
                if (Values[lo] == Values[lo + 1] && _rightSlope[lo] == 0f && _leftSlope[lo + 1] == 0f)
                    return Values[lo];
                float right = _rightWeight[lo], left = _leftWeight[lo + 1];
                double parameter = u;
                if ((_flags[lo] & 0x03000000) != 0)
                {
                    // Weighted handles change time as well as value. Invert Bezier X before
                    // evaluating Y; using normalized time directly produces the wrong easing.
                    double lower = 0d, upper = 1d;
                    for (int iteration = 0; iteration < 32; iteration++)
                    {
                        parameter = (lower + upper) * 0.5d;
                        if (Bezier(0d, right, 1d - left, 1d, parameter) < u) lower = parameter;
                        else upper = parameter;
                    }
                }
                return (float)Bezier(Values[lo], Values[lo] + _rightSlope[lo] * right * (t1 - t0),
                    Values[lo + 1] - _leftSlope[lo + 1] * left * (t1 - t0), Values[lo + 1], parameter);
            }
            return Values[lo] + (Values[lo + 1] - Values[lo]) * u;
        }

        private static double Bezier(double a, double b, double c, double d, double t)
        {
            double inverse = 1d - t;
            return inverse * inverse * inverse * a + 3d * inverse * inverse * t * b +
                3d * inverse * t * t * c + t * t * t * d;
        }
    }
}
