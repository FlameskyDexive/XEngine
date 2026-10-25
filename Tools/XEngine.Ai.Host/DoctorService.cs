using XEngine.Ai.Protocol;

namespace XEngine.Ai.Host;

/// <summary>
/// Runs environment diagnostics and returns a structured <see cref="DoctorReport"/>. Each check
/// is independent and returns Passed/Warning/Failed/Skipped with a suggested action so an agent
/// can self-heal (XMcp.DoctorService pattern). De-Unity-ified: checks .NET, the XEngine project
/// file, the solution, and protocol version rather than Unity/UPM.
/// </summary>
internal sealed class DoctorService
{
    private readonly ProjectDescriptor _project;

    public DoctorService(ProjectDescriptor project)
    {
        _project = project;
    }

    public Task<DoctorReport> RunAsync(CancellationToken cancellationToken)
    {
        List<DoctorCheck> checks =
        [
            CheckDotNetRuntime(),
            CheckProjectDirectory(),
            CheckProjectFile(),
            CheckSolution(),
            CheckGitBranch(),
            CheckProtocolVersion(),
        ];

        DoctorStatus overall = checks switch
        {
            _ when checks.Any(c => c.Status == DoctorStatus.Failed) => DoctorStatus.Failed,
            _ when checks.Any(c => c.Status == DoctorStatus.Warning) => DoctorStatus.Warning,
            _ => DoctorStatus.Passed,
        };

        return Task.FromResult(new DoctorReport(
            Status: overall,
            ProjectId: _project.ProjectId,
            Checks: checks,
            CheckedAtUtc: DateTimeOffset.UtcNow));
    }

    private DoctorCheck CheckDotNetRuntime()
    {
        Version version = Environment.Version;
        // The Host targets net10.0; warn if somehow running under an older runtime.
        if (version.Major < 10)
        {
            return new DoctorCheck(
                "dotnet-runtime",
                DoctorStatus.Warning,
                $"Running on .NET runtime {version}; XEngine targets .NET 10.",
                "Install the .NET 10 SDK.");
        }

        return new DoctorCheck(
            "dotnet-runtime",
            DoctorStatus.Passed,
            $"Running on .NET runtime {version}.");
    }

    private DoctorCheck CheckProjectDirectory()
    {
        return Directory.Exists(_project.ProjectRoot)
            ? new DoctorCheck("project-directory", DoctorStatus.Passed, _project.ProjectRoot)
            : new DoctorCheck(
                "project-directory",
                DoctorStatus.Failed,
                $"Project directory not found: {_project.ProjectRoot}",
                "Pass --project <path> pointing at an XEngine project root.");
    }

    private DoctorCheck CheckProjectFile()
    {
        string? xengineFile = Directory.GetFiles(_project.ProjectRoot, "*.xengine").FirstOrDefault();
        return xengineFile is not null
            ? new DoctorCheck("project-file", DoctorStatus.Passed, Path.GetFileName(xengineFile))
            : new DoctorCheck(
                "project-file",
                DoctorStatus.Warning,
                "No *.xengine project file found (running against a non-game project, e.g. the engine repo).",
                null);
    }

    private DoctorCheck CheckSolution()
    {
        return _project.SolutionPath is not null && File.Exists(_project.SolutionPath)
            ? new DoctorCheck("solution", DoctorStatus.Passed, _project.SolutionPath)
            : new DoctorCheck(
                "solution",
                DoctorStatus.Warning,
                "XEngine.sln not found at project root (code-symbol/reference tools will be unavailable).",
                "Run xengine-cli against the engine repo root, or a project with a linked solution.");
    }

    private DoctorCheck CheckGitBranch()
    {
        return _project.GitBranch is not null
            ? new DoctorCheck("git-branch", DoctorStatus.Passed, _project.GitBranch)
            : new DoctorCheck("git-branch", DoctorStatus.Skipped, "Not a git repository.");
    }

    private DoctorCheck CheckProtocolVersion()
    {
        return new DoctorCheck(
            "protocol-version",
            DoctorStatus.Passed,
            $"Protocol {ProtocolConstants.CurrentVersion}, Host {typeof(DoctorService).Assembly.GetName().Version}.");
    }
}
