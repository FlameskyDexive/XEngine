using System;
using System.Collections.Generic;
using System.Globalization;
using XEngine.Ai.Protocol;
using XEngine.Echo;

namespace XEngine.Ai.ThreeFx;

/// <summary>
/// Schema v1 of a <c>.threefx</c> recipe document. A recipe is the authoritative source for the
/// authoring chain: the host renders it, the cache identity hashes it, and the validator gates
/// everything an AI agent (or a human) can express. Deliberately constrained — no arbitrary
/// scene graphs, no scripting; richer effects arrive as fixed templates (A4) or bakes (A5).
/// </summary>
public sealed record ThreeFxRecipe
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Explicit stable ID ([a-z0-9_]{3,64}); artifact names derive from it.</summary>
    public string StableId { get; init; } = "";

    public ThreeFxScene Recipe { get; init; } = new();

    /// <summary>Project-relative dependency paths; their content hashes join the cache identity.</summary>
    public IReadOnlyList<string> Dependencies { get; init; } = Array.Empty<string>();

    public int Seed { get; init; }

    public int ExportWidth { get; init; } = 512;

    public int ExportHeight { get; init; } = 512;

    public ThreeFxEffects Effects { get; init; } = new();

    /// <summary>Present → the bake operation produces a flipbook sheet + per-frame PNGs.</summary>
    public ThreeFxBake? Bake { get; init; }
}

/// <summary>
/// A5 — fixed-time flipbook bake settings. Frame times are loop-phase-aligned samples
/// (t_i = i/N · duration), so a looping effect tiles seamlessly; the sheet is laid out
/// column-major in a near-square grid and every frame is also written as its own PNG for
/// platforms without compute (the "baked variant" consumed by such builds).
/// </summary>
public sealed record ThreeFxBake
{
    public int Frames { get; init; } = 8;

    public double Duration { get; init; } = 2.0;

    /// <summary>sheet | frames | both (default: both).</summary>
    public string Layout { get; init; } = "both";

    /// <summary>Bake with a transparent background (alpha channel preserved).</summary>
    public bool Transparent { get; init; }
}

public sealed record ThreeFxScene
{
    /// <summary>torusKnot | sphere | box.</summary>
    public string Mesh { get; init; } = "torusKnot";

    /// <summary>Hue 0..1 (seed-independent channel of the deterministic look).</summary>
    public double Hue { get; init; } = 0.25;

    public double Metalness { get; init; } = 0.85;

    public double Roughness { get; init; } = 0.25;

    /// <summary>Linear RGB background 0..1; empty → page default.</summary>
    public IReadOnlyList<double> Background { get; init; } = Array.Empty<double>();
}

/// <summary>
/// A4 — the constrained native-effect set. Each entry maps to a fixed template on the authoring
/// page (fresnel shell, loop/burst billboard particles, bloom/vignette/color post chain); the
/// validator bounds every parameter so recipes stay inside what the templates can express.
/// </summary>
public sealed record ThreeFxEffects
{
    public ThreeFxFresnel? Fresnel { get; init; }

    public ThreeFxParticles? Particles { get; init; }

    public ThreeFxPost? Post { get; init; }

    public bool IsEmpty => Fresnel is null && Particles is null && Post is null;
}

public sealed record ThreeFxFresnel
{
    /// <summary>Linear RGB 0..1.</summary>
    public IReadOnlyList<double> Color { get; init; } = new[] { 1.0, 0.6, 0.2 };

    public double Power { get; init; } = 2.5;

    public double Intensity { get; init; } = 1.0;
}

public sealed record ThreeFxParticles
{
    /// <summary>loop | burst.</summary>
    public string Mode { get; init; } = "loop";

    public int Count { get; init; } = 120;

    public IReadOnlyList<double> Color { get; init; } = new[] { 1.0, 0.8, 0.4 };

    public double Size { get; init; } = 0.06;

    public double Speed { get; init; } = 1.0;
}

public sealed record ThreeFxPost
{
    public double BloomIntensity { get; init; }

    /// <summary>Luminance threshold above which pixels bloom.</summary>
    public double BloomThreshold { get; init; } = 0.85;

    /// <summary>0 = off, 1 = strongest corner darkening.</summary>
    public double Vignette { get; init; }

    /// <summary>-1 grayscale … 0 neutral … +1 oversaturated.</summary>
    public double Saturation { get; init; }

    /// <summary>-1 darker … 0 neutral … +1 brighter.</summary>
    public double Brightness { get; init; }

    /// <summary>aces | neutral | none.</summary>
    public string Tonemap { get; init; } = "aces";
}

/// <summary>
/// Parses <c>.threefx</c> Echo text into <see cref="ThreeFxRecipe"/> and validates it. All rules
/// are pure functions of the document; filesystem existence checks live in
/// <see cref="ThreeFxValidator.ValidateDependencies"/>.
/// </summary>
public static class ThreeFxDocument
{
    public static bool TryParse(string text, out ThreeFxRecipe? recipe, out List<ThreeAuthoringDiagnostic> diagnostics)
    {
        recipe = null;
        diagnostics = [];
        EchoObject root;
        try
        {
            root = EchoObject.ReadFromString(text);
        }
        catch (Exception ex)
        {
            diagnostics.Add(new ThreeAuthoringDiagnostic("parse_failed", "error", ex.Message));
            return false;
        }

        if (root.TagType != EchoType.Compound)
        {
            diagnostics.Add(new ThreeAuthoringDiagnostic("schema", "error",
                "Root must be a compound object."));
            return false;
        }

        int schemaVersion = GetInt(root, "schemaVersion") ?? ThreeFxRecipe.CurrentSchemaVersion;
        if (schemaVersion != ThreeFxRecipe.CurrentSchemaVersion)
        {
            diagnostics.Add(new ThreeAuthoringDiagnostic("schema_version", "error",
                $"schemaVersion must be {ThreeFxRecipe.CurrentSchemaVersion}, got {schemaVersion}."));
            return false;
        }

        var scene = new ThreeFxScene();
        if (root.Get("recipe") is { } recipeTag && recipeTag.TagType == EchoType.Compound)
        {
            IReadOnlyList<double> background = Array.Empty<double>();
            if (recipeTag.Get("background") is { } bgTag && bgTag.TagType == EchoType.List)
            {
                var channels = new List<double>();
                foreach (EchoObject channel in bgTag.List)
                    channels.Add(channel.DoubleValue);
                background = channels;
            }

            scene = scene with
            {
                Mesh = GetString(recipeTag, "mesh") ?? scene.Mesh,
                Hue = GetDouble(recipeTag, "hue") ?? scene.Hue,
                Metalness = GetDouble(recipeTag, "metalness") ?? scene.Metalness,
                Roughness = GetDouble(recipeTag, "roughness") ?? scene.Roughness,
                Background = background,
            };
        }

        var dependencies = new List<string>();
        if (root.Get("dependencies") is { } depsTag && depsTag.TagType == EchoType.List)
        {
            foreach (EchoObject dep in depsTag.List)
            {
                if (dep.TagType == EchoType.String)
                    dependencies.Add(dep.StringValue);
            }
        }

        ThreeFxEffects effects = ParseEffects(root);
        ThreeFxBake? bake = null;
        if (root.Get("bake") is { } bakeTag && bakeTag.TagType == EchoType.Compound)
        {
            bake = new ThreeFxBake
            {
                Frames = GetInt(bakeTag, "frames") ?? 8,
                Duration = GetDouble(bakeTag, "duration") ?? 2.0,
                Layout = GetString(bakeTag, "layout") ?? "both",
                Transparent = GetBool(bakeTag, "transparent"),
            };
        }

        recipe = new ThreeFxRecipe
        {
            SchemaVersion = schemaVersion,
            StableId = GetString(root, "stableId") ?? "",
            Recipe = scene,
            Dependencies = dependencies,
            Seed = GetInt(root, "seed") ?? 0,
            ExportWidth = GetInt(root, "exportWidth") ?? 512,
            ExportHeight = GetInt(root, "exportHeight") ?? 512,
            Effects = effects,
            Bake = bake,
        };
        return true;
    }

    public static string Normalize(string text)
    {
        // Canonical form: parse, then re-serialize with compound keys in sorted order and
        // invariant-culture numbers, so whitespace/key-order/formatting differences never change
        // the cache identity — only semantic edits do.
        EchoObject root = EchoObject.ReadFromString(text);
        var sb = new System.Text.StringBuilder();
        WriteCanonical(root, sb);
        return sb.ToString();
    }

    private static void WriteCanonical(EchoObject tag, System.Text.StringBuilder sb)
    {
        switch (tag.TagType)
        {
            case EchoType.Compound:
                sb.Append('{');
                bool firstKey = true;
                foreach (string key in tag.GetNames().OrderBy(static k => k, StringComparer.Ordinal))
                {
                    if (!firstKey)
                        sb.Append(',');
                    firstKey = false;
                    sb.Append('"').Append(key).Append("\":");
                    WriteCanonical(tag.Get(key)!, sb);
                }
                sb.Append('}');
                break;
            case EchoType.List:
                sb.Append('[');
                for (int i = 0; i < tag.Count; i++)
                {
                    if (i > 0)
                        sb.Append(',');
                    WriteCanonical(tag.Get(i), sb);
                }
                sb.Append(']');
                break;
            case EchoType.String:
                sb.Append('"').Append(tag.StringValue).Append('"');
                break;
            case EchoType.Double:
                sb.Append(tag.DoubleValue.ToString("R", CultureInfo.InvariantCulture));
                break;
            case EchoType.Float:
                sb.Append(tag.FloatValue.ToString("R", CultureInfo.InvariantCulture));
                break;
            default:
                sb.Append(tag.Value?.ToString() ?? "null");
                break;
        }
    }

    private static ThreeFxEffects ParseEffects(EchoObject root)
    {
        if (root.Get("effects") is not { } effectsTag || effectsTag.TagType != EchoType.Compound)
            return new ThreeFxEffects();

        ThreeFxFresnel? fresnel = null;
        if (effectsTag.Get("fresnel") is { } fresnelTag && fresnelTag.TagType == EchoType.Compound)
        {
            fresnel = new ThreeFxFresnel
            {
                Color = ReadColor(fresnelTag, "color", new[] { 1.0, 0.6, 0.2 }),
                Power = GetDouble(fresnelTag, "power") ?? 2.5,
                Intensity = GetDouble(fresnelTag, "intensity") ?? 1.0,
            };
        }

        ThreeFxParticles? particles = null;
        if (effectsTag.Get("particles") is { } particlesTag && particlesTag.TagType == EchoType.Compound)
        {
            particles = new ThreeFxParticles
            {
                Mode = GetString(particlesTag, "mode") ?? "loop",
                Count = GetInt(particlesTag, "count") ?? 120,
                Color = ReadColor(particlesTag, "color", new[] { 1.0, 0.8, 0.4 }),
                Size = GetDouble(particlesTag, "size") ?? 0.06,
                Speed = GetDouble(particlesTag, "speed") ?? 1.0,
            };
        }

        ThreeFxPost? post = null;
        if (effectsTag.Get("post") is { } postTag && postTag.TagType == EchoType.Compound)
        {
            post = new ThreeFxPost
            {
                BloomIntensity = GetDouble(postTag, "bloomIntensity") ?? 0,
                BloomThreshold = GetDouble(postTag, "bloomThreshold") ?? 0.85,
                Vignette = GetDouble(postTag, "vignette") ?? 0,
                Saturation = GetDouble(postTag, "saturation") ?? 0,
                Brightness = GetDouble(postTag, "brightness") ?? 0,
                Tonemap = GetString(postTag, "tonemap") ?? "aces",
            };
        }

        return new ThreeFxEffects { Fresnel = fresnel, Particles = particles, Post = post };
    }

    private static IReadOnlyList<double> ReadColor(EchoObject compound, string key, double[] fallback)
    {
        if (compound.Get(key) is { } colorTag && colorTag.TagType == EchoType.List)
        {
            var channels = new List<double>();
            foreach (EchoObject channel in colorTag.List)
                channels.Add(channel.DoubleValue);
            return channels;
        }
        return fallback;
    }

    private static bool GetBool(EchoObject compound, string key) =>
        compound.Get(key) is { TagType: EchoType.Bool } tag && tag.BoolValue;

    private static string? GetString(EchoObject compound, string key) =>
        compound.Get(key) is { TagType: EchoType.String } tag ? tag.StringValue : null;

    private static int? GetInt(EchoObject compound, string key) =>
        compound.Get(key) is { TagType: EchoType.Int } tag ? tag.IntValue
        : compound.Get(key) is { TagType: EchoType.Long } longTag ? checked((int)longTag.LongValue)
        : null;

    private static double? GetDouble(EchoObject compound, string key) =>
        compound.Get(key) is { TagType: EchoType.Double } tag ? tag.DoubleValue
        : compound.Get(key) is { TagType: EchoType.Float } floatTag ? floatTag.FloatValue
        : null;
}

/// <summary>Validation rules for a parsed recipe. Returns one diagnostic per violation.</summary>
public static class ThreeFxValidator
{
    public static IReadOnlyList<ThreeAuthoringDiagnostic> Validate(ThreeFxRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        var diagnostics = new List<ThreeAuthoringDiagnostic>();

        if (recipe.StableId.Length is < 3 or > 64
            || !System.Text.RegularExpressions.Regex.IsMatch(recipe.StableId, "^[a-z0-9_]+$"))
        {
            diagnostics.Add(new ThreeAuthoringDiagnostic("stable_id", "error",
                "stableId must match [a-z0-9_]{3,64}."));
        }

        switch (recipe.Recipe.Mesh)
        {
            case "torusKnot" or "sphere" or "box":
                break;
            default:
                diagnostics.Add(new ThreeAuthoringDiagnostic("mesh", "error",
                    "recipe.mesh must be torusKnot, sphere or box."));
                break;
        }

        ValidateUnitInterval(recipe.Recipe.Hue, "recipe.hue", diagnostics);
        ValidateUnitInterval(recipe.Recipe.Metalness, "recipe.metalness", diagnostics);
        ValidateUnitInterval(recipe.Recipe.Roughness, "recipe.roughness", diagnostics);

        if (recipe.Recipe.Background.Count is not 0 and not 3)
        {
            diagnostics.Add(new ThreeAuthoringDiagnostic("background", "error",
                "recipe.background must have exactly 3 channels."));
        }
        else
        {
            for (int i = 0; i < recipe.Recipe.Background.Count; i++)
            {
                if (recipe.Recipe.Background[i] is < 0 or > 1 || !IsFinite(recipe.Recipe.Background[i]))
                    diagnostics.Add(new ThreeAuthoringDiagnostic("background", "error",
                        $"recipe.background[{i}] must be within 0..1."));
            }
        }

        if (recipe.Seed < 0)
            diagnostics.Add(new ThreeAuthoringDiagnostic("seed", "error", "seed must be >= 0."));

        ValidateDimension(recipe.ExportWidth, "exportWidth", diagnostics);
        ValidateDimension(recipe.ExportHeight, "exportHeight", diagnostics);

        foreach (string dependency in recipe.Dependencies)
            ValidateDependencyPath(dependency, diagnostics);

        ValidateEffects(recipe.Effects, diagnostics);
        ValidateBake(recipe.Bake, diagnostics);

        return diagnostics;
    }

    private static void ValidateBake(ThreeFxBake? bake, List<ThreeAuthoringDiagnostic> diagnostics)
    {
        if (bake is null)
            return;

        ValidateRange(bake.Frames, 1, 64, "bake.frames", diagnostics);
        ValidateRange(bake.Duration, 0.1, 60, "bake.duration", diagnostics);
        if (bake.Layout is not ("sheet" or "frames" or "both"))
            diagnostics.Add(new ThreeAuthoringDiagnostic("bake.layout", "error",
                "bake.layout must be sheet, frames or both."));
    }

    /// <summary>Filesystem half of dependency validation: every path must exist below the root.</summary>
    public static void ValidateDependencies(
        ThreeFxRecipe recipe,
        string projectRoot,
        List<ThreeAuthoringDiagnostic> diagnostics)
    {
        foreach (string dependency in recipe.Dependencies)
        {
            string full;
            try
            {
                full = ThreeAuthoringPath.ResolveBelowRoot(projectRoot, dependency);
            }
            catch (IOException ex)
            {
                diagnostics.Add(new ThreeAuthoringDiagnostic("dependency_path", "error", ex.Message));
                continue;
            }

            if (!File.Exists(full))
                diagnostics.Add(new ThreeAuthoringDiagnostic("dependency_missing", "error",
                    $"Dependency not found: {dependency}"));
        }
    }

    private static void ValidateUnitInterval(double value, string field, List<ThreeAuthoringDiagnostic> diagnostics)
    {
        if (value is < 0 or > 1 || !IsFinite(value))
            diagnostics.Add(new ThreeAuthoringDiagnostic(field, "error", $"{field} must be within 0..1."));
    }

    private static void ValidateDimension(int value, string field, List<ThreeAuthoringDiagnostic> diagnostics)
    {
        if (value is < 1 or > ThreeAuthoringProtocol.MaxPreviewDimension)
            diagnostics.Add(new ThreeAuthoringDiagnostic(field, "error",
                $"{field} must be between 1 and {ThreeAuthoringProtocol.MaxPreviewDimension}."));
    }

    private static void ValidateEffects(ThreeFxEffects effects, List<ThreeAuthoringDiagnostic> diagnostics)
    {
        if (effects.IsEmpty)
            return;

        if (effects.Fresnel is { } fresnel)
        {
            ValidateColor(fresnel.Color, "effects.fresnel.color", diagnostics);
            ValidateRange(fresnel.Power, 0.1, 10, "effects.fresnel.power", diagnostics);
            ValidateRange(fresnel.Intensity, 0, 10, "effects.fresnel.intensity", diagnostics);
        }

        if (effects.Particles is { } particles)
        {
            if (particles.Mode is not ("loop" or "burst"))
                diagnostics.Add(new ThreeAuthoringDiagnostic("effects.particles.mode", "error",
                    "effects.particles.mode must be loop or burst."));
            ValidateRange(particles.Count, 1, 2000, "effects.particles.count", diagnostics);
            ValidateColor(particles.Color, "effects.particles.color", diagnostics);
            ValidateRange(particles.Size, 0.01, 1, "effects.particles.size", diagnostics);
            ValidateRange(particles.Speed, 0, 5, "effects.particles.speed", diagnostics);
        }

        if (effects.Post is { } post)
        {
            ValidateRange(post.BloomIntensity, 0, 5, "effects.post.bloomIntensity", diagnostics);
            ValidateRange(post.BloomThreshold, 0, 1, "effects.post.bloomThreshold", diagnostics);
            ValidateRange(post.Vignette, 0, 1, "effects.post.vignette", diagnostics);
            ValidateRange(post.Saturation, -1, 1, "effects.post.saturation", diagnostics);
            ValidateRange(post.Brightness, -1, 1, "effects.post.brightness", diagnostics);
            if (post.Tonemap is not ("aces" or "neutral" or "none"))
                diagnostics.Add(new ThreeAuthoringDiagnostic("effects.post.tonemap", "error",
                    "effects.post.tonemap must be aces, neutral or none."));
        }
    }

    private static void ValidateColor(IReadOnlyList<double> color, string field, List<ThreeAuthoringDiagnostic> diagnostics)
    {
        if (color.Count != 3)
        {
            diagnostics.Add(new ThreeAuthoringDiagnostic(field, "error", $"{field} must have exactly 3 channels."));
            return;
        }
        for (int i = 0; i < color.Count; i++)
        {
            if (color[i] is < 0 or > 1 || !IsFinite(color[i]))
                diagnostics.Add(new ThreeAuthoringDiagnostic(field, "error", $"{field}[{i}] must be within 0..1."));
        }
    }

    private static void ValidateRange(double value, double min, double max, string field, List<ThreeAuthoringDiagnostic> diagnostics)
    {
        if (value < min || value > max || !IsFinite(value))
            diagnostics.Add(new ThreeAuthoringDiagnostic(field, "error",
                $"{field} must be within {Invariant2(min)}..{Invariant2(max)}."));
    }

    private static string Invariant2(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static void ValidateDependencyPath(string path, List<ThreeAuthoringDiagnostic> diagnostics)
    {
        string normalized = path.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized)
            || System.IO.Path.IsPathRooted(path)
            || normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.StartsWith("../", StringComparison.Ordinal)
            || normalized.Contains("/../", StringComparison.Ordinal)
            || normalized.EndsWith("/..", StringComparison.Ordinal)
            || normalized.Equals("..", StringComparison.Ordinal))
        {
            diagnostics.Add(new ThreeAuthoringDiagnostic("dependency_path", "error",
                "Dependency paths must be project-relative and stay below the project root."));
        }
    }

    private static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);
}

/// <summary>
/// Project-root-relative path resolution shared with the host; mirrors the bridge's backstop so
/// the validator can check dependency existence with identical escape rules.
/// </summary>
public static class ThreeAuthoringPath
{
    public static string ResolveBelowRoot(string root, string relativePath)
    {
        string fullRoot = Path.GetFullPath(root);
        string combined = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        string rootPrefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(combined, fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"Resolved path escapes the project root: {relativePath}");
        }
        return combined;
    }

    public static string FormatInvariant(double value) =>
        value.ToString("R", CultureInfo.InvariantCulture);
}
