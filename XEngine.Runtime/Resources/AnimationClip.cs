// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Animation;
using XEngine.Animation.Humanoid;
using MC = XEngine.Animation.Humanoid.MuscleCurve;
using XEngine.Echo;
using XEngine.Vector;
namespace XEngine.Runtime;

public enum AnimationWrapMode
{
    Once,
    Loop,
    PingPong,
    ClampForever,
}

/// <summary>How a path-curve clip chooses its trajectory. Missing serialized values retain legacy heuristics.</summary>
public enum ClipRootMotionPolicy
{
    LegacyAutomatic = 0,
    ExplicitBone = 1,
    Disabled = 2,
}

/// <summary>
/// An animation clip containing per-bone animation curves.
/// The Animation component evaluates these curves and applies them to bone Transforms by name.
/// No Skeleton reference needed bones are resolved by name at runtime.
/// </summary>
public sealed class AnimationClip : EngineObject, ISerializable
{
    private float _duration;
    private float _ticksPerSecond = 1f;
    private float _durationInTicks;
    private AnimationWrapMode _wrap;
    private string? _rootBonePath;
    private ClipRootMotionPolicy _rootMotionPolicy;

    public float Duration { get { EnsureNotDisposed(); return _duration; } set { EnsureNotDisposed(); _duration = value; } }
    public float TicksPerSecond { get { EnsureNotDisposed(); return _ticksPerSecond; } set { EnsureNotDisposed(); _ticksPerSecond = value; } }
    public float DurationInTicks { get { EnsureNotDisposed(); return _durationInTicks; } set { EnsureNotDisposed(); _durationInTicks = value; } }
    public AnimationWrapMode Wrap { get { EnsureNotDisposed(); return _wrap; } set { EnsureNotDisposed(); _wrap = value; } }
    /// <summary>
    /// Model-relative trajectory path. LegacyAutomatic falls back when this is absent/unresolved;
    /// ExplicitBone never falls back. Disabled ignores this path. Changes take effect at Bind/SetClip.
    /// Explicit "." selects the model-root trajectory (empty clip path), never an absolute actor TRS write.
    /// </summary>
    public string? RootBonePath { get { EnsureNotDisposed(); return _rootBonePath; } set { EnsureNotDisposed(); _rootBonePath = value; } }

    /// <summary>Path-curve trajectory policy, captured at Bind/SetClip. Unknown values disable path extraction.
    /// Humanoid muscle/TDoF playback has its own Avatar-based root handling.</summary>
    public ClipRootMotionPolicy RootMotionPolicy { get { EnsureNotDisposed(); return _rootMotionPolicy; } set { EnsureNotDisposed(); _rootMotionPolicy = value; } }

    private List<AnimBone> _bones = [];
    public List<AnimBone> Bones { get { EnsureNotDisposed(); return _bones; } }

    /// <summary>★ M38 — Non-TRS float bindings (path + component type + property). Disk Bones stay unchanged.</summary>
    private List<GenericCurve> _genericCurves = [];
    public List<GenericCurve> GenericCurves { get { EnsureNotDisposed(); return _genericCurves; } }

    public GenericCurve? FindGenericCurve(string path, string typeName, string property)
    {
        EnsureNotDisposed();
        var list = GenericCurves;
        for (int i = 0; i < list.Count; i++)
        {
            GenericCurve g = list[i];
            if (g.Path == path && g.TypeName == typeName && g.Property == property)
                return g;
        }
        return null;
    }

    /// <summary>Blend-shape weight tracks. Each targets a renderer (by path) and a named blend shape.</summary>
    private List<BlendShapeAnim> _blendShapes = [];
    public List<BlendShapeAnim> BlendShapes { get { EnsureNotDisposed(); return _blendShapes; } }

    /// <summary>★ M40 — Authored animation events (function name + params), fired by the clip playable.</summary>
    private List<AnimationClipEvent> _events = [];
    public List<AnimationClipEvent> Events { get { EnsureNotDisposed(); return _events; } }

    /// <summary>
    /// M54 — true after <see cref="Humanoid.HumanoidClipBaker"/> converted the mapped humanoid bone
    /// curves into muscle curves; the clip then plays on any avatar exposing the same roles.
    /// </summary>
    public bool IsHumanoid { get { EnsureNotDisposed(); return _isHumanoid; } set { EnsureNotDisposed(); _isHumanoid = value; } }
    private bool _isHumanoid;

    /// <summary>
    /// M55 - the SOURCE avatar's leg length captured at bake time; retargeting scales the Hips TDoF
    /// meters by target/source. Zero means unknown (no rescale).
    /// </summary>
    public float HumanoidSourceLegLength { get { EnsureNotDisposed(); return _humanoidSourceLegLength; } set { EnsureNotDisposed(); _humanoidSourceLegLength = value; } }
    private float _humanoidSourceLegLength;

    /// <summary>M54 — baked muscle channels (role + channel + curve). Empty for generic clips.</summary>
    private List<MuscleCurve> _muscleCurves = [];
    public List<MuscleCurve> MuscleCurves { get { EnsureNotDisposed(); return _muscleCurves; } }

    /// <summary>Linear scan for the muscle channel (role, channel), or null.</summary>
    public MuscleCurve? FindMuscleCurve(HumanoidBone bone, int channel)
    {
        EnsureNotDisposed();
        var list = _muscleCurves;
        for (int i = 0; i < list.Count; i++)
        {
            MuscleCurve m = list[i];
            if (m.Bone == bone && m.Channel == channel)
                return m;
        }
        return null;
    }

    private Dictionary<string, AnimBone> _boneMap = [];

    public void AddBone(AnimBone bone)
    {
        EnsureNotDisposed();
        Bones.Add(bone);
        _boneMap[bone.BoneName] = bone;
    }

    public void AddBlendShape(BlendShapeAnim blendShape) { EnsureNotDisposed(); BlendShapes.Add(blendShape); }

    /// <summary>The blend-shape track for (renderer path, shape name), or null.</summary>
    public BlendShapeAnim? FindBlendShapeTrack(string path, string shapeName)
    {
        EnsureNotDisposed();
        var list = _blendShapes;
        for (int i = 0; i < list.Count; i++)
        {
            var t = list[i];
            if (t != null && t.Path == path && t.ShapeName == shapeName)
                return t;
        }
        return null;
    }

    public AnimBone? GetBone(string name)
    {
        EnsureNotDisposed();
        // Deserialized clips (binary cache) load Bones without the private lookup — rebuild it on
        // first miss so path lookups work without an explicit RebuildBoneMap call.
        if (_boneMap.Count == 0 && _bones.Count > 0)
            RebuildBoneMap();
        if (_boneMap.TryGetValue(name, out AnimBone? bone))
            return bone;
        return null;
    }

    /// <summary>★ Gen-P4.2 — Rebuild the name lookup after bone names are remapped in place.</summary>
    public void RebuildBoneMap()
    {
        EnsureNotDisposed();
        _boneMap.Clear();
        List<AnimBone> bones = Bones;
        for (int i = 0; i < bones.Count; i++)
            _boneMap[bones[i].BoneName] = bones[i];
    }

    public void EnsureQuaternionContinuity()
    {
        EnsureNotDisposed();
        foreach (AnimBone bone in Bones)
        {
            if (bone.RotX == null || bone.RotX.Keys.Count < 2) continue;

            Quaternion prev = new(
                bone.RotX.Keys[0].Value,
                bone.RotY.Keys[0].Value,
                bone.RotZ.Keys[0].Value,
                bone.RotW.Keys[0].Value
            );

            for (int i = 1; i < bone.RotX.Keys.Count; i++)
            {
                Quaternion cur = new(
                    bone.RotX.Keys[i].Value,
                    bone.RotY.Keys[i].Value,
                    bone.RotZ.Keys[i].Value,
                    bone.RotW.Keys[i].Value
                );

                Quaternion midQ = (prev + cur) * 0.5f;
                Quaternion midQFlipped = (prev + (-cur)) * 0.5f;

                float angle = Quaternion.Angle(prev, midQ);
                float angleFlipped = Quaternion.Angle(prev, midQFlipped);
                Quaternion continuous = angleFlipped < angle ? (-cur) : cur;

                bone.RotX.Keys[i].Value = continuous.X;
                bone.RotY.Keys[i].Value = continuous.Y;
                bone.RotZ.Keys[i].Value = continuous.Z;
                bone.RotW.Keys[i].Value = continuous.W;

                prev = continuous;
            }
        }
    }

    public void Deserialize(EchoObject value, SerializationContext ctx)
    {
        Name = value.Get("Name")?.StringValue ?? "Animation";
        Duration = value.Get("Duration")?.FloatValue ?? 0;
        TicksPerSecond = value.Get("TicksPerSecond")?.FloatValue ?? 1;
        DurationInTicks = value.Get("DurationInTicks")?.FloatValue ?? 0;
        Wrap = (AnimationWrapMode)(value.Get("Wrap")?.IntValue ?? 0);
        RootBonePath = value.Get("RootBonePath")?.StringValue;
        RootMotionPolicy = (ClipRootMotionPolicy)(value.Get("RootMotionPolicy")?.IntValue ?? 0);

        EchoObject? boneList = value.Get("Bones");
        if (boneList != null)
        {
            // Scoped to this import: no global intern table can keep unloaded clips alive.
            var timelines = new Dictionary<(int, ulong), List<AnimationCurve>>();
            foreach (EchoObject boneProp in boneList.List)
            {
                var bone = new AnimBone();
                bone.BoneName = boneProp.Get("BoneName")?.StringValue ?? "";

                bone.PosX = Serializer.Deserialize<AnimationCurve>(boneProp.Get("PosX"), ctx);
                bone.PosY = Serializer.Deserialize<AnimationCurve>(boneProp.Get("PosY"), ctx);
                bone.PosZ = Serializer.Deserialize<AnimationCurve>(boneProp.Get("PosZ"), ctx);

                bone.RotX = Serializer.Deserialize<AnimationCurve>(boneProp.Get("RotX"), ctx);
                bone.RotY = Serializer.Deserialize<AnimationCurve>(boneProp.Get("RotY"), ctx);
                bone.RotZ = Serializer.Deserialize<AnimationCurve>(boneProp.Get("RotZ"), ctx);
                bone.RotW = Serializer.Deserialize<AnimationCurve>(boneProp.Get("RotW"), ctx);

                bone.ScaleX = Serializer.Deserialize<AnimationCurve>(boneProp.Get("ScaleX"), ctx);
                bone.ScaleY = Serializer.Deserialize<AnimationCurve>(boneProp.Get("ScaleY"), ctx);
                bone.ScaleZ = Serializer.Deserialize<AnimationCurve>(boneProp.Get("ScaleZ"), ctx);

                bone.PosX?.ShareTimeline(timelines);
                bone.PosY?.ShareTimeline(timelines);
                bone.PosZ?.ShareTimeline(timelines);
                bone.RotX?.ShareTimeline(timelines);
                bone.RotY?.ShareTimeline(timelines);
                bone.RotZ?.ShareTimeline(timelines);
                bone.RotW?.ShareTimeline(timelines);
                bone.ScaleX?.ShareTimeline(timelines);
                bone.ScaleY?.ShareTimeline(timelines);
                bone.ScaleZ?.ShareTimeline(timelines);

                // Optional source-rig reference pose. Older/user-authored clips do not carry it
                // and continue to sample their local TRS directly without retargeting.
                EchoObject? sourceRotW = boneProp.Get("SourceRotW");
                if (sourceRotW != null)
                {
                    bone.HasSourceReferencePose = true;
                    bone.SourcePosition = new Float3(
                        boneProp.Get("SourcePosX")?.FloatValue ?? 0f,
                        boneProp.Get("SourcePosY")?.FloatValue ?? 0f,
                        boneProp.Get("SourcePosZ")?.FloatValue ?? 0f);
                    bone.SourceRotation = new Quaternion(
                        boneProp.Get("SourceRotX")?.FloatValue ?? 0f,
                        boneProp.Get("SourceRotY")?.FloatValue ?? 0f,
                        boneProp.Get("SourceRotZ")?.FloatValue ?? 0f,
                        sourceRotW.FloatValue);
                    bone.SourceScale = new Float3(
                        boneProp.Get("SourceScaleX")?.FloatValue ?? 1f,
                        boneProp.Get("SourceScaleY")?.FloatValue ?? 1f,
                        boneProp.Get("SourceScaleZ")?.FloatValue ?? 1f);
                }

                Bones.Add(bone);
            }

            _boneMap = new Dictionary<string, AnimBone>();
            for (int i = 0; i < Bones.Count; i++)
                _boneMap[Bones[i].BoneName] = Bones[i];
        }

        // M54 - humanoid muscle channels.
        if (value.Get("IsHumanoid")?.BoolValue == true)
        {
            IsHumanoid = true;
            HumanoidSourceLegLength = value.Get("HumanoidSourceLegLength")?.FloatValue ?? 0f;
            var muscleTimelines = new Dictionary<(int, ulong), List<AnimationCurve>>();
            EchoObject? muscleList = value.Get("MuscleCurves");
            if (muscleList != null)
            {
                foreach (EchoObject entry in muscleList.List)
                {
                    var muscle = new MC
                    {
                        Bone = (HumanoidBone)entry.Get("Bone")!.IntValue,
                        Channel = entry.Get("Channel")!.IntValue,
                        Curve = Serializer.Deserialize<AnimationCurve>(entry.Get("Curve"), ctx) ?? new(),
                    };
                    muscle.Curve?.ShareTimeline(muscleTimelines);
                    MuscleCurves.Add(muscle);
                }
            }
        }

        EchoObject? bsList = value.Get("BlendShapes");
        if (bsList != null)
        {
            foreach (EchoObject bsProp in bsList.List)
            {
                BlendShapes.Add(new BlendShapeAnim
                {
                    Path = bsProp.Get("Path")?.StringValue ?? "",
                    ShapeName = bsProp.Get("ShapeName")?.StringValue ?? "",
                    Weight = Serializer.Deserialize<AnimationCurve>(bsProp.Get("Weight"), ctx),
                });
            }
        }

        EchoObject? genList = value.Get("GenericCurves");
        if (genList != null)
        {
            foreach (EchoObject n in genList.List)
            {
                GenericCurves.Add(new GenericCurve
                {
                    Path = n.Get("Path")?.StringValue ?? "",
                    TypeName = n.Get("TypeName")?.StringValue ?? "",
                    Property = n.Get("Property")?.StringValue ?? "",
                    Curve = Serializer.Deserialize<AnimationCurve>(n.Get("Curve"), ctx) ?? new AnimationCurve(),
                });
            }
        }

        EchoObject? evList = value.Get("Events");
        if (evList != null)
        {
            foreach (EchoObject n in evList.List)
            {
                var ev = new AnimationClipEvent
                {
                    Time = n.Get("Time")?.FloatValue ?? 0f,
                    FunctionName = n.Get("FunctionName")?.StringValue ?? "",
                    FloatParam = n.Get("FloatParam")?.FloatValue ?? 0f,
                    IntParam = n.Get("IntParam")?.IntValue ?? 0,
                    StringParam = n.Get("StringParam")?.StringValue ?? "",
                };
                string? guid = n.Get("ObjectParam")?.StringValue;
                if (!string.IsNullOrEmpty(guid) && Guid.TryParse(guid, out Guid id))
                    ev.ObjectParam = new AssetRef<EngineObject>(id);
                Events.Add(ev);
            }
        }
    }

    public void Serialize(ref EchoObject value, SerializationContext ctx)
    {
        value.Add("Name", new EchoObject(Name));
        value.Add("Duration", new EchoObject(Duration));
        value.Add("TicksPerSecond", new EchoObject(TicksPerSecond));
        value.Add("DurationInTicks", new EchoObject(DurationInTicks));
        value.Add("Wrap", new EchoObject((int)Wrap));
        if (RootBonePath != null)
            value.Add("RootBonePath", new EchoObject(RootBonePath));
        if (RootMotionPolicy != ClipRootMotionPolicy.LegacyAutomatic)
            value.Add("RootMotionPolicy", new EchoObject((int)RootMotionPolicy));

        var boneList = EchoObject.NewList();
        foreach (AnimBone bone in Bones)
        {
            var boneProp = EchoObject.NewCompound();
            boneProp.Add("BoneName", new EchoObject(bone.BoneName));

            boneProp.Add("PosX", Serializer.Serialize(bone.PosX, ctx));
            boneProp.Add("PosY", Serializer.Serialize(bone.PosY, ctx));
            boneProp.Add("PosZ", Serializer.Serialize(bone.PosZ, ctx));

            boneProp.Add("RotX", Serializer.Serialize(bone.RotX, ctx));
            boneProp.Add("RotY", Serializer.Serialize(bone.RotY, ctx));
            boneProp.Add("RotZ", Serializer.Serialize(bone.RotZ, ctx));
            boneProp.Add("RotW", Serializer.Serialize(bone.RotW, ctx));

            boneProp.Add("ScaleX", Serializer.Serialize(bone.ScaleX, ctx));
            boneProp.Add("ScaleY", Serializer.Serialize(bone.ScaleY, ctx));
            boneProp.Add("ScaleZ", Serializer.Serialize(bone.ScaleZ, ctx));

            if (bone.HasSourceReferencePose)
            {
                boneProp.Add("SourcePosX", new EchoObject(bone.SourcePosition.X));
                boneProp.Add("SourcePosY", new EchoObject(bone.SourcePosition.Y));
                boneProp.Add("SourcePosZ", new EchoObject(bone.SourcePosition.Z));
                boneProp.Add("SourceRotX", new EchoObject(bone.SourceRotation.X));
                boneProp.Add("SourceRotY", new EchoObject(bone.SourceRotation.Y));
                boneProp.Add("SourceRotZ", new EchoObject(bone.SourceRotation.Z));
                boneProp.Add("SourceRotW", new EchoObject(bone.SourceRotation.W));
                boneProp.Add("SourceScaleX", new EchoObject(bone.SourceScale.X));
                boneProp.Add("SourceScaleY", new EchoObject(bone.SourceScale.Y));
                boneProp.Add("SourceScaleZ", new EchoObject(bone.SourceScale.Z));
            }

            boneList.ListAdd(boneProp);
        }
        value.Add("Bones", boneList);

        // M54 — humanoid muscle channels.
        if (IsHumanoid)
        {
            value.Add("IsHumanoid", new EchoObject(true));
            value.Add("HumanoidSourceLegLength", new EchoObject(HumanoidSourceLegLength));
            var muscleList = EchoObject.NewList();
            foreach (MC muscle in MuscleCurves)
            {
                var node = EchoObject.NewCompound();
                node.Add("Bone", new EchoObject((int)muscle.Bone));
                node.Add("Channel", new EchoObject(muscle.Channel));
                node.Add("Curve", Serializer.Serialize(muscle.Curve, ctx));
                muscleList.ListAdd(node);
            }
            value.Add("MuscleCurves", muscleList);
        }

        var bsList = EchoObject.NewList();
        foreach (BlendShapeAnim bs in BlendShapes)
        {
            var bsProp = EchoObject.NewCompound();
            bsProp.Add("Path", new EchoObject(bs.Path));
            bsProp.Add("ShapeName", new EchoObject(bs.ShapeName));
            bsProp.Add("Weight", Serializer.Serialize(bs.Weight, ctx));
            bsList.ListAdd(bsProp);
        }
        value.Add("BlendShapes", bsList);

        if (GenericCurves.Count > 0)
        {
            var genList = EchoObject.NewList();
            foreach (GenericCurve g in GenericCurves)
            {
                var node = EchoObject.NewCompound();
                node.Add("Path", new EchoObject(g.Path ?? ""));
                node.Add("TypeName", new EchoObject(g.TypeName ?? ""));
                node.Add("Property", new EchoObject(g.Property ?? ""));
                node.Add("Curve", Serializer.Serialize(g.Curve, ctx));
                genList.ListAdd(node);
            }
            value.Add("GenericCurves", genList);
        }

        if (Events.Count > 0)
        {
            var evList = EchoObject.NewList();
            foreach (AnimationClipEvent e in Events)
            {
                var node = EchoObject.NewCompound();
                node.Add("Time", new EchoObject(e.Time));
                node.Add("FunctionName", new EchoObject(e.FunctionName ?? ""));
                node.Add("FloatParam", new EchoObject(e.FloatParam));
                node.Add("IntParam", new EchoObject(e.IntParam));
                node.Add("StringParam", new EchoObject(e.StringParam ?? ""));
                if (e.ObjectParam.AssetID != Guid.Empty)
                    node.Add("ObjectParam", new EchoObject(e.ObjectParam.AssetID.ToString("N")));
                evList.ListAdd(node);
            }
            value.Add("Events", evList);
        }
    }


    /// <summary>
    /// A blend-shape weight track. <see cref="Path"/> is the renderer GameObject's path relative to the
    /// animation root; <see cref="ShapeName"/> selects the blend shape on that renderer's mesh.
    /// </summary>
    public class BlendShapeAnim
    {
        public string Path = string.Empty;
        public string ShapeName = string.Empty;
        public AnimationCurve Weight;

        public float EvaluateAt(float time) => Weight?.Evaluate(time) ?? 0f;
    }

    /// <summary>★ M38 — One float curve bound to a GameObject path + component type + property.</summary>
    public class GenericCurve
    {
        public string Path = string.Empty;
        public string TypeName = string.Empty;
        public string Property = string.Empty;
        public AnimationCurve Curve = new();
    }

    /// <summary>Per-bone animation data with separate curves for each transform component.</summary>
    public class AnimBone
    {
        public string BoneName;

        /// <summary>
        /// True when the importer captured this bone's static local transform from the source rig.
        /// It is deliberately separate from animation time zero: frame zero is authored motion,
        /// not a bind pose. Runtime retargeting uses this reference to preserve that motion while
        /// changing between source- and destination-bone coordinate frames.
        /// </summary>
        public bool HasSourceReferencePose;
        public Float3 SourcePosition;
        public Quaternion SourceRotation = Quaternion.Identity;
        public Float3 SourceScale = Float3.One;

        public AnimationCurve PosX, PosY, PosZ;
        public AnimationCurve RotX, RotY, RotZ, RotW;
        public AnimationCurve ScaleX, ScaleY, ScaleZ;

        public Float3 EvaluatePositionAt(float time)
        {
            if (PosX == null || PosY == null || PosZ == null) return Float3.Zero;
            return new(PosX.Evaluate(time), PosY.Evaluate(time), PosZ.Evaluate(time));
        }

        public Quaternion EvaluateRotationAt(float time)
        {
            if (RotX == null || RotX.KeyCount == 0)
                return Quaternion.Identity;

            // Evaluate each component at the given time
            float x = RotX.Evaluate(time);
            float y = RotY.Evaluate(time);
            float z = RotZ.Evaluate(time);
            float w = RotW.Evaluate(time);

            // Normalize (curve interpolation may denormalize the quaternion)
            return NormalizeQuaternion(new Quaternion(x, y, z, w));
        }

        public Float3 EvaluateScaleAt(float time)
        {
            if (ScaleX == null || ScaleY == null || ScaleZ == null) return Float3.One;
            return new(ScaleX.Evaluate(time), ScaleY.Evaluate(time), ScaleZ.Evaluate(time));
        }

        private static Quaternion NormalizeQuaternion(Quaternion q)
        {
            float length = Maths.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
            if (length < 1e-6f) return Quaternion.Identity;
            float invLength = 1.0f / length;
            return new Quaternion(
                q.X * invLength,
                q.Y * invLength,
                q.Z * invLength,
                q.W * invLength
            );
        }
    }
}
