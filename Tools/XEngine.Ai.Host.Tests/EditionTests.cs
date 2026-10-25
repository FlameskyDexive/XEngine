// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Reflection;
using ModelContextProtocol.Server;
using XEngine.Ai.Host;
using XEngine.Ai.Protocol;
using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>
/// ★ Edition split — the same xengine-cli binary serves two audiences. The engine repository
/// (engine-dev) gets the full surface; a user game project (user) never sees engine-source tools.
/// These tests pin: layout detection, the type-level registration filter, the capability snapshot
/// honesty per edition, the --edition flag parsing, and the CLI verb gate.
/// </summary>
public class EditionTests : IDisposable
{
    private readonly string _root;
    private static readonly Assembly HostAssembly = typeof(AiTools).Assembly;

    public EditionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "xengine-edition-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // ---- AiEditionDetector ----

    [Fact]
    public void Detector_EngineRepoLayout_IsEngineDev()
    {
        File.WriteAllText(Path.Combine(_root, "XEngine.sln"), "");
        Directory.CreateDirectory(Path.Combine(_root, "Tools"));

        Assert.Equal(AiEdition.EngineDev, AiEditionDetector.Detect(_root));
    }

    [Fact]
    public void Detector_UserProjectLayout_IsUser()
    {
        File.WriteAllText(Path.Combine(_root, "Game.xengine"), "{}");
        Directory.CreateDirectory(Path.Combine(_root, "Assets"));

        Assert.Equal(AiEdition.User, AiEditionDetector.Detect(_root));
    }

    [Fact]
    public void Detector_SolutionWithoutToolsDir_IsUser()
    {
        // A game project that ships its own .sln (code_symbols needs it, but it is not the engine
        // repo without Tools/) — the sln alone must not flip the edition.
        File.WriteAllText(Path.Combine(_root, "Game.sln"), "");

        Assert.Equal(AiEdition.User, AiEditionDetector.Detect(_root));
    }

    // ---- ToolTypeCatalog (registration filter) ----

    [Fact]
    public void Catalog_EngineDev_IncludesRestrictedTools()
    {
        Type[] types = ToolTypeCatalog.Collect(HostAssembly, AiEdition.EngineDev);

        Assert.Contains(types, t => t == typeof(EngineDevTools));
        Assert.Contains(types, t => t == typeof(AiTools));
    }

    [Fact]
    public void Catalog_User_ExcludesRestrictedTools()
    {
        Type[] types = ToolTypeCatalog.Collect(HostAssembly, AiEdition.User);

        Assert.DoesNotContain(types, t => t == typeof(EngineDevTools));
        Assert.Contains(types, t => t == typeof(AiTools));
    }

    [Fact]
    public void Catalog_UserToolSurface_IsEngineDevMinusEngineSourceTools()
    {
        var devNames = ToolTypeCatalog.ToolNames(ToolTypeCatalog.Collect(HostAssembly, AiEdition.EngineDev)).ToHashSet();
        var userIdx = ToolTypeCatalog.ToolNames(ToolTypeCatalog.Collect(HostAssembly, AiEdition.User)).ToHashSet();

        Assert.Subset(devNames, userIdx);
        Assert.Equal(new[] { "code_symbols", "run_tests" }, devNames.Except(userIdx).OrderBy(n => n));
    }

    // ---- Capability snapshot honesty per edition ----

    [Fact]
    public void Snapshot_EngineDev_AdvertisesEverything()
    {
        var snapshot = new CapabilityCatalog(Project("dev"), AiEdition.EngineDev).GetSnapshot();

        Assert.Equal("engine-dev", snapshot.Edition);
        Assert.Contains(snapshot.Tools, t => t.Name == "code_symbols");
        Assert.Contains(snapshot.Tools, t => t.Name == "run_tests");
        Assert.Contains(snapshot.Tools, t => t.Name == "scene_object_patch");
        // runtime_* descriptors stay visible in every edition (game-dev live tools).
        Assert.Contains(snapshot.Tools, t => t.Name == "runtime_screenshot");
    }

    [Fact]
    public void Snapshot_User_HidesEngineSourceTools()
    {
        var snapshot = new CapabilityCatalog(Project("user"), AiEdition.User).GetSnapshot();

        Assert.Equal("user", snapshot.Edition);
        Assert.DoesNotContain(snapshot.Tools, t => t.Name == "code_symbols");
        Assert.DoesNotContain(snapshot.Tools, t => t.Name == "run_tests");
        // Game-dev surface stays.
        Assert.Contains(snapshot.Tools, t => t.Name == "asset_import");
        // Packs are rebuilt from the filtered tool list, so no pack lists a hidden tool.
        Assert.All(snapshot.Packs, p => Assert.All(p.Tools,
            name => Assert.DoesNotContain(name, (IEnumerable<string>)["code_symbols", "run_tests"])));
    }

    // ---- HostOptions --edition flag ----

    [Fact]
    public void HostOptions_EditionFlag_Parses()
    {
        HostOptions options = HostOptions.Parse(new[] { "--edition", "user", "--project", _root });
        Assert.Equal(AiEdition.User, options.Edition);

        options = HostOptions.Parse(new[] { "--project", _root, "--edition", "engine-dev" });
        Assert.Equal(AiEdition.EngineDev, options.Edition);
    }

    [Fact]
    public void HostOptions_EditionDefault_AndExplicitAuto_AreNull()
    {
        Assert.Null(HostOptions.Parse(new[] { "--project", _root }).Edition);
        Assert.Null(HostOptions.Parse(new[] { "--project", _root, "--edition", "auto" }).Edition);
    }

    [Fact]
    public void HostOptions_UnknownEditionValue_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => HostOptions.Parse(new[] { "--project", _root, "--edition", "free" }));
    }

    [Fact]
    public void HostCommandParser_SkipsEditionValue()
    {
        // `--edition user` must not make "user" parse as the command verb.
        Assert.Equal(HostCommand.Health, HostCommandParser.Parse(new[] { "health", "--edition", "user" }));
        Assert.Equal(HostCommand.Serve, HostCommandParser.Parse(new[] { "--edition", "user" }));
    }

    // ---- CLI verb gate ----

    [Fact]
    public void VerbGate_EngineVerbs_RequireEngineDev()
    {
        Assert.Equal(AiEdition.EngineDev, HostCommandEdition.RequiredEdition(HostCommand.CodeSymbols));
        Assert.Equal(AiEdition.EngineDev, HostCommandEdition.RequiredEdition(HostCommand.RunTests));
    }

    [Fact]
    public void VerbGate_GameDevVerbs_RunEverywhere()
    {
        Assert.Null(HostCommandEdition.RequiredEdition(HostCommand.SceneList));
        Assert.Null(HostCommandEdition.RequiredEdition(HostCommand.BuildProject));
        Assert.Null(HostCommandEdition.RequiredEdition(HostCommand.FindInFile));
    }

    private ProjectDescriptor Project(string name) =>
        new(name, _root, null, null, null, "edition-test-fingerprint");
}
