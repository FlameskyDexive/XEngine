// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text;

namespace XEngine.Runtime.Rendering;

/// <summary>Unity-style display names for Frame Debugger events.</summary>
public static class FrameDebuggerLabels
{
    /// <summary>"color+depth+stencil" style flag list.</summary>
    public static string DescribeClearFlags(ClearFlags flags)
    {
        var sb = new StringBuilder(24);
        if ((flags & ClearFlags.Color) != 0) sb.Append("color");
        if ((flags & ClearFlags.Depth) != 0)
        {
            if (sb.Length > 0) sb.Append('+');
            sb.Append("depth");
        }
        if ((flags & ClearFlags.Stencil) != 0)
        {
            if (sb.Length > 0) sb.Append('+');
            sb.Append("stencil");
        }
        return sb.Length > 0 ? sb.ToString() : "none";
    }

    /// <summary>Unity-style event kind for the details header: "Event #N &lt;kind&gt;".</summary>
    public static string DescribeKind(FrameEvent ev, CapturedOp? op)
    {
        switch (ev.Kind)
        {
            case FrameEventKind.Clear:
                return op != null ? $"Clear ({DescribeClearFlags(op.ClearFlags)})" : "Clear";
            case FrameEventKind.Draw:
            case FrameEventKind.DrawInstanced:
            {
                bool instanced = ev.Kind == FrameEventKind.DrawInstanced;
                if (op?.Opcode == CommandOpcode.DrawIndexedIndirect)
                    return "Draw Indirect";
                if (op?.Opcode == CommandOpcode.DrawArrays)
                    return "Draw Procedural";
                if (ev.Annotation != null && !string.IsNullOrEmpty(ev.Annotation.MeshName))
                    return instanced ? "Draw Mesh (instanced)" : "Draw Mesh";
                return instanced ? "Draw (instanced)" : "Draw";
            }
            case FrameEventKind.Blit:
                return "Blit";
            case FrameEventKind.Dispatch:
                return op != null && op.Opcode == CommandOpcode.DispatchIndirect
                    ? "Dispatch Indirect"
                    : "Dispatch Compute";
            case FrameEventKind.SetRenderTarget:
                return "RenderTarget";
            case FrameEventKind.UpdateResource:
                return "Resource Barrier";
            default:
                return ev.Kind.ToString();
        }
    }
}
