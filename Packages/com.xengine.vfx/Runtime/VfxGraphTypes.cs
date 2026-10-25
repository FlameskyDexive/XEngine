using System;
using System.Collections.Generic;
using System.Globalization;

namespace XEngine.VFX;

public sealed class VfxGraphCompileException : Exception
{
    public VfxGraphCompileException(string message) : base(message) { }
}

public enum VfxTemplateKind
{
    Empty = 0,
    Loop = 1,
    Burst = 2,
    Trail = 3,
    Mesh = 4,
    Subgraph = 5,
    Decal = 6,
}

public enum VfxValueType
{
    Float = 0,
    Float2 = 1,
    Float3 = 2,
    Float4 = 3,
    Color = 4,
    Texture = 5,
    Mesh = 6,
    Texture3D = 7,
}

public static class VfxExposedNames
{
    public const string SpawnRate = "SpawnRate";
    public const string BurstCount = "BurstCount";
    public const string SpawnOverDistance = "SpawnOverDistance";
    public const string Lifetime = "Lifetime";
    public const string StartSpeed = "StartSpeed";
    public const string InheritVelocity = "InheritVelocity";
    public const string TriggerEventRate = "TriggerEventRate";
    public const string TriggerEventOverDistance = "TriggerEventOverDistance";
    public const string TriggerEventOnce = "TriggerEventOnce";
    public const string SizeOverSpeed = "SizeOverSpeed";
    public const string MultiplySize = "MultiplySize";
    public const string MultiplyColor = "MultiplyColor";
    public const string AddSize = "AddSize";
    public const string AddColor = "AddColor";
    public const string AddVelocity = "AddVelocity";
    public const string MultiplyVelocity = "MultiplyVelocity";
    public const string OverwriteSize = "OverwriteSize";
    public const string OverwriteColor = "OverwriteColor";
    public const string OverwriteVelocity = "OverwriteVelocity";
    public const string OverwriteLifetime = "OverwriteLifetime";
    public const string AddLifetime = "AddLifetime";
    public const string MultiplyLifetime = "MultiplyLifetime";
    public const string AddAge = "AddAge";
    public const string MultiplyAge = "MultiplyAge";
    public const string SetAge = "SetAge";
    public const string AddPosition = "AddPosition";
    public const string SetPosition = "SetPosition";
    public const string ColorOverSpeed = "ColorOverSpeed";
    public const string ScreenSpaceSize = "ScreenSpaceSize";
    public const string CollisionFriction = "CollisionFriction";
    public const string CollisionElasticity = "CollisionElasticity";
    public const string CollisionLifetimeLoss = "CollisionLifetimeLoss";
    public const string LimitVelocity = "LimitVelocity";
    public const string KillSphere = "KillSphere";
    public const string KillAABox = "KillAABox";
    public const string KillPlane = "KillPlane";
    public const string Attract = "Attract";
    public const string ConformToSphere = "ConformToSphere";
    public const string Vortex = "Vortex";
    public const string ConformToCylinder = "ConformToCylinder";
    public const string OrientLookAt = "OrientLookAt";
    public const string FlipbookRate = "FlipbookRate";
    public const string FlipbookStart = "FlipbookStart";
    public const string StartSize = "StartSize";
    public const string Gravity = "Gravity";
    public const string StartColor = "StartColor";
    public const string MainTexture = "MainTexture";
    public const string MainTexShader = "_MainTex";
    public const string FlipbookColumns = "FlipbookColumns";
    public const string FlipbookRows = "FlipbookRows";
    public const string FlipbookSize = "FlipbookSize";
    public const string FlipbookColumnsShader = "_FlipbookColumns";
    public const string FlipbookRowsShader = "_FlipbookRows";
    public const string FlipbookBlend = "FlipbookBlend";
    public const string FlipbookBlendShader = "_FlipbookBlend";
    public const string FlipbookMotion = "FlipbookMotion";
    public const string FlipbookMotionScale = "FlipbookMotionScale";
    public const string FlipbookMotionShader = "_FlipbookMotion";
    public const string MotionVectorMode = "MotionVectorMode";
    public const string MotionVectorScale = "MotionVectorScale";
    public const string MotionVectorModeShader = "_MotionVectorMode";
    public const string MotionVectorScaleShader = "_MotionVectorScale";
    public const string OrientMode = "OrientMode";
    public const string OrientModeShader = "_OrientMode";
    public const string CollisionMode = "CollisionMode";
    public const string CollisionBounce = "CollisionBounce";
    public const string CollisionPlane = "CollisionPlane";
    public const string SdfCollisionMode = "SdfCollisionMode";
    public const string SdfShape = "SdfShape";
    public const string SdfCenter = "SdfCenter";
    public const string SdfSize = "SdfSize";
    public const string SdfVolume = "SdfVolume";
    public const string SdfVolumeShader = "_SdfVolume";
    public const string TurbulenceIntensity = "TurbulenceIntensity";
    public const string TurbulenceFrequency = "TurbulenceFrequency";
    public const string OutputMesh = "OutputMesh";
    public const string OutputModeShader = "_OutputMode";
    public const float OutputModeQuad = 0f;
    public const float OutputModeMesh = 1f;
    public const float OutputModeStrip = 2f;
    public const float OutputModeDecal = 3f;
    public const string DecalPlaneShader = "_DecalPlane";
    public const string SubgraphPath = "Path";
    public const string SubgraphGraph = "Graph";
    public const string LitMode = "LitMode";
    public const string LightDirection = "LightDirection";
    public const string LightColor = "LightColor";
    public const string AmbientColor = "AmbientColor";
    public const string Shininess = "Shininess";
    public const string LitModeShader = "_LitMode";
    public const string LightDirectionShader = "_LightDirection";
    public const string LightColorShader = "_LightColor";
    public const string AmbientColorShader = "_AmbientColor";
    public const string SoftParticleFade = "SoftParticleFade";
    public const string SoftParticleFadeShader = "_SoftParticleFade";
    public const string CameraDepthShader = "_CameraDepthTexture";
    public const string VfxCameraDepthShader = "_VfxCameraDepth";
    public const string EventKind = "EventKind";
    public const string EventCount = "EventCount";
    public const string DepthCollisionMode = "DepthCollisionMode";
    public const string CameraDepth = "CameraDepth";
    public const string ConformAttraction = "ConformAttraction";
    public const string VectorFieldIntensity = "VectorFieldIntensity";
    public const string VectorField = "VectorField";
    public const string VectorFieldShader = "_VectorField";
    public const string AttributeMapMode = "AttributeMapMode";
    public const string AttributeMapScale = "AttributeMapScale";
    public const string AttributeMap = "AttributeMap";
    public const string AttributeMapShader = "_AttributeMap";
    public const string DistortionIntensity = "DistortionIntensity";
    public const string DistortionIntensityShader = "_DistortionIntensity";
    public const string SceneColor = "SceneColor";
    public const string SceneColorShader = "_VfxSceneColor";
    public const string ParticleLightIntensity = "ParticleLightIntensity";
    public const string ParticleLightRange = "ParticleLightRange";
    public const string ParticleLightCount = "ParticleLightCount";
    public const string SortMode = "SortMode";
    public const string CameraFadeNear = "CameraFadeNear";
    public const string CameraFadeFar = "CameraFadeFar";
    public const string CameraFadeNearShader = "_CameraFadeNear";
    public const string CameraFadeFarShader = "_CameraFadeFar";

    public static bool EqualsName(string? left, string right)
        => string.Equals(left, right, StringComparison.Ordinal);
}

public static class VfxPropertyValue
{
    public static bool TryParseFloat(string? raw, out float value)
    {
        value = 0f;
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        return float.TryParse(
            raw.Trim().TrimEnd('F', 'f'),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value);
    }

    public static int ParseFloats(string? raw, Span<float> destination)
    {
        if (string.IsNullOrWhiteSpace(raw) || destination.Length == 0)
            return 0;
        int count = 0;
        int start = 0;
        string text = raw.Trim();
        for (int i = 0; i <= text.Length && count < destination.Length; i++)
        {
            bool end = i == text.Length;
            char c = end ? ',' : text[i];
            if (c != ',' && c != ' ' && c != ';' && !end)
                continue;
            if (i > start)
            {
                if (TryParseFloat(text.Substring(start, i - start), out float parsed))
                    destination[count++] = parsed;
            }
            start = i + 1;
        }
        return count;
    }
}

public static class VfxProperties
{
    public static VfxProperty? Find(IReadOnlyList<VfxProperty>? properties, string name)
    {
        if (properties == null || string.IsNullOrEmpty(name))
            return null;
        for (int i = 0; i < properties.Count; i++)
        {
            VfxProperty p = properties[i];
            if (VfxExposedNames.EqualsName(p.Name, name) || VfxExposedNames.EqualsName(p.DisplayName, name))
                return p;
        }
        return null;
    }

    public static VfxProperty Add(List<VfxProperty> properties, string name, VfxValueType type, string defaultValue = "")
    {
        ArgumentNullException.ThrowIfNull(properties);
        string unique = UniqueName(properties, string.IsNullOrWhiteSpace(name) ? "Property" : name.Trim());
        var property = new VfxProperty
        {
            Id = "p_" + Guid.NewGuid().ToString("N")[..8],
            Name = unique,
            DisplayName = unique,
            Type = type,
            DefaultValue = defaultValue ?? "",
        };
        properties.Add(property);
        return property;
    }

    public static bool Remove(List<VfxProperty> properties, string name)
    {
        ArgumentNullException.ThrowIfNull(properties);
        for (int i = 0; i < properties.Count; i++)
        {
            if (!VfxExposedNames.EqualsName(properties[i].Name, name)
                && !VfxExposedNames.EqualsName(properties[i].DisplayName, name))
            {
                continue;
            }
            properties.RemoveAt(i);
            return true;
        }
        return false;
    }

    private static string UniqueName(List<VfxProperty> properties, string name)
    {
        if (Find(properties, name) == null)
            return name;
        for (int n = 2; n < 1000; n++)
        {
            string candidate = name + "_" + n.ToString(CultureInfo.InvariantCulture);
            if (Find(properties, candidate) == null)
                return candidate;
        }
        return name + "_" + Guid.NewGuid().ToString("N")[..4];
    }
}

/// <summary>Age-driven flipbook atlas UVs. Must match Output in <c>VfxParticles.shader</c>.</summary>
public static class VfxFlipbook
{
    /// <summary>
    /// Frame cursor. Rate &gt; 0 is <c>age * rate</c> wrapped into <c>[0, frames)</c>.
    /// Rate &lt;= 0 is lifetime <c>saturate(age/life) * frames</c>. Must match <c>VfxFlipbookFrame</c>.
    /// </summary>
    public static float Frame(float age, float lifetime, float frames, float rate)
    {
        float f = frames < 1f ? 1f : frames;
        if (rate > 0f)
        {
            float frameF = age * rate;
            frameF -= f * MathF.Floor(frameF / f);
            return frameF < 0f ? 0f : frameF;
        }

        float life = lifetime > 1e-5f ? lifetime : 1e-5f;
        float t = age / life;
        if (t < 0f) t = 0f;
        else if (t > 1f) t = 1f;
        return t * f;
    }

    /// <summary>
    /// Adds a start-frame offset, then wraps into <c>[0, frames)</c>.
    /// Start == 0 is a no-op. Must match <c>VfxFlipbookFrame</c>.
    /// </summary>
    public static float Frame(float age, float lifetime, float frames, float rate, float start)
    {
        float frameF = Frame(age, lifetime, frames, rate);
        if (start == 0f)
            return frameF;
        float f = frames < 1f ? 1f : frames;
        frameF += start;
        frameF -= f * MathF.Floor(frameF / f);
        return frameF < 0f ? 0f : frameF;
    }

    public static void FrameUv(
        float u,
        float v,
        float age,
        float lifetime,
        float columns,
        float rows,
        out float atlasU,
        out float atlasV)
    {
        float cols = columns < 1f ? 1f : columns;
        float rowCount = rows < 1f ? 1f : rows;
        float frames = cols * rowCount;
        float life = lifetime > 1e-5f ? lifetime : 1e-5f;
        float t = age / life;
        if (t < 0f) t = 0f;
        else if (t > 1f) t = 1f;
        float frame = MathF.Floor(t * frames);
        if (frame > frames - 1f)
            frame = frames - 1f;
        float col = frame - cols * MathF.Floor(frame / cols);
        float row = MathF.Floor(frame / cols);
        atlasU = (u + col) / cols;
        atlasV = (v + (rowCount - 1f - row)) / rowCount;
    }

    /// <summary>
    /// Adjacent-cell UVs plus blend weight. <paramref name="blend"/> &lt;= 0 is a no-op
    /// (<paramref name="w"/> = 0). Must match Output <c>VfxFlipbookBlend</c>.
    /// </summary>
    public static void FrameUvBlend(
        float u,
        float v,
        float age,
        float lifetime,
        float columns,
        float rows,
        float blend,
        out float u0,
        out float v0,
        out float u1,
        out float v1,
        out float w)
    {
        FrameUv(u, v, age, lifetime, columns, rows, out u0, out v0);
        u1 = u0;
        v1 = v0;
        w = 0f;
        if (blend <= 0f)
            return;
        float cols = columns < 1f ? 1f : columns;
        float rowCount = rows < 1f ? 1f : rows;
        float frames = cols * rowCount;
        float life = lifetime > 1e-5f ? lifetime : 1e-5f;
        float t = age / life;
        if (t < 0f) t = 0f;
        else if (t > 1f) t = 1f;
        float frameF = t * frames;
        float frame = MathF.Floor(frameF);
        if (frame > frames - 1f)
            frame = frames - 1f;
        float next = frame + 1f;
        if (next > frames - 1f)
            next = frames - 1f;
        float col = next - cols * MathF.Floor(next / cols);
        float row = MathF.Floor(next / cols);
        u1 = (u + col) / cols;
        v1 = (v + (rowCount - 1f - row)) / rowCount;
        w = blend;
        if (w > 1f) w = 1f;
        w *= frameF - frame;
    }

    public static void Blend(
        ref float r, ref float g, ref float b, ref float a,
        float r1, float g1, float b1, float a1, float w)
    {
        if (w <= 0f)
            return;
        if (w > 1f) w = 1f;
        r += (r1 - r) * w;
        g += (g1 - g) * w;
        b += (b1 - b) * w;
        a += (a1 - a) * w;
    }
}

/// <summary>
/// Flipbook motion-vector UV warp. RG 0.5 is rest. Must match Output <c>VfxFlipbookWarp</c>.
/// Scale &lt;= 0 is a no-op.
/// </summary>
public static class VfxFlipbookMotion
{
    public static void Decode(float r, float g, out float mx, out float my)
    {
        mx = r * 2f - 1f;
        my = g * 2f - 1f;
    }

    public static void Warp(
        float u, float v, float mx, float my, float weight, float scale,
        out float ou, out float ov)
    {
        ou = u;
        ov = v;
        if (scale <= 0f)
            return;
        ou = u + mx * weight * scale;
        ov = v + my * weight * scale;
    }

    /// <summary>Current cell warps by <paramref name="w"/>; next cell warps by <c>w - 1</c>.</summary>
    public static void WarpPair(
        float u0, float v0, float u1, float v1,
        float mx0, float my0, float mx1, float my1,
        float w, float scale,
        out float au, out float av, out float bu, out float bv)
    {
        float t = w;
        if (t < 0f) t = 0f;
        else if (t > 1f) t = 1f;
        Warp(u0, v0, mx0, my0, t, scale, out au, out av);
        Warp(u1, v1, mx1, my1, t - 1f, scale, out bu, out bv);
    }
}

/// <summary>
/// Texture2D attribute map sampled at Init. Must match Init in <c>VfxParticles.shader</c>.
/// Sequential texel <c>slot % (w*h)</c>. UNORM RGB 0.5 is zero position offset.
/// Mode 0 off / 1 position / 2 color / 3 size / 4 position+color.
/// </summary>
public static class VfxAttributeMap
{
    public const float ModeOff = 0f;
    public const float ModePosition = 1f;
    public const float ModeColor = 2f;
    public const float ModeSize = 3f;
    public const float ModePositionColor = 4f;

    public static void Sample(
        ReadOnlySpan<float> rgba, int width, int height, uint slot,
        out float r, out float g, out float b, out float a)
    {
        r = 1f;
        g = 1f;
        b = 1f;
        a = 1f;
        int w = width < 1 ? 1 : width;
        int h = height < 1 ? 1 : height;
        int n = w * h;
        if (rgba.Length < n * 4)
            return;
        int i = (int)(slot % (uint)n);
        int o = i * 4;
        r = rgba[o];
        g = rgba[o + 1];
        b = rgba[o + 2];
        a = rgba[o + 3];
    }

    public static void Apply(
        ref float px,
        ref float py,
        ref float pz,
        ref float size,
        ref float cr,
        ref float cg,
        ref float cb,
        ref float ca,
        float ex,
        float ey,
        float ez,
        float startSize,
        float mode,
        float scale,
        ReadOnlySpan<float> rgba,
        int width,
        int height,
        uint slot)
    {
        if (mode < 0.5f)
            return;
        Sample(rgba, width, height, slot, out float r, out float g, out float b, out float a);
        if (mode < 1.5f || mode >= 3.5f)
        {
            float s = scale;
            px = ex + (r * 2f - 1f) * s;
            py = ey + (g * 2f - 1f) * s;
            pz = ez + (b * 2f - 1f) * s;
        }
        if ((mode >= 1.5f && mode < 2.5f) || mode >= 3.5f)
        {
            cr = r;
            cg = g;
            cb = b;
            ca = a;
        }
        if (mode >= 2.5f && mode < 3.5f)
            size = startSize * (r > 0f ? r : 0f);
    }
}

/// <summary>
/// Unity SpawnOverDistance: emit <c>rate</c> particles per unit the emitter travels.
/// Fractional remainder stays in <paramref name="leftover"/>. The first sample records
/// position without spawning so a start/teleport does not dump a burst. Rate &lt;= 0
/// still tracks position.
/// </summary>
public static class VfxSpawnOverDistance
{
    public static uint Apply(
        ref float leftover,
        ref float lastX,
        ref float lastY,
        ref float lastZ,
        ref bool hasPrev,
        float x,
        float y,
        float z,
        float ratePerUnit,
        int capacity)
    {
        if (!hasPrev)
        {
            lastX = x;
            lastY = y;
            lastZ = z;
            hasPrev = true;
            return 0;
        }

        float dx = x - lastX;
        float dy = y - lastY;
        float dz = z - lastZ;
        lastX = x;
        lastY = y;
        lastZ = z;
        if (ratePerUnit <= 0f)
            return 0;
        leftover += MathF.Sqrt(dx * dx + dy * dy + dz * dz) * ratePerUnit;
        if (leftover < 1f)
            return 0;
        int spawn = (int)leftover;
        leftover -= spawn;
        int cap = capacity < 0 ? 0 : capacity;
        if (spawn > cap)
            spawn = cap;
        return (uint)spawn;
    }
}

/// <summary>
/// Init inherit source velocity. Must match Init in <c>VfxParticles.shader</c>
/// (<c>particle.velocity += p.gravity * amount</c> on the Init dispatch only).
/// Amount &lt;= 0 is a no-op. First sample records position without a delta.
/// </summary>
public static class VfxInheritVelocity
{
    public static void Sample(
        bool hasPrev,
        float lastX,
        float lastY,
        float lastZ,
        float x,
        float y,
        float z,
        float dt,
        out float vx,
        out float vy,
        out float vz)
    {
        vx = 0f;
        vy = 0f;
        vz = 0f;
        if (!hasPrev || dt <= 1e-8f)
            return;
        vx = (x - lastX) / dt;
        vy = (y - lastY) / dt;
        vz = (z - lastZ) / dt;
    }

    public static void Add(
        ref float vx, ref float vy, ref float vz,
        float ex, float ey, float ez, float amount)
    {
        if (amount <= 0f)
            return;
        vx += ex * amount;
        vy += ey * amount;
        vz += ez * amount;
    }
}

/// <summary>
/// Unity Periodic Burst. Emits <paramref name="count"/> particles every
/// <paramref name="period"/> seconds. Count or period &lt;= 0 is a no-op.
/// The first burst fires after one period.
/// </summary>
public static class VfxPeriodicBurst
{
    public static uint Apply(ref float leftover, float dt, float count, float period, int capacity)
    {
        if (count <= 0f || period <= 0f || dt <= 0f)
            return 0;
        leftover += dt;
        if (leftover < period)
            return 0;
        int pulses = (int)(leftover / period);
        leftover -= pulses * period;
        int spawn = pulses * (int)count;
        if (spawn < 0)
            spawn = 0;
        int cap = capacity < 0 ? 0 : capacity;
        if (spawn > cap)
            spawn = cap;
        return (uint)spawn;
    }
}

/// <summary>
/// Unity Trigger Event Rate. Accumulates <c>rate * dt</c> and emits
/// <c>floor(leftover)</c> events onto the CPU GPU-event queue.
/// Rate &lt;= 0 is a no-op.
/// </summary>
public static class VfxTriggerEventRate
{
    public static uint Apply(ref float leftover, float dt, float rate, int capacity)
    {
        if (rate <= 0f || dt <= 0f)
            return 0;
        leftover += rate * dt;
        if (leftover < 1f)
            return 0;
        int n = (int)leftover;
        leftover -= n;
        if (n < 0)
            n = 0;
        int cap = capacity < 0 ? 0 : capacity;
        if (n > cap)
            n = cap;
        return (uint)n;
    }
}

/// <summary>
/// Unity Trigger Event Over Distance. Accumulates
/// <c>distance * rate</c> from the previous emitter pose and emits
/// <c>floor(leftover)</c> events onto the CPU GPU-event queue.
/// Does not write last position — <see cref="VfxSpawnOverDistance"/> still tracks it.
/// Rate &lt;= 0 or first sample is a no-op.
/// </summary>
public static class VfxTriggerEventOverDistance
{
    public static uint Apply(
        ref float leftover,
        bool hasPrev,
        float lastX,
        float lastY,
        float lastZ,
        float x,
        float y,
        float z,
        float ratePerUnit,
        int capacity)
    {
        if (!hasPrev || ratePerUnit <= 0f)
            return 0;
        float dx = x - lastX;
        float dy = y - lastY;
        float dz = z - lastZ;
        leftover += MathF.Sqrt(dx * dx + dy * dy + dz * dz) * ratePerUnit;
        if (leftover < 1f)
            return 0;
        int n = (int)leftover;
        leftover -= n;
        if (n < 0)
            n = 0;
        int cap = capacity < 0 ? 0 : capacity;
        if (n > cap)
            n = cap;
        return (uint)n;
    }
}

/// <summary>
/// Unity Trigger Event Once. Emits <c>count</c> events on the first
/// successful sample, then stays armed until reset. Count &lt;= 0 is a
/// no-op and does not arm.
/// </summary>
public static class VfxTriggerEventOnce
{
    public static uint Apply(ref bool fired, float count, int capacity)
    {
        if (fired || count <= 0f)
            return 0;
        fired = true;
        int n = (int)count;
        if (n < 0)
            n = 0;
        int cap = capacity < 0 ? 0 : capacity;
        if (n > cap)
            n = cap;
        return (uint)n;
    }
}

/// <summary>
/// Infinite-plane particle collision. Must match Update in <c>VfxParticles.shader</c>.
/// Signed distance is <c>dot(position, normalize(N)) + D</c>. Mode: 0 off, 1 kill, 2 bounce.
/// </summary>
public static class VfxCollision
{
    public const float ModeOff = 0f;
    public const float ModeKill = 1f;
    public const float ModeBounce = 2f;

    public static void CollidePlane(
        ref float px,
        ref float py,
        ref float pz,
        ref float vx,
        ref float vy,
        ref float vz,
        ref uint alive,
        float nx,
        float ny,
        float nz,
        float planeW,
        float mode,
        float bounce)
    {
        if (alive == 0u || mode < 0.5f)
            return;
        float nlen2 = nx * nx + ny * ny + nz * nz;
        if (nlen2 <= 1e-8f)
            return;
        float inv = 1f / MathF.Sqrt(nlen2);
        nx *= inv;
        ny *= inv;
        nz *= inv;
        float dist = px * nx + py * ny + pz * nz + planeW;
        if (dist >= 0f)
            return;
        if (mode < 1.5f)
        {
            alive = 0u;
            return;
        }
        px -= nx * dist;
        py -= ny * dist;
        pz -= nz * dist;
        float vn = vx * nx + vy * ny + vz * nz;
        if (vn < 0f)
        {
            float k = (1f + bounce) * vn;
            vx -= k * nx;
            vy -= k * ny;
            vz -= k * nz;
        }
    }

    public const float ShapeOff = 0f;
    public const float ShapeSphere = 1f;
    public const float ShapeBox = 2f;
    public const float ShapeVolume = 3f;
    public const float ShapeCylinder = 4f;
    public const float ShapeCone = 5f;

    /// <summary>
    /// Signed distance. Shape &lt; 1.5 sphere (radius <paramref name="sx"/>),
    /// &lt; 2.5 AABB, &lt; 3.5 Texture3D (returns a large positive here; GPU samples the volume),
    /// &lt; 4.5 Y-capped cylinder (radius <paramref name="sx"/>, half-height <paramref name="sy"/>),
    /// else Y-capped cone (bottom <paramref name="sx"/>, top <paramref name="sz"/>, half-height <paramref name="sy"/>).
    /// Must match Update in <c>VfxParticles.shader</c>.
    /// </summary>
    public static float SampleSdf(
        float px,
        float py,
        float pz,
        float cx,
        float cy,
        float cz,
        float sx,
        float sy,
        float sz,
        float shape)
    {
        float dx = px - cx;
        float dy = py - cy;
        float dz = pz - cz;
        if (shape < 1.5f)
        {
            float r = sx > 0f ? sx : 0f;
            return MathF.Sqrt(dx * dx + dy * dy + dz * dz) - r;
        }
        if (shape < 2.5f)
        {
            float qx = MathF.Abs(dx) - sx;
            float qy = MathF.Abs(dy) - sy;
            float qz = MathF.Abs(dz) - sz;
            float mx = qx > 0f ? qx : 0f;
            float my = qy > 0f ? qy : 0f;
            float mz = qz > 0f ? qz : 0f;
            float outside = MathF.Sqrt(mx * mx + my * my + mz * mz);
            float inside = qx;
            if (qy > inside) inside = qy;
            if (qz > inside) inside = qz;
            return inside < 0f ? inside : outside;
        }
        if (shape < 3.5f)
            return 1e6f;
        if (shape < 4.5f)
            return SampleCylinder(dx, dy, dz, sx, sy);
        return SampleCone(dx, dy, dz, sy, sx, sz);
    }

    /// <summary>Y-capped cylinder. Must match HLSL <c>VfxSampleCylinder</c>.</summary>
    public static float SampleCylinder(float dx, float dy, float dz, float radius, float halfHeight)
    {
        float r = radius > 0f ? radius : 0f;
        float h = halfHeight > 1e-5f ? halfHeight : 1e-5f;
        float qx = MathF.Sqrt(dx * dx + dz * dz) - r;
        float qy = MathF.Abs(dy) - h;
        float mx = qx > 0f ? qx : 0f;
        float my = qy > 0f ? qy : 0f;
        float inside = qx > qy ? qx : qy;
        if (inside > 0f)
            inside = 0f;
        return inside + MathF.Sqrt(mx * mx + my * my);
    }

    /// <summary>Y-capped cone, bottom radius r1 at y=-h, top r2 at y=+h. Must match HLSL <c>VfxSampleCone</c>.</summary>
    public static float SampleCone(float dx, float dy, float dz, float halfHeight, float r1, float r2)
    {
        float h = halfHeight > 1e-5f ? halfHeight : 1e-5f;
        float bottom = r1 > 0f ? r1 : 0f;
        float top = r2 > 0f ? r2 : 0f;
        float qx = MathF.Sqrt(dx * dx + dz * dz);
        float qy = dy;
        float k1x = top;
        float k1y = h;
        float k2x = top - bottom;
        float k2y = 2f * h;
        float capR = qy < 0f ? bottom : top;
        float cax = qx - (qx < capR ? qx : capR);
        float cay = MathF.Abs(qy) - h;
        float k2len2 = k2x * k2x + k2y * k2y;
        float t = 0f;
        if (k2len2 > 1e-12f)
        {
            t = ((k1x - qx) * k2x + (k1y - qy) * k2y) / k2len2;
            if (t < 0f) t = 0f;
            else if (t > 1f) t = 1f;
        }
        float cbx = qx - k1x + k2x * t;
        float cby = qy - k1y + k2y * t;
        float s = (cbx < 0f && cay < 0f) ? -1f : 1f;
        float ca2 = cax * cax + cay * cay;
        float cb2 = cbx * cbx + cby * cby;
        return s * MathF.Sqrt(ca2 < cb2 ? ca2 : cb2);
    }

    public static void CollideSdf(
        ref float px,
        ref float py,
        ref float pz,
        ref float vx,
        ref float vy,
        ref float vz,
        ref uint alive,
        float cx,
        float cy,
        float cz,
        float sx,
        float sy,
        float sz,
        float shape,
        float mode,
        float bounce,
        float particleRadius = 0f)
    {
        if (alive == 0u || mode < 0.5f || shape < 0.5f)
            return;
        if (shape >= 2.5f && shape < 3.5f)
            return;
        float dist = SampleSdf(px, py, pz, cx, cy, cz, sx, sy, sz, shape) - particleRadius;
        if (dist >= 0f)
            return;
        if (mode < 1.5f)
        {
            alive = 0u;
            return;
        }

        const float e = 1e-3f;
        float nx = SampleSdf(px + e, py, pz, cx, cy, cz, sx, sy, sz, shape)
            - SampleSdf(px - e, py, pz, cx, cy, cz, sx, sy, sz, shape);
        float ny = SampleSdf(px, py + e, pz, cx, cy, cz, sx, sy, sz, shape)
            - SampleSdf(px, py - e, pz, cx, cy, cz, sx, sy, sz, shape);
        float nz = SampleSdf(px, py, pz + e, cx, cy, cz, sx, sy, sz, shape)
            - SampleSdf(px, py, pz - e, cx, cy, cz, sx, sy, sz, shape);
        float nlen2 = nx * nx + ny * ny + nz * nz;
        if (nlen2 <= 1e-10f)
        {
            nx = 0f;
            ny = 1f;
            nz = 0f;
        }
        else
        {
            float inv = 1f / MathF.Sqrt(nlen2);
            nx *= inv;
            ny *= inv;
            nz *= inv;
        }
        px -= nx * dist;
        py -= ny * dist;
        pz -= nz * dist;
        float vn = vx * nx + vy * ny + vz * nz;
        if (vn < 0f)
        {
            float k = (1f + bounce) * vn;
            vx -= k * nx;
            vy -= k * ny;
            vz -= k * nz;
        }
    }

    /// <summary>
    /// Writes a sphere SDF into a dense volume. Voxel (0,0,0) is the min corner of the
    /// box <c>center ± size</c>. Must match Update <c>VfxSampleVolumeSdf</c> mapping.
    /// </summary>
    public static void BakeSphereVolume(
        Span<float> voxels,
        int resolution,
        float sphereX,
        float sphereY,
        float sphereZ,
        float radius,
        float cx,
        float cy,
        float cz,
        float sx,
        float sy,
        float sz)
    {
        int res = resolution < 2 ? 2 : resolution;
        if (voxels.Length < res * res * res)
            throw new ArgumentException("voxels is smaller than resolution^3.", nameof(voxels));
        float hx = sx > 1e-5f ? sx : 1e-5f;
        float hy = sy > 1e-5f ? sy : 1e-5f;
        float hz = sz > 1e-5f ? sz : 1e-5f;
        float r = radius > 0f ? radius : 0f;
        for (int z = 0; z < res; z++)
        {
            float uz = (z + 0.5f) / res;
            float wz = (cz - hz) + uz * (2f * hz);
            for (int y = 0; y < res; y++)
            {
                float uy = (y + 0.5f) / res;
                float wy = (cy - hy) + uy * (2f * hy);
                int row = (z * res + y) * res;
                for (int x = 0; x < res; x++)
                {
                    float ux = (x + 0.5f) / res;
                    float wx = (cx - hx) + ux * (2f * hx);
                    float dx = wx - sphereX;
                    float dy = wy - sphereY;
                    float dz = wz - sphereZ;
                    voxels[row + x] = MathF.Sqrt(dx * dx + dy * dy + dz * dz) - r;
                }
            }
        }
    }

    /// <summary>
    /// Trilinear sample of a dense SDF volume. UV 0..1 maps across the box
    /// <c>center ± size</c>. Outside the box returns a large positive distance.
    /// Must match Update in <c>VfxParticles.shader</c>.
    /// </summary>
    public static float SampleVolumeSdf(
        float px,
        float py,
        float pz,
        float cx,
        float cy,
        float cz,
        float sx,
        float sy,
        float sz,
        ReadOnlySpan<float> voxels,
        int width,
        int height,
        int depth)
    {
        int w = width < 1 ? 1 : width;
        int h = height < 1 ? 1 : height;
        int d = depth < 1 ? 1 : depth;
        if (voxels.Length < w * h * d)
            return 1e6f;
        float hx = sx > 1e-5f ? sx : 1e-5f;
        float hy = sy > 1e-5f ? sy : 1e-5f;
        float hz = sz > 1e-5f ? sz : 1e-5f;
        float ux = (px - (cx - hx)) / (2f * hx);
        float uy = (py - (cy - hy)) / (2f * hy);
        float uz = (pz - (cz - hz)) / (2f * hz);
        if (ux < 0f || uy < 0f || uz < 0f || ux > 1f || uy > 1f || uz > 1f)
            return 1e6f;
        return SampleVolumeTexel(voxels, w, h, d, ux, uy, uz);
    }

    public static void CollideVolumeSdf(
        ref float px,
        ref float py,
        ref float pz,
        ref float vx,
        ref float vy,
        ref float vz,
        ref uint alive,
        float cx,
        float cy,
        float cz,
        float sx,
        float sy,
        float sz,
        float mode,
        float bounce,
        ReadOnlySpan<float> voxels,
        int width,
        int height,
        int depth,
        float particleRadius = 0f)
    {
        if (alive == 0u || mode < 0.5f)
            return;
        float dist = SampleVolumeSdf(px, py, pz, cx, cy, cz, sx, sy, sz, voxels, width, height, depth) - particleRadius;
        if (dist >= 0f)
            return;
        if (mode < 1.5f)
        {
            alive = 0u;
            return;
        }

        const float e = 1e-3f;
        float nx = SampleVolumeSdf(px + e, py, pz, cx, cy, cz, sx, sy, sz, voxels, width, height, depth)
            - SampleVolumeSdf(px - e, py, pz, cx, cy, cz, sx, sy, sz, voxels, width, height, depth);
        float ny = SampleVolumeSdf(px, py + e, pz, cx, cy, cz, sx, sy, sz, voxels, width, height, depth)
            - SampleVolumeSdf(px, py - e, pz, cx, cy, cz, sx, sy, sz, voxels, width, height, depth);
        float nz = SampleVolumeSdf(px, py, pz + e, cx, cy, cz, sx, sy, sz, voxels, width, height, depth)
            - SampleVolumeSdf(px, py, pz - e, cx, cy, cz, sx, sy, sz, voxels, width, height, depth);
        float nlen2 = nx * nx + ny * ny + nz * nz;
        if (nlen2 <= 1e-10f)
        {
            nx = 0f;
            ny = 1f;
            nz = 0f;
        }
        else
        {
            float inv = 1f / MathF.Sqrt(nlen2);
            nx *= inv;
            ny *= inv;
            nz *= inv;
        }
        px -= nx * dist;
        py -= ny * dist;
        pz -= nz * dist;
        float vn = vx * nx + vy * ny + vz * nz;
        if (vn < 0f)
        {
            float k = (1f + bounce) * vn;
            vx -= k * nx;
            vy -= k * ny;
            vz -= k * nz;
        }
    }

    private static float SampleVolumeTexel(
        ReadOnlySpan<float> voxels, int w, int h, int d, float ux, float uy, float uz)
    {
        float fx = ux * w - 0.5f;
        float fy = uy * h - 0.5f;
        float fz = uz * d - 0.5f;
        int x0 = (int)MathF.Floor(fx);
        int y0 = (int)MathF.Floor(fy);
        int z0 = (int)MathF.Floor(fz);
        float tx = fx - x0;
        float ty = fy - y0;
        float tz = fz - z0;
        return Lerp(
            Lerp(
                Lerp(Voxel(voxels, w, h, d, x0, y0, z0), Voxel(voxels, w, h, d, x0 + 1, y0, z0), tx),
                Lerp(Voxel(voxels, w, h, d, x0, y0 + 1, z0), Voxel(voxels, w, h, d, x0 + 1, y0 + 1, z0), tx),
                ty),
            Lerp(
                Lerp(Voxel(voxels, w, h, d, x0, y0, z0 + 1), Voxel(voxels, w, h, d, x0 + 1, y0, z0 + 1), tx),
                Lerp(Voxel(voxels, w, h, d, x0, y0 + 1, z0 + 1), Voxel(voxels, w, h, d, x0 + 1, y0 + 1, z0 + 1), tx),
                ty),
            tz);
    }

    private static float Voxel(ReadOnlySpan<float> voxels, int w, int h, int d, int x, int y, int z)
    {
        if (x < 0) x = 0;
        else if (x >= w) x = w - 1;
        if (y < 0) y = 0;
        else if (y >= h) y = h - 1;
        if (z < 0) z = 0;
        else if (z >= d) z = d - 1;
        return voxels[(z * h + y) * w + x];
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}

/// <summary>
/// Attract particles to an SDF isosurface. Must match Update in <c>VfxParticles.shader</c>.
/// <c>position -= n * dist * saturate(attraction * dt)</c>. Attraction &lt;= 0 is a no-op.
/// </summary>
public static class VfxConform
{
    public static void Apply(
        ref float px,
        ref float py,
        ref float pz,
        float cx,
        float cy,
        float cz,
        float sx,
        float sy,
        float sz,
        float shape,
        float attraction,
        float dt)
    {
        if (attraction <= 0f || shape < 0.5f)
            return;
        float dist = VfxCollision.SampleSdf(px, py, pz, cx, cy, cz, sx, sy, sz, shape);
        if (dist > 1e5f)
            return;
        const float e = 1e-3f;
        float nx = VfxCollision.SampleSdf(px + e, py, pz, cx, cy, cz, sx, sy, sz, shape)
            - VfxCollision.SampleSdf(px - e, py, pz, cx, cy, cz, sx, sy, sz, shape);
        float ny = VfxCollision.SampleSdf(px, py + e, pz, cx, cy, cz, sx, sy, sz, shape)
            - VfxCollision.SampleSdf(px, py - e, pz, cx, cy, cz, sx, sy, sz, shape);
        float nz = VfxCollision.SampleSdf(px, py, pz + e, cx, cy, cz, sx, sy, sz, shape)
            - VfxCollision.SampleSdf(px, py, pz - e, cx, cy, cz, sx, sy, sz, shape);
        float nlen2 = nx * nx + ny * ny + nz * nz;
        if (nlen2 <= 1e-10f)
        {
            nx = 0f;
            ny = 1f;
            nz = 0f;
        }
        else
        {
            float inv = 1f / MathF.Sqrt(nlen2);
            nx *= inv;
            ny *= inv;
            nz *= inv;
        }
        float k = attraction * dt;
        if (k > 1f)
            k = 1f;
        px -= nx * dist * k;
        py -= ny * dist * k;
        pz -= nz * dist * k;
    }
}

/// <summary>
/// Texture3D vector-field force. Must match Update in <c>VfxParticles.shader</c>.
/// Samples a signed vector over the box <c>center ± size</c> (same mapping as SDF volume)
/// and adds <c>sample * intensity * dt</c> to velocity. Intensity &lt;= 0 is a no-op.
/// GPU textures store UNORM RGB where 0.5 is zero; use <see cref="DecodeUnorm"/>.
/// </summary>
public static class VfxVectorField
{
    public static void DecodeUnorm(float r, float g, float b, out float x, out float y, out float z)
    {
        x = r * 2f - 1f;
        y = g * 2f - 1f;
        z = b * 2f - 1f;
    }

    public static void BakeConstant(Span<float> vectors, int resolution, float x, float y, float z)
    {
        int res = resolution < 1 ? 1 : resolution;
        int count = res * res * res;
        if (vectors.Length < count * 3)
            throw new ArgumentException("vectors is smaller than resolution^3 * 3.", nameof(vectors));
        for (int i = 0; i < count; i++)
        {
            int o = i * 3;
            vectors[o] = x;
            vectors[o + 1] = y;
            vectors[o + 2] = z;
        }
    }

    public static void Sample(
        float px,
        float py,
        float pz,
        float cx,
        float cy,
        float cz,
        float sx,
        float sy,
        float sz,
        ReadOnlySpan<float> vectors,
        int width,
        int height,
        int depth,
        out float vx,
        out float vy,
        out float vz)
    {
        vx = 0f;
        vy = 0f;
        vz = 0f;
        int w = width < 1 ? 1 : width;
        int h = height < 1 ? 1 : height;
        int d = depth < 1 ? 1 : depth;
        if (vectors.Length < w * h * d * 3)
            return;
        float hx = sx > 1e-5f ? sx : 1e-5f;
        float hy = sy > 1e-5f ? sy : 1e-5f;
        float hz = sz > 1e-5f ? sz : 1e-5f;
        float ux = (px - (cx - hx)) / (2f * hx);
        float uy = (py - (cy - hy)) / (2f * hy);
        float uz = (pz - (cz - hz)) / (2f * hz);
        if (ux < 0f || uy < 0f || uz < 0f || ux > 1f || uy > 1f || uz > 1f)
            return;
        SampleTexel(vectors, w, h, d, ux, uy, uz, out vx, out vy, out vz);
    }

    public static void Apply(
        ref float vx,
        ref float vy,
        ref float vz,
        float px,
        float py,
        float pz,
        float cx,
        float cy,
        float cz,
        float sx,
        float sy,
        float sz,
        float intensity,
        float dt,
        ReadOnlySpan<float> vectors,
        int width,
        int height,
        int depth)
    {
        if (intensity <= 0f || dt == 0f)
            return;
        Sample(px, py, pz, cx, cy, cz, sx, sy, sz, vectors, width, height, depth,
            out float fx, out float fy, out float fz);
        float k = intensity * dt;
        vx += fx * k;
        vy += fy * k;
        vz += fz * k;
    }

    private static void SampleTexel(
        ReadOnlySpan<float> vectors, int w, int h, int d, float ux, float uy, float uz,
        out float vx, out float vy, out float vz)
    {
        float fx = ux * w - 0.5f;
        float fy = uy * h - 0.5f;
        float fz = uz * d - 0.5f;
        int x0 = (int)MathF.Floor(fx);
        int y0 = (int)MathF.Floor(fy);
        int z0 = (int)MathF.Floor(fz);
        float tx = fx - x0;
        float ty = fy - y0;
        float tz = fz - z0;
        Voxel(vectors, w, h, d, x0, y0, z0, out float x000, out float y000, out float z000);
        Voxel(vectors, w, h, d, x0 + 1, y0, z0, out float x100, out float y100, out float z100);
        Voxel(vectors, w, h, d, x0, y0 + 1, z0, out float x010, out float y010, out float z010);
        Voxel(vectors, w, h, d, x0 + 1, y0 + 1, z0, out float x110, out float y110, out float z110);
        Voxel(vectors, w, h, d, x0, y0, z0 + 1, out float x001, out float y001, out float z001);
        Voxel(vectors, w, h, d, x0 + 1, y0, z0 + 1, out float x101, out float y101, out float z101);
        Voxel(vectors, w, h, d, x0, y0 + 1, z0 + 1, out float x011, out float y011, out float z011);
        Voxel(vectors, w, h, d, x0 + 1, y0 + 1, z0 + 1, out float x111, out float y111, out float z111);
        vx = Lerp(Lerp(Lerp(x000, x100, tx), Lerp(x010, x110, tx), ty), Lerp(Lerp(x001, x101, tx), Lerp(x011, x111, tx), ty), tz);
        vy = Lerp(Lerp(Lerp(y000, y100, tx), Lerp(y010, y110, tx), ty), Lerp(Lerp(y001, y101, tx), Lerp(y011, y111, tx), ty), tz);
        vz = Lerp(Lerp(Lerp(z000, z100, tx), Lerp(z010, z110, tx), ty), Lerp(Lerp(z001, z101, tx), Lerp(z011, z111, tx), ty), tz);
    }

    private static void Voxel(
        ReadOnlySpan<float> vectors, int w, int h, int d, int x, int y, int z,
        out float vx, out float vy, out float vz)
    {
        if (x < 0) x = 0;
        else if (x >= w) x = w - 1;
        if (y < 0) y = 0;
        else if (y >= h) y = h - 1;
        if (z < 0) z = 0;
        else if (z >= d) z = d - 1;
        int o = ((z * h + y) * w + x) * 3;
        vx = vectors[o];
        vy = vectors[o + 1];
        vz = vectors[o + 2];
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}

/// <summary>
/// Curl-of-value-noise turbulence. Must match Update in <c>VfxParticles.shader</c>.
/// Adds <c>curl(position * frequency) * intensity * dt</c> to velocity.
/// </summary>
public static class VfxTurbulence
{
    public static void Apply(
        ref float vx,
        ref float vy,
        ref float vz,
        float px,
        float py,
        float pz,
        float intensity,
        float frequency,
        float dt)
    {
        if (intensity <= 0f || dt == 0f)
            return;
        float freq = frequency > 1e-5f ? frequency : 1e-5f;
        Curl(px * freq, py * freq, pz * freq, out float cx, out float cy, out float cz);
        vx += cx * intensity * dt;
        vy += cy * intensity * dt;
        vz += cz * intensity * dt;
    }

    public static void Curl(float x, float y, float z, out float cx, out float cy, out float cz)
    {
        const float e = 0.15f;
        Potential(x, y + e, z, out float ypx, out _, out float ypz);
        Potential(x, y - e, z, out float ymx, out _, out float ymz);
        Potential(x, y, z + e, out float zpx, out float zpy, out _);
        Potential(x, y, z - e, out float zmx, out float zmy, out _);
        Potential(x + e, y, z, out _, out float xpy, out float xpz);
        Potential(x - e, y, z, out _, out float xmy, out float xmz);
        float inv = 1f / (2f * e);
        cx = (ypz - ymz - (zpy - zmy)) * inv;
        cy = (zpx - zmx - (xpz - xmz)) * inv;
        cz = (xpy - xmy - (ypx - ymx)) * inv;
    }

    private static void Potential(float x, float y, float z, out float px, out float py, out float pz)
    {
        px = Noise(x, y, z);
        py = Noise(x + 17.1f, y + 9.2f, z + 3.4f);
        pz = Noise(x + 31.7f, y + 5.8f, z + 13.3f);
    }

    private static float Noise(float x, float y, float z)
    {
        float ix = MathF.Floor(x);
        float iy = MathF.Floor(y);
        float iz = MathF.Floor(z);
        float fx = x - ix;
        float fy = y - iy;
        float fz = z - iz;
        float ux = fx * fx * (3f - 2f * fx);
        float uy = fy * fy * (3f - 2f * fy);
        float uz = fz * fz * (3f - 2f * fz);
        float n000 = Hash(ix, iy, iz);
        float n100 = Hash(ix + 1f, iy, iz);
        float n010 = Hash(ix, iy + 1f, iz);
        float n110 = Hash(ix + 1f, iy + 1f, iz);
        float n001 = Hash(ix, iy, iz + 1f);
        float n101 = Hash(ix + 1f, iy, iz + 1f);
        float n011 = Hash(ix, iy + 1f, iz + 1f);
        float n111 = Hash(ix + 1f, iy + 1f, iz + 1f);
        float n00 = n000 + (n100 - n000) * ux;
        float n10 = n010 + (n110 - n010) * ux;
        float n01 = n001 + (n101 - n001) * ux;
        float n11 = n011 + (n111 - n011) * ux;
        float n0 = n00 + (n10 - n00) * uy;
        float n1 = n01 + (n11 - n01) * uy;
        return n0 + (n1 - n0) * uz;
    }

    private static float Hash(float x, float y, float z)
    {
        float px = Frac(x * 0.1031f);
        float py = Frac(y * 0.1031f);
        float pz = Frac(z * 0.1031f);
        float d = px * (py + 33.33f) + py * (pz + 33.33f) + pz * (px + 33.33f);
        px += d;
        py += d;
        pz += d;
        return Frac((px + py) * pz);
    }

    private static float Frac(float v) => v - MathF.Floor(v);
}

/// <summary>
/// Output lighting. Must match Fragment in <c>VfxParticles.shader</c>.
/// Mode: 0 unlit, 1 Lambert, 2 Blinn-Phong. <paramref name="intensity"/> multiplies light RGB.
/// </summary>
public static class VfxLighting
{
    public const float ModeUnlit = 0f;
    public const float ModeLambert = 1f;
    public const float ModeBlinn = 2f;

    public static void Shade(
        float br,
        float bg,
        float bb,
        float ba,
        float nx,
        float ny,
        float nz,
        float vx,
        float vy,
        float vz,
        float lx,
        float ly,
        float lz,
        float cr,
        float cg,
        float cb,
        float intensity,
        float ar,
        float ag,
        float ab,
        float mode,
        float shininess,
        out float or,
        out float og,
        out float ob,
        out float oa)
    {
        oa = ba;
        if (mode < 0.5f)
        {
            or = br;
            og = bg;
            ob = bb;
            return;
        }

        float nlen2 = nx * nx + ny * ny + nz * nz;
        if (nlen2 > 1e-8f)
        {
            float inv = 1f / MathF.Sqrt(nlen2);
            nx *= inv;
            ny *= inv;
            nz *= inv;
        }
        float llen2 = lx * lx + ly * ly + lz * lz;
        if (llen2 > 1e-8f)
        {
            float inv = 1f / MathF.Sqrt(llen2);
            lx *= inv;
            ly *= inv;
            lz *= inv;
        }

        float nDotL = nx * lx + ny * ly + nz * lz;
        if (nDotL < 0f)
            nDotL = 0f;
        float li = intensity;
        or = br * (ar + cr * li * nDotL);
        og = bg * (ag + cg * li * nDotL);
        ob = bb * (ab + cb * li * nDotL);
        if (mode < 1.5f)
            return;

        float vlen2 = vx * vx + vy * vy + vz * vz;
        if (vlen2 > 1e-8f)
        {
            float inv = 1f / MathF.Sqrt(vlen2);
            vx *= inv;
            vy *= inv;
            vz *= inv;
        }
        float hx = lx + vx;
        float hy = ly + vy;
        float hz = lz + vz;
        float hlen2 = hx * hx + hy * hy + hz * hz;
        if (hlen2 > 1e-8f)
        {
            float inv = 1f / MathF.Sqrt(hlen2);
            hx *= inv;
            hy *= inv;
            hz *= inv;
        }
        float nDotH = nx * hx + ny * hy + nz * hz;
        if (nDotH < 0f)
            nDotH = 0f;
        float shiny = shininess < 1f ? 1f : shininess;
        float spec = MathF.Pow(nDotH, shiny) * li;
        or += cr * spec;
        og += cg * spec;
        ob += cb * spec;
    }
}

/// <summary>
/// Camera-depth collision. Must match Update in <c>VfxParticles.shader</c>.
/// Projects with a fixed look-at camera, samples scene eye Z, then Kill/Bounce
/// when <c>particleEye &gt; sceneEye - offset</c>. Mode: 0 off, 1 kill, 2 bounce.
/// </summary>
public static class VfxDepthCollision
{
    public const float CamX = 0f;
    public const float CamY = 1.2f;
    public const float CamZ = 3.2f;
    public const float TargetX = 0f;
    public const float TargetY = 0.4f;
    public const float TargetZ = 0f;
    public const float Near = 0.1f;
    public const float Far = 100f;
    public const float TanHalfFov = 0.57735027f;
    public const float Aspect = 1.7777778f;
    public const float Offset = 0.05f;
    public const float ModeOff = 0f;
    public const float ModeKill = 1f;
    public const float ModeBounce = 2f;

    public static void Basis(
        out float fx, out float fy, out float fz,
        out float sx, out float sy, out float sz,
        out float ux, out float uy, out float uz)
    {
        fx = TargetX - CamX;
        fy = TargetY - CamY;
        fz = TargetZ - CamZ;
        float flen2 = fx * fx + fy * fy + fz * fz;
        float finv = flen2 > 1e-12f ? 1f / MathF.Sqrt(flen2) : 1f;
        fx *= finv;
        fy *= finv;
        fz *= finv;
        sx = fy * 0f - fz * 1f;
        sy = fz * 0f - fx * 0f;
        sz = fx * 1f - fy * 0f;
        float slen2 = sx * sx + sy * sy + sz * sz;
        if (slen2 <= 1e-12f)
        {
            sx = 1f;
            sy = 0f;
            sz = 0f;
        }
        else
        {
            float sinv = 1f / MathF.Sqrt(slen2);
            sx *= sinv;
            sy *= sinv;
            sz *= sinv;
        }
        ux = sy * fz - sz * fy;
        uy = sz * fx - sx * fz;
        uz = sx * fy - sy * fx;
    }

    public static bool TryProject(
        float px, float py, float pz, out float u, out float v, out float particleEye)
    {
        u = 0f;
        v = 0f;
        particleEye = 0f;
        Basis(out float fx, out float fy, out float fz, out float sx, out float sy, out float sz,
            out float ux, out float uy, out float uz);
        float rx = px - CamX;
        float ry = py - CamY;
        float rz = pz - CamZ;
        float z = rx * fx + ry * fy + rz * fz;
        if (z <= Near)
            return false;
        float x = rx * sx + ry * sy + rz * sz;
        float y = rx * ux + ry * uy + rz * uz;
        u = 0.5f + 0.5f * (x / (z * TanHalfFov * Aspect));
        v = 0.5f + 0.5f * (y / (z * TanHalfFov));
        particleEye = z;
        return u >= 0f && u <= 1f && v >= 0f && v <= 1f;
    }

    public static float LinearEyeDepth(float raw)
    {
        float clipZ = raw * 2f - 1f;
        return (2f * Near * Far) / (Far + Near - clipZ * (Far - Near));
    }

    public static float RawFromEye(float eye)
    {
        float e = eye < Near ? Near : eye;
        float clipZ = (2f * Near * Far / e - Far - Near) / -(Far - Near);
        return (clipZ + 1f) * 0.5f;
    }

    public static void Collide(
        ref float px,
        ref float py,
        ref float pz,
        ref float vx,
        ref float vy,
        ref float vz,
        ref uint alive,
        float sceneRaw,
        float mode,
        float bounce)
    {
        if (alive == 0u || mode < 0.5f)
            return;
        if (!TryProject(px, py, pz, out _, out _, out float particleEye))
            return;
        float sceneEye = LinearEyeDepth(sceneRaw);
        float dist = sceneEye - Offset - particleEye;
        if (dist >= 0f)
            return;
        if (mode < 1.5f)
        {
            alive = 0u;
            return;
        }
        float nx = CamX - px;
        float ny = CamY - py;
        float nz = CamZ - pz;
        float nlen2 = nx * nx + ny * ny + nz * nz;
        if (nlen2 <= 1e-10f)
        {
            nx = 0f;
            ny = 1f;
            nz = 0f;
        }
        else
        {
            float inv = 1f / MathF.Sqrt(nlen2);
            nx *= inv;
            ny *= inv;
            nz *= inv;
        }
        px -= nx * dist;
        py -= ny * dist;
        pz -= nz * dist;
        float vn = vx * nx + vy * ny + vz * nz;
        if (vn < 0f)
        {
            float k = (1f + bounce) * vn;
            vx -= k * nx;
            vy -= k * ny;
            vz -= k * nz;
        }
    }
}

/// <summary>
/// Camera-facing particle strip. Must match Output in <c>VfxParticles.shader</c>.
/// Vertex sits at <c>lerp(p0, p1, along01) + side * localX * width</c>,
/// where <c>side = normalize(cross(p1 - p0, camera - midpoint))</c>.
/// </summary>
public static class VfxStrip
{
    public static void Extrude(
        float p0x,
        float p0y,
        float p0z,
        float p1x,
        float p1y,
        float p1z,
        float localX,
        float along01,
        float width,
        float camX,
        float camY,
        float camZ,
        out float ox,
        out float oy,
        out float oz)
    {
        float mx = p0x + (p1x - p0x) * along01;
        float my = p0y + (p1y - p0y) * along01;
        float mz = p0z + (p1z - p0z) * along01;
        float ax = p1x - p0x;
        float ay = p1y - p0y;
        float az = p1z - p0z;
        float tx = camX - mx;
        float ty = camY - my;
        float tz = camZ - mz;
        float sx = ay * tz - az * ty;
        float sy = az * tx - ax * tz;
        float sz = ax * ty - ay * tx;
        float slen2 = sx * sx + sy * sy + sz * sz;
        if (slen2 <= 1e-10f)
        {
            sx = 1f;
            sy = 0f;
            sz = 0f;
        }
        else
        {
            float inv = 1f / MathF.Sqrt(slen2);
            sx *= inv;
            sy *= inv;
            sz *= inv;
        }
        float k = localX * width;
        ox = mx + sx * k;
        oy = my + sy * k;
        oz = mz + sz * k;
    }
}

/// <summary>
/// GPU Event OnDie / OnCollide / Always / OnPlay. Must match Update in <c>VfxParticles.shader</c>.
/// Kind: 0 off, 1 OnDie, 2 OnCollide, 3 Always, 4 OnPlay.
/// Count is the event units (and OnDie child spawns, capped at 8). Always / OnPlay are CPU-side.
/// </summary>
public static class VfxGpuEvent
{
    public const float KindOff = 0f;
    public const float KindOnDie = 1f;
    public const float KindOnCollide = 2f;
    public const float KindAlways = 3f;
    public const float KindOnPlay = 4f;
    public const uint MaxChildSpawn = 8;

    public static uint EmitCount(float kind, float count, bool died, bool collided)
    {
        if (count < 0.5f)
            return 0;
        if (kind >= 0.5f && kind < 1.5f && died)
            return (uint)count;
        if (kind >= 1.5f && kind < 2.5f && collided)
            return (uint)count;
        return 0;
    }

    public static uint EmitAlways(float kind, float count)
    {
        if (count < 0.5f)
            return 0;
        if (kind >= 2.5f && kind < 3.5f)
            return (uint)count;
        return 0;
    }

    public static uint EmitOnPlay(float kind, float count)
    {
        if (count < 0.5f)
            return 0;
        if (kind >= 3.5f)
            return (uint)count;
        return 0;
    }

    public static uint ChildSpawnCount(float kind, float count, bool died, bool collided)
    {
        uint n = EmitCount(kind, count, died, collided);
        if (n == 0 || !died)
            return 0;
        return n > MaxChildSpawn ? MaxChildSpawn : n;
    }

    public static bool HasTarget(string? name)
        => !string.IsNullOrWhiteSpace(name);

    public static uint QueueTarget(ref uint pending, uint count, int capacity)
    {
        if (count == 0)
            return 0;
        uint cap = capacity < 0 ? 0u : (uint)capacity;
        uint room = cap > pending ? cap - pending : 0u;
        uint added = count > room ? room : count;
        pending += added;
        return added;
    }

    public static uint Consume(ref uint pending)
    {
        uint n = pending;
        pending = 0;
        return n;
    }
}

/// <summary>
/// Soft-particle depth fade. Must match Output in <c>VfxParticles.shader</c>.
/// <c>alpha *= saturate((sceneEyeZ - particleEyeZ) / fadeDistance)</c>. Fade &lt;= 0 is a no-op.
/// </summary>
/// <summary>
/// Unity Distortion / heat haze. Must match Output in <c>VfxParticles.shader</c>.
/// Intensity &lt;= 0 is a no-op. Offset is <c>(source * 2 - 1) * intensity</c> on screen UV.
/// </summary>
public static class VfxDistortion
{
    public static void OffsetUv(
        float u, float v, float intensity, float sourceU, float sourceV,
        out float ou, out float ov)
    {
        if (intensity <= 0f)
        {
            ou = u;
            ov = v;
            return;
        }
        ou = u + (sourceU * 2f - 1f) * intensity;
        ov = v + (sourceV * 2f - 1f) * intensity;
        if (ou < 0f) ou = 0f;
        else if (ou > 1f) ou = 1f;
        if (ov < 0f) ov = 0f;
        else if (ov > 1f) ov = 1f;
    }

    public static void Apply(
        ref float r, ref float g, ref float b,
        float sceneR, float sceneG, float sceneB,
        float alpha, float intensity)
    {
        if (intensity <= 0f)
            return;
        float w = intensity;
        if (w > 1f) w = 1f;
        float a = alpha;
        if (a < 0f) a = 0f;
        else if (a > 1f) a = 1f;
        w *= a;
        r += (sceneR - r) * w;
        g += (sceneG - g) * w;
        b += (sceneB - b) * w;
    }
}

/// <summary>
/// Unity Sort. Back-to-front camera-distance order of live indices.
/// Must match Pass Sort in <c>VfxParticles.shader</c>. Mode &lt;= 0 is a no-op.
/// Sort bit packs into <c>indexCount</c> bits 24–31 so <see cref="VfxSimParams"/> stays 160 bytes.
/// </summary>
public static class VfxSort
{
    public const float ModeOff = 0f;
    public const float ModeCamera = 1f;

    public static bool IsEnabled(float sortMode) => sortMode >= 0.5f;

    public static bool IsEnabled(uint packedIndexCount) => (packedIndexCount >> 24) != 0u;

    public static uint PackIndexCount(uint indices, uint outputMode, float sortMode)
    {
        uint idx = indices < 3u ? 3u : indices;
        if (idx > 0xffffu)
            idx = 0xffffu;
        uint mode = outputMode & 0xffu;
        uint sort = sortMode >= 0.5f ? 1u : 0u;
        return idx | (mode << 16) | (sort << 24);
    }

    public static uint NextPow2(uint n)
    {
        if (n <= 1u)
            return 1u;
        n--;
        n |= n >> 1;
        n |= n >> 2;
        n |= n >> 4;
        n |= n >> 8;
        n |= n >> 16;
        return n + 1u;
    }

    public static int BitonicPassCount(uint capacity)
    {
        uint size = NextPow2(capacity < 1u ? 1u : capacity);
        int passes = 0;
        for (uint k = 2u; k <= size; k <<= 1)
        {
            for (uint j = k >> 1; j > 0u; j >>= 1)
                passes++;
        }
        return passes;
    }

    public static float DistanceSq(float px, float py, float pz, float cx, float cy, float cz)
    {
        float dx = px - cx;
        float dy = py - cy;
        float dz = pz - cz;
        return dx * dx + dy * dy + dz * dz;
    }

    /// <summary>Descending distance (far first). Matches HLSL bitonic polarity.</summary>
    public static bool BitonicShouldSwap(uint i, uint k, float d2i, float d2j)
    {
        bool ascending = (i & k) == 0u;
        return ascending ? d2i < d2j : d2i > d2j;
    }

    public static void SortLiveByCamera(
        Span<uint> live,
        int count,
        ReadOnlySpan<float> px,
        ReadOnlySpan<float> py,
        ReadOnlySpan<float> pz,
        float cx, float cy, float cz)
    {
        if (count <= 1)
            return;
        for (int i = 1; i < count; i++)
        {
            uint key = live[i];
            float keyD = DistanceSq(px[(int)key], py[(int)key], pz[(int)key], cx, cy, cz);
            int j = i;
            while (j > 0)
            {
                uint prev = live[j - 1];
                float prevD = DistanceSq(px[(int)prev], py[(int)prev], pz[(int)prev], cx, cy, cz);
                if (prevD >= keyD)
                    break;
                live[j] = prev;
                j--;
            }
            live[j] = key;
        }
    }
}

/// <summary>
/// Unity Output Particle Decal. Projects a particle onto plane <c>n·x + d = 0</c>.
/// Must match Output <c>VfxDecalProject</c> in <c>VfxParticles.shader</c>.
/// Degenerate normals fall back to +Y.
/// </summary>
public static class VfxDecal
{
    public const float Bias = 0.01f;

    public static bool Project(
        float px, float py, float pz,
        float nx, float ny, float nz, float d,
        out float qx, out float qy, out float qz,
        out float onx, out float ony, out float onz)
    {
        float n2 = nx * nx + ny * ny + nz * nz;
        if (n2 <= 1e-10f)
        {
            qx = px;
            qy = -d;
            qz = pz;
            onx = 0f;
            ony = 1f;
            onz = 0f;
            return false;
        }

        float dist = (px * nx + py * ny + pz * nz + d) / n2;
        qx = px - nx * dist;
        qy = py - ny * dist;
        qz = pz - nz * dist;
        float inv = 1f / MathF.Sqrt(n2);
        onx = nx * inv;
        ony = ny * inv;
        onz = nz * inv;
        return true;
    }

    public static void Extrude(
        float qx, float qy, float qz,
        float onx, float ony, float onz,
        float localX, float localY, float size,
        out float wx, out float wy, out float wz)
    {
        float tx = onz;
        float ty = 0f;
        float tz = -onx;
        float t2 = tx * tx + ty * ty + tz * tz;
        if (t2 <= 1e-10f)
        {
            tx = 0f;
            ty = onz;
            tz = -ony;
            t2 = tx * tx + ty * ty + tz * tz;
        }

        float inv = 1f / MathF.Sqrt(t2);
        tx *= inv;
        ty *= inv;
        tz *= inv;
        float bx = ony * tz - onz * ty;
        float by = onz * tx - onx * tz;
        float bz = onx * ty - ony * tx;
        wx = qx + tx * (localX * size) + bx * (localY * size) + onx * Bias;
        wy = qy + ty * (localX * size) + by * (localY * size) + ony * Bias;
        wz = qz + tz * (localX * size) + bz * (localY * size) + onz * Bias;
    }
}

/// <summary>
/// Unity Output Particle Light. Host-safe ballistic proxies matching Init <c>HashDir</c>.
/// Intensity or count &lt;= 0 is a no-op. Count clamps to <see cref="MaxLights"/>.
/// </summary>
public static class VfxParticleLight
{
    public const int MaxLights = 16;

    public static int ResolveCount(float count)
    {
        if (count <= 0f)
            return 0;
        int n = (int)count;
        if (n > MaxLights)
            n = MaxLights;
        return n;
    }

    public static uint Hash(uint x)
    {
        x ^= x >> 16;
        x *= 0x7feb352du;
        x ^= x >> 15;
        x *= 0x846ca68bu;
        x ^= x >> 16;
        return x;
    }

    public static void HashDir(uint n, out float dx, out float dy, out float dz)
    {
        uint h = Hash(n);
        float z = (h & 0xffffu) / 32767.5f - 1f;
        float a = ((h >> 16) & 0xffffu) / 65535f * 6.2831853f;
        float r = MathF.Sqrt(Math.Max(0f, 1f - z * z));
        dx = MathF.Cos(a) * r;
        dy = z;
        dz = MathF.Sin(a) * r;
    }

    public static bool TryEvaluate(
        uint slot,
        int count,
        float ex, float ey, float ez,
        float startSpeed,
        float gx, float gy, float gz,
        float lifetime,
        float cr, float cg, float cb,
        float intensity,
        float range,
        out float px, out float py, out float pz,
        out float or, out float og, out float ob,
        out float outIntensity, out float outRange)
    {
        px = ex;
        py = ey;
        pz = ez;
        or = cr;
        og = cg;
        ob = cb;
        outIntensity = 0f;
        outRange = 0f;
        if (intensity <= 0f || count <= 0 || slot >= (uint)count)
            return false;
        float life = lifetime < 0.01f ? 0.01f : lifetime;
        float t = life * 0.4f;
        HashDir(slot, out float dx, out float dy, out float dz);
        float halfT2 = 0.5f * t * t;
        px = ex + dx * startSpeed * t + gx * halfT2;
        py = ey + dy * startSpeed * t + gy * halfT2;
        pz = ez + dz * startSpeed * t + gz * halfT2;
        outIntensity = intensity;
        outRange = range > 0f ? range : 0f;
        return true;
    }
}

/// <summary>
/// Unity Position (Sphere / Box / Cone / Circle). Must match Init
/// <c>VfxPositionShape</c>. Mode &lt;= 0 is a no-op.
/// </summary>
public static class VfxPositionShape
{
    public static void Offset(
        uint slot, float mode, float sx, float sy, float sz,
        out float x, out float y, out float z)
    {
        x = 0f;
        y = 0f;
        z = 0f;
        if (mode <= 0f)
            return;
        if (mode < 1.5f)
        {
            VfxParticleLight.HashDir(slot + 91u, out float dx, out float dy, out float dz);
            float u = (VfxParticleLight.Hash(slot + 7u) & 0xffffu) / 65535f;
            float r = sx * MathF.Pow(u < 0f ? 0f : u, 0.3333333f);
            x = dx * r;
            y = dy * r;
            z = dz * r;
            return;
        }
        if (mode < 2.5f)
        {
            uint h = VfxParticleLight.Hash(slot + 13u);
            x = ((h & 0xffu) / 127.5f - 1f) * sx;
            y = (((h >> 8) & 0xffu) / 127.5f - 1f) * sy;
            z = (((h >> 16) & 0xffu) / 127.5f - 1f) * sz;
            return;
        }
        if (mode < 3.5f)
        {
            float t = (VfxParticleLight.Hash(slot + 19u) & 0xffffu) / 65535f;
            float ang = ((VfxParticleLight.Hash(slot + 23u) >> 16) & 0xffffu) / 65535f * 6.2831853f;
            float rad = sx * t;
            x = MathF.Cos(ang) * rad;
            y = t * sy;
            z = MathF.Sin(ang) * rad;
            return;
        }

        float cang = ((VfxParticleLight.Hash(slot + 29u) >> 16) & 0xffffu) / 65535f * 6.2831853f;
        float cr = sx * ((VfxParticleLight.Hash(slot + 31u) & 0xffffu) / 65535f);
        x = MathF.Cos(cang) * cr;
        y = 0f;
        z = MathF.Sin(cang) * cr;
    }
}

/// <summary>
/// Unity Sequential position. Must match Init <c>VfxSequential</c>.
/// Mode &lt;= 0 is a no-op. 1 = XZ circle, 2 = line X, else line Y.
/// Sz is the point count.
/// </summary>
public static class VfxSequential
{
    public static void Offset(
        uint slot, float mode, float sx, float sy, float sz,
        out float x, out float y, out float z)
    {
        x = 0f;
        y = 0f;
        z = 0f;
        if (mode <= 0f)
            return;
        float n = sz < 1f ? 1f : sz;
        uint count = (uint)n;
        if (count < 1u)
            count = 1u;
        uint idx = slot % count;
        float t = count <= 1u ? 0f : idx / (float)(count - 1u);
        if (mode < 1.5f)
        {
            float ang = idx * (6.2831853f / n);
            x = MathF.Cos(ang) * sx;
            z = MathF.Sin(ang) * sx;
            return;
        }

        float along = (t * 2f) - 1f;
        if (mode < 2.5f)
            x = along * sx;
        else
            y = along * sy;
    }
}

/// <summary>
/// Unity Linear Drag. Must match Update <c>VfxDrag</c>. Amount &lt;= 0 is a no-op.
/// </summary>
public static class VfxDrag
{
    public static void Apply(ref float vx, ref float vy, ref float vz, float amount, float dt)
    {
        if (amount <= 0f)
            return;
        float w = MathF.Exp(-amount * dt);
        vx *= w;
        vy *= w;
        vz *= w;
    }
}

/// <summary>
/// Unity Force. Must match Update <c>VfxForce</c>. Zero force is a no-op.
/// </summary>
public static class VfxForce
{
    public static void Apply(ref float vx, ref float vy, ref float vz, float fx, float fy, float fz, float dt)
    {
        if (fx == 0f && fy == 0f && fz == 0f)
            return;
        vx += fx * dt;
        vy += fy * dt;
        vz += fz * dt;
    }
}

/// <summary>
/// Unity Angular Velocity. Integrates <c>pad0</c> and spins local XY.
/// Must match Update <c>VfxAngularVelocity</c> and Output <c>VfxSpinLocal</c>.
/// Rate 0 leaves angle unchanged; angle 0 is a no-op spin.
/// </summary>
public static class VfxSpin
{
    public static void Integrate(ref float angle, float rate, float dt)
    {
        angle += rate * dt;
    }

    public static void Local(float lx, float ly, float angle, out float sx, out float sy)
    {
        if (angle == 0f)
        {
            sx = lx;
            sy = ly;
            return;
        }

        float c = MathF.Cos(angle);
        float s = MathF.Sin(angle);
        sx = lx * c - ly * s;
        sy = lx * s + ly * c;
    }
}

/// <summary>
/// Unity Custom HLSL. Empty / whitespace code is a no-op.
/// </summary>
public static class VfxCustomHlsl
{
    public static bool HasCode(string? code)
        => !string.IsNullOrWhiteSpace(code);
}

/// <summary>
/// Age 0–1 mix for ColorOverLife / SizeOverLife. Must match Update compiler emission
/// (<c>from + (to - from) * t</c>, no <c>lerp</c>).
/// </summary>
public static class VfxOverLife
{
    public static float Saturate01(float t)
    {
        if (t < 0f) return 0f;
        if (t > 1f) return 1f;
        return t;
    }

    public static void Color(
        float t,
        float fr, float fg, float fb, float fa,
        float tr, float tg, float tb, float ta,
        out float r, out float g, out float b, out float a)
    {
        t = Saturate01(t);
        r = fr + (tr - fr) * t;
        g = fg + (tg - fg) * t;
        b = fb + (tb - fb) * t;
        a = fa + (ta - fa) * t;
    }

    public static float Size(float current, float t, float from, float to)
    {
        t = Saturate01(t);
        return current * (from + (to - from) * t);
    }
}

/// <summary>
/// Unity Size over Speed. Must match Update compiler emission
/// (<c>particle.size = particle.size * (1.0 + length(velocity) * amount)</c>).
/// Amount &lt;= 0 is a no-op.
/// </summary>
public static class VfxSizeOverSpeed
{
    public static float Apply(float size, float vx, float vy, float vz, float amount)
    {
        if (amount <= 0f)
            return size;
        return size * (1f + MathF.Sqrt(vx * vx + vy * vy + vz * vz) * amount);
    }
}

/// <summary>
/// Multiply Size. Scales particle size by Amount.
/// Amount == 1 is a no-op. Must match Update <c>VfxMultiplySize</c>.
/// </summary>
public static class VfxMultiplySize
{
    public static float Apply(float size, float amount)
    {
        if (amount == 1f)
            return size;
        return size * amount;
    }
}

/// <summary>
/// Multiply Color. Scales particle RGBA by a constant color.
/// Identity (1,1,1,1) is a no-op. Must match Update <c>VfxMultiplyColor</c>.
/// </summary>
public static class VfxMultiplyColor
{
    public static void Apply(
        float r, float g, float b, float a,
        float mr, float mg, float mb, float ma,
        out float or, out float og, out float ob, out float oa)
    {
        if (mr == 1f && mg == 1f && mb == 1f && ma == 1f)
        {
            or = r;
            og = g;
            ob = b;
            oa = a;
            return;
        }

        or = r * mr;
        og = g * mg;
        ob = b * mb;
        oa = a * ma;
    }
}

/// <summary>
/// Add Size. Adds Amount to particle size.
/// Amount == 0 is a no-op. Must match Update <c>VfxAddSize</c>.
/// </summary>
public static class VfxAddSize
{
    public static float Apply(float size, float amount)
    {
        if (amount == 0f)
            return size;
        return size + amount;
    }
}

/// <summary>
/// Add Color. Adds a constant RGBA to particle color.
/// Identity (0,0,0,0) is a no-op. Must match Update <c>VfxAddColor</c>.
/// </summary>
public static class VfxAddColor
{
    public static void Apply(
        float r, float g, float b, float a,
        float ar, float ag, float ab, float aa,
        out float or, out float og, out float ob, out float oa)
    {
        if (ar == 0f && ag == 0f && ab == 0f && aa == 0f)
        {
            or = r;
            og = g;
            ob = b;
            oa = a;
            return;
        }

        or = r + ar;
        og = g + ag;
        ob = b + ab;
        oa = a + aa;
    }
}

/// <summary>
/// Add Velocity. Adds a constant XYZ to particle velocity (not scaled by dt).
/// Zero vector is a no-op. Must match Update <c>VfxAddVelocity</c>.
/// </summary>
public static class VfxAddVelocity
{
    public static void Apply(ref float vx, ref float vy, ref float vz, float ax, float ay, float az)
    {
        if (ax == 0f && ay == 0f && az == 0f)
            return;
        vx += ax;
        vy += ay;
        vz += az;
    }
}

/// <summary>
/// Multiply Velocity. Scales particle velocity by Amount.
/// Amount == 1 is a no-op. Must match Update <c>VfxMultiplyVelocity</c>.
/// </summary>
public static class VfxMultiplyVelocity
{
    public static void Apply(ref float vx, ref float vy, ref float vz, float amount)
    {
        if (amount == 1f)
            return;
        vx *= amount;
        vy *= amount;
        vz *= amount;
    }
}

/// <summary>
/// Set Size. Overwrites particle size with Amount.
/// Amount == 0 is a no-op (use MultiplySize 0 for a true zero). Must match Update <c>VfxSetSize</c>.
/// </summary>
public static class VfxSetSize
{
    public static float Apply(float size, float amount)
    {
        if (amount == 0f)
            return size;
        return amount;
    }
}

/// <summary>
/// Set Color. Overwrites particle RGBA with Amount.
/// Identity (0,0,0,0) is a no-op (use MultiplyColor 0 for a true zero). Must match Update <c>VfxSetColor</c>.
/// </summary>
public static class VfxSetColor
{
    public static void Apply(
        float r, float g, float b, float a,
        float ar, float ag, float ab, float aa,
        out float or, out float og, out float ob, out float oa)
    {
        if (ar == 0f && ag == 0f && ab == 0f && aa == 0f)
        {
            or = r;
            og = g;
            ob = b;
            oa = a;
            return;
        }

        or = ar;
        og = ag;
        ob = ab;
        oa = aa;
    }
}

/// <summary>
/// Set Velocity. Overwrites particle velocity with XYZ (not scaled by dt).
/// Zero vector is a no-op (use MultiplyVelocity 0 for a true zero). Must match Update <c>VfxSetVelocity</c>.
/// </summary>
public static class VfxSetVelocity
{
    public static void Apply(ref float vx, ref float vy, ref float vz, float ax, float ay, float az)
    {
        if (ax == 0f && ay == 0f && az == 0f)
            return;
        vx = ax;
        vy = ay;
        vz = az;
    }
}

/// <summary>
/// Set Lifetime. Overwrites particle lifetime with Amount.
/// Amount == 0 is a no-op. Must match Update <c>VfxSetLifetime</c>.
/// </summary>
public static class VfxSetLifetime
{
    public static float Apply(float lifetime, float amount)
    {
        if (amount == 0f)
            return lifetime;
        return amount;
    }
}

/// <summary>
/// Add Lifetime. Adds Amount to particle lifetime.
/// Amount == 0 is a no-op. Must match Update <c>VfxAddLifetime</c>.
/// </summary>
public static class VfxAddLifetime
{
    public static float Apply(float lifetime, float amount)
    {
        if (amount == 0f)
            return lifetime;
        return lifetime + amount;
    }
}

/// <summary>
/// Multiply Lifetime. Scales particle lifetime by Amount.
/// Amount == 1 is a no-op. Must match Update <c>VfxMultiplyLifetime</c>.
/// </summary>
public static class VfxMultiplyLifetime
{
    public static float Apply(float lifetime, float amount)
    {
        if (amount == 1f)
            return lifetime;
        return lifetime * amount;
    }
}

/// <summary>
/// Add Age. Adds Amount to particle age.
/// Amount == 0 is a no-op. Must match Update <c>VfxAddAge</c>.
/// </summary>
public static class VfxAddAge
{
    public static float Apply(float age, float amount)
    {
        if (amount == 0f)
            return age;
        return age + amount;
    }
}

/// <summary>
/// Multiply Age. Scales particle age by Amount.
/// Amount == 1 is a no-op. Must match Update <c>VfxMultiplyAge</c>.
/// </summary>
public static class VfxMultiplyAge
{
    public static float Apply(float age, float amount)
    {
        if (amount == 1f)
            return age;
        return age * amount;
    }
}

/// <summary>
/// Set Age. Overwrites particle age with Amount.
/// Amount == 0 is a no-op. Must match Update <c>VfxSetAge</c>.
/// </summary>
public static class VfxSetAge
{
    public static float Apply(float age, float amount)
    {
        if (amount == 0f)
            return age;
        return amount;
    }
}

/// <summary>
/// Add Position. Adds a constant XYZ to particle position (not scaled by dt).
/// Zero vector is a no-op. Must match Update <c>VfxAddPosition</c>.
/// </summary>
public static class VfxAddPosition
{
    public static void Apply(ref float px, ref float py, ref float pz, float ax, float ay, float az)
    {
        if (ax == 0f && ay == 0f && az == 0f)
            return;
        px += ax;
        py += ay;
        pz += az;
    }
}

/// <summary>
/// Set Position. Overwrites particle position with a constant XYZ.
/// Init owns <c>PositionShape</c>, not this name. Zero vector is a no-op
/// (use AddPosition with a canceling offset for a true origin).
/// Must match Update <c>VfxSetPosition</c>.
/// </summary>
public static class VfxSetPosition
{
    public static void Apply(ref float px, ref float py, ref float pz, float ax, float ay, float az)
    {
        if (ax == 0f && ay == 0f && az == 0f)
            return;
        px = ax;
        py = ay;
        pz = az;
    }
}

/// <summary>
/// Unity Color over Speed. Must match Update compiler emission
/// (<c>from + (to - from) * saturate(length(velocity) / scale)</c>, no <c>lerp</c>).
/// Scale &lt;= 0 is a no-op.
/// </summary>
public static class VfxColorOverSpeed
{
    public static void Apply(
        float vx, float vy, float vz, float scale,
        float fr, float fg, float fb, float fa,
        float tr, float tg, float tb, float ta,
        out float r, out float g, out float b, out float a)
    {
        if (scale <= 0f)
        {
            r = fr;
            g = fg;
            b = fb;
            a = fa;
            return;
        }
        float speed = MathF.Sqrt(vx * vx + vy * vy + vz * vz);
        VfxOverLife.Color(speed / scale, fr, fg, fb, fa, tr, tg, tb, ta, out r, out g, out b, out a);
    }
}

/// <summary>
/// Output screen-space size. Amount is the reference camera distance at which
/// authored world size is unchanged. Amount &lt;= 0 is a no-op. Must match
/// Output <c>vfxScreenRef</c> (<c>size *= camDist / Amount</c>).
/// </summary>
public static class VfxScreenSpaceSize
{
    public static float Apply(float size, float camDx, float camDy, float camDz, float amount)
    {
        if (amount <= 0f)
            return size;
        float dist = MathF.Sqrt(camDx * camDx + camDy * camDy + camDz * camDz);
        return size * (dist / MathF.Max(amount, 1e-5f));
    }
}

/// <summary>
/// Collision friction. Scales the tangent of velocity after a bounce.
/// Amount 0 is a no-op; 1 removes all tangent. Must match Update
/// <c>VfxCollisionFriction</c>.
/// </summary>
public static class VfxCollisionFriction
{
    public static void Apply(
        ref float vx, ref float vy, ref float vz,
        float nx, float ny, float nz,
        float amount)
    {
        if (amount <= 0f)
            return;
        float n2 = nx * nx + ny * ny + nz * nz;
        if (n2 <= 1e-10f)
            return;
        float inv = 1f / MathF.Sqrt(n2);
        nx *= inv;
        ny *= inv;
        nz *= inv;
        float vn = vx * nx + vy * ny + vz * nz;
        float tx = vx - vn * nx;
        float ty = vy - vn * ny;
        float tz = vz - vn * nz;
        float keep = 1f - amount;
        if (keep < 0f)
            keep = 0f;
        if (keep > 1f)
            keep = 1f;
        vx = vn * nx + tx * keep;
        vy = vn * ny + ty * keep;
        vz = vn * nz + tz * keep;
    }
}

/// <summary>
/// Collision elasticity. Scales the outgoing bounce normal after contact.
/// Amount 0 is a no-op; 1 removes all normal (slide). Must match Update
/// <c>VfxCollisionElasticity</c>.
/// </summary>
public static class VfxCollisionElasticity
{
    public static void Apply(
        ref float vx, ref float vy, ref float vz,
        float nx, float ny, float nz,
        float amount)
    {
        if (amount <= 0f)
            return;
        float n2 = nx * nx + ny * ny + nz * nz;
        if (n2 <= 1e-10f)
            return;
        float inv = 1f / MathF.Sqrt(n2);
        nx *= inv;
        ny *= inv;
        nz *= inv;
        float vn = vx * nx + vy * ny + vz * nz;
        float keep = 1f - amount;
        if (keep < 0f)
            keep = 0f;
        if (keep > 1f)
            keep = 1f;
        float tx = vx - vn * nx;
        float ty = vy - vn * ny;
        float tz = vz - vn * nz;
        vx = tx + vn * nx * keep;
        vy = ty + vn * ny * keep;
        vz = tz + vn * nz * keep;
    }
}

/// <summary>
/// Collision lifetime loss. Scales remaining life after a contact.
/// Amount 0 is a no-op; 1 sets lifetime to age. Must match Update
/// <c>VfxCollisionLifetimeLoss</c>.
/// </summary>
public static class VfxCollisionLifetimeLoss
{
    public static float Apply(float age, float lifetime, float amount)
    {
        if (amount <= 0f)
            return lifetime;
        float keep = 1f - amount;
        if (keep < 0f)
            keep = 0f;
        if (keep > 1f)
            keep = 1f;
        return age + (lifetime - age) * keep;
    }
}

/// <summary>
/// Limit velocity. Clamps speed to <paramref name="maxSpeed"/>.
/// Max speed &lt;= 0 is a no-op. Must match Update <c>VfxLimitVelocity</c>.
/// </summary>
public static class VfxLimitVelocity
{
    public static void Apply(ref float vx, ref float vy, ref float vz, float maxSpeed)
    {
        if (maxSpeed <= 0f)
            return;
        float spd2 = vx * vx + vy * vy + vz * vz;
        float max2 = maxSpeed * maxSpeed;
        if (spd2 <= max2)
            return;
        float inv = maxSpeed / MathF.Sqrt(spd2);
        vx *= inv;
        vy *= inv;
        vz *= inv;
    }
}

/// <summary>
/// Kill sphere. True when the point is inside the sphere.
/// Radius &lt;= 0 is a no-op. Must match Update <c>VfxKillSphere</c>.
/// </summary>
public static class VfxKillSphere
{
    public static bool Apply(
        float px, float py, float pz,
        float cx, float cy, float cz,
        float radius)
    {
        if (radius <= 0f)
            return false;
        float dx = px - cx;
        float dy = py - cy;
        float dz = pz - cz;
        return dx * dx + dy * dy + dz * dz <= radius * radius;
    }
}

/// <summary>
/// Kill AABox. True when the point is inside the axis-aligned box.
/// Any half-extent &lt;= 0 is a no-op. Must match Update <c>VfxKillAABox</c>.
/// </summary>
public static class VfxKillAABox
{
    public static bool Apply(
        float px, float py, float pz,
        float cx, float cy, float cz,
        float sx, float sy, float sz)
    {
        if (sx <= 0f || sy <= 0f || sz <= 0f)
            return false;
        return MathF.Abs(px - cx) <= sx
            && MathF.Abs(py - cy) <= sy
            && MathF.Abs(pz - cz) <= sz;
    }
}

/// <summary>
/// Kill plane. True when the point is on the back side of the plane
/// (<c>dot(p, n) - d &lt; 0</c>). Zero normal is a no-op. Must match Update <c>VfxKillPlane</c>.
/// </summary>
public static class VfxKillPlane
{
    public static bool Apply(
        float px, float py, float pz,
        float nx, float ny, float nz,
        float distance)
    {
        float n2 = nx * nx + ny * ny + nz * nz;
        if (n2 <= 1e-10f)
            return false;
        float inv = 1f / MathF.Sqrt(n2);
        return px * nx * inv + py * ny * inv + pz * nz * inv - distance < 0f;
    }
}

/// <summary>
/// Attract. Adds velocity toward a target point.
/// Strength &lt;= 0 is a no-op. Must match Update <c>VfxAttract</c>.
/// </summary>
public static class VfxAttract
{
    public static void Apply(
        ref float vx, ref float vy, ref float vz,
        float px, float py, float pz,
        float tx, float ty, float tz,
        float strength, float dt)
    {
        if (strength <= 0f)
            return;
        float dx = tx - px;
        float dy = ty - py;
        float dz = tz - pz;
        float d2 = dx * dx + dy * dy + dz * dz;
        if (d2 <= 1e-10f)
            return;
        float s = strength * dt / MathF.Sqrt(d2);
        vx += dx * s;
        vy += dy * s;
        vz += dz * s;
    }
}

/// <summary>
/// Conform to Sphere. Adds velocity toward the sphere surface.
/// Strength &lt;= 0 is a no-op. Must match Update <c>VfxConformToSphere</c>.
/// </summary>
public static class VfxConformToSphere
{
    public static void Apply(
        ref float vx, ref float vy, ref float vz,
        float px, float py, float pz,
        float cx, float cy, float cz,
        float radius, float strength, float dt)
    {
        if (strength <= 0f)
            return;
        float dx = px - cx;
        float dy = py - cy;
        float dz = pz - cz;
        float d2 = dx * dx + dy * dy + dz * dz;
        if (d2 <= 1e-10f)
            return;
        float dist = MathF.Sqrt(d2);
        float s = (radius - dist) * strength * dt / dist;
        vx += dx * s;
        vy += dy * s;
        vz += dz * s;
    }
}

/// <summary>
/// Vortex. Adds tangential rotation and optional pull toward an axis.
/// Zero axis, or both Rotation and Attraction == 0, is a no-op. Must match Update <c>VfxVortex</c>.
/// </summary>
public static class VfxVortex
{
    public static void Apply(
        ref float vx, ref float vy, ref float vz,
        float px, float py, float pz,
        float cx, float cy, float cz,
        float ax, float ay, float az,
        float rotation, float attraction, float dt)
    {
        if (rotation == 0f && attraction == 0f)
            return;
        float a2 = ax * ax + ay * ay + az * az;
        if (a2 <= 1e-10f)
            return;
        float invA = 1f / MathF.Sqrt(a2);
        ax *= invA;
        ay *= invA;
        az *= invA;
        float rx = px - cx;
        float ry = py - cy;
        float rz = pz - cz;
        float along = rx * ax + ry * ay + rz * az;
        float radx = rx - ax * along;
        float rady = ry - ay * along;
        float radz = rz - az * along;
        float r2 = radx * radx + rady * rady + radz * radz;
        if (r2 <= 1e-10f)
            return;
        float invR = 1f / MathF.Sqrt(r2);
        float nx = radx * invR;
        float ny = rady * invR;
        float nz = radz * invR;
        float tx = ay * nz - az * ny;
        float ty = az * nx - ax * nz;
        float tz = ax * ny - ay * nx;
        float rot = rotation * dt;
        float att = attraction * dt;
        vx += tx * rot - nx * att;
        vy += ty * rot - ny * att;
        vz += tz * rot - nz * att;
    }
}

/// <summary>
/// Conform to Cylinder. Adds velocity toward the cylinder surface around an axis.
/// Strength &lt;= 0 or a zero axis is a no-op. Must match Update <c>VfxConformToCylinder</c>.
/// </summary>
public static class VfxConformToCylinder
{
    public static void Apply(
        ref float vx, ref float vy, ref float vz,
        float px, float py, float pz,
        float cx, float cy, float cz,
        float ax, float ay, float az,
        float radius, float strength, float dt)
    {
        if (strength <= 0f)
            return;
        float a2 = ax * ax + ay * ay + az * az;
        if (a2 <= 1e-10f)
            return;
        float invA = 1f / MathF.Sqrt(a2);
        ax *= invA;
        ay *= invA;
        az *= invA;
        float rx = px - cx;
        float ry = py - cy;
        float rz = pz - cz;
        float along = rx * ax + ry * ay + rz * az;
        float radx = rx - ax * along;
        float rady = ry - ay * along;
        float radz = rz - az * along;
        float r2 = radx * radx + rady * rady + radz * radz;
        if (r2 <= 1e-10f)
            return;
        float dist = MathF.Sqrt(r2);
        float s = (radius - dist) * strength * dt / dist;
        vx += radx * s;
        vy += rady * s;
        vz += radz * s;
    }
}

/// <summary>
/// Orient Look At. Axis is <c>target - position</c>.
/// Must match Output <c>VfxOrientLookAt</c>.
/// </summary>
public static class VfxOrientLookAt
{
    public static void Apply(
        float px, float py, float pz,
        float tx, float ty, float tz,
        out float ax, out float ay, out float az)
    {
        ax = tx - px;
        ay = ty - py;
        az = tz - pz;
    }
}

/// <summary>
/// Screen-space motion from current vs previous clip. Must match Output
/// <c>VfxMotionNdcDelta</c> / <c>VfxMotionEncode</c>. Mode &lt;= 0 is a no-op.
/// Previous world is <c>position - velocity * dt</c>.
/// </summary>
public static class VfxMotionVector
{
    public static void PrevPosition(
        float px, float py, float pz,
        float vx, float vy, float vz,
        float dt,
        out float qx, out float qy, out float qz)
    {
        qx = px - vx * dt;
        qy = py - vy * dt;
        qz = pz - vz * dt;
    }

    public static void NdcDelta(
        float cx, float cy, float cw,
        float px, float py, float pw,
        out float dx, out float dy)
    {
        float iw = cw > 1e-5f ? 1f / cw : 0f;
        float ip = pw > 1e-5f ? 1f / pw : 0f;
        dx = (cx * iw - px * ip) * 0.5f;
        dy = (cy * iw - py * ip) * 0.5f;
    }

    public static void EncodeRgb(float dx, float dy, float scale, out float r, out float g, out float b)
    {
        r = dx * scale * 0.5f + 0.5f;
        g = dy * scale * 0.5f + 0.5f;
        b = 0.5f;
        if (r < 0f) r = 0f; else if (r > 1f) r = 1f;
        if (g < 0f) g = 0f; else if (g > 1f) g = 1f;
    }

    public static void Apply(
        ref float r, ref float g, ref float b, float a,
        float dx, float dy, float mode, float scale)
    {
        if (mode <= 0f)
            return;
        EncodeRgb(dx, dy, scale, out float er, out float eg, out float eb);
        float w = mode;
        if (w > 1f) w = 1f;
        if (a < 0f) a = 0f;
        else if (a > 1f) a = 1f;
        w *= a;
        r += (er - r) * w;
        g += (eg - g) * w;
        b += (eb - b) * w;
    }
}

/// <summary>
/// Unity Orient: Along Velocity. Local Y follows velocity, X is camera-facing
/// sideways. Must match Output <c>VfxOrientAlongVelocity</c>. Mode &lt;= 0 is a no-op.
/// </summary>
public static class VfxOrient
{
    public static void AlongVelocity(
        float cx, float cy, float cz,
        float vx, float vy, float vz,
        float camx, float camy, float camz,
        float localX, float localY, float localZ, float size,
        out float wx, out float wy, out float wz)
    {
        float a2 = vx * vx + vy * vy + vz * vz;
        float ax, ay, az;
        if (a2 <= 1e-10f)
        {
            ax = 0f;
            ay = 1f;
            az = 0f;
        }
        else
        {
            float inv = 1f / MathF.Sqrt(a2);
            ax = vx * inv;
            ay = vy * inv;
            az = vz * inv;
        }

        float tcx = camx - cx;
        float tcy = camy - cy;
        float tcz = camz - cz;
        float sx = ay * tcz - az * tcy;
        float sy = az * tcx - ax * tcz;
        float sz = ax * tcy - ay * tcx;
        float s2 = sx * sx + sy * sy + sz * sz;
        if (s2 <= 1e-10f)
        {
            sx = ay * 1f - az * 0f;
            sy = az * 0f - ax * 1f;
            sz = ax * 0f - ay * 0f;
            s2 = sx * sx + sy * sy + sz * sz;
        }
        if (s2 <= 1e-10f)
        {
            sx = 1f;
            sy = 0f;
            sz = 0f;
        }
        else
        {
            float inv = 1f / MathF.Sqrt(s2);
            sx *= inv;
            sy *= inv;
            sz *= inv;
        }

        float ux = sy * az - sz * ay;
        float uy = sz * ax - sx * az;
        float uz = sx * ay - sy * ax;
        wx = cx + (sx * localX + ax * localY + ux * localZ) * size;
        wy = cy + (sy * localX + ay * localY + uy * localZ) * size;
        wz = cz + (sz * localX + az * localY + uz * localZ) * size;
    }

    /// <summary>
    /// Mode &gt;= 1.5 uses the fixed axis (default world Y). Otherwise velocity.
    /// Must match Output <c>vfxOrientAlong</c>.
    /// </summary>
    public static void ResolveAlong(
        float mode, float vx, float vy, float vz, float fx, float fy, float fz,
        out float ax, out float ay, out float az)
    {
        if (mode >= 1.5f)
        {
            ax = fx;
            ay = fy;
            az = fz;
            if (ax == 0f && ay == 0f && az == 0f)
                ay = 1f;
            return;
        }

        ax = vx;
        ay = vy;
        az = vz;
    }
}

/// <summary>
/// Unity Camera Fade. Multiplies alpha by near fade-in and far fade-out.
/// Near/far &lt;= 0 are no-ops. Must match Output <c>VfxCameraFade</c>.
/// </summary>
public static class VfxCameraFade
{
    public static float Fade(float distance, float near, float far)
    {
        float a = 1f;
        if (near > 0f)
        {
            a = distance / near;
            if (a < 0f) a = 0f;
            else if (a > 1f) a = 1f;
        }
        if (far > 0f)
        {
            float f = (far - distance) / far;
            if (f < 0f) f = 0f;
            else if (f > 1f) f = 1f;
            a *= f;
        }
        return a;
    }
}

public static class VfxSoftParticles
{
    public static float Fade(float particleEyeZ, float sceneEyeZ, float fadeDistance)
    {
        if (fadeDistance <= 0f)
            return 1f;
        float d = sceneEyeZ - particleEyeZ;
        if (d <= 0f)
            return 0f;
        if (d >= fadeDistance)
            return 1f;
        return d / fadeDistance;
    }
}

public enum VfxNodeKind
{
    Context = 0,
    Block = 1,
    Operator = 2,
}

public sealed class VfxProperty
{
    public string Id = "";
    public string Name = "";
    public string DisplayName = "";
    public VfxValueType Type;
    public string DefaultValue = "";
}

public sealed class VfxNodeField
{
    public string Key = "";
    public string Value = "";
}

public sealed class VfxContext
{
    public string Id = "";
    public string Type = "";
    public float PosX;
    public float PosY;
    /// <summary>Packaged or project <c>.vfx</c> path when <see cref="Type"/> is Subgraph.</summary>
    public string Asset = "";
}

public sealed class VfxBlock
{
    public string Id = "";
    public string ContextId = "";
    public string Type = "";
    public int Order;
    public float PosX;
    public float PosY;
    public List<VfxNodeField> Fields = new();
}

public sealed class VfxOperator
{
    public string Id = "";
    public string Type = "";
    public float PosX;
    public float PosY;
    public List<VfxNodeField> Fields = new();
}

public sealed class VfxEdge
{
    public string Id = "";
    public string FromNode = "";
    public string FromPort = "";
    public string ToNode = "";
    public string ToPort = "";
}
