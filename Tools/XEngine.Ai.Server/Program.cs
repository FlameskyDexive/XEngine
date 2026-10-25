// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Threading;
using System.Threading.Tasks;
using XEngine.Ai.Server;

namespace XEngine.Ai.Server;

/// <summary>
/// PoC entry point: runs the in-process MCP stdio server while a background timer drains the
/// dispatcher (simulating the engine main loop). In the real editor this Main is absent — the editor
/// constructs AiServerHost at startup and calls Dispatcher.Drain() in its per-frame update.
/// </summary>
internal static class Program
{
    public static async Task Main(string[] args)
    {
        using var host = new AiServerHost(edition: null);
        await host.StartAsync();

        // Simulate the engine main loop: drain the dispatcher ~60 Hz so tool handlers execute.
        using var loopCts = new CancellationTokenSource();
        Task loop = Task.Run(async () =>
        {
            while (!loopCts.IsCancellationRequested)
            {
                host.Dispatcher.Drain();
                await Task.Delay(16, loopCts.Token).ConfigureAwait(false);
            }
        }, loopCts.Token);

        // The host's background RunAsync task completes when the stdio transport ends (stdin EOF from
        // the client) or on cancellation. Await it, then clean up. (Ctrl+C cancels via the runtime.)
        try
        {
            await host.WaitUntilStoppedAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C / shutdown
        }

        loopCts.Cancel();
        await host.StopAsync();
    }
}
