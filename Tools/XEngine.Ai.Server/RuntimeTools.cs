// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Server;

namespace XEngine.Ai.Server;

/// <summary>
/// ★ Layer 2 (B3 PoC) — A minimal in-process runtime tool proving the server + dispatcher chain.
/// <c>runtime_health</c> hops onto the "main thread" via the dispatcher and returns the pending
/// queue depth. Engine-facing runtime tools (live scene inspect, Debug.OnLog subscription / logs,
/// playmode, eval) are added in the Editor once the editor loop calls Dispatcher.Drain().
/// </summary>
[McpServerToolType]
public static class RuntimeTools
{
    [McpServerTool(Name = "runtime_health", UseStructuredContent = true)]
    [Description("In-process MCP server health: confirms the server is live inside the engine and the main-thread dispatcher is wired.")]
    public static async Task<RuntimeHealth> Health(MainThreadDispatcher dispatcher)
    {
        // Prove the marshal works: this runs on the "main thread" (whatever drains the dispatcher).
        int depth = await dispatcher.InvokeOnMainThreadAsync(() => dispatcher.PendingCount);
        return new RuntimeHealth("healthy", depth);
    }
}

/// <summary>Health payload for the in-process server.</summary>
public sealed record RuntimeHealth(string Status, int DispatcherPendingCount);
