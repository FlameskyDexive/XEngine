// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text.Json.Serialization;

namespace XEngine.Ai.SpineGen;

/// <summary>A bone-channel key as the LLM / built-in motions express them: absolute values in
/// bone degrees/units (spine rotate keys are absolute-relative-to-setup, matching this directly).</summary>
public sealed class MotionKey
{
    [JsonPropertyName("t")] public float Time { get; set; }
    [JsonPropertyName("value")] public float Value { get; set; }
    /// <summary>"linear" (default) | "stepped" | [c1,c2,c3,c4].</summary>
    [JsonPropertyName("curve")] public JsonElement? Curve { get; set; }
}

public sealed class MotionBoneTrack
{
    [JsonPropertyName("bone")] public required string Bone { get; set; }
    [JsonPropertyName("rotate")] public List<MotionKey>? Rotate { get; set; }
    [JsonPropertyName("translate")] public List<MotionKey>? Translate { get; set; }
    /// <summary>Translate keys pair x/y in one track; Value holds X, second holds Y when present.</summary>
    [JsonPropertyName("translateY")] public List<MotionKey>? TranslateY { get; set; }
    /// <summary>Uniform scale keys (X and Y scale the same); Value holds the scale factor.</summary>
    [JsonPropertyName("scale")] public List<MotionKey>? Scale { get; set; }
}

/// <summary>One animation request: exactly the shape we ask the LLM to emit.</summary>
public sealed class MotionPlan
{
    [JsonPropertyName("animation")] public required string Animation { get; set; }
    [JsonPropertyName("loop")] public bool Loop { get; set; } = true;
    [JsonPropertyName("duration")] public float Duration { get; set; } = 2f;
    [JsonPropertyName("bones")] public List<MotionBoneTrack> Bones { get; set; } = [];
    [JsonPropertyName("slots")] public List<MotionSlotTrack> Slots { get; set; } = [];

    /// <summary>True for local authored plans and the deterministic built-in library — skips the LLM amplitude limits
    /// (combat moves swing ±150° and lunge hundreds of px) while keeping NaN/monotonic/
    /// time-range/loop-closure checks.</summary>
    [JsonIgnore]
    public bool Trusted { get; set; }
}

public sealed class MotionSlotTrack
{
    [JsonPropertyName("slot")] public required string Slot { get; set; }
    [JsonPropertyName("attachment")] public List<SpineAttachmentKey> Attachment { get; set; } = [];
}

public sealed class MotionViolation
{
    [JsonPropertyName("code")] public required string Code { get; set; }
    [JsonPropertyName("message")] public required string Message { get; set; }
}

public static class MotionLimits
{
    public const float MaxRotateDegrees = 60f;
    public const float MaxTranslatePixels = 40f;
    public const float MaxScaleValue = 4f;
    public const float MaxDurationSeconds = 10f;
    public const float MinDurationSeconds = 0.2f;
}

/// <summary>Strict schema + semantic validation for LLM keyframe plans. Returns every violation so
/// the animate service can feed them back for a repair round (the video's Claude feedback loop).</summary>
public static class MotionValidator
{
    public static List<MotionViolation> Validate(MotionPlan plan, RigManifest rig, SpineSkeletonDoc? doc = null)
    {
        var violations = new List<MotionViolation>();
        HashSet<string> known = rig.Bones.Select(b => b.Name).ToHashSet(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(plan.Animation))
            violations.Add(new MotionViolation { Code = "missing-name", Message = "animation name required" });
        if (!float.IsFinite(plan.Duration) || plan.Duration is < MotionLimits.MinDurationSeconds or > MotionLimits.MaxDurationSeconds)
            violations.Add(new MotionViolation
            {
                Code = "duration-range",
                Message = $"duration {plan.Duration}s outside [{MotionLimits.MinDurationSeconds},{MotionLimits.MaxDurationSeconds}]",
            });
        if (plan.Bones.Count == 0 && plan.Slots.Count == 0)
            violations.Add(new MotionViolation { Code = "empty", Message = "plan has no bone tracks" });

        var tracks=new HashSet<string>(StringComparer.Ordinal);
        foreach (MotionBoneTrack track in plan.Bones)
        {
            foreach(var channel in new[]{("rotate",track.Rotate),("translate",track.Translate),("translateY",track.TranslateY),("scale",track.Scale)})
                if(channel.Item2!=null && !tracks.Add(track.Bone+":"+channel.Item1))
                    violations.Add(new(){Code="duplicate-track",Message=$"Duplicate channel: {track.Bone}/{channel.Item1}"});
            if (track.TranslateY != null && (track.Translate == null || track.Translate.Count != track.TranslateY.Count ||
                track.Translate.Where((key,index)=>key.Time!=track.TranslateY[index].Time).Any()))
                violations.Add(new MotionViolation { Code="translate-pair",Message="Translate X/Y keys must have identical times." });
            if (!known.Contains(track.Bone))
            {
                violations.Add(new MotionViolation
                {
                    Code = "unknown-bone",
                    Message = $"bone '{track.Bone}' not in rig (known: {string.Join(',', known)})",
                });
            }

            CheckKeys(track.Rotate, "rotate", plan, violations);
            CheckKeys(track.Translate, "translate", plan, violations);
            CheckKeys(track.TranslateY, "translateY", plan, violations);
            CheckKeys(track.Scale, "scale", plan, violations);
        }

        var slots=new HashSet<string>(StringComparer.Ordinal);
        foreach(var track in plan.Slots)
        {
            if(!slots.Add(track.Slot) || doc?.Slots?.Exists(s=>s.Name==track.Slot)!=true)
                violations.Add(new(){Code="slot",Message=$"Unknown or duplicate slot: {track.Slot}"});
            if(track.Attachment.Count==0)violations.Add(new(){Code="empty-track",Message=$"Empty attachment track: {track.Slot}"});
            float last=-1;
            foreach(var key in track.Attachment)
            {
                if(!float.IsFinite(key.Time) || key.Time<0 || key.Time>plan.Duration || key.Time<=last || key.Curve!=null)
                    violations.Add(new(){Code="attachment-key",Message="Attachment keys need distinct increasing times in duration and no curve."});
                last=key.Time;
                if(key.Name!=null && doc?.Skins?.Exists(s=>s.Attachments.TryGetValue(track.Slot,out var a)&&a.ContainsKey(key.Name))!=true)
                    violations.Add(new(){Code="attachment",Message=$"Unknown attachment {track.Slot}/{key.Name}"});
            }
            if(plan.Loop && track.Attachment.Count>0 && (track.Attachment[0].Time!=0 || track.Attachment[^1].Time!=plan.Duration || track.Attachment[0].Name!=track.Attachment[^1].Name))
                violations.Add(new(){Code="loop-mismatch",Message=$"Attachment loop is not closed: {track.Slot}"});
        }
        return violations;
    }

    private static void CheckKeys(List<MotionKey>? keys, string channel, MotionPlan plan,
        List<MotionViolation> violations)
    {
        if (keys == null)
            return;
        if (keys.Count == 0)
        {
            violations.Add(new MotionViolation { Code = "empty-track", Message = $"{channel}: empty key list" });
            return;
        }

        float last = float.NegativeInfinity;
        foreach (MotionKey key in keys)
        {
            if (!float.IsFinite(key.Time) || !float.IsFinite(key.Value))
            {
                violations.Add(new MotionViolation { Code = "nan", Message = $"{channel}: NaN key" });
                continue;
            }
            if (key.Time < last - 1e-4f)
            {
                violations.Add(new MotionViolation
                {
                    Code = "non-monotonic",
                    Message = $"{channel}: time {key.Time} after {last}",
                });
            }
            if (key.Curve is { ValueKind: not JsonValueKind.Null } curve)
            {
                bool valid=curve.ValueKind==JsonValueKind.String && curve.GetString() is "linear" or "stepped";
                if(curve.ValueKind==JsonValueKind.Array && curve.GetArrayLength()==4)
                {
                    var values=new float[4]; int i=0;valid=true;
                    foreach(var item in curve.EnumerateArray())
                    {
                        if(item.ValueKind!=JsonValueKind.Number || !item.TryGetSingle(out values[i]) || !float.IsFinite(values[i]))valid=false;
                        i++;
                    }
                    valid &= values[0]>=0 && values[2]<=1 && values[0]<=values[2];
                }
                if(!valid)violations.Add(new MotionViolation{Code="curve",Message="Expected linear, stepped, or four finite normalized Bezier controls with ordered X in [0,1]."});
            }
            last = key.Time;

            if (key.Time < -1e-4f || key.Time > plan.Duration + 1e-3f)
                violations.Add(new MotionViolation
                {
                    Code = "time-range",
                    Message = $"{channel}: t={key.Time} outside [0,{plan.Duration}]",
                });

            // Trusted (built-in library) plans skip amplitude limits: combat moves legitimately
            // swing far past the LLM guardrails. NaN/monotonicity/time-range still apply.
            if (plan.Trusted)
                continue;

            float limit = channel switch
            {
                "rotate" => MotionLimits.MaxRotateDegrees,
                "scale" => MotionLimits.MaxScaleValue,
                _ => MotionLimits.MaxTranslatePixels,
            };
            if (MathF.Abs(key.Value) > limit)
                violations.Add(new MotionViolation
                {
                    Code = "value-range",
                    Message = $"{channel}: |{key.Value}| exceeds ±{limit}",
                });
        }

        if (plan.Loop && keys.Count > 0)
        {
            MotionKey first = keys[0];
            MotionKey lastKey = keys[^1];
            if (MathF.Abs(first.Value - lastKey.Value) > 1e-3f ||
                MathF.Abs(first.Time) > 1e-3f ||
                MathF.Abs(lastKey.Time - plan.Duration) > 1e-2f)
            {
                violations.Add(new MotionViolation
                {
                    Code = "loop-mismatch",
                    Message = $"{channel}: loop animations must start at t=0 and end at t=duration with equal values",
                });
            }
        }
    }

    /// <summary>Converts a validated plan into the exporter model (per-channel timelines).</summary>
    public static SpineAnimation ToAnimation(MotionPlan plan)
    {
        var animation = new SpineAnimation();
        foreach (MotionBoneTrack track in plan.Bones)
        {
            SpineBoneTimelines timelines = animation.Bone(track.Bone);
            if (track.Rotate is { Count: > 0 })
                timelines.Rotate = [.. track.Rotate.Select((k, i) => new SpineRotateKey
                {
                    Time = k.Time,
                    Angle = k.Value,
                    Curve = ToCurve(track.Rotate, i),
                })];
            if (track.Translate is { Count: > 0 })
                timelines.Translate = [.. track.Translate.Select((k, i) => new SpineTranslateKey
                {
                    Time = k.Time,
                    X = k.Value,
                    Y = track.TranslateY != null && i < track.TranslateY.Count ? track.TranslateY[i].Value : null,
                    Curve = ToCurve(track.Translate, i, track.TranslateY, true),
                })];
            if (track.Scale is { Count: > 0 })
                timelines.Scale = [.. track.Scale.Select((k, i) => new SpineScaleKey
                {
                    Time = k.Time,
                    X = k.Value,
                    Y = k.Value,
                    Curve = ToCurve(track.Scale, i, track.Scale, true),
                })];
        }
        if(plan.Slots.Count>0)
        {
            animation.Slots=[];
            foreach(var track in plan.Slots)animation.Slots.Add(track.Slot,new(){Attachment=track.Attachment});
        }
        return animation;
    }

    private static SpineKeyCurve? ToCurve(List<MotionKey> keys, int index,
        List<MotionKey>? second = null, bool twoChannels = false)
    {
        MotionKey key=keys[index];
        if (index+1>=keys.Count || key.Curve is not { ValueKind: not JsonValueKind.Null } raw)
            return null;
        if (raw.ValueKind==JsonValueKind.String)
            return raw.GetString()=="stepped"?SpineKeyCurve.Stepped:null;
        // MotionPlan uses normalized easing. Spine 4.3 stores absolute time/value
        // controls, four per channel (eight for translate and scale).
        float[] normalized=raw.EnumerateArray().Select(v=>v.GetSingle()).ToArray();
        float[] controls=new float[twoChannels?8:4];
        void Channel(int offset,List<MotionKey>? track)
        {
            float start=track?[index].Value??0, end=track?[index+1].Value??0;
            float dt=keys[index+1].Time-key.Time;
            controls[offset]=key.Time+normalized[0]*dt;
            controls[offset+1]=start+normalized[1]*(end-start);
            controls[offset+2]=key.Time+normalized[2]*dt;
            controls[offset+3]=start+normalized[3]*(end-start);
        }
        Channel(0,keys);
        if(twoChannels)Channel(4,second);
        return new SpineKeyCurve{ControlPoints=controls};
    }

}

/// <summary>Deterministic procedural motions for the built-in library. Each generator only touches
/// bones present in the rig and emits loop-safe keys (first==last) for loops, so animations never
/// pop. Combat moves are Trusted: they swing past the LLM amplitude guardrails on purpose.</summary>
public static class MotionLibrary
{
    public static IReadOnlyList<string> Names =>
    [
        "idle_breath", "sway", "bob", "wave_arm",
        // locomotion
        "walk", "run", "jump",
        // sword combo (Optimus-style greatsword): 普攻三连击
        "atk_1", "atk_2", "atk_3",
        // 技能三连
        "skill_1", "skill_2", "skill_3",
        // damage / lifecycle
        "hit", "fall", "standup", "death", "respawn",
    ];

    public static MotionPlan Build(string name, RigManifest rig, float? durationOverride = null)
    {
        return name switch
        {
            "idle_breath" => Breath(rig, durationOverride ?? 2f),
            "sway" => Sway(rig, durationOverride ?? 3f),
            "bob" => Bob(rig, durationOverride ?? 1.2f),
            "wave_arm" => Wave(rig, durationOverride ?? 1.6f),
            "walk" => Walk(rig, durationOverride ?? 1f),
            "run" => Run(rig, durationOverride ?? 0.7f),
            "jump" => Jump(rig, durationOverride ?? 0.95f),
            "atk_1" => Slash(rig, "atk_1", durationOverride ?? 0.55f, windup: -30, strike: 20, spineTwist: -5, step: 22),
            "atk_2" => Slash(rig, "atk_2", durationOverride ?? 0.6f, windup: 25, strike: -38, spineTwist: 6, step: 14, dip: 12),
            "atk_3" => OverheadSmash(rig, durationOverride ?? 0.75f),
            "skill_1" => ChargedSlam(rig, durationOverride ?? 1.15f),
            "skill_2" => LungingThrust(rig, durationOverride ?? 0.85f),
            "skill_3" => SpinSlash(rig, durationOverride ?? 1.05f),
            "hit" => Hit(rig, durationOverride ?? 0.38f),
            "fall" => Fall(rig, durationOverride ?? 0.75f),
            "standup" => StandUp(rig, durationOverride ?? 0.95f),
            "death" => Death(rig, durationOverride ?? 1.3f),
            "respawn" => Respawn(rig, durationOverride ?? 1.1f),
            _ => throw new ArgumentException(
                $"Unknown motion '{name}'. Built-in: {string.Join(", ", Names)}"),
        };
    }

    private static MotionPlan Plan(string name, float duration, bool loop = true)
    {
        var bones = new List<MotionBoneTrack>();
        return new MotionPlan
        {
            Animation = name,
            Loop = loop,
            Duration = duration,
            Bones = bones,
            Trusted = true,
        };
    }

    private static MotionBoneTrack Track(string bone, params MotionKey[] keys) => new()
    {
        Bone = bone,
        Rotate = [.. keys],
    };

    private static MotionBoneTrack TranslateTrack(string bone, params (float t, float x, float y)[] keys) => new()
    {
        Bone = bone,
        Translate = [.. keys.Select(k => new MotionKey { Time = k.t, Value = k.x })],
        TranslateY = [.. keys.Select(k => new MotionKey { Time = k.t, Value = k.y })],
    };

    private static MotionBoneTrack ScaleTrack(string bone, params MotionKey[] keys) => new()
    {
        Bone = bone,
        Scale = [.. keys],
    };

    private static MotionKey K(float t, float v) => new() { Time = t, Value = v };

    /// <summary>Adds the track only when the rig actually has the bone.</summary>
    private static void AddIfPresent(MotionPlan plan, RigManifest rig, MotionBoneTrack track)
    {
        if (rig.Bones.Any(b => b.Name == track.Bone))
            plan.Bones.Add(track);
    }

    private static MotionPlan Breath(RigManifest rig, float duration)
    {
        var plan = Plan("idle_breath", duration);
        float half = duration / 2f;
        AddIfPresent(plan, rig, Track("spine", K(0, 0), K(half, 2.2f), K(duration, 0)));
        AddIfPresent(plan, rig, Track("head", K(0, 0), K(half, -1.6f), K(duration, 0)));
        return plan;
    }

    private static MotionPlan Sway(RigManifest rig, float duration)
    {
        var plan = Plan("sway", duration);
        float q = duration / 4f;
        AddIfPresent(plan, rig, Track("hips", K(0, 0), K(2 * q, 3f), K(3 * q, 0), K(duration, 0)));
        AddIfPresent(plan, rig, Track("spine", K(0, 0), K(q, -2.4f), K(2 * q, 0), K(3 * q, 2.4f), K(duration, 0)));
        return plan;
    }

    private static MotionPlan Bob(RigManifest rig, float duration)
    {
        var plan = Plan("bob", duration);
        float half = duration / 2f;
        AddIfPresent(plan, rig, TranslateTrack("root", (0, 0, 0), (half, 0, -3f), (duration, 0, 0)));
        return plan;
    }

    private static MotionPlan Wave(RigManifest rig, float duration)
    {
        var plan = Plan("wave_arm", duration);
        string? arm = new[] { "arm_r", "arm_l", "arm_000" }
            .FirstOrDefault(n => rig.Bones.Any(b => b.Name == n));
        if (arm != null)
        {
            float q = duration / 4f;
            AddIfPresent(plan, rig, Track(arm, K(0, 0), K(q, 28f), K(2 * q, 12f), K(3 * q, 28f), K(duration, 0)));
            if (arm is "arm_r" or "arm_l")
            {
                string side = arm[^1..];
                AddIfPresent(plan, rig, Track($"forearm_{side}", K(0, 0), K(q, -18f),
                    K(2 * q, 22f), K(3 * q, -18f), K(duration, 0)));
                AddIfPresent(plan, rig, Track($"hand_{side}", K(0, 0), K(q, -7f),
                    K(2 * q, 9f), K(3 * q, -7f), K(duration, 0)));
            }
        }
        return plan;
    }

    /// <summary>Walk cycle: counter-phased leg/arm swings with a double-beat body bob.</summary>
    private static MotionPlan Walk(RigManifest rig, float duration)
    {
        var plan = Plan("walk", duration);
        float half = duration / 2f;
        float quarter = duration / 4f;
        AddIfPresent(plan, rig, Track("leg_l", K(0, 13), K(half, -13), K(duration, 13)));
        AddIfPresent(plan, rig, Track("leg_r", K(0, -13), K(half, 13), K(duration, -13)));
        AddIfPresent(plan, rig, Track("arm_l", K(0, -9), K(half, 9), K(duration, -9)));
        AddIfPresent(plan, rig, Track("arm_r", K(0, 9), K(half, -9), K(duration, 9)));
        AddIfPresent(plan, rig, Track("forearm_l", K(0, 5), K(half, -5), K(duration, 5)));
        AddIfPresent(plan, rig, Track("forearm_r", K(0, -5), K(half, 5), K(duration, -5)));
        AddIfPresent(plan, rig, Track("shin_l", K(0, -7), K(quarter, 8), K(half, 16),
            K(3 * quarter, 3), K(duration, -7)));
        AddIfPresent(plan, rig, Track("shin_r", K(0, 16), K(quarter, 3), K(half, -7),
            K(3 * quarter, 8), K(duration, 16)));
        AddIfPresent(plan, rig, Track("spine", K(0, 0), K(quarter, 1.5f), K(3 * quarter, -1.5f), K(duration, 0)));
        AddIfPresent(plan, rig, TranslateTrack("root",
            (0, 0, -2), (quarter, 0, 0), (half, 0, -2), (3 * quarter, 0, 0), (duration, 0, -2)));
        return plan;
    }

    /// <summary>Run cycle: wider swings, forward torso lean, faster bob.</summary>
    private static MotionPlan Run(RigManifest rig, float duration)
    {
        var plan = Plan("run", duration);
        float half = duration / 2f;
        float quarter = duration / 4f;
        AddIfPresent(plan, rig, Track("leg_l", K(0, 20), K(half, -20), K(duration, 20)));
        AddIfPresent(plan, rig, Track("leg_r", K(0, -20), K(half, 20), K(duration, -20)));
        AddIfPresent(plan, rig, Track("arm_l", K(0, -13), K(half, 13), K(duration, -13)));
        AddIfPresent(plan, rig, Track("arm_r", K(0, 13), K(half, -13), K(duration, 13)));
        AddIfPresent(plan, rig, Track("forearm_l", K(0, 8), K(half, -8), K(duration, 8)));
        AddIfPresent(plan, rig, Track("forearm_r", K(0, -8), K(half, 8), K(duration, -8)));
        AddIfPresent(plan, rig, Track("shin_l", K(0, -10), K(quarter, 13), K(half, 24),
            K(3 * quarter, 5), K(duration, -10)));
        AddIfPresent(plan, rig, Track("shin_r", K(0, 24), K(quarter, 5), K(half, -10),
            K(3 * quarter, 13), K(duration, 24)));
        AddIfPresent(plan, rig, Track("spine", K(0, 4), K(duration, 4)));
        AddIfPresent(plan, rig, TranslateTrack("root",
            (0, 0, -4), (quarter, 0, 0), (half, 0, -4), (3 * quarter, 0, 0), (duration, 0, -4)));
        return plan;
    }

    /// <summary>Jump: crouch → launch → air tuck → land → settle. One-shot.</summary>
    private static MotionPlan Jump(RigManifest rig, float duration)
    {
        var plan = Plan("jump", duration, loop: false);
        AddIfPresent(plan, rig, TranslateTrack("root",
            (0, 0, 0), (.14f * duration, 0, -34), (.44f * duration, 0, 240), (.64f * duration, 0, 230),
            (.82f * duration, 0, -26), (duration, 0, 0)));
        AddIfPresent(plan, rig, Track("leg_l", K(0, 0), K(.14f * duration, 9), K(.44f * duration, 16),
            K(.64f * duration, 15), K(.82f * duration, 8), K(duration, 0)));
        AddIfPresent(plan, rig, Track("leg_r", K(0, 0), K(.14f * duration, -9), K(.44f * duration, -16),
            K(.64f * duration, -15), K(.82f * duration, -8), K(duration, 0)));
        AddIfPresent(plan, rig, Track("arm_l", K(0, 0), K(.14f * duration, 11), K(.44f * duration, -26),
            K(.64f * duration, -24), K(.82f * duration, 9), K(duration, 0)));
        AddIfPresent(plan, rig, Track("arm_r", K(0, 0), K(.14f * duration, -11), K(.44f * duration, 26),
            K(.64f * duration, 24), K(.82f * duration, -9), K(duration, 0)));
        AddIfPresent(plan, rig, Track("forearm_l", K(0, 0), K(.14f * duration, -8), K(.44f * duration, 24),
            K(.64f * duration, 22), K(.82f * duration, -5), K(duration, 0)));
        AddIfPresent(plan, rig, Track("forearm_r", K(0, 0), K(.14f * duration, 8), K(.44f * duration, -24),
            K(.64f * duration, -22), K(.82f * duration, 5), K(duration, 0)));
        AddIfPresent(plan, rig, Track("shin_l", K(0, 0), K(.14f * duration, -16), K(.44f * duration, 28),
            K(.64f * duration, 24), K(.82f * duration, -12), K(duration, 0)));
        AddIfPresent(plan, rig, Track("shin_r", K(0, 0), K(.14f * duration, 16), K(.44f * duration, -28),
            K(.64f * duration, -24), K(.82f * duration, 12), K(duration, 0)));
        AddIfPresent(plan, rig, Track("spine", K(0, 0), K(.14f * duration, 4), K(.44f * duration, 0),
            K(.82f * duration, 5), K(duration, 0)));
        return plan;
    }

    /// <summary>Sword slash primitive: windup → strike → recover, with torso twist and an
    /// optional forward step and body dip. Optimus greatsword strikes #1 and #2 use it.</summary>
    private static MotionPlan Slash(RigManifest rig, string name, float duration,
        float windup, float strike, float spineTwist, float step, float dip = 0f)
    {
        var plan = Plan(name, duration, loop: false);
        AddIfPresent(plan, rig, Track("arm_r", K(0, 0), K(.18f * duration, windup), K(.36f * duration, strike),
            K(.62f * duration, strike * 0.6f), K(duration, 0)));
        AddIfPresent(plan, rig, Track("forearm_r", K(0, 0), K(.18f * duration, windup * 0.72f),
            K(.36f * duration, strike * 0.85f), K(.62f * duration, strike * 0.45f), K(duration, 0)));
        AddIfPresent(plan, rig, Track("hand_r", K(0, 0), K(.18f * duration, windup * 0.28f),
            K(.36f * duration, strike * 0.42f), K(.62f * duration, strike * 0.2f), K(duration, 0)));
        AddIfPresent(plan, rig, Track("spine", K(0, 0), K(.18f * duration, -spineTwist * 0.6f),
            K(.36f * duration, spineTwist * -0.9f), K(duration, 0)));
        AddIfPresent(plan, rig, TranslateTrack("root",
            (0, 0, 0), (.36f * duration, step, -dip), (.62f * duration, step * 0.7f, -dip * 0.7f), (duration, 0, 0)));
        return plan;
    }

    /// <summary>Overhead smash: both arms wind up over the head, crash down with a body dip
    /// and knee bend. Optimus greatsword strike #3.</summary>
    private static MotionPlan OverheadSmash(RigManifest rig, float duration)
    {
        var plan = Plan("atk_3", duration, loop: false);
        AddIfPresent(plan, rig, Track("arm_r", K(0, 0), K(.24f * duration, -34), K(.4f * duration, 26),
            K(.64f * duration, 20), K(duration, 0)));
        AddIfPresent(plan, rig, Track("arm_l", K(0, 0), K(.24f * duration, -13), K(.4f * duration, 9), K(duration, 0)));
        AddIfPresent(plan, rig, Track("forearm_r", K(0, 0), K(.24f * duration, -28), K(.4f * duration, 38),
            K(.64f * duration, 24), K(duration, 0)));
        AddIfPresent(plan, rig, Track("hand_r", K(0, 0), K(.24f * duration, -10), K(.4f * duration, 18),
            K(.64f * duration, 10), K(duration, 0)));
        AddIfPresent(plan, rig, Track("forearm_l", K(0, 0), K(.24f * duration, -12), K(.4f * duration, 14),
            K(duration, 0)));
        AddIfPresent(plan, rig, Track("spine", K(0, 0), K(.24f * duration, -6), K(.4f * duration, 10), K(duration, 0)));
        AddIfPresent(plan, rig, TranslateTrack("root",
            (0, 0, 0), (.4f * duration, 0, -45), (.64f * duration, 0, -38), (duration, 0, 0)));
        AddIfPresent(plan, rig, Track("leg_l", K(0, 0), K(.4f * duration, 9), K(.64f * duration, 7), K(duration, 0)));
        AddIfPresent(plan, rig, Track("leg_r", K(0, 0), K(.4f * duration, -9), K(.64f * duration, -7), K(duration, 0)));
        AddIfPresent(plan, rig, Track("shin_l", K(0, 0), K(.4f * duration, -16), K(.64f * duration, -10), K(duration, 0)));
        AddIfPresent(plan, rig, Track("shin_r", K(0, 0), K(.4f * duration, 16), K(.64f * duration, 10), K(duration, 0)));
        return plan;
    }

    /// <summary>Charged AoE slam: raise both arms overhead, hold with a tremble, crash down hard.</summary>
    private static MotionPlan ChargedSlam(RigManifest rig, float duration)
    {
        var plan = Plan("skill_1", duration, loop: false);
        float w = .19f * duration, hold1 = .33f * duration, hold2 = .43f * duration, hit = .54f * duration;
        AddIfPresent(plan, rig, Track("arm_r", K(0, 0), K(w, -38), K(hold1, -34), K(hold2, -38), K(hit, 28),
            K(.7f * duration, 40), K(duration, 0)));
        AddIfPresent(plan, rig, Track("arm_l", K(0, 0), K(w, -32), K(hold1, -29), K(hold2, -32), K(hit, 24),
            K(.7f * duration, 19), K(duration, 0)));
        AddIfPresent(plan, rig, Track("forearm_r", K(0, 0), K(w, -30), K(hold1, -26), K(hold2, -31), K(hit, 42),
            K(.7f * duration, 26), K(duration, 0)));
        AddIfPresent(plan, rig, Track("hand_r", K(0, 0), K(w, -12), K(hold2, -13), K(hit, 20),
            K(.7f * duration, 10), K(duration, 0)));
        AddIfPresent(plan, rig, Track("forearm_l", K(0, 0), K(w, -20), K(hold2, -22), K(hit, 28),
            K(.7f * duration, 15), K(duration, 0)));
        AddIfPresent(plan, rig, TranslateTrack("root",
            (0, 0, 0), (w, 0, 10), (hold2, 0, 8), (hit, 0, -60), (.7f * duration, 0, -50), (duration, 0, 0)));
        AddIfPresent(plan, rig, Track("spine", K(0, 0), K(w, -8), K(hold2, -7), K(hit, 12), K(duration, 0)));
        AddIfPresent(plan, rig, Track("head", K(0, 0), K(w, -10), K(hit, 8), K(duration, 0)));
        AddIfPresent(plan, rig, Track("leg_l", K(0, 0), K(hit, 12), K(.7f * duration, 9), K(duration, 0)));
        AddIfPresent(plan, rig, Track("leg_r", K(0, 0), K(hit, -12), K(.7f * duration, -9), K(duration, 0)));
        AddIfPresent(plan, rig, Track("shin_l", K(0, 0), K(hit, -22), K(.7f * duration, -14), K(duration, 0)));
        AddIfPresent(plan, rig, Track("shin_r", K(0, 0), K(hit, 22), K(.7f * duration, 14), K(duration, 0)));
        return plan;
    }

    /// <summary>Lunging thrust: gather back, rocket forward, hold the point, return.</summary>
    private static MotionPlan LungingThrust(RigManifest rig, float duration)
    {
        var plan = Plan("skill_2", duration, loop: false);
        AddIfPresent(plan, rig, TranslateTrack("root",
            (0, 0, 0), (.28f * duration, -12, 0), (.45f * duration, 190, 0), (.73f * duration, 190, 0), (duration, 0, 0)));
        AddIfPresent(plan, rig, Track("arm_r", K(0, 0), K(.28f * duration, -28), K(.45f * duration, 6),
            K(.7f * duration, 4), K(duration, 0)));
        AddIfPresent(plan, rig, Track("forearm_r", K(0, 0), K(.28f * duration, -18), K(.45f * duration, 14),
            K(.7f * duration, 11), K(duration, 0)));
        AddIfPresent(plan, rig, Track("hand_r", K(0, 0), K(.28f * duration, -6), K(.45f * duration, 6),
            K(.7f * duration, 5), K(duration, 0)));
        AddIfPresent(plan, rig, Track("spine", K(0, 0), K(.28f * duration, -3), K(.45f * duration, 6),
            K(.7f * duration, 5), K(duration, 0)));
        AddIfPresent(plan, rig, Track("leg_l", K(0, 0), K(.45f * duration, 14), K(.7f * duration, 13), K(duration, 0)));
        AddIfPresent(plan, rig, Track("leg_r", K(0, 0), K(.45f * duration, -12), K(.7f * duration, -11), K(duration, 0)));
        AddIfPresent(plan, rig, Track("shin_l", K(0, 0), K(.45f * duration, -18), K(.7f * duration, -14), K(duration, 0)));
        AddIfPresent(plan, rig, Track("shin_r", K(0, 0), K(.45f * duration, 16), K(.7f * duration, 13), K(duration, 0)));
        return plan;
    }

    /// <summary>Spin slash: hop into a full 360° revolution with both arms extended, land settled.
    /// Ends at -360° — visually identical to setup, no unwind pop.</summary>
    private static MotionPlan SpinSlash(RigManifest rig, float duration)
    {
        var plan = Plan("skill_3", duration, loop: false);
        AddIfPresent(plan, rig, Track("hips", K(0, 0), K(.15f * duration, 0), K(.68f * duration, -360),
            K(duration, -360)));
        AddIfPresent(plan, rig, Track("arm_r", K(0, 0), K(.15f * duration, -32), K(.5f * duration, -35),
            K(.68f * duration, -32), K(duration, 0)));
        AddIfPresent(plan, rig, Track("arm_l", K(0, 0), K(.15f * duration, 30), K(.5f * duration, 33),
            K(.68f * duration, 30), K(duration, 0)));
        AddIfPresent(plan, rig, Track("forearm_r", K(0, 0), K(.15f * duration, -18), K(.5f * duration, -20),
            K(.68f * duration, -18), K(duration, 0)));
        AddIfPresent(plan, rig, Track("hand_r", K(0, 0), K(.15f * duration, -8), K(.68f * duration, -8), K(duration, 0)));
        AddIfPresent(plan, rig, Track("forearm_l", K(0, 0), K(.15f * duration, 16), K(.5f * duration, 18),
            K(.68f * duration, 16), K(duration, 0)));
        AddIfPresent(plan, rig, TranslateTrack("root",
            (0, 0, 0), (.4f * duration, 0, -26), (.55f * duration, 0, -26), (.75f * duration, 0, 0), (duration, 0, 0)));
        return plan;
    }

    /// <summary>Hit react: knock back, wobble, settle.</summary>
    private static MotionPlan Hit(RigManifest rig, float duration)
    {
        var plan = Plan("hit", duration, loop: false);
        AddIfPresent(plan, rig, Track("spine", K(0, 0), K(.18f * duration, -13), K(.58f * duration, 7), K(duration, 0)));
        AddIfPresent(plan, rig, TranslateTrack("root",
            (0, 0, 0), (.18f * duration, -20, 0), (.53f * duration, -8, 0), (duration, 0, 0)));
        AddIfPresent(plan, rig, Track("head", K(0, 0), K(.18f * duration, 12), K(duration, 0)));
        return plan;
    }

    /// <summary>Fall: crumple backward onto the ground and stay there. One-shot, ends prone.</summary>
    private static MotionPlan Fall(RigManifest rig, float duration)
    {
        var plan = Plan("fall", duration, loop: false);
        float down = .67f * duration;
        AddIfPresent(plan, rig, Track("hips", K(0, 0), K(down, -6), K(duration, -6)));
        AddIfPresent(plan, rig, TranslateTrack("root", (0, 0, 0), (down, 0, -200), (duration, 0, -200)));
        AddIfPresent(plan, rig, Track("arm_l", K(0, 0), K(down, -8), K(duration, -8)));
        AddIfPresent(plan, rig, Track("arm_r", K(0, 0), K(down, 7), K(duration, 7)));
        AddIfPresent(plan, rig, Track("forearm_l", K(0, 0), K(down, 18), K(duration, 18)));
        AddIfPresent(plan, rig, Track("forearm_r", K(0, 0), K(down, -16), K(duration, -16)));
        AddIfPresent(plan, rig, Track("leg_l", K(0, 0), K(down, 4), K(duration, 4)));
        AddIfPresent(plan, rig, Track("leg_r", K(0, 0), K(down, -3), K(duration, -3)));
        AddIfPresent(plan, rig, Track("shin_l", K(0, 0), K(down, -20), K(duration, -20)));
        AddIfPresent(plan, rig, Track("shin_r", K(0, 0), K(down, 17), K(duration, 17)));
        AddIfPresent(plan, rig, Track("head", K(0, 0), K(down, 6), K(duration, 8)));
        return plan;
    }

    /// <summary>Stand up: push off the ground from the prone pose with an effort pause.</summary>
    private static MotionPlan StandUp(RigManifest rig, float duration)
    {
        var plan = Plan("standup", duration, loop: false);
        AddIfPresent(plan, rig, Track("hips", K(0, -6), K(.32f * duration, -4), K(.58f * duration, -2),
            K(.79f * duration, -1), K(duration, 0)));
        AddIfPresent(plan, rig, TranslateTrack("root",
            (0, 0, -200), (.32f * duration, 0, -178), (.58f * duration, 0, -80), (.79f * duration, 0, -12), (duration, 0, 0)));
        AddIfPresent(plan, rig, Track("spine", K(0, 5), K(.58f * duration, 3), K(.79f * duration, -2), K(duration, 0)));
        AddIfPresent(plan, rig, Track("arm_l", K(0, -8), K(.58f * duration, -5), K(duration, 0)));
        AddIfPresent(plan, rig, Track("arm_r", K(0, 7), K(.58f * duration, 4), K(duration, 0)));
        AddIfPresent(plan, rig, Track("forearm_l", K(0, 18), K(.58f * duration, 8), K(duration, 0)));
        AddIfPresent(plan, rig, Track("forearm_r", K(0, -16), K(.58f * duration, -7), K(duration, 0)));
        AddIfPresent(plan, rig, Track("leg_l", K(0, 10), K(.58f * duration, 6), K(duration, 0)));
        AddIfPresent(plan, rig, Track("leg_r", K(0, -9), K(.58f * duration, -5), K(duration, 0)));
        AddIfPresent(plan, rig, Track("shin_l", K(0, -22), K(.58f * duration, -10), K(duration, 0)));
        AddIfPresent(plan, rig, Track("shin_r", K(0, 20), K(.58f * duration, 9), K(duration, 0)));
        return plan;
    }

    /// <summary>Death: slower crumple with a head droop; ends prone and still.</summary>
    private static MotionPlan Death(RigManifest rig, float duration)
    {
        var plan = Plan("death", duration, loop: false);
        float down = .5f * duration;
        AddIfPresent(plan, rig, Track("hips", K(0, 0), K(.19f * duration, -2), K(down, -8), K(duration, -9)));
        AddIfPresent(plan, rig, TranslateTrack("root",
            (0, 0, 0), (down, 0, -210), (duration, 0, -212)));
        AddIfPresent(plan, rig, Track("head", K(0, 0), K(down, 5), K(duration, 7)));
        AddIfPresent(plan, rig, Track("spine", K(0, 0), K(.27f * duration, 8), K(down, -6), K(duration, -8)));
        AddIfPresent(plan, rig, Track("arm_l", K(0, 0), K(down, -9), K(duration, -10)));
        AddIfPresent(plan, rig, Track("arm_r", K(0, 0), K(down, 8), K(duration, 9)));
        AddIfPresent(plan, rig, Track("forearm_l", K(0, 0), K(down, 22), K(duration, 24)));
        AddIfPresent(plan, rig, Track("forearm_r", K(0, 0), K(down, -20), K(duration, -22)));
        AddIfPresent(plan, rig, Track("leg_l", K(0, 0), K(down, 4), K(duration, 5)));
        AddIfPresent(plan, rig, Track("leg_r", K(0, 0), K(down, -3), K(duration, -4)));
        AddIfPresent(plan, rig, Track("shin_l", K(0, 0), K(down, -24), K(duration, -26)));
        AddIfPresent(plan, rig, Track("shin_r", K(0, 0), K(down, 21), K(duration, 23)));
        return plan;
    }

    /// <summary>Respawn: rise from the prone pose while popping to full size.</summary>
    private static MotionPlan Respawn(RigManifest rig, float duration)
    {
        var plan = Plan("respawn", duration, loop: false);
        AddIfPresent(plan, rig, Track("hips", K(0, -70), K(.32f * duration, -30), K(.64f * duration, 0), K(duration, 0)));
        AddIfPresent(plan, rig, TranslateTrack("root",
            (0, 0, -58), (.32f * duration, 0, -26), (.64f * duration, 0, 0), (duration, 0, 0)));
        AddIfPresent(plan, rig, ScaleTrack("root", K(0, 0.05f), K(.3f * duration, 1.14f),
            K(.45f * duration, 0.96f), K(.55f * duration, 1f), K(duration, 1f)));
        AddIfPresent(plan, rig, Track("spine", K(0, 6), K(.45f * duration, -3), K(duration, 0)));
        AddIfPresent(plan, rig, Track("head", K(0, 10), K(.55f * duration, -3), K(duration, 0)));
        AddIfPresent(plan, rig, Track("arm_l", K(0, -10), K(.45f * duration, -4), K(duration, 0)));
        AddIfPresent(plan, rig, Track("arm_r", K(0, 9), K(.45f * duration, 4), K(duration, 0)));
        AddIfPresent(plan, rig, Track("forearm_l", K(0, 24), K(.45f * duration, 8), K(duration, 0)));
        AddIfPresent(plan, rig, Track("forearm_r", K(0, -22), K(.45f * duration, -7), K(duration, 0)));
        return plan;
    }
}
