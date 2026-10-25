// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Vector;

namespace XEngine.Runtime;

public class GraphicsProgram : IDisposable
{
    private readonly RHI.IGraphicsDevice? _ownerDevice;

    private static int _nextId = 0;

    public int ID { get; }

    // Uniform cache - tracks what values are currently set in this shader program
    internal class UniformCache
    {
        public Dictionary<string, float> floats = [];
        public Dictionary<string, int> ints = [];
        public Dictionary<string, Float2> vectors2 = [];
        public Dictionary<string, Float3> vectors3 = [];
        public Dictionary<string, Float4> vectors4 = [];
        public Dictionary<string, Float4x4> matrices = [];
        public Dictionary<string, GraphicsBuffer> buffers = [];

        public void Clear()
        {
            floats.Clear();
            ints.Clear();
            vectors2.Clear();
            vectors3.Clear();
            vectors4.Clear();
            matrices.Clear();
            buffers.Clear();
        }
    }

    internal UniformCache uniformCache = new();

    // Per-program lookup caches walked by backend property binders.
    internal readonly Dictionary<string, int> uniformLocations = [];
    internal readonly Dictionary<string, uint> blockIndices = [];

    public bool IsDisposed { get; protected set; }

    public uint Handle { get; internal set; }
    public RHI.GpuHandle NativeHandle => new(Handle);

    // Held only until the selected backend compiles the program, then nulled.
    private string? _fragmentSource;
    private string? _vertexSource;
    private string? _geometrySource;

    internal string? PendingFragmentSource => _fragmentSource;
    internal string? PendingVertexSource => _vertexSource;
    internal string? PendingGeometrySource => _geometrySource;

    /// <summary>Clear pending GLSL after a backend has consumed it.</summary>
    internal void ClearPendingSources()
    {
        _fragmentSource = null;
        _vertexSource = null;
        _geometrySource = null;
    }

    internal void MarkCompilationFailed() => IsDisposed = true;

    public GraphicsProgram(string fragmentSource, string vertexSource, string geometrySource) : base()
    {
        ID = System.Threading.Interlocked.Increment(ref _nextId);
        _ownerDevice = Graphics.Device;
        _fragmentSource = fragmentSource;
        _vertexSource = vertexSource;
        _geometrySource = geometrySource;
        Handle = 0;

        // SubmitAndWait so compile / link errors surface synchronously to
        // ShaderPass.TryGetVariantProgram (it catches and falls back).
        using var cmd = Graphics.GetCommandBuffer("GraphicsProgram.Compile");
        cmd.EncodeCompileShader(this);
        Graphics.SubmitAndWait(cmd);
    }

    public static GraphicsProgram? currentProgram = null;

    public void Dispose()
    {
        if (IsDisposed)
            return;
        IsDisposed = true;

        if (!ReferenceEquals(_ownerDevice, Graphics.Device))
            return;

        using var cmd = Graphics.GetCommandBuffer("GraphicsProgram.Dispose");
        cmd.EncodeDisposeShader(this);
        Graphics.Submit(cmd);
    }

    public override string ToString()
    {
        return Handle.ToString();
    }
}
