// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using XEngine.Ai.Protocol;
using XEngine.Ai.SpineGen;

namespace XEngine.Ai.Host;

/// <summary>
/// SpineGen stage 5 — text-to-animation. Two routes:
///  · built-in motion library (offline, deterministic, loop-safe);
///  · LLM keyframe generation through an OpenAI-compatible chat endpoint, validated against the
///    rig schema, with up to N repair rounds feeding violations back (the video's agent loop).
/// Validated plans are appended to rig/skeleton.json so the editor and the vendored runtime play
/// them immediately.
/// </summary>
internal sealed class SpineGenAnimateService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public const int MaxRepairRounds = 3;

    private readonly ProjectDescriptor _project;
    private readonly Func<string, string?> _secretResolver;

    public SpineGenAnimateService(ProjectDescriptor project)
        : this(project, name => Gateway.SecretResolver.Resolve(name, project.ProjectRoot))
    {
    }

    internal SpineGenAnimateService(ProjectDescriptor project, Func<string, string?> secretResolver)
    {
        _project = project;
        _secretResolver = secretResolver;
    }

    public async Task<SpineGenAnimateResult> AnimateAsync(string sliceDir, string? motion, string? prompt,
        string? provider, string? model, CancellationToken cancellationToken, string? planPath = null)
    {
        if (string.IsNullOrWhiteSpace(sliceDir))
            throw new ArgumentException("--slice is required.");
        bool llmRoute = !string.IsNullOrWhiteSpace(prompt);
        if ((llmRoute ? 1 : 0) + (motion != null ? 1 : 0) + (planPath != null ? 1 : 0) != 1)
            throw new ArgumentException("Pass exactly one of --motion, --prompt, or --plan.");

        sliceDir = sliceDir.Replace('/', Path.DirectorySeparatorChar);
        string absSliceDir = Path.IsPathRooted(sliceDir) ? sliceDir : Path.Combine(_project.ProjectRoot, sliceDir);
        SliceManifest slice = JsonSerializer.Deserialize<SliceManifest>(
            File.ReadAllText(Path.Combine(absSliceDir, "slice_manifest.json")))
            ?? throw new InvalidOperationException("Unreadable slice manifest.");
        RigManifest rig = JsonSerializer.Deserialize<RigManifest>(
            File.ReadAllText(Path.Combine(absSliceDir, "rig", "rig_manifest.json")))
            ?? throw new InvalidOperationException("Unreadable rig manifest.");

        string source;
        var violationsSeen = new List<string>();
        MotionPlan plan;

        if (planPath != null)
        {
            string absolutePlan = Path.IsPathRooted(planPath) ? planPath : Path.Combine(_project.ProjectRoot, planPath);
            plan = JsonSerializer.Deserialize<MotionPlan>(File.ReadAllText(absolutePlan))
                ?? throw new ArgumentException("Motion plan is null.");
            // Explicit local authoring has the same amplitude freedom as built-ins.
            // This flag cannot be set by JSON; LLM responses retain conservative limits.
            plan.Trusted = true;
            source = "plan:" + ToRelative(absolutePlan);
        }
        else if (!llmRoute)
        {
            source = $"builtin:{motion}";
            plan = MotionLibrary.Build(motion!, rig);
        }
        else
        {
            provider ??= "openai";
            source = $"llm:{provider}";
            (plan, violationsSeen) = await GenerateViaLlmAsync(prompt!, provider, model, rig,
                cancellationToken).ConfigureAwait(false);
        }

        string skeletonPath = Path.Combine(absSliceDir, "rig", "skeleton.json");
        SpineSkeletonDoc doc = SpineExporter.Read(File.ReadAllText(skeletonPath));
        List<MotionViolation> violations = MotionValidator.Validate(plan, rig, doc);
        if (violations.Count > 0)
            throw new InvalidOperationException(
                "Motion plan invalid after repair rounds: " +
                string.Join("; ", violations.Select(v => $"{v.Code}:{v.Message}")));

        // Append into the existing skeleton (preserving meshes written by stage 4).
        doc.Animations ??= [];
        doc.Animations[plan.Animation] = MotionValidator.ToAnimation(plan);
        File.WriteAllText(skeletonPath, SpineExporter.Write(doc));

        var report = new MotionManifest
        {
            Source = source,
            Prompt = prompt,
            Animation = plan.Animation,
            Loop = plan.Loop,
            Duration = plan.Duration,
            Bones = [.. plan.Bones.Select(b => b.Bone)],
            RepairViolations = violationsSeen,
        };
        string reportRel = ToRelative(Path.Combine(absSliceDir, "rig", "motion_manifest.json"));
        File.WriteAllText(Path.Combine(absSliceDir, "rig", "motion_manifest.json"),
            JsonSerializer.Serialize(report, JsonOpts));

        return new SpineGenAnimateResult
        {
            SliceDirectory = ToRelative(absSliceDir),
            SkeletonJson = ToRelative(skeletonPath),
            MotionManifest = reportRel,
            Animation = plan.Animation,
            DurationSeconds = plan.Duration,
            Loop = plan.Loop,
            Bones = report.Bones,
            RepairRounds = violationsSeen.Count,
            Source = source,
        };
    }

    private async Task<(MotionPlan Plan, List<string> RepairHistory)> GenerateViaLlmAsync(
        string prompt, string provider, string? model, RigManifest rig, CancellationToken ct)
    {
        if (provider == "mock")
            return (MockPlanFor(prompt, rig), []);

        string apiKey = _secretResolver($"{provider.ToUpperInvariant()}_API_KEY")
                        ?? throw new InvalidOperationException(
                            $"No API key for provider '{provider}' (set {provider.ToUpperInvariant()}_API_KEY or .xengine/secrets.json).");
        string baseUrl = provider switch
        {
            "openai" => "https://api.openai.com/v1",
            _ => throw new InvalidOperationException($"LLM provider '{provider}' unknown (mock|openai)."),
        };

        var history = new List<string>();
        string lastFeedback = BuildUserPrompt(prompt, rig, feedback: null);
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);

        for (int round = 0; round <= MaxRepairRounds; round++)
        {
            ct.ThrowIfCancellationRequested();
            string content = await ChatOnceAsync(http, baseUrl, model ?? "gpt-4o-mini", lastFeedback, ct)
                .ConfigureAwait(false);
            string json = ExtractJson(content);
            MotionPlan? plan;
            List<MotionViolation> violations;
            try
            {
                plan = JsonSerializer.Deserialize<MotionPlan>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                });
                violations = plan == null
                    ? [new MotionViolation { Code = "null", Message = "model returned null plan" }]
                    : MotionValidator.Validate(plan, rig);
            }
            catch (JsonException ex)
            {
                plan = null;
                violations = [new MotionViolation { Code = "json", Message = ex.Message }];
            }

            if (plan != null && violations.Count == 0)
                return (plan, history);

            history.AddRange(violations.Select(v => $"{v.Code}: {v.Message}"));
            lastFeedback = BuildUserPrompt(prompt, rig,
                "Your previous attempt violated these rules:\n- " +
                string.Join("\n- ", violations.Select(v => $"{v.Code}: {v.Message}")));
        }

        throw new InvalidOperationException(
            $"LLM failed to produce a valid motion plan within {MaxRepairRounds} repair rounds.");
    }

    internal static string BuildUserPrompt(string userPrompt, RigManifest rig, string? feedback)
    {
        string bones = string.Join(", ", rig.Bones.Select(b => b.Name));
        string schema = "{\"animation\":\"snake_case_name\",\"loop\":true,\"duration\":<seconds 0.2-10>," +
            "\"bones\":[{\"bone\":\"<name>\",\"rotate\":[{\"t\":0,\"value\":0},...]|null," +
            "\"translate\":[{\"t\":0,\"value\":0}]|null,\"translateY\":[...]|null}]}";
        string rules =
            "Rules: only listed bones; keys sorted by t; t within [0,duration]; loop plans must end " +
            "at t=duration with the same value they start with; |rotate|<=60, |translate|<=40; " +
            "rotate value = degrees, translate value = px.";
        string nl = "\n";
        string fb = string.IsNullOrEmpty(feedback) ? "" : nl + feedback;
        return "You animate a 2D spine rig. Available bones: " + bones + "." + nl +
               "Image size: " + rig.ImageWidth + "x" + rig.ImageHeight +
               " px, y-down, character stands on the bottom." + nl +
               "Request: " + userPrompt + nl + nl +
               "Reply with ONLY a JSON object:" + nl + schema + nl + rules + fb;
    }
    internal static string ExtractJson(string content)
    {
        int start = content.IndexOf('{');
        int end = content.LastIndexOf('}');
        return start >= 0 && end > start ? content[start..(end + 1)] : content;
    }

    /// <summary>Deterministic offline stand-in: derives a breath+sway blend from the prompt hash so
    /// the LLM code path (schema → validate → convert) is exercised without network.</summary>
    internal static MotionPlan MockPlanFor(string prompt, RigManifest rig)
    {
        uint hash = 2166136261;
        foreach (char c in prompt)
            hash = (hash ^ c) * 16777619;

        var plan = PlanFrom($"mock_{hash % 9999:0000}", rig, duration: 2f);
        float amp = 1.5f + (hash % 100) / 100f * 1.5f;
        if (rig.Bones.Any(b => b.Name == "spine"))
            plan.Bones.Add(new MotionBoneTrack
            {
                Bone = "spine",
                Rotate = [K(0, -amp), K(1f, amp), K(2f, -amp)],
            });
        if (rig.Bones.Any(b => b.Name == "head"))
            plan.Bones.Add(new MotionBoneTrack
            {
                Bone = "head",
                Rotate = [K(0, 0), K(0.5f, -amp), K(1.5f, amp), K(2f, 0)],
            });
        return plan;

        static MotionPlan PlanFrom(string name, RigManifest _, float duration) => new()
        {
            Animation = name,
            Loop = true,
            Duration = duration,
            Bones = [],
        };
    }

    private static MotionKey K(float t, float v) => new() { Time = t, Value = v };

    private static async Task<string> ChatOnceAsync(HttpClient http, string baseUrl, string model,
        string userMessage, CancellationToken ct)
    {
        var payload = new
        {
            model,
            messages = new object[]
            {
                new { role = "system", content = "You are a 2D animation keyframe generator. Output only JSON." },
                new { role = "user", content = userMessage },
            },
            temperature = 0.2,
            response_format = new { type = "json_object" },
        };

        using HttpResponseMessage response = await http.PostAsJsonAsync($"{baseUrl}/chat/completions",
            payload, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message")
            .GetProperty("content").GetString() ?? "";
    }

    private string ToRelative(string absolutePath) =>
        Path.GetRelativePath(_project.ProjectRoot, absolutePath).Replace('\\', '/');
}

public sealed class MotionManifest
{
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("prompt")] public string? Prompt { get; set; }
    [JsonPropertyName("animation")] public string Animation { get; set; } = "";
    [JsonPropertyName("loop")] public bool Loop { get; set; }
    [JsonPropertyName("duration")] public float Duration { get; set; }
    [JsonPropertyName("bones")] public List<string> Bones { get; set; } = [];
    [JsonPropertyName("repairViolations")] public List<string> RepairViolations { get; set; } = [];
}

public sealed class SpineGenAnimateResult
{
    [JsonPropertyName("sliceDirectory")] public string SliceDirectory { get; set; } = "";
    [JsonPropertyName("skeletonJson")] public string SkeletonJson { get; set; } = "";
    [JsonPropertyName("motionManifest")] public string MotionManifest { get; set; } = "";
    [JsonPropertyName("animation")] public string Animation { get; set; } = "";
    [JsonPropertyName("durationSeconds")] public float DurationSeconds { get; set; }
    [JsonPropertyName("loop")] public bool Loop { get; set; }
    [JsonPropertyName("bones")] public List<string> Bones { get; set; } = [];
    [JsonPropertyName("repairRounds")] public int RepairRounds { get; set; }
    [JsonPropertyName("source")] public string Source { get; set; } = "";
}
