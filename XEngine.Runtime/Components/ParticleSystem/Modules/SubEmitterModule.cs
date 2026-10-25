// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Vector;
using XEngine.Runtime.ParticleSystem.Modules;

namespace XEngine.Runtime.ParticleSystem;

/// <summary>When the sub-emitted particle spawns (Unity SubModule emitter type).</summary>
public enum SubEmitterTrigger
{
    Birth = 0,
    Death = 1,
    Collision = 2,
}

/// <summary>
/// One sub-emitter entry. The target system is referenced by transform path from the effect
/// root ("ChildName/GrandChildName") because cross-GameObject component references do not
/// exist in the prefab format; the Unity→XEngine converter resolves Shuriken fileIDs into
/// these paths. Serialized fileIDs that were 0 in the source prefab are dropped there
/// (6 known-broken entries in the Hovl pack) and reported as conversion warnings.
/// </summary>
public sealed class SubEmitterSetting
{
    public string TargetPath = string.Empty;
    public SubEmitterTrigger Trigger = SubEmitterTrigger.Birth;
    public float Probability = 1f;
    /// <summary>Spawn the sub particle's speed along the parent particle's velocity.</summary>
    public bool InheritVelocityDirection = true;
    public bool InheritSize;
    public bool InheritColor;
    /// <summary>Sub particle dies together with the parent (lifetime = parent remaining).</summary>
    public bool InheritLifetime;
    /// <summary>Birth starts a child emitter for the parent's lifetime, using the target's emission modules.</summary>
    public bool UseTargetEmission;
}

/// <summary>
/// Unity SubModule: spawns particles in another system of the same effect when parent
/// particles are born, die, or collide. Triggering is driven by ParticleSystemComponent
/// (birth in EmitParticle, death in the expiry branch, collision via CollisionModule's
/// CollidedSeeds); this module holds the settings and the per-path target cache.
/// </summary>
public sealed class SubEmitterModule : ParticleSystemModule
{
    public List<SubEmitterSetting> Emitters = new();

    // Runtime cache: path → resolved target (invalidated when the target disappears).
    private readonly Dictionary<string, ParticleSystemComponent?> _resolved = new();
    private readonly List<ParticleSystemComponent> _componentScratch = new();
    private readonly List<AttachedEmitter> _attached = new();
    private readonly List<AttachedEmitter> _attachmentPool = new();
    private readonly Dictionary<uint, AttachedEmitter> _parentAttachments = new();
    private const int MaxRetainedAttachments = 1024;
    private static readonly MinMaxCurve InactiveRate = new(0f);
    private static readonly List<ParticleBurst> NoBursts = new();

    private sealed class AttachedEmitter
    {
        public uint ParentSeed;
        public int ActiveIndex;
        public ParticleSystemComponent Target = null!;
        public SubEmitterSetting Setting = null!;
        public AttachedEmitter? Next;
        public Float3 PreviousPosition;
        public Float3 Position;
        public Float3 Velocity;
        public Float4 Color;
        public float Size;
        public float RemainingLifetime;
        public float Time;
        public float Delay;
        public readonly EmissionModule Emission = new();
    }

    public bool HasTriggers(SubEmitterTrigger trigger)
    {
        if (!Enabled) return false;
        for (int i = 0; i < Emitters.Count; i++)
            if (Emitters[i].Trigger == trigger)
                return true;
        return false;
    }

    /// <summary>Fires every matching emitter for one parent-particle event.</summary>
    public void Trigger(SubEmitterTrigger trigger, ParticleSystemComponent parent,
        Float3 worldPosition, Float3 worldVelocity, Float4 parentColor, float parentSize,
        float parentRemainingLifetime, Random random, uint parentSeed = 0)
    {
        if (!Enabled) return;
        for (int i = 0; i < Emitters.Count; i++)
        {
            SubEmitterSetting setting = Emitters[i];
            if (setting.Trigger != trigger || string.IsNullOrEmpty(setting.TargetPath))
                continue;
            if (setting.Probability < 1f && (float)random.NextDouble() > setting.Probability)
                continue;

            ParticleSystemComponent? target = Resolve(parent, setting.TargetPath);
            if (target is null || !target.Enabled)
                continue;

            if (trigger == SubEmitterTrigger.Birth && setting.UseTargetEmission)
            {
                Attach(parentSeed, target, setting, worldPosition, worldVelocity, parentColor,
                    parentSize, parentRemainingLifetime, random);
                continue;
            }

            EmitOne(target, setting, worldPosition, worldVelocity, parentColor,
                parentSize, parentRemainingLifetime, random);
        }
    }

    private static void EmitOne(ParticleSystemComponent target, SubEmitterSetting setting,
        Float3 worldPosition, Float3 worldVelocity, Float4 parentColor, float parentSize,
        float parentRemainingLifetime, Random random)
    {
        float speed = target.Initial.StartSpeed.EvaluateInitial(random);
        Float3 direction = Float3.NormalizeSafe(worldVelocity, new Float3(0f, 1f, 0f));
        Float3 velocity = setting.InheritVelocityDirection
            ? direction * speed
            : new Float3(0f, speed, 0f);
        float size = setting.InheritSize ? parentSize : 1f;
        float lifetime = setting.InheritLifetime
            ? MathF.Max(0.05f, parentRemainingLifetime)
            : 0f; // 0 = use the target's own start lifetime

        target.SpawnSubParticle(worldPosition, velocity, size, parentColor,
            setting.InheritColor, lifetime, random);
    }

    private void Attach(uint seed, ParticleSystemComponent target, SubEmitterSetting setting,
        Float3 position, Float3 velocity, Float4 color, float size, float lifetime, Random random)
    {
        AttachedEmitter state;
        int last = _attachmentPool.Count - 1;
        if (last >= 0)
        {
            state = _attachmentPool[last];
            _attachmentPool.RemoveAt(last);
        }
        else
            state = new AttachedEmitter();
        state.ParentSeed = seed;
        state.ActiveIndex = _attached.Count;
        state.Target = target;
        state.Setting = setting;
        state.PreviousPosition = position;
        state.Position = position;
        state.Velocity = velocity;
        state.Color = color;
        state.Size = size;
        state.RemainingLifetime = lifetime;
        state.Time = 0f;
        state.Delay = MathF.Max(0f, target.StartDelay);
        state.Emission.Reset();
        SyncEmission(state);
        _parentAttachments.TryGetValue(seed, out state.Next);
        _parentAttachments[seed] = state;
        _attached.Add(state);
        if (state.Delay == 0f)
        {
            int count = state.Emission.CalculateEmitCount(0f, 0f, random, target.Duration);
            for (int i = 0; i < count && target.ParticleCount < target.MaxParticles; i++)
                EmitOne(target, setting, position, velocity, color, size, lifetime, random);
        }
    }

    internal void UpdateParent(uint seed, Float3 position, Float3 velocity, Float4 color, float size, float lifetime)
    {
        if (!_parentAttachments.TryGetValue(seed, out AttachedEmitter? state))
            return;
        while (state != null)
        {
            state.Position = position;
            state.Velocity = velocity;
            state.Color = color;
            state.Size = size;
            state.RemainingLifetime = lifetime;
            state = state.Next;
        }
    }

    internal void TickAttachedEmitters(float deltaTime, Random random)
    {
        for (int i = _attached.Count - 1; i >= 0; i--)
        {
            AttachedEmitter state = _attached[i];
            if (!Enabled || state.Target.IsDisposed || !state.Target.Enabled || state.Target.Duration <= 0f)
            {
                Retire(state);
                continue;
            }
            SyncEmission(state);
            Float3 movement = state.Position - state.PreviousPosition;
            float remaining = deltaTime;
            float elapsed = 0f;
            if (state.Delay > 0f)
            {
                float delayStep = MathF.Min(state.Delay, remaining);
                state.Delay -= delayStep;
                remaining -= delayStep;
                elapsed += delayStep;
            }
            while (remaining > 0f)
            {
                float step = MathF.Max(0f, MathF.Min(remaining, state.Target.Duration - state.Time));
                Float3 start = state.PreviousPosition + movement * (elapsed / deltaTime);
                Float3 end = state.PreviousPosition + movement * ((elapsed + step) / deltaTime);
                state.Time += step;
                int timed = state.Emission.CalculateEmitCount(step, state.Time, random, state.Target.Duration);
                for (int p = 0; p < timed && state.Target.ParticleCount < state.Target.MaxParticles; p++)
                    EmitOne(state.Target, state.Setting, end, state.Velocity, state.Color,
                        state.Size, state.RemainingLifetime, random);

                int distanceCount = state.Emission.CalculateDistanceEmitCount(Float3.Length(end - start),
                    state.Time / state.Target.Duration, random, out float first, out float spacing);
                for (int p = 0; p < distanceCount && state.Target.ParticleCount < state.Target.MaxParticles; p++)
                {
                    Float3 position = start + (end - start) * Math.Clamp(first + p * spacing, 0f, 1f);
                    EmitOne(state.Target, state.Setting, position, state.Velocity, state.Color,
                        state.Size, state.RemainingLifetime, random);
                }
                elapsed += step;
                remaining -= step;
                if (state.Time >= state.Target.Duration)
                {
                    if (!state.Target.Looping)
                    {
                        Retire(state);
                        break;
                    }
                    state.Time = 0f;
                    state.Emission.RestartCycle();
                }
            }
            state.PreviousPosition = state.Position;
        }
    }

    private static void SyncEmission(AttachedEmitter state)
    {
        EmissionModule source = state.Target.Emission;
        state.Emission.Enabled = source.Enabled;
        state.Emission.UseUnityBurstScheduling = source.UseUnityBurstScheduling;
        state.Emission.RateOverTime = source.RateOverTime;
        state.Emission.RateOverDistance = source.RateOverDistance;
        state.Emission.Bursts = source.Bursts;
    }

    internal void OnParticleDied(uint seed)
    {
        while (_parentAttachments.TryGetValue(seed, out AttachedEmitter? state))
            Retire(state);
    }

    private void Retire(AttachedEmitter state)
    {
        if (_parentAttachments.TryGetValue(state.ParentSeed, out AttachedEmitter? head))
        {
            if (ReferenceEquals(head, state))
            {
                if (state.Next == null) _parentAttachments.Remove(state.ParentSeed);
                else _parentAttachments[state.ParentSeed] = state.Next;
            }
            else
            {
                while (head.Next != null && !ReferenceEquals(head.Next, state)) head = head.Next;
                if (head.Next != null) head.Next = state.Next;
            }
        }
        int last = _attached.Count - 1;
        if (state.ActiveIndex != last)
        {
            AttachedEmitter moved = _attached[last];
            _attached[state.ActiveIndex] = moved;
            moved.ActiveIndex = state.ActiveIndex;
        }
        _attached.RemoveAt(last);
        state.Target = null!;
        state.Setting = null!;
        state.Next = null;
        state.Emission.Reset();
        state.Emission.RateOverTime = InactiveRate;
        state.Emission.RateOverDistance = InactiveRate;
        state.Emission.Bursts = NoBursts;
        if (_attachmentPool.Count < MaxRetainedAttachments)
            _attachmentPool.Add(state);
    }

    private ParticleSystemComponent? Resolve(ParticleSystemComponent parent, string path)
    {
        if (_resolved.TryGetValue(path, out ParticleSystemComponent? cached))
        {
            // Cached handles go stale when the effect instance is destroyed/replayed.
            if (cached is { IsDisposed: false })
                return cached;
            _resolved.Remove(path);
        }

        // Prefab instances can sit under gameplay/viewer containers. Resolve relative
        // to the nearest ancestor containing the authored path, not the scene root.
        string[] segments = path.Split('/');
        for (GameObject? root = parent.GameObject; root is not null; root = root.Parent)
        {
            GameObject? node = root;
            for (int i = 0; i < segments.Length && node is not null; i++)
            {
                GameObject? next = null;
                for (int c = 0; c < node.Children.Count; c++)
                {
                    GameObject child = node.Children[c];
                    if (string.Equals(child.Name, segments[i], StringComparison.Ordinal))
                    {
                        next = child;
                        break;
                    }
                }
                node = next;
            }
            if (node is null)
                continue;

            _componentScratch.Clear();
            node.GetComponents(_componentScratch);
            if (_componentScratch.Count == 0)
                continue;
            ParticleSystemComponent found = _componentScratch[0];
            _resolved[path] = found;
            return found;
        }
        return null;
    }

    public void ClearCache()
    {
        _resolved.Clear();
        _componentScratch.Clear();
        while (_attached.Count > 0)
            Retire(_attached[^1]);
    }
}
