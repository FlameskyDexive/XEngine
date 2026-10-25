// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Single source of truth for which opcodes are Frame Debugger tree leaves. The decoder's
/// ShowInTree and every backend's NoteLeafGpuOpAndShouldSkip call sites must agree with this
/// set exactly, or the selected tree index would truncate at the wrong GPU op.
/// SetComputeShader is deliberately NOT a leaf — it is state binding, like SetShader.
/// </summary>
internal static class FrameDebuggerOpClassifier
{
    public static bool IsLeafGpuOp(CommandOpcode op) => op switch
    {
        CommandOpcode.SetRenderTarget or
        CommandOpcode.SetRenderTargets or
        CommandOpcode.ClearRenderTarget or
        CommandOpcode.BlitFramebuffer or
        CommandOpcode.ResolveTexture or
        CommandOpcode.DrawIndexed or
        CommandOpcode.DrawIndexedInstanced or
        CommandOpcode.DrawArrays or
        CommandOpcode.DrawIndexedIndirect or
        CommandOpcode.DispatchCompute or
        CommandOpcode.DispatchIndirect or
        CommandOpcode.ResourceBarrier => true,
        _ => false,
    };
}
