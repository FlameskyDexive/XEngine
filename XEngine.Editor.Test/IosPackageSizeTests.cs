// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text.Json;

using XEngine.Editor.Build;
using XEngine.Editor.Build.Ios;
using XEngine.Editor.Projects.Build;
using XEngine.Editor.Projects.Scripting;
using XEngine.Editor.Projects.Settings;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class IosPackageSizeTests : EditorTestHarness
{
    public IosPackageSizeTests()
    {
        EditorRegistries.Initialize();
        EditorRegistries.OnProjectOpened();
    }

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "XEngineIosPackageSizeTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task MeasureAndWrite_ClassifiesExportAndKeepsPhysicalDeviceEvidenceNotRun()
    {
        Directory.CreateDirectory(_root);
        IosPlayerProjectLayout layout = await IosPlayerProjectGenerator.GenerateAsync(
            Path.Combine(_root, "export"),
            CreateOptions());
        IosPackageSizeSnapshot snapshot = IosPackageSizeReport.MeasureAndWrite(
            layout.RootDirectory,
            IosPackageSizeBudget.Disabled);
        IosP6Evidence.WriteStartupSkip(layout.RootDirectory);
        IosP6Evidence.WriteDeviceMatrixSkip(layout.RootDirectory);

        Assert.True(snapshot.PackageBytes > 0);
        Assert.Contains(snapshot.Categories, category => category.Id == "managed" && category.Bytes > 0);
        using JsonDocument size = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(layout.RootDirectory, "verification", IosPackageSizeReport.FileName)));
        Assert.False(size.RootElement.GetProperty("signedApp").GetBoolean());
        using JsonDocument startup = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(layout.RootDirectory, "verification", IosP6Evidence.StartupFileName)));
        Assert.False(startup.RootElement.GetProperty("hostCanMeasure").GetBoolean());
        Assert.Equal(System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            startup.RootElement.GetProperty("hostOSDescription").GetString());
        Assert.Equal("not-run", startup.RootElement.GetProperty("status").GetString());
        Assert.Equal(0, startup.RootElement.GetProperty("sampleCount").GetInt32());
        Assert.Contains("physical-device", startup.RootElement.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, startup.RootElement.GetProperty("p50Ms").ValueKind);
        Assert.Equal(JsonValueKind.Null, startup.RootElement.GetProperty("p95Ms").ValueKind);
        using JsonDocument matrix = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(layout.RootDirectory, "verification", IosP6Evidence.DeviceMatrixFileName)));
        Assert.False(matrix.RootElement.GetProperty("supported").GetBoolean());
        Assert.False(matrix.RootElement.GetProperty("hostCanRun").GetBoolean());
        Assert.Equal(System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            matrix.RootElement.GetProperty("hostOSDescription").GetString());
        Assert.Equal("not-run", matrix.RootElement.GetProperty("status").GetString());
        Assert.False(matrix.RootElement.GetProperty("physicalDeviceVerified").GetBoolean());
        Assert.Contains("Simulator", matrix.RootElement.GetProperty("reason").GetString());
        Assert.Equal(0, matrix.RootElement.GetProperty("cyclesPassed").GetInt32());
    }

    [Fact]
    public async Task MeasureAndWrite_EnforcesManagedBudget()
    {
        Directory.CreateDirectory(_root);
        IosPlayerProjectLayout layout = await IosPlayerProjectGenerator.GenerateAsync(
            Path.Combine(_root, "budget"),
            CreateOptions());
        var budget = new IosPackageSizeBudget(0, MaxManagedBytes: 1, 0, 0, 0);
        IosPackageSizeSnapshot snapshot = IosPackageSizeReport.MeasureAndWrite(layout.RootDirectory, budget);
        string report = Path.Combine(layout.RootDirectory, "verification", IosPackageSizeReport.FileName);
        IosPackageSizeBudgetExceededException ex = Assert.Throws<IosPackageSizeBudgetExceededException>(
            () => snapshot.Enforce(report));
        Assert.Contains("managed", ex.Message, StringComparison.Ordinal);
        Assert.Equal(report, ex.ReportPath);
    }

    [Fact]
    public async Task BuildAsync_WritesP6ReportsAndFailsOffMac()
    {
        string output = Path.Combine(_root, "pipeline");
        Directory.CreateDirectory(_root);
        var compilation = ScriptCompiler.CompileAll(Project);
        Assert.True(compilation.Success, compilation.Output);
        Guid sceneGuid = AuthorEmptyScene();
        var settings = new BuildSettings();
        settings.Scenes.Add(new SceneBuildEntry { Path = "Main.scene", SceneGuid = sceneGuid, Enabled = true });
        var pipeline = new IosBuildPipeline();
        var (result, diagnostics) = await IosPipelineTestLog.CaptureAsync(() => pipeline.BuildAsync(Project.RootPath, settings, output));

        Assert.False(string.IsNullOrWhiteSpace(result.OutputPath), result.Errors + Environment.NewLine + diagnostics);
        Assert.True(File.Exists(Path.Combine(result.OutputPath, "verification", IosPackageSizeReport.FileName)));
        Assert.True(File.Exists(Path.Combine(result.OutputPath, "verification", IosP6Evidence.StartupFileName)));
        Assert.True(File.Exists(Path.Combine(result.OutputPath, "verification", IosP6Evidence.DeviceMatrixFileName)));
        Assert.True(File.Exists(Path.Combine(result.OutputPath, "trimming", IosManagedTypeRootReport.FileName)));
        if (!OperatingSystem.IsMacOS())
        {
            Assert.False(result.Success);
            Assert.Contains("no desktop executable", result.Errors, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task BuildAsync_FullTrimmingFailsClosed()
    {
        string output = Path.Combine(_root, "full");
        Directory.CreateDirectory(_root);
        var settings = new BuildSettings();
        settings.GetProfile<IosBuildProfile>(typeof(IosBuildPipeline)).ManagedStripping = IosManagedStrippingMode.Full;
        var pipeline = new IosBuildPipeline();
        BuildResult result = await pipeline.BuildAsync("irrelevant", settings, output);
        Assert.False(result.Success);
        Assert.Contains("Full managed trimming is blocked", result.Errors, StringComparison.Ordinal);
    }

    public override void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        base.Dispose();
    }

    private static IosPlayerProjectOptions CreateOptions() => new()
    {
        ProjectName = "Size Game",
        ApplicationId = "com.xengine.size",
        ApplicationLabel = "Size Game",
        VersionName = "1.0",
        VersionCode = 1,
        RuntimeIdentifier = "ios-arm64",
        SupportedOSPlatformVersion = "16.0",
        PreferredFramesPerSecond = 60,
        ManagedAssemblies =
        [
            new IosManagedAssemblyInput(
                "XEngine.Runtime",
                typeof(XEngine.Runtime.EngineObject).Assembly.Location,
                "engine-runtime",
                "package size test"),
        ],
    };
}
