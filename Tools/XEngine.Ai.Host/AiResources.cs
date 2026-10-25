using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using XEngine.Ai.Protocol;

namespace XEngine.Ai.Host;

/// <summary>
/// ★ MCP Resources primitive — URI-addressable, read-only project data an AI client can read with
/// `resources/read` (an alternative to calling a tool). Surfaces the same data as some tools but in
/// the resource-addressable form clients like Claude Code surface in a "project context" pane.
///
/// <para>URIs: <c>xengine://project/info</c>, <c>xengine://capabilities</c>,
/// <c>xengine://journal</c> (recent), <c>xengine://workflows</c>.</para>
/// </summary>
[McpServerResourceType]
public static class AiResources
{
    [McpServerResource(UriTemplate = "xengine://project/info", Name = "project_info")]
    [Description("The target project's stable identity, engine version and solution path.")]
    public static ProjectDescriptor ProjectInfo(IServiceProvider services)
        => services.GetRequiredService<ProjectDescriptor>();

    [McpServerResource(UriTemplate = "xengine://capabilities", Name = "capabilities")]
    [Description("All tools, capability packs, risk levels and limits the Host exposes.")]
    public static CapabilitySnapshot Capabilities(IServiceProvider services)
        => services.GetRequiredService<CapabilityCatalog>().GetSnapshot();

    [McpServerResource(UriTemplate = "xengine://journal", Name = "journal_recent")]
    [Description("The most recent operation-journal entries (newest-first).")]
    public static JournalPage JournalRecent(IServiceProvider services)
    {
        string root = services.GetRequiredService<ProjectDescriptor>().ProjectRoot;
        return JournalReader.Query(root, new JournalReadQuery(Limit: 50));
    }

    [McpServerResource(UriTemplate = "xengine://workflows", Name = "workflows")]
    [Description("The declarative workflow definitions available in the project.")]
    public static WorkflowListResult Workflows(IServiceProvider services)
        => services.GetRequiredService<WorkflowCatalog>().List();
}
