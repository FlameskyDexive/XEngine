// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections;
using System.Collections.Generic;

using XEngine.Echo;
using XEngine.Vector;

namespace XEngine.Runtime;

// MIT License - Copyright (C) The Mono.Xna Team
// Modified from: https://github.com/MonoGame/MonoGame/blob/develop/MonoGame.Framework/Curve.cs

/// <summary>
/// Defines how the <see cref="AnimationCurve"/> value is determined for position before first point or after the end point on the <see cref="AnimationCurve"/>.
/// </summary>
public enum CurveLoopType
{
    Constant,
    Cycle,
    CycleOffset,
    Oscillate,
    Linear
}

/// <summary> Defines the different tangent types to be calculated for <see cref="KeyFrame"/> points in a <see cref="AnimationCurve"/>. </summary>
public enum CurveTangent
{
    Flat,
    Linear,
    Smooth
}

/// <summary>★ M41 — Per-side key tangent mode (Unity 5 / 2022 AnimationUtility.TangentMode + Broken).</summary>
public enum AnimationTangentMode
{
    Auto,
    ClampedAuto,
    Free,
    Linear,
    Constant,
    Broken
}

/// <summary>★ M41 — Which Hermite handles use <see cref="KeyFrame.InWeight"/> / <see cref="KeyFrame.OutWeight"/> (Unity 2018+).</summary>
[Flags]
public enum AnimationWeightedMode
{
    None = 0,
    In = 1,
    Out = 2,
    Both = In | Out
}

public class AnimationCurve : ISerializable
{

    #region Public Properties
    /// <summary>
    /// Defines how to handle weighting values that are less than the first control point in the curve.
    /// </summary>
    public CurveLoopType PreLoop { get; set; }

    /// <summary>
    /// Defines how to handle weighting values that are greater than the last control point in the curve.
    /// </summary>
    public CurveLoopType PostLoop { get; set; }

    /// <summary>
    /// The collection of curve keys.
    /// </summary>
    private CurveKeyCollection _keys = new();
    private float[]? _runtimeKeys;
    // Immutable and shared only with other packed curves in the same deserialized clip.
    // When present, _runtimeKeys holds value/in-tangent/out-tangent triplets.
    private float[]? _runtimeTimes;
    private float _runtimeInWeight, _runtimeOutWeight;
    private AnimationTangentMode _runtimeInMode, _runtimeOutMode;
    internal int KeyCount => _runtimeTimes?.Length ?? _runtimeKeys?.Length / 4 ?? _keys.Count;
    internal bool HasPackedRuntimeKeys => _runtimeKeys != null;
    public CurveKeyCollection Keys
    {
        get
        {
            if (_runtimeKeys is { } packed)
            {
                int count = KeyCount;
                _keys.EnsureCapacity(count);
                for (int i = 0; i < count; i++)
                {
                    Sample sample = ReadSample(i);
                    _keys.Add(new KeyFrame(sample.Position, sample.Value, sample.TangentIn, sample.TangentOut)
                    {
                        InWeight = _runtimeInWeight, OutWeight = _runtimeOutWeight,
                        InTangentMode = _runtimeInMode, OutTangentMode = _runtimeOutMode,
                    });
                }
                _runtimeKeys = null;
                _runtimeTimes = null;
            }
            return _keys;
        }
        private set { _runtimeKeys = null; _runtimeTimes = null; _keys = value; }
    }

    private readonly record struct Sample(float Position, float Value, float TangentIn, float TangentOut,
        float InWeight, float OutWeight, CurveContinuity Continuity,
        AnimationTangentMode InTangentMode, AnimationTangentMode OutTangentMode, AnimationWeightedMode WeightedMode);

    private float ReadPosition(int index) => _runtimeTimes is { } times ? times[index]
        : _runtimeKeys is { } packed ? packed[index * 4] : _keys[index].Position;

    private Sample ReadSample(int index)
    {
        if (_runtimeKeys is { } packed)
        {
            if (_runtimeTimes is { } times)
            {
                int offset = index * 3;
                return new Sample(times[index], packed[offset], packed[offset + 1], packed[offset + 2],
                    _runtimeInWeight, _runtimeOutWeight, CurveContinuity.Smooth,
                    _runtimeInMode, _runtimeOutMode, AnimationWeightedMode.None);
            }
            int i = index * 4;
            return new Sample(packed[i], packed[i + 1], packed[i + 2], packed[i + 3],
                _runtimeInWeight, _runtimeOutWeight, CurveContinuity.Smooth,
                _runtimeInMode, _runtimeOutMode, AnimationWeightedMode.None);
        }
        KeyFrame key = _keys[index];
        return new Sample(key.Position, key.Value, key.TangentIn, key.TangentOut,
            key.InWeight, key.OutWeight, key.Continuity, key.InTangentMode, key.OutTangentMode, key.WeightedMode);
    }

    #endregion

    #region Public Constructors

    /// <summary>
    /// Constructs a curve.
    /// </summary>
    public AnimationCurve()
    {
        Keys =
        [
            new KeyFrame(0, 0),
            new KeyFrame(1, 1)
        ];
        // Add Default keys
        SmoothTangents(CurveTangent.Smooth);
    }

    /// <summary>
    /// Constructs a curve.
    /// </summary>
    public AnimationCurve(IEnumerable<KeyFrame> keyFrames)
    {
        Keys = new CurveKeyCollection();
        foreach (var key in keyFrames)
        {
            Keys.Add(key);
        }
        // Add Default keys
        SmoothTangents(CurveTangent.Smooth);
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Evaluate the value at a position of this <see cref="AnimationCurve"/>.
    /// </summary>
    /// <param name="position">The position on this <see cref="AnimationCurve"/>.</param>
    /// <returns>Value at the position on this <see cref="AnimationCurve"/>.</returns>
    public float Evaluate(float position)
    {
        if (KeyCount == 0)
        {
            return 0.0f;
        }

        if (KeyCount == 1)
        {
            return ReadSample(0).Value;
        }

        Sample first = ReadSample(0);
        Sample last = ReadSample(KeyCount - 1);

        if (position < first.Position)
        {
            switch (PreLoop)
            {
                case CurveLoopType.Constant:
                    //constant
                    return first.Value;

                case CurveLoopType.Linear:
                    // linear y = a*x +b with a tangeant of last point
                    return first.Value - first.TangentIn * (first.Position - position);

                case CurveLoopType.Cycle:
                    //start -> end / start -> end
                    int cycle = GetNumberOfCycle(position);
                    float virtualPos = position - (cycle * (last.Position - first.Position));
                    return GetCurvePosition(virtualPos);

                case CurveLoopType.CycleOffset:
                    //make the curve continue (with no step) so must up the curve each cycle of delta(value)
                    cycle = GetNumberOfCycle(position);
                    virtualPos = position - (cycle * (last.Position - first.Position));
                    return (GetCurvePosition(virtualPos) + cycle * (last.Value - first.Value));

                case CurveLoopType.Oscillate:
                    //go back on curve from end and target start
                    // start-> end / end -> start
                    cycle = GetNumberOfCycle(position);
                    if (0 == cycle % 2.0)//if pair
                        virtualPos = position - (cycle * (last.Position - first.Position));
                    else
                        virtualPos = last.Position - position + first.Position + (cycle * (last.Position - first.Position));
                    return GetCurvePosition(virtualPos);
            }
        }
        else if (position > last.Position)
        {
            int cycle;
            switch (PostLoop)
            {
                case CurveLoopType.Constant:
                    //constant
                    return last.Value;

                case CurveLoopType.Linear:
                    // linear y = a*x +b with the tangent of the last point
                    return last.Value + last.TangentOut * (position - last.Position);

                case CurveLoopType.Cycle:
                    //start -> end / start -> end
                    cycle = GetNumberOfCycle(position);
                    float virtualPos = position - (cycle * (last.Position - first.Position));
                    return GetCurvePosition(virtualPos);

                case CurveLoopType.CycleOffset:
                    //make the curve continue (with no step) so must up the curve each cycle of delta(value)
                    cycle = GetNumberOfCycle(position);
                    virtualPos = position - (cycle * (last.Position - first.Position));
                    return (GetCurvePosition(virtualPos) + cycle * (last.Value - first.Value));

                case CurveLoopType.Oscillate:
                    //go back on curve from end and target start
                    // start-> end / end -> start
                    cycle = GetNumberOfCycle(position);
                    //virtualPos = position - (cycle * (last.Position - first.Position));
                    if (0 == cycle % 2.0)//if pair
                        virtualPos = position - (cycle * (last.Position - first.Position));
                    else
                        virtualPos = last.Position - position + first.Position + (cycle * (last.Position - first.Position));
                    return GetCurvePosition(virtualPos);
            }
        }

        //in curve
        return GetCurvePosition(position);
    }

    /// <summary>
    /// Computes tangents for all keys in the collection.
    /// </summary>
    /// <param name="tangentType">The tangent type for both in and out.</param>
    public void SmoothTangents(CurveTangent tangentType)
    {
        SmoothTangents(tangentType, tangentType);
    }

    /// <summary>
    /// Computes tangents for all keys in the collection.
    /// </summary>
    /// <param name="tangentInType">The tangent in-type. <see cref="KeyFrame.TangentIn"/> for more details.</param>
    /// <param name="tangentOutType">The tangent out-type. <see cref="KeyFrame.TangentOut"/> for more details.</param>
    public void SmoothTangents(CurveTangent tangentInType, CurveTangent tangentOutType)
    {
        for (int i = 0; i < Keys.Count; ++i)
        {
            SmoothTangent(i, tangentInType, tangentOutType);
        }
    }

    /// <summary>
    /// Computes tangent for the specific key in the collection.
    /// </summary>
    /// <param name="keyIndex">The index of a key in the collection.</param>
    /// <param name="tangentType">The tangent type for both in and out.</param>
    public void SmoothTangent(int keyIndex, CurveTangent tangentType) => SmoothTangent(keyIndex, tangentType, tangentType);

    /// <summary>
    /// Computes tangent for the specific key in the collection.
    /// </summary>
    /// <param name="keyIndex">The index of key in the collection.</param>
    /// <param name="tangentInType">The tangent in-type. <see cref="KeyFrame.TangentIn"/> for more details.</param>
    /// <param name="tangentOutType">The tangent out-type. <see cref="KeyFrame.TangentOut"/> for more details.</param>
    public void SmoothTangent(int keyIndex, CurveTangent tangentInType, CurveTangent tangentOutType)
    {
        // See http://msdn.microsoft.com/en-us/library/microsoft.xna.framework.curvetangent.aspx

        KeyFrame key = Keys[keyIndex];

        float p0, p, p1;
        p0 = p = p1 = key.Position;

        float v0, v, v1;
        v0 = v = v1 = key.Value;

        if (keyIndex > 0)
        {
            p0 = Keys[keyIndex - 1].Position;
            v0 = Keys[keyIndex - 1].Value;
        }

        if (keyIndex < Keys.Count - 1)
        {
            p1 = Keys[keyIndex + 1].Position;
            v1 = Keys[keyIndex + 1].Value;
        }

        switch (tangentInType)
        {
            case CurveTangent.Flat:
                key.TangentIn = 0;
                break;
            case CurveTangent.Linear:
                key.TangentIn = v - v0;
                break;
            case CurveTangent.Smooth:
                float pn = p1 - p0;
                if (Maths.Abs(pn) < float.Epsilon)
                    key.TangentIn = 0;
                else
                    key.TangentIn = (v1 - v0) * ((p - p0) / pn);
                break;
        }

        switch (tangentOutType)
        {
            case CurveTangent.Flat:
                key.TangentOut = 0;
                break;
            case CurveTangent.Linear:
                key.TangentOut = v1 - v;
                break;
            case CurveTangent.Smooth:
                float pn = p1 - p0;
                if (Maths.Abs(pn) < float.Epsilon)
                    key.TangentOut = 0;
                else
                    key.TangentOut = (v1 - v0) * ((p1 - p) / pn);
                break;
        }
    }

    /// <summary>★ M41 — Recompute Auto / ClampedAuto / Linear / Constant tangents. Free keys are left as stored.</summary>
    public void RecalculateNonFreeTangents()
    {
        for (int i = 0; i < Keys.Count; i++)
            ApplyTangentModes(i);
    }

    /// <summary>★ M41 — Apply the stored tangent modes of one key to its TangentIn / TangentOut.</summary>
    public void ApplyTangentModes(int keyIndex)
    {
        if ((uint)keyIndex >= (uint)Keys.Count)
            return;
        ApplySideTangent(keyIndex, incoming: true);
        ApplySideTangent(keyIndex, incoming: false);
    }

    #endregion

    #region Private Methods

    private int GetNumberOfCycle(float position)
    {
        float cycle = (position - ReadPosition(0)) / (ReadPosition(KeyCount - 1) - ReadPosition(0));
        return (int)Math.Floor(cycle);
    }

    private float GetCurvePosition(float position)
    {
        //only for position in curve
        // Lower-bound lookup preserves the first interval ending at duplicate timestamps.
        int low = 1, high = KeyCount - 1;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (ReadPosition(middle) >= position) high = middle;
            else low = middle + 1;
        }
        Sample next = ReadSample(low);
        Sample prev = ReadSample(low - 1);
        if (next.Position >= position)
        {
            if ((prev.Continuity == CurveContinuity.Step || prev.OutTangentMode == AnimationTangentMode.Constant)
                && position < next.Position)
            {
                // Step / Constant holds the previous key's value until the next key.
                return prev.Value;
            }
            float dt = next.Position - prev.Position;
            if (dt <= 1e-8f)
                return prev.Value;
            bool weighted = (prev.WeightedMode & AnimationWeightedMode.Out) != 0
                || (next.WeightedMode & AnimationWeightedMode.In) != 0;
            if (weighted)
                return EvaluateWeighted(prev, next, position, dt);
            float t = (position - prev.Position) / dt;// to have t in [0,1]
            float ts = t * t;
            float tss = ts * t;
            //http://en.wikipedia.org/wiki/Cubic_Hermite_spline
            return (2 * tss - 3 * ts + 1.0f) * prev.Value + (tss - 2 * ts + t) * prev.TangentOut + (3 * ts - 2 * tss) * next.Value + (tss - ts) * next.TangentIn;
        }
        return 0.0f;
    }

    void ApplySideTangent(int keyIndex, bool incoming)
    {
        KeyFrame key = Keys[keyIndex];
        AnimationTangentMode mode = incoming ? key.InTangentMode : key.OutTangentMode;
        if (mode == AnimationTangentMode.Free || mode == AnimationTangentMode.Broken)
            return;

        float p0 = key.Position, v0 = key.Value;
        float p1 = key.Position, v1 = key.Value;
        if (keyIndex > 0)
        {
            p0 = Keys[keyIndex - 1].Position;
            v0 = Keys[keyIndex - 1].Value;
        }
        if (keyIndex < Keys.Count - 1)
        {
            p1 = Keys[keyIndex + 1].Position;
            v1 = Keys[keyIndex + 1].Value;
        }
        float p = key.Position, v = key.Value;
        float pn = p1 - p0;
        float autoIn = Maths.Abs(pn) < float.Epsilon ? 0f : (v1 - v0) * ((p - p0) / pn);
        float autoOut = Maths.Abs(pn) < float.Epsilon ? 0f : (v1 - v0) * ((p1 - p) / pn);
        bool extremum = (v - v0) * (v1 - v) <= 0f;

        switch (mode)
        {
            case AnimationTangentMode.Linear:
                if (incoming) key.TangentIn = v - v0;
                else key.TangentOut = v1 - v;
                break;
            case AnimationTangentMode.Constant:
                if (incoming) key.TangentIn = 0f;
                else
                {
                    key.TangentOut = 0f;
                    key.Continuity = CurveContinuity.Step;
                }
                break;
            case AnimationTangentMode.Auto:
                if (incoming) key.TangentIn = autoIn;
                else
                {
                    key.TangentOut = autoOut;
                    if (key.Continuity == CurveContinuity.Step)
                        key.Continuity = CurveContinuity.Smooth;
                }
                break;
            case AnimationTangentMode.ClampedAuto:
                if (extremum) { autoIn = 0f; autoOut = 0f; }
                if (incoming) key.TangentIn = autoIn;
                else
                {
                    key.TangentOut = autoOut;
                    if (key.Continuity == CurveContinuity.Step)
                        key.Continuity = CurveContinuity.Smooth;
                }
                break;
        }
    }

    static float EvaluateWeighted(Sample prev, Sample next, float position, float dt)
    {
        float outW = (prev.WeightedMode & AnimationWeightedMode.Out) != 0 ? prev.OutWeight : (1f / 3f);
        float inW = (next.WeightedMode & AnimationWeightedMode.In) != 0 ? next.InWeight : (1f / 3f);
        if (outW < 0.0001f) outW = 0.0001f;
        if (inW < 0.0001f) inW = 0.0001f;
        if (outW > 1f) outW = 1f;
        if (inW > 1f) inW = 1f;

        float t0 = prev.Position, t3 = next.Position;
        float t1 = t0 + outW * dt;
        float t2 = t3 - inW * dt;
        float v0 = prev.Value, v3 = next.Value;
        float v1 = v0 + outW * prev.TangentOut;
        float v2 = v3 - inW * next.TangentIn;

        float u = (position - t0) / dt;
        for (int i = 0; i < 8; i++)
        {
            float tu = Bezier(t0, t1, t2, t3, u) - position;
            float d = BezierDerivative(t0, t1, t2, t3, u);
            if (Maths.Abs(d) < 1e-8f)
                break;
            u -= tu / d;
            if (u < 0f) u = 0f;
            else if (u > 1f) u = 1f;
        }
        return Bezier(v0, v1, v2, v3, u);
    }

    static float Bezier(float a, float b, float c, float d, float t)
    {
        float o = 1f - t;
        float oo = o * o;
        float tt = t * t;
        return oo * o * a + 3f * oo * t * b + 3f * o * tt * c + tt * t * d;
    }

    static float BezierDerivative(float a, float b, float c, float d, float t)
    {
        float o = 1f - t;
        return 3f * o * o * (b - a) + 6f * o * t * (c - b) + 3f * t * t * (d - c);
    }

    public void Serialize(ref EchoObject value, SerializationContext ctx)
    {
        value.Add("PreLoop", new EchoObject((int)PreLoop));
        value.Add("PostLoop", new EchoObject((int)PostLoop));

        // Flat binary fast path: animation clips carry ~100K keys each (107 bones × 7 curves ×
        // 242 resampled keys), and the per-key Echo compound list deserialized into millions of
        // EchoObject allocations with string-keyed lookups — seconds per character at Play start.
        // The blob is 6 floats + 4 ints per key, lossless. The legacy "Keys" list is deliberately
        // NOT written alongside: the Echo parse of that list (millions of compound objects) is
        // the very cost this fast path removes. Readers older than this change will fail to find
        // "Keys" and simply re-import the asset (project caches regenerate automatically).
        // Saving/cloning a scene is a read operation: do not materialize the mutable Keys API.
        // Write straight into the existing wire format, avoiding two temporary arrays per curve.
        int count = KeyCount;
        int floatBytes = checked(count * 6 * sizeof(float));
        var bytes = new byte[checked(count * 40)];
        Span<float> blob = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes.AsSpan(0, floatBytes));
        Span<int> extra = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(bytes.AsSpan(floatBytes));
        int k = 0, e = 0;
        for (int i = 0; i < count; i++)
        {
            Sample key = ReadSample(i);
            blob[k++] = key.Position;
            blob[k++] = key.Value;
            blob[k++] = key.TangentIn;
            blob[k++] = key.TangentOut;
            blob[k++] = key.InWeight;
            blob[k++] = key.OutWeight;
            extra[e++] = (int)key.Continuity;
            extra[e++] = (int)key.InTangentMode;
            extra[e++] = (int)key.OutTangentMode;
            extra[e++] = (int)key.WeightedMode;
        }
        value.Add("KeysBinary", new EchoObject(bytes));
    }

    public void Deserialize(EchoObject value, SerializationContext ctx)
    {
        PreLoop = (CurveLoopType)value.Get("PreLoop").IntValue;
        PostLoop = (CurveLoopType)value.Get("PostLoop").IntValue;

        _runtimeKeys = null;
        _runtimeTimes = null;
        Keys.Clear();

        // Fast path: flat binary keys (see Serialize) — lossless, including tangent modes and
        // weighted modes.
        if (value.Get("KeysBinary") is EchoObject binary && binary.Value is byte[] bytes)
        {
            if (bytes.Length % 40 != 0)
                throw new System.IO.InvalidDataException("Animation curve key payload must contain complete 40-byte keys.");
            int keyCount = bytes.Length / (6 * sizeof(float) + 4 * sizeof(int));
            int floatBytes = keyCount * 6 * sizeof(float);
            ReadOnlySpan<float> floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes.AsSpan(0, floatBytes));
            ReadOnlySpan<int> ints = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(bytes.AsSpan(floatBytes));
            // Player curves do not need redundant authored keys on constant tracks. Keep both
            // endpoints (including their tangents) so extrapolation and loop periods survive.
            bool constant = Application.IsPlayerBuild && IsConstantBinaryTrack(floats, ints, keyCount);
            // Packing is lossless and the Keys getter preserves mutable editor access. Unlike
            // player-only constant reduction, this retains every authored key in the editor.
            if (!constant && TryPackRuntimeKeys(floats, ints, keyCount))
                return;
            Keys.EnsureCapacity(constant ? 2 : keyCount);
            for (int i = 0; i < keyCount; i++)
            {
                if (constant && i > 0 && i < keyCount - 1) continue;
                int f = i * 6, n = i * 4;
                Keys.Add(new KeyFrame(floats[f], floats[f + 1], floats[f + 2], floats[f + 3], (CurveContinuity)ints[n])
                {
                    InWeight = floats[f + 4],
                    OutWeight = floats[f + 5],
                    InTangentMode = (AnimationTangentMode)ints[n + 1],
                    OutTangentMode = (AnimationTangentMode)ints[n + 2],
                    WeightedMode = (AnimationWeightedMode)ints[n + 3],
                });
            }
            return;
        }

        List<EchoObject> keyList = value.Get("Keys").List;
        foreach (EchoObject key in keyList)
        {
            float position = key.Get("Position").FloatValue;
            var curveKey = new KeyFrame(position, key.Get("Value").FloatValue, key.Get("TangentIn").FloatValue, key.Get("TangentOut").FloatValue, (CurveContinuity)key.Get("Continuity").IntValue);
            if (key.Get("InTangentMode") is EchoObject inMode)
                curveKey.InTangentMode = (AnimationTangentMode)inMode.IntValue;
            if (key.Get("OutTangentMode") is EchoObject outMode)
                curveKey.OutTangentMode = (AnimationTangentMode)outMode.IntValue;
            if (key.Get("WeightedMode") is EchoObject wm)
                curveKey.WeightedMode = (AnimationWeightedMode)wm.IntValue;
            if (key.Get("InWeight") is EchoObject iw)
                curveKey.InWeight = iw.FloatValue;
            if (key.Get("OutWeight") is EchoObject ow)
                curveKey.OutWeight = ow.FloatValue;
            Keys.Add(curveKey);
        }
    }

    // Retain exact float samples without one managed object and reference per key. Uniform
    // metadata is shared by the track; editing Keys materializes the original mutable API.
    private bool TryPackRuntimeKeys(ReadOnlySpan<float> floats, ReadOnlySpan<int> modes, int count)
    {
        if (count < 3) return false;
        for (int i = 0; i < count; i++)
        {
            int f = i * 6, n = i * 4;
            if (modes[n] != (int)CurveContinuity.Smooth || modes[n + 3] != 0 ||
                modes[n + 1] != modes[1] || modes[n + 2] != modes[2] ||
                floats[f + 4] != floats[4] || floats[f + 5] != floats[5]) return false;
        }
        var packed = new float[count * 4];
        for (int i = 0; i < count; i++) floats.Slice(i * 6, 4).CopyTo(packed.AsSpan(i * 4));
        _runtimeInWeight = floats[4]; _runtimeOutWeight = floats[5];
        _runtimeInMode = (AnimationTangentMode)modes[1];
        _runtimeOutMode = (AnimationTangentMode)modes[2];
        _runtimeKeys = packed;
        return true;
    }

    internal void ShareTimeline(Dictionary<(int Count, ulong Hash), List<AnimationCurve>> timelines)
    {
        if (_runtimeKeys is not { } packed || _runtimeTimes != null) return;
        int count = packed.Length / 4;
        ulong hash = 14695981039346656037UL;
        for (int i = 0; i < packed.Length; i += 4)
            hash = unchecked((hash ^ (uint)BitConverter.SingleToInt32Bits(packed[i])) * 1099511628211UL);
        var key = (count, hash);
        if (!timelines.TryGetValue(key, out var candidates))
        {
            timelines.Add(key, new List<AnimationCurve> { this });
            return;
        }
        foreach (var candidate in candidates)
        {
            if (ReferenceEquals(candidate, this)) return;
            bool equal = true;
            for (int i = 0; i < count; i++)
            {
                float other = candidate._runtimeTimes is { } times ? times[i] : candidate._runtimeKeys![i * 4];
                if (BitConverter.SingleToInt32Bits(packed[i * 4]) != BitConverter.SingleToInt32Bits(other))
                { equal = false; break; }
            }
            if (!equal) continue;
            float[] shared = candidate._runtimeTimes!;
            if (shared == null)
            {
                shared = new float[count];
                for (int i = 0; i < count; i++) shared[i] = packed[i * 4];
                candidate.UseSharedTimeline(shared);
            }
            UseSharedTimeline(shared);
            return;
        }
        // Hash collisions must never merge different authored times.
        candidates.Add(this);
    }

    private void UseSharedTimeline(float[] times)
    {
        var samples = new float[times.Length * 3];
        for (int i = 0; i < times.Length; i++)
            _runtimeKeys!.AsSpan(i * 4 + 1, 3).CopyTo(samples.AsSpan(i * 3, 3));
        _runtimeKeys = samples;
        _runtimeTimes = times;
    }

    internal static bool IsConstantBinaryTrack(ReadOnlySpan<float> floats, ReadOnlySpan<int> modes, int count)
    {
        if (count <= 2) return false;
        float value = floats[1];
        if (!float.IsFinite(value)) return false;
        for (int i = 0; i < count; i++)
        {
            int f = i * 6, n = i * 4;
            if (!float.IsFinite(floats[f]) || floats[f + 1] != value ||
                floats[f + 2] != 0f || floats[f + 3] != 0f ||
                modes[n] != (int)CurveContinuity.Smooth ||
                modes[n + 3] != (int)AnimationWeightedMode.None ||
                (i > 0 && floats[f] <= floats[f - 6])) return false;
        }
        return true;
    }

    #endregion
}

/// <summary>
/// Defines the continuity of keys on a <see cref="AnimationCurve"/>.
/// </summary>
public enum CurveContinuity
{
    /// <summary> Interpolation can be used between this key and the next. </summary>
    Smooth,
    /// <summary> Interpolation cannot be used. A position between the two points returns this point. </summary>
    Step
}

public class KeyFrame : IEquatable<KeyFrame>, IComparable<KeyFrame>
{
    #region Properties

    /// <summary> Gets or sets the indicator whether the segment between this point and the next point on the curve is discrete or continuous. </summary>
    public CurveContinuity Continuity { get; set; }

    /// <summary> Gets a position of the key on the curve. </summary>
    public float Position { get; }

    /// <summary> Gets or sets a tangent when approaching this point from the previous point on the curve. </summary>
    public float TangentIn { get; set; }

    /// <summary> Gets or sets a tangent when leaving this point to the next point on the curve. </summary>
    public float TangentOut { get; set; }

    /// <summary> Gets a value of this point. </summary>
    public float Value { get; set; }

    /// <summary>★ M41 — Incoming tangent mode. Defaults to Free so stored tangents on old clips are preserved.</summary>
    public AnimationTangentMode InTangentMode { get; set; } = AnimationTangentMode.Free;

    /// <summary>★ M41 — Outgoing tangent mode.</summary>
    public AnimationTangentMode OutTangentMode { get; set; } = AnimationTangentMode.Free;

    /// <summary>★ M41 — Which handles use weights. None keeps classic Hermite.</summary>
    public AnimationWeightedMode WeightedMode { get; set; }

    /// <summary>★ M41 — Incoming Bezier weight in (0,1]. Unity default is 1/3.</summary>
    public float InWeight { get; set; } = DefaultWeight;

    /// <summary>★ M41 — Outgoing Bezier weight in (0,1]. Unity default is 1/3.</summary>
    public float OutWeight { get; set; } = DefaultWeight;

    public const float DefaultWeight = 1f / 3f;

    #endregion

    #region Constructors

    /// <summary> Creates a new instance of <see cref="KeyFrame"/> class with position: 0 and value: 0. </summary>
    public KeyFrame() : this(0, 0)
    {
        // This parameterless constructor is needed for correct serialization of CurveKeyCollection and CurveKey.
    }

    /// <summary> Creates a new instance of <see cref="KeyFrame"/> class. </summary>
    public KeyFrame(float position, float value, float tangentIn = 0, float tangentOut = 0, CurveContinuity continuity = CurveContinuity.Smooth)
    {
        Position = position;
        Value = value;
        TangentIn = tangentIn;
        TangentOut = tangentOut;
        Continuity = continuity;
    }

    /// <summary>★ M41 — Copy this key to a new time, preserving tangents, modes, and weights.</summary>
    public KeyFrame WithPosition(float position)
        => new KeyFrame(position, Value, TangentIn, TangentOut, Continuity)
        {
            InTangentMode = InTangentMode,
            OutTangentMode = OutTangentMode,
            WeightedMode = WeightedMode,
            InWeight = InWeight,
            OutWeight = OutWeight,
        };

    #endregion

    public static bool operator !=(KeyFrame value1, KeyFrame value2)
    {
        return !(value1 == value2);
    }

    public static bool operator ==(KeyFrame value1, KeyFrame value2)
    {
        if (Equals(value1, null))
            return Equals(value2, null);

        if (Equals(value2, null))
            return Equals(value1, null);

        return (value1.Position == value2.Position)
               && value1.Value == value2.Value
               && value1.TangentIn == value2.TangentIn
               && value1.TangentOut == value2.TangentOut
               && value1.Continuity == value2.Continuity
               && value1.InTangentMode == value2.InTangentMode
               && value1.OutTangentMode == value2.OutTangentMode
               && value1.WeightedMode == value2.WeightedMode
               && value1.InWeight == value2.InWeight
               && value1.OutWeight == value2.OutWeight;
    }

    #region Inherited Methods

    public int CompareTo(KeyFrame? other) => other is null ? 1 : Position.CompareTo(other.Position);
    public bool Equals(KeyFrame? other) => (this == other);
    public override bool Equals(object? obj) => (obj as KeyFrame) != null && Equals((KeyFrame)obj);
    public override int GetHashCode() =>
        Position.GetHashCode() ^ Value.GetHashCode() ^ TangentIn.GetHashCode() ^
        TangentOut.GetHashCode() ^ Continuity.GetHashCode() ^ InTangentMode.GetHashCode() ^
        OutTangentMode.GetHashCode() ^ WeightedMode.GetHashCode();

    #endregion
}

/// <summary>
/// The collection of the <see cref="KeyFrame"/> elements and a part of the <see cref="AnimationCurve"/> class.
/// </summary>
public class CurveKeyCollection : ICollection<KeyFrame>
{
    #region Private Fields

    private readonly List<KeyFrame> _keys;

    #endregion

    #region Properties

    public KeyFrame this[int index]
    {
        get { return _keys[index]; }
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            if (index >= _keys.Count)
                throw new IndexOutOfRangeException();

            if (_keys[index].Position == value.Position)
                _keys[index] = value;
            else
            {
                _keys.RemoveAt(index);
                Add(value); // sorted insert, not List.Add (which would append out of order)
            }
        }
    }

    /// <summary> Returns the count of keys in this collection. </summary>
    public int Count => _keys.Count;

    /// <summary> Returns false because it is not a read-only collection. </summary>
    public bool IsReadOnly => false;

    #endregion

    #region Constructors

    /// <summary> Creates a new instance of <see cref="CurveKeyCollection"/> class. </summary>
    public CurveKeyCollection() => _keys = [];

    internal void EnsureCapacity(int capacity) => _keys.EnsureCapacity(capacity);

    #endregion

    IEnumerator IEnumerable.GetEnumerator() => _keys.GetEnumerator();


    /// <summary> Adds a key to this collection. </summary>
    /// <exception cref="ArgumentNullException">Throws if <paramref name="item"/> is null.</exception>
    /// <remarks>The new key would be added respectively to a position of that key and the position of other keys.</remarks>
    public void Add(KeyFrame item)
    {
        ArgumentNullException.ThrowIfNull(item);

        // Imported/serialized keys arrive in time order. Re-scanning from the first
        // key on every append made loading n sampled keys O(n²) per bone/channel.
        // Equal timestamps still append after their peers, preserving stable ordering.
        if (_keys.Count == 0 || item.Position >= _keys[^1].Position)
        {
            _keys.Add(item);
            return;
        }

        for (int i = 0; i < _keys.Count; i++)
        {
            if (item.Position < _keys[i].Position)
            {
                _keys.Insert(i, item);
                return;
            }
        }

        _keys.Add(item);
    }

    /// <summary> Removes all keys from this collection. </summary>
    public void Clear() => _keys.Clear();

    /// <summary> Determines whether this collection contains a specific key. </summary>
    public bool Contains(KeyFrame item) => _keys.Contains(item);

    /// <summary> Copies the keys of this collection to an array, starting at the array index provided. </summary>
    public void CopyTo(KeyFrame[] array, int arrayIndex) => _keys.CopyTo(array, arrayIndex);

    /// <summary>
    /// Returns an enumerator that iterates through the collection.
    /// </summary>
    /// <returns>An enumerator for the <see cref="CurveKeyCollection"/>.</returns>
    public IEnumerator<KeyFrame> GetEnumerator() => _keys.GetEnumerator();

    /// <summary> Finds element in the collection and returns its index. </summary>
    /// <returns>Index of the element; or -1 if item is not found.</returns>
    public int IndexOf(KeyFrame item) => _keys.IndexOf(item);

    /// <summary> Removes element at the specified index.  </summary>
    public void RemoveAt(int index) => _keys.RemoveAt(index);

    /// <summary> Removes specific element. </summary>
    /// <returns><c>true</c> if item is successfully removed; <c>false</c> otherwise. This method also returns <c>false</c> if item was not found.</returns>
    public bool Remove(KeyFrame item) => _keys.Remove(item);
}
