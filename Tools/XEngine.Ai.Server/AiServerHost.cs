// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using XEngine.Ai.Protocol;

namespace XEngine.Ai.Server;

/// <summary>
/// ★ Layer 2 (B3) — Hosts the MCP stdio server IN-PROCESS (inside the editor/Player), so an AI agent
/// can drive a LIVE engine instance: read the running scene, subscribe to Debug.OnLog, toggle playmode,
/// eval C# on the main thread. Unlike the standalone xengine-cli Host (Layer 1, file-level), this one
/// shares the engine's process and uses <see cref="MainThreadDispatcher"/> to marshal tool calls onto
/// the main loop.
///
/// <para>Tools are registered edition-aware (see <see cref="ToolRegistrationExtensions"/>): the
/// editor resolves the edition from the opened project and passes it in; today every runtime_* tool
/// serves both editions, but engine-internal tools must never leak into the user surface.</para>
///
/// <para>Lifecycle: <see cref="StartAsync"/> builds the IHost with AddMcpServer + stdio transport and
/// runs it on a background Task; <see cref="StopAsync"/> shuts it down. The editor's per-frame update
/// must call <see cref="Dispatcher"/>.<see cref="MainThreadDispatcher.Drain"/> to execute queued tool
/// work.</para>
/// </summary>
public sealed class AiServerHost : IDisposable
{
    /// <summary>The dispatcher tools use to hop onto the main loop. The editor drains this each frame.</summary>
    public MainThreadDispatcher Dispatcher { get; } = new();

    private IHost? _host;
    private Task? _hostTask;
    private CancellationTokenSource? _cts;
    private readonly AiEdition _edition;
    private readonly System.Reflection.Assembly[] _extraToolAssemblies;

    /// <param name="edition">Tool-surface edition. Null resolves to <see cref="AiEdition.EngineDev"/>
    /// (full surface) — callers that know the target project should pass the detected edition.</param>
    /// <param name="extraToolAssemblies">Extra assemblies to scan for [McpServerTool] methods beyond
    /// this one (e.g. the Editor assembly, so editor-side runtime tools are discovered).</param>
    public AiServerHost(AiEdition? edition, params System.Reflection.Assembly[] extraToolAssemblies)
    {
        _edition = edition ?? AiEdition.EngineDev;
        _extraToolAssemblies = extraToolAssemblies ?? Array.Empty<System.Reflection.Assembly>();
    }

    /// <summary>Start the in-process MCP stdio server. Returns once the host is running in the background.</summary>
    public Task StartAsync()
    {
        if (_host is not null) return Task.CompletedTask;

        _cts = new CancellationTokenSource();

        _host = Microsoft.Extensions.Hosting.Host
            .CreateApplicationBuilder(Environment.GetCommandLineArgs())
            .Host_BuildServer(Dispatcher, _edition, _extraToolAssemblies);

        _hostTask = _host.RunAsync(_cts.Token);
        return Task.CompletedTask;
    }

    /// <summary>Stop the server and release resources.</summary>
    public async Task StopAsync()
    {
        if (_cts is not null) _cts.Cancel();
        if (_hostTask is not null)
        {
            try { await _hostTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected on shutdown */ }
        }

        _host?.Dispose();
        _host = null;
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>Await the background host task (completes when the stdio transport ends / on cancel).
    /// Used by the PoC Program to keep the process alive until the client disconnects.</summary>
    public Task WaitUntilStoppedAsync() => _hostTask ?? Task.CompletedTask;

    public void Dispose() => _ = StopAsync();
}

internal static class AiServerHostBuilderExtensions
{
    // Separated so the build step is readable: clear stdout logging (stdio is the transport), register
    // the dispatcher as a singleton so tools can inject it, and reflect tools from this assembly + any
    // extra assemblies (e.g. the Editor) passed by the host — filtered by edition.
    public static IHost Host_BuildServer(this HostApplicationBuilder builder, MainThreadDispatcher dispatcher, AiEdition edition, System.Reflection.Assembly[] extraToolAssemblies)
    {
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(c => c.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services.AddSingleton(dispatcher);
        var mcp = builder.Services.AddMcpServer().WithStdioServerTransport();
        mcp.WithEditionTools(typeof(AiServerHost).Assembly, edition);
        foreach (var asm in extraToolAssemblies)
            mcp.WithEditionTools(asm, edition);

        return builder.Build();
    }
}
