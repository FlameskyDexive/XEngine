using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;
using XEngine.Ai.Protocol;

namespace XEngine.Ai.Host;

/// <summary>
/// Builds the capability snapshot advertised to clients. ★ Fixed: previously a hardcoded 6-tool list
/// that drifted badly from what WithToolsFromAssembly() actually registers. This now DERIVES the tool
/// list by reflecting [McpServerTool] + [Description] attributes off the Host assembly, so the
/// advertised surface can never lie about what is registered. Tool group/risk metadata comes from a
/// small lookup keyed by tool name (new tools default to group="misc", risk=ReadOnly).
/// </summary>
internal sealed class CapabilityCatalog
{
    private const string HostVersion = "0.1.0";
    private readonly ProjectDescriptor _project;
    private readonly AiEdition _edition;
    private readonly Assembly _toolsAssembly;

    public CapabilityCatalog(ProjectDescriptor project) : this(project, AiEdition.EngineDev) { }

    // Overload lets tests inject a known edition + assembly.
    public CapabilityCatalog(ProjectDescriptor project, AiEdition edition)
        : this(project, edition, typeof(AiTools).Assembly) { }

    public CapabilityCatalog(ProjectDescriptor project, AiEdition edition, Assembly toolsAssembly)
    {
        _project = project;
        _edition = edition;
        _toolsAssembly = toolsAssembly;
    }

    public CapabilitySnapshot GetSnapshot()
    {
        // Reflect only the tool types visible to THIS edition (same filter the MCP registration
        // uses), so the snapshot can't advertise tools the server never registered.
        CapabilityToolDescriptor[] tools = ReflectTools(ToolTypeCatalog.Collect(_toolsAssembly, _edition));
        // ★ P2-6: append the static runtime_* descriptors (in-process tools the Host can't reflect)
        // so the AI knows they exist and that they require a running editor. All runtime_* tools are
        // game-dev surface → visible in every edition.
        tools = tools.Concat(RuntimeToolDescriptors()).OrderBy(t => t.Name, StringComparer.Ordinal).ToArray();

        CapabilityPackDescriptor[] packs = BuildPacks(tools);

        Dictionary<string, int> limits = new()
        {
            ["maxRequestBytes"] = ProtocolConstants.Limits.MaxRequestBytes,
            ["maxResponseBytes"] = ProtocolConstants.Limits.MaxResponseBytes,
            ["maxIdempotencyKeyLength"] = ProtocolConstants.Limits.MaxIdempotencyKeyLength,
            ["maxObjectsPerHierarchy"] = ProtocolConstants.Limits.MaxObjectsPerHierarchy,
            ["operationRetentionDays"] = ProtocolConstants.Limits.OperationRetentionDays,
        };

        return new CapabilitySnapshot(
            ProtocolVersion: ProtocolConstants.CurrentVersion,
            HostVersion: HostVersion,
            ProjectId: _project.ProjectId,
            Edition: _edition.ToWireName(),
            Tools: tools,
            Packs: packs,
            Limits: limits,
            GeneratedAtUtc: DateTimeOffset.UtcNow);
    }

    private CapabilityToolDescriptor[] ReflectTools(Type[] visibleToolTypes)
    {
        var list = new List<CapabilityToolDescriptor>();
        foreach (Type type in visibleToolTypes)
        {
            if (!type.IsClass || !type.IsDefined(typeof(McpServerToolTypeAttribute), inherit: false)) continue;

            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            {
                McpServerToolAttribute? attr = method.GetCustomAttribute<McpServerToolAttribute>();
                if (attr is null) continue;

                string name = !string.IsNullOrEmpty(attr.Name) ? attr.Name! : method.Name;
                string description = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";
                (string group, RiskLevel risk) = MetadataFor(name);
                list.Add(new CapabilityToolDescriptor(
                    Name: name,
                    Group: group,
                    Description: description,
                    RiskLevel: risk,
                    // ★ P2-6: runtime_* tools (screenshot/stats/selection/menu/eval/playmode/logs/
                    // live_scene/state) only work in the in-process server (editor --serve) because they
                    // touch live engine state. Everything in the offline Host is engine-agnostic.
                    RequiresEngine: name.StartsWith("runtime_", StringComparison.Ordinal),
                    // ★ Gen-P0.1 — asset file ops (import/move/delete) have no dryRun parameter today;
                    // exclude them from the name heuristic so the advertised flag stays truthful.
                    // ★ Gen-P1.2 — gen_submit takes an explicit dryRun (hash + validation, no billing).
                    SupportsDryRun: name is "gen_submit" or "gen_ensure_budget" or "character_wire"
                                  or "spinegen_slice"
                                  or "sprite_slice" or "sprite_wire" or "clip_retarget"
                                  || (name is not ("asset_import" or "asset_move" or "asset_delete")
                                  && (name.Contains("patch", StringComparison.OrdinalIgnoreCase)
                                  || name.Contains("create", StringComparison.OrdinalIgnoreCase)
                                  || name.Contains("delete", StringComparison.OrdinalIgnoreCase)
                                  || name.Contains("add", StringComparison.OrdinalIgnoreCase)
                                  || name.Contains("remove", StringComparison.OrdinalIgnoreCase)
                                  || name.Contains("build", StringComparison.OrdinalIgnoreCase)
                                  || name.Contains("workflow_run", StringComparison.OrdinalIgnoreCase))),
                    SupportsCancel: false,
                    MayReloadScripts: name == "build_project" || name.Contains("compile", StringComparison.OrdinalIgnoreCase),
                    Version: HostVersion));
            }
        }

        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
        return list.ToArray();
    }

    /// <summary>★ P2-6 — The runtime_* tools live in the XEngine.Editor assembly (the in-process
    /// server, editor --serve) and are invisible to the offline Host's reflection scan. So the AI
    /// has a truthful picture of the FULL surface, append a static descriptor for each. All require a
    /// live engine. (If the Host ever links the Editor assembly, this becomes redundant — the
    /// ReflectTools scan would pick them up.)</summary>
    private static readonly string[] RuntimeToolNames =
    {
        "runtime_state", "runtime_logs", "runtime_playmode", "runtime_live_scene",
        "runtime_screenshot", "runtime_stats", "runtime_render_debug", "runtime_frame_debug", "runtime_panel_screenshot",
        "runtime_selection", "runtime_menu", "runtime_eval",
        "runtime_refresh", "runtime_animation_bind", "runtime_animation_record_key",
        "runtime_animator_state", "runtime_animator_set_parameter", "runtime_animator_play",
        "runtime_animation_preview",
        "runtime_nav_state", "runtime_nav_bake", "runtime_nav_agent_move", "runtime_nav_agent_state",
        "runtime_spine_state", "runtime_spine_play", "runtime_spine_queue", "runtime_spine_set_skin",
        "runtime_vfx_state", "runtime_vfx_preview",
    };

    private static IEnumerable<CapabilityToolDescriptor> RuntimeToolDescriptors() =>
        RuntimeToolNames.Select(name =>
        {
            (string group, RiskLevel risk) = MetadataFor(name);
            return new CapabilityToolDescriptor(
                Name: name,
                Group: group,
                Description: "In-process only (editor --serve). Touches live engine state — requires a running editor.",
                RiskLevel: risk,
                RequiresEngine: true,
                SupportsDryRun: false,
                SupportsCancel: false,
                MayReloadScripts: name == "runtime_eval",
                Version: HostVersion);
        });

    /// <summary>Group + risk classification by tool name prefix. New tools default to misc/ReadOnly.</summary>
    private static (string group, RiskLevel risk) MetadataFor(string name)
    {
        // ★ A2 — three.js authoring chain: validate/preview are read-only; export writes artifacts.
        if (name.StartsWith("three_", StringComparison.Ordinal))
        {
            bool write = name.Contains("export", StringComparison.Ordinal);
            return ("three", write ? RiskLevel.SafeWrite : RiskLevel.ReadOnly);
        }
        if (name == "animator_component_add") return ("scene-write", RiskLevel.SafeWrite);
        if (name.StartsWith("animator_", StringComparison.Ordinal)
            || name.StartsWith("clip_", StringComparison.Ordinal)
            || name.StartsWith("blendtree_", StringComparison.Ordinal)
            || name.StartsWith("overridecontroller_", StringComparison.Ordinal)
            || name.StartsWith("avatarmask_", StringComparison.Ordinal))
        {
            bool write = name.Contains("patch", StringComparison.Ordinal)
                || name.Contains("create", StringComparison.Ordinal)
                || name.Contains("delete", StringComparison.Ordinal)
                || name.Contains("add", StringComparison.Ordinal)
                || name.Contains("remove", StringComparison.Ordinal)
                || name.Contains("move", StringComparison.Ordinal)
                || name.Contains("reorder", StringComparison.Ordinal)
                || name.Contains("retarget", StringComparison.Ordinal);
            return ("animation", write ? RiskLevel.SafeWrite : RiskLevel.ReadOnly);
        }
        if (name.StartsWith("scene_object_", StringComparison.Ordinal)) return ("scene-write", RiskLevel.SafeWrite);
        // ★ scene_component_* / prefab_component_* are write ops (add/remove/patch) — must be checked
        // before the generic scene_/prefab_ ReadOnly catch-all, or the snapshot mislabels them ReadOnly.
        if (name.StartsWith("scene_component_", StringComparison.Ordinal)) return ("scene-write", RiskLevel.SafeWrite);
        if (name.StartsWith("prefab_component_", StringComparison.Ordinal)) return ("scene-write", RiskLevel.SafeWrite);
        if (name.StartsWith("prefab_object_", StringComparison.Ordinal)) return ("scene-write", RiskLevel.SafeWrite);
        if (name.StartsWith("scene_settings_", StringComparison.Ordinal)) return ("scene-write", RiskLevel.SafeWrite);
        if (name.StartsWith("script_", StringComparison.Ordinal)) return ("code", RiskLevel.SafeWrite);
        if (name == "material_patch" || name == "asset_data_patch" || name == "volume_patch") return ("asset", RiskLevel.SafeWrite);
        if (name == "volume_inspect") return ("asset", RiskLevel.ReadOnly);
        if (name.StartsWith("shadergraph_", StringComparison.Ordinal))
        {
            bool write = name.Contains("patch", StringComparison.Ordinal)
                || name.Contains("compile", StringComparison.Ordinal);
            return ("asset", write ? RiskLevel.SafeWrite : RiskLevel.ReadOnly);
        }
        if (name.StartsWith("vfxgraph_", StringComparison.Ordinal)
            || name.StartsWith("runtime_vfx_", StringComparison.Ordinal))
        {
            bool write = name.Contains("patch", StringComparison.Ordinal)
                || name.Contains("compile", StringComparison.Ordinal)
                || name.Contains("create", StringComparison.Ordinal)
                || name.Contains("preview", StringComparison.Ordinal);
            return ("vfx", write ? RiskLevel.SafeWrite : RiskLevel.ReadOnly);
        }
        if (name.StartsWith("spinegen_", StringComparison.Ordinal))
            return ("spinegen", RiskLevel.SafeWrite);
        if (name.StartsWith("psd_", StringComparison.Ordinal))
            return ("2d", name.Contains("render", StringComparison.Ordinal) ? RiskLevel.SafeWrite : RiskLevel.ReadOnly);
        if (name.StartsWith("pxl_", StringComparison.Ordinal))
            return ("xui", name.Contains("render", StringComparison.Ordinal) ? RiskLevel.SafeWrite : RiskLevel.ReadOnly);
        if (name.StartsWith("xui_", StringComparison.Ordinal)
            || name.StartsWith("runtime_xui_", StringComparison.Ordinal))
        {
            bool write = name.Contains("patch", StringComparison.Ordinal)
                || name.Contains("scaffold", StringComparison.Ordinal)
                || name.Contains("extract", StringComparison.Ordinal)
                || name.Contains("from_psd", StringComparison.Ordinal)
                || name.Contains("bake", StringComparison.Ordinal)
                || name.Contains("open", StringComparison.Ordinal)
                || name.Contains("reload", StringComparison.Ordinal)
                || name.Contains("click", StringComparison.Ordinal);
            return ("xui", write ? RiskLevel.SafeWrite : RiskLevel.ReadOnly);
        }
        if (name == "run_tests") return ("testing", RiskLevel.ProcessControl);
        // ★ Gen-P1.2 — generation gateway: status is a read; submit spends provider credits and
        // fetch writes files into Assets/, both SafeWrite (journaled, idempotent).
        if (name == "gen_status" || name == "gen_candidates") return ("generate", RiskLevel.ReadOnly);
        if (name == "gen_candidate_choose" || name == "gen_candidate_discard") return ("generate", RiskLevel.SafeWrite);
        if (name == "gen_ensure_budget") return ("generate", RiskLevel.SafeWrite);
        if (name.StartsWith("gen_", StringComparison.Ordinal)) return ("generate", RiskLevel.SafeWrite);
        if (name == "asset_mesh_stats") return ("asset", RiskLevel.ReadOnly);
        if (name == "scene_collider_add") return ("scene-write", RiskLevel.SafeWrite);
        if (name == "prefab_create") return ("scene-write", RiskLevel.SafeWrite);
        if (name == "character_wire") return ("animation", RiskLevel.SafeWrite);
        if (name == "sprite_slice") return ("asset", RiskLevel.SafeWrite);
        if (name == "sprite_wire") return ("scene-write", RiskLevel.SafeWrite);
        if (name == "style_anchor_list") return ("generate", RiskLevel.ReadOnly);
        if (name == "style_anchor_upsert") return ("generate", RiskLevel.SafeWrite);
        if (name.StartsWith("scene_", StringComparison.Ordinal)) return ("scene-read", RiskLevel.ReadOnly);
        // ★ Gen-P0.1 — asset file ops mutate disk; the generic asset_ catch-all below used to label
        // them ReadOnly, so agents gating on risk saw asset_import/move/delete as harmless reads.
        // import/move are SafeWrite (journaled, GUID-preserving); delete removes files → Destructive.
        if (name == "asset_import" || name == "asset_move") return ("asset", RiskLevel.SafeWrite);
        if (name == "asset_delete") return ("asset", RiskLevel.Destructive);
        // asset_data_patch matched above; the remaining asset_data_* (inspect) is a read.
        if (name.StartsWith("asset_data_", StringComparison.Ordinal)) return ("asset", RiskLevel.ReadOnly);
        if (name.StartsWith("asset_", StringComparison.Ordinal)) return ("asset", RiskLevel.ReadOnly);
        if (name == "journal_query") return ("journal", RiskLevel.ReadOnly);
        if (name.StartsWith("code_", StringComparison.Ordinal)) return ("code", RiskLevel.ReadOnly);
        if (name == "build_project") return ("build", RiskLevel.ProcessControl);
        if (name == "run_headless") return ("run", RiskLevel.ProcessControl);
        if (name.StartsWith("workflow_", StringComparison.Ordinal)) return ("workflow", RiskLevel.SafeWrite);
        if (name.StartsWith("nav_", StringComparison.Ordinal)
            || name.StartsWith("runtime_nav_", StringComparison.Ordinal))
        {
            bool write = name.Contains("patch", StringComparison.Ordinal)
                || name.Contains("export", StringComparison.Ordinal)
                || name.Contains("bake", StringComparison.Ordinal)
                || name.Contains("move", StringComparison.Ordinal);
            return ("navigation", write ? RiskLevel.SafeWrite : RiskLevel.ReadOnly);
        }
        if (name.StartsWith("runtime_", StringComparison.Ordinal)) return ("runtime", RiskLevel.SafeWrite);
        if (name == "operation_cancel") return ("system", RiskLevel.ProcessControl);
        if (name.StartsWith("package_", StringComparison.Ordinal))
        {
            // Registry reads are ReadOnly; manifest mutations and publishing are SafeWrite.
            bool write = name is "package_add" or "package_remove" or "package_install" or "package_update" or "package_publish";
            return ("packages", write ? RiskLevel.SafeWrite : RiskLevel.ReadOnly);
        }
        if (name.StartsWith("xengine_", StringComparison.Ordinal) || name == "project_inspect"
            || name.StartsWith("operation_", StringComparison.Ordinal) || name == "package_graph")
        {
            return ("system", RiskLevel.ReadOnly);
        }

        return ("misc", RiskLevel.ReadOnly);
    }

    private static CapabilityPackDescriptor[] BuildPacks(CapabilityToolDescriptor[] tools)
    {
        var byGroup = tools.GroupBy(t => t.Group).OrderBy(g => g.Key, StringComparer.Ordinal);
        return byGroup
            .Select(g => new CapabilityPackDescriptor(g.Key, HostVersion, "stable", g.Select(t => t.Name).ToArray()))
            .ToArray();
    }
}
