using System;
using System.IO;
using System.Text.Json;
using XEngine.Editor.Projects;

namespace XEngine.Editor;

/// <summary>
/// <c>--hub-command</c> executor (XHub plan §4.3): reads exactly one JSON request from stdin,
/// executes it, reports NDJSON events on stdout (progress / success / failure with stable error
/// codes), and exits. Runs fully headless — no graphics or registry initialization. V1 covers
/// project creation; module and toolchain commands join with the respective XHub nodes.
/// </summary>
public static class EditorHubCommands
{
    private sealed record HubRequest(int ProtocolVersion, string RequestId, string Type, JsonElement Data)
    {
        public HubRequest() : this(1, "", "noop", default) { }
    }

    public const string CodeUnsupportedCommand = "HUB_UNSUPPORTED_COMMAND";
    public const string CodeBadRequest = "HUB_BAD_REQUEST";
    public const string CodeCreateFailed = "CREATE_PROJECT_FAILED";

    public static void RunOnce()
    {
        string? line = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(line))
        {
            Emit("", "failure", CodeBadRequest, new { message = "empty stdin" });
            Environment.ExitCode = 1;
            return;
        }

        HubRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<HubRequest>(line,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            Emit("", "failure", CodeBadRequest, new { message = ex.Message });
            Environment.ExitCode = 1;
            return;
        }
        if (request == null || string.IsNullOrWhiteSpace(request.Type))
        {
            Emit("", "failure", CodeBadRequest, new { message = "missing type" });
            Environment.ExitCode = 1;
            return;
        }

        string requestId = request.RequestId ?? "";
        switch (request.Type)
        {
            case "createProject":
                RunCreateProject(requestId, request.Data);
                return;
            default:
                Emit(requestId, "failure", CodeUnsupportedCommand, new { command = request.Type });
                Environment.ExitCode = 1;
                return;
        }
    }

    private static void RunCreateProject(string requestId, JsonElement data)
    {
        string parentFolder = GetString(data, "parentFolder");
        string projectName = GetString(data, "projectName");
        if (parentFolder.Length == 0 || projectName.Length == 0)
        {
            Emit(requestId, "failure", CodeBadRequest, new { message = "parentFolder and projectName are required" });
            Environment.ExitCode = 1;
            return;
        }

        try
        {
            Emit(requestId, "progress", "", new { stage = "creating", message = parentFolder });
            var project = Project.Create(parentFolder, projectName);
            Emit(requestId, "progress", "", new { stage = "seeding", message = project.RootPath });
            Emit(requestId, "success", "", new
            {
                projectRoot = project.RootPath,
                name = project.Name,
                engineVersion = ProjectEngineVersion.Canonicalize(
                    XEngine.Editor.Core.EditorApplication.CanonicalEngineVersion),
            });
        }
        catch (Exception ex)
        {
            Emit(requestId, "failure", CodeCreateFailed, new { message = ex.Message });
            Environment.ExitCode = 1;
        }
    }

    private static string GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static void Emit(string requestId, string type, string code, object data)
    {
        var envelope = new
        {
            protocolVersion = HubSession.ProtocolVersion,
            requestId,
            type,
            code,
            data,
            ts = DateTime.UtcNow.ToString("o"),
        };
        Console.WriteLine(JsonSerializer.Serialize(envelope));
    }
}
