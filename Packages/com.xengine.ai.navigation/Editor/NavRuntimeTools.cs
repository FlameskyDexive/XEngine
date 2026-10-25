// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.ComponentModel;
using System.Threading.Tasks;

using ModelContextProtocol.Server;
using XEngine.Ai.Server;
using XEngine.Editor.Core;
using XEngine.Runtime;
using XEngine.Runtime.Navigation;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.AI.Navigation.Editor;

/// <summary>
/// In-process MCP tools for live NavMesh bake/query. Registered when
/// <c>com.xengine.ai.navigation</c> Editor is loaded; <see cref="McpToolAssemblyScan"/> picks this assembly up.
/// </summary>
[McpServerToolType]
public static class NavRuntimeTools
{
    [McpServerTool(Name = "runtime_nav_state", UseStructuredContent = true)]
    [Description("Live navmesh snapshot: loaded mesh count, agents, obstacles, off-mesh links, crowd size, scene name.")]
    public static Task<NavLiveStateResult> NavState(MainThreadDispatcher dispatcher)
        => dispatcher.InvokeOnMainThreadAsync(() =>
        {
            Scene? scene = Scene.Current;
            int agents = 0, obstacles = 0, links = 0;
            if (scene != null)
            {
                foreach (GameObject go in scene.AllObjects)
                {
                    if (go.GetComponent<NavMeshAgent>() != null) agents++;
                    if (go.GetComponent<NavMeshObstacle>() != null) obstacles++;
                    if (go.GetComponent<OffMeshLink>() != null) links++;
                }
            }

            return new NavLiveStateResult(
                scene?.Name ?? "(none)",
                NavMesh.HasNavMesh,
                NavMesh.LoadedCount,
                agents,
                obstacles,
                links,
                NavMesh.Crowd.ActiveCount,
                null);
        });

    [McpServerTool(Name = "runtime_nav_bake", UseStructuredContent = true)]
    [Description("Bake a navmesh from the live scene (static collect + Recast) and load it for queries. Does not write a .navmesh file.")]
    public static Task<NavLiveBakeResult> NavBake(MainThreadDispatcher dispatcher)
        => dispatcher.InvokeOnMainThreadAsync(() =>
        {
            Scene? scene = Scene.Current;
            if (scene == null)
                return new NavLiveBakeResult(false, 0, 0, 0, null, "No current scene.");
            try
            {
                NavMeshData data = NavMesh.BuildNavMesh(scene);
                NavMesh.RemoveAllNavMeshData();
                NavMesh.AddNavMeshData(data);
                return new NavLiveBakeResult(true, data.Tiles.Count, data.SettingsHash, data.TileData?.Length ?? 0,
                    new NavLiveVec3(data.Bounds.Min.X, data.Bounds.Min.Y, data.Bounds.Min.Z),
                    null);
            }
            catch (Exception ex)
            {
                return new NavLiveBakeResult(false, 0, 0, 0, null, ex.Message);
            }
        });

    [McpServerTool(Name = "runtime_nav_agent_move", UseStructuredContent = true)]
    [Description("SetDestination on a live NavMeshAgent. identifier is a GameObject Guid; omit to use the current selection.")]
    public static Task<NavLiveAgentMoveResult> NavAgentMove(
        MainThreadDispatcher dispatcher,
        [Description("World X of the destination.")] float x,
        [Description("World Y of the destination.")] float y,
        [Description("World Z of the destination.")] float z,
        [Description("Optional GameObject Identifier (Guid). When omitted, uses the active selection.")] string? identifier = null)
        => dispatcher.InvokeOnMainThreadAsync(() =>
        {
            NavMeshAgent? agent = FindNavMeshAgent(identifier, out string? err, out string? goName);
            if (agent == null)
                return new NavLiveAgentMoveResult(false, goName, x, y, z, false, err);
            bool ok = agent.SetDestination(new Float3(x, y, z));
            return new NavLiveAgentMoveResult(ok, goName, x, y, z, agent.HasPath, ok ? null : "SetDestination failed (no navmesh or unreachable).");
        });

    [McpServerTool(Name = "runtime_nav_agent_state", UseStructuredContent = true)]
    [Description("Live NavMeshAgent snapshot: pose, destination, remaining distance, velocity, path flags. identifier is a GameObject Guid; omit to use the current selection.")]
    public static Task<NavLiveAgentStateResult> NavAgentState(
        MainThreadDispatcher dispatcher,
        [Description("Optional GameObject Identifier (Guid). When omitted, uses the active selection.")] string? identifier = null)
        => dispatcher.InvokeOnMainThreadAsync(() =>
        {
            NavMeshAgent? agent = FindNavMeshAgent(identifier, out string? err, out string? goName);
            if (agent == null)
                return new NavLiveAgentStateResult(false, goName, null, null, null, 0f, null, false, false, false, false, err);
            Float3 pos = agent.Transform.Position;
            Float3 dest = agent.Destination;
            Float3 vel = agent.Velocity;
            return new NavLiveAgentStateResult(
                true, goName,
                new NavLiveVec3(pos.X, pos.Y, pos.Z),
                new NavLiveVec3(dest.X, dest.Y, dest.Z),
                new NavLiveVec3(vel.X, vel.Y, vel.Z),
                agent.RemainingDistance,
                agent.Path.Status.ToString(),
                agent.HasPath,
                agent.IsOnNavMesh,
                agent.IsOnOffMeshLink,
                agent.IsStopped,
                null);
        });

    static NavMeshAgent? FindNavMeshAgent(string? identifier, out string? error, out string? goName)
    {
        goName = null;
        GameObject? go = null;
        if (!string.IsNullOrEmpty(identifier))
        {
            if (!Guid.TryParse(identifier, out Guid guid))
            {
                error = $"Invalid identifier: {identifier}";
                return null;
            }

            go = Scene.Current?.FindObjectByIdentifier<GameObject>(guid);
            if (go is null)
            {
                error = $"GameObject '{identifier}' not found in the current scene.";
                return null;
            }
        }
        else if (Selection.ActiveObject is GameObject selected)
            go = selected;

        if (go is null)
        {
            error = "No GameObject: pass identifier or select one in the scene.";
            return null;
        }

        goName = go.Name;
        NavMeshAgent? agent = go.GetComponent<NavMeshAgent>();
        if (agent == null)
        {
            error = $"GameObject '{go.Name}' has no NavMeshAgent.";
            return null;
        }

        error = null;
        return agent;
    }

    public sealed record NavLiveVec3(float X, float Y, float Z);

    public sealed record NavLiveStateResult(
        string SceneName, bool HasNavMesh, int LoadedCount,
        int AgentCount, int ObstacleCount, int OffMeshLinkCount, int CrowdCount, string? Error);

    public sealed record NavLiveBakeResult(
        bool Succeeded, int TileCount, int SettingsHash, int TileDataBytes, NavLiveVec3? BoundsMin, string? Error);

    public sealed record NavLiveAgentMoveResult(
        bool Succeeded, string? ObjectName, float X, float Y, float Z, bool HasPath, string? Error);

    public sealed record NavLiveAgentStateResult(
        bool Succeeded, string? ObjectName,
        NavLiveVec3? Position, NavLiveVec3? Destination, NavLiveVec3? Velocity,
        float RemainingDistance, string? PathStatus,
        bool HasPath, bool IsOnNavMesh, bool IsOnOffMeshLink, bool IsStopped, string? Error);
}
