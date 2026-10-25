// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime;

public enum LogSeverity
{
    Success = 1 << 0,
    Normal = 1 << 1,
    Warning = 1 << 2,
    Error = 1 << 3,
    Exception = 1 << 4
}


public delegate void OnLog(string message, DebugStackTrace? stackTrace, LogSeverity logSeverity);


public record DebugStackFrame(string FileName, int? Line = null, int? Column = null, string? Method = null)
{
    public override string ToString()
    {
        string locSuffix = Line != null ? Column != null ? $"({Line},{Column})" : $"({Line})" : "";

        if (!string.IsNullOrEmpty(Method))
            return $"In {Method} at {FileName}{locSuffix}";
        else
            return $"At {FileName}{locSuffix}";
    }

}


public record DebugStackTrace(params DebugStackFrame[] StackFrames)
{
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Diagnostic-only path: in a trimmed build, missing method metadata gracefully degrades to a stack frame without method info.")]
    public static explicit operator DebugStackTrace(StackTrace stackTrace)
    {
        DebugStackFrame[] stackFrames = new DebugStackFrame[stackTrace.FrameCount];

        for (int i = 0; i < stackFrames.Length; i++)
        {
            StackFrame srcFrame = stackTrace.GetFrame(i);

            MethodBase? m = srcFrame.GetMethod();
            string? method = m != null ? $"{m.DeclaringType?.Name}.{m.Name}" : null;

            stackFrames[i] = new DebugStackFrame(srcFrame.GetFileName(), srcFrame.GetFileLineNumber(), srcFrame.GetFileColumnNumber(), method);
        }

        return new DebugStackTrace(stackFrames);
    }


    public override string ToString()
    {
        StringBuilder sb = new();

        for (int i = 0; i < StackFrames.Length; i++)
            sb.AppendLine($"\t{StackFrames[i]}");

        return sb.ToString();
    }
}


public static class Debug
{
#if XENGINE_ANDROID
    private const string AndroidLogTag = "XEngine";

    private enum AndroidLogPriority
    {
        Info = 4,
        Warning = 5,
        Error = 6,
    }

    [DllImport("log", EntryPoint = "__android_log_write")]
    private static extern int AndroidLogWrite(
        AndroidLogPriority priority,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string tag,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string message);
#endif

    public static event OnLog? OnLog;

    /// <summary>
    /// -1 unknown, 1 attached, 0 missing. Release WinExe has no console; Title/ForegroundColor
    /// throw IOException (invalid handle). Probe once and skip afterwards.
    /// </summary>
    private static int s_consoleUi = -1;

    /// <summary>
    /// Sets the process console title when a console exists. No-ops for WinExe / hosts without one.
    /// </summary>
    internal static void TrySetConsoleTitle(string title)
    {
        if (s_consoleUi == 0) return;
        try
        {
            Console.Title = title;
            s_consoleUi = 1;
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
        {
            s_consoleUi = 0;
        }
    }

    private static void WriteConsole(string message, ConsoleColor color)
    {
        if (s_consoleUi == 0)
        {
            try { Console.WriteLine(message); } catch { }
            return;
        }

        try
        {
            ConsoleColor prevColor = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(message);
            Console.ForegroundColor = prevColor;
            s_consoleUi = 1;
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
        {
            s_consoleUi = 0;
            try { Console.WriteLine(message); } catch { }
        }
    }

    [OnAssemblyUnload]
    internal static void ClearLogSubscribers()
    {
        OnLog = null;
        // GizmoFilter is user/editor-assignable and takes a MonoBehaviour argument; a delegate bound
        // to script code pins the collectible ALC and turns every hot-reload into an editor restart.
        GizmoFilter = null;
    }

    public static void Log(object message)
        => Log(message.ToString(), LogSeverity.Normal);

    public static void Log(string message)
        => Log(message, LogSeverity.Normal);

    public static void LogWarning(object message)
        => Log(message.ToString(), LogSeverity.Warning);

    public static void LogWarning(string message)
        => Log(message, LogSeverity.Warning);

    public static void LogError(object message)
        => Log(message.ToString(), LogSeverity.Error);

    public static void LogError(string message)
        => Log(message, LogSeverity.Error);

    public static void LogSuccess(object message)
        => Log(message.ToString(), LogSeverity.Success);

    public static void LogSuccess(string message)
        => Log(message, LogSeverity.Success);

    // Says both what the condition was and that the repeats are not being shown, so nobody reads a
    // single line and concludes it happened once.
    private static string OnceTag(string id, string message) => $"{message} [{id}, repeats suppressed]";

    private static readonly HashSet<string> _onceIds = new();
    private static readonly object _onceLock = new();

    /// <summary>
    /// Logs an error once per <paramref name="id"/> for the lifetime of the process. An offending
    /// call site usually repeats every frame; the id keeps the log readable without hiding the issue.
    /// </summary>
    public static void LogErrorOnce(string id, string message)
    {
        lock (_onceLock)
        {
            if (!_onceIds.Add(id)) return;
        }
        LogError(OnceTag(id, message));
    }

    /// <summary>
    /// Logs a warning once per <paramref name="id"/> for the lifetime of the process, mirroring
    /// <see cref="LogErrorOnce"/> for recoverable conditions that would otherwise repeat per frame.
    /// </summary>
    public static void LogWarningOnce(string id, string message)
    {
        lock (_onceLock)
        {
            if (!_onceIds.Add(id)) return;
        }
        LogWarning(OnceTag(id, message));
    }

    /// <summary>
    /// Reports when a main-thread-only API is called from somewhere else, and says whether it was.
    /// Deduplicated per caller, because an offending call site usually repeats every frame.
    /// <para/>
    /// The point is to fail where the rule was broken. Touching scene or engine state off-thread
    /// usually surfaces as a corrupted read or a crash somewhere unrelated, long after the call that
    /// caused it. Callers that can bail should act on the result; the rest at least get a name.
    /// <para/>
    /// Always true before the engine loop is initialized (tests, tools, early startup), since there
    /// is no engine thread to be off.
    /// </summary>
    /// <param name="member">Defaults to the calling member.</param>
    /// <returns>True when the caller is on the main (engine loop) thread.</returns>
    public static bool EnsureMainThread([CallerMemberName] string member = "")
    {
        if (!Async.XTaskLoopHelper.IsInitialized) return true;
        if (Environment.CurrentManagedThreadId == Async.XTaskLoopHelper.MainThreadId) return true;

        LogErrorOnce($"MainThread.{member}",
            $"{member} must be called on the main thread, and was called from thread {Environment.CurrentManagedThreadId}. Marshal the call back to the main thread.");
        return false;
    }

    public static void LogException(Exception exception)
    {
        DebugStackTrace trace = (DebugStackTrace)new StackTrace(exception.InnerException ?? exception, true);
#if XENGINE_ANDROID
        WriteAndroidLog(exception.Message, LogSeverity.Exception);

        if (exception.InnerException != null)
            WriteAndroidLog(exception.InnerException.Message, LogSeverity.Exception);

        WriteAndroidLog(trace.ToString(), LogSeverity.Exception);
#elif XENGINE_WEB
        // browser-wasm Console has no ForegroundColor (PlatformNotSupportedException).
        Console.WriteLine(exception.Message);
        if (exception.InnerException != null)
            Console.WriteLine(exception.InnerException.Message);
        Console.WriteLine(trace.ToString());
#else
        if (s_isHarmonyOs && WriteHarmonyOSLogIfAvailable(exception.Message, LogSeverity.Exception))
        {
            if (exception.InnerException != null)
                WriteHarmonyOSLogIfAvailable(exception.InnerException.Message, LogSeverity.Exception);
            WriteHarmonyOSLogIfAvailable(trace.ToString(), LogSeverity.Exception);
        }
        else
        {
            WriteConsole(exception.Message, ConsoleColor.DarkRed);
            if (exception.InnerException != null)
                WriteConsole(exception.InnerException.Message, ConsoleColor.DarkRed);
            WriteConsole(trace.ToString(), ConsoleColor.DarkRed);
        }
#endif

        OnLog?.Invoke(exception.Message + "\n" + (exception.InnerException?.Message ?? ""), trace, LogSeverity.Exception);
    }

    // NOTE : StackTrace is pretty fast on modern .NET, so it's nice to keep it on by default, since it gives useful line numbers for debugging purposes.
    // For reference, getting a stack trace on a modern machine takes around 15 μs at a depth of 15.
    public static void Log(string message, LogSeverity logSeverity, DebugStackTrace? customTrace = null)
    {
#if XENGINE_ANDROID
        WriteAndroidLog(message, logSeverity);

        if (customTrace != null)
            WriteAndroidLog(customTrace.ToString(), logSeverity);
#elif XENGINE_WEB
        Console.WriteLine(message);
        if (customTrace != null)
            Console.WriteLine(customTrace.ToString());
#else
        if (s_isHarmonyOs && WriteHarmonyOSLogIfAvailable(message, logSeverity))
        {
            if (customTrace != null)
                WriteHarmonyOSLogIfAvailable(customTrace.ToString(), logSeverity);
        }
        else
        {
            ConsoleColor color = logSeverity switch
            {
                LogSeverity.Success => ConsoleColor.Green,
                LogSeverity.Warning => ConsoleColor.Yellow,
                LogSeverity.Error => ConsoleColor.Red,
                LogSeverity.Exception => ConsoleColor.DarkRed,
                _ => ConsoleColor.White
            };

            WriteConsole(message, color);
            if (customTrace != null)
                WriteConsole(customTrace.ToString(), color);
        }
#endif

        if (customTrace != null)
            OnLog?.Invoke(message, customTrace, logSeverity);
        else
        {
            StackTrace trace = new(2, true);
            OnLog?.Invoke(message, (DebugStackTrace)trace, logSeverity);
        }
    }

#if XENGINE_ANDROID
    private static void WriteAndroidLog(string message, LogSeverity severity)
    {
        AndroidLogPriority priority = severity switch
        {
            LogSeverity.Warning => AndroidLogPriority.Warning,
            LogSeverity.Error or LogSeverity.Exception => AndroidLogPriority.Error,
            _ => AndroidLogPriority.Info,
        };

        try
        {
            _ = AndroidLogWrite(priority, AndroidLogTag, message);
        }
        catch
        {
            // Console.WriteLine is supported by the Android runtime even though console colors are not.
            Console.WriteLine(message);
        }
    }
#else
    // HarmonyOS detection for the desktop-built XEngine.Runtime.dll (the player consumes the same
    // assembly regardless of target). We detect libhilog.so by attempting the P/Invoke directly:
    // a missing library raises DllNotFoundException (catchable), not an abort. We deliberately
    // avoid NativeLibrary.TryLoad because its internal LibraryNameVariation string handling
    // triggers GlobalizationMode -> ICU, which aborts on HarmonyOS (no libicu shipped).
    // Returns true (and writes the log) when running on HarmonyOS, false otherwise.
    //
    // s_isHarmonyOs gates the hilog probe so desktop/editor runs (Windows/Linux/macOS) never
    // attempt the libhilog.so P/Invoke — RuntimeInformation.IsOSPlatform is a cheap pure-runtime
    // check with no native dependency.
    private static readonly bool s_isHarmonyOs =
        RuntimeInformation.IsOSPlatform(OSPlatform.Create("OHOS")) ||
        RuntimeInformation.IsOSPlatform(OSPlatform.Create("OPENHARMONY"));
    private static int s_harmonyLogState; // 0 = unprobed, 1 = available, 2 = unavailable
    private const string HarmonyLogTag = "XEngine";
    private const uint HarmonyLogDomain = 0xD003500;

    [DllImport("libhilog.so", EntryPoint = "OH_LOG_Print")]
    private static extern void OH_LOG_Print(
        int type,
        int level,
        uint domain,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string tag,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string fmt,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string message);

    private static bool WriteHarmonyOSLogIfAvailable(string message, LogSeverity severity)
    {
        int state = s_harmonyLogState;
        if (state == 0)
        {
            try
            {
                OH_LOG_Print(0, 1, HarmonyLogDomain, HarmonyLogTag, "%{public}s", "hilog probe");
                state = 1;
            }
            catch
            {
                state = 2;
            }

            s_harmonyLogState = state;
        }

        if (state != 1)
            return false;

        int level = severity switch
        {
            LogSeverity.Warning => 3, // LOG_TYPE_WARN
            LogSeverity.Error or LogSeverity.Exception => 4, // LOG_TYPE_ERROR
            _ => 1, // LOG_TYPE_INFO
        };

        try
        {
            // %{public}s interpolates a public (non-redacted) string argument.
            OH_LOG_Print(/*LOG_APP*/0, level, HarmonyLogDomain, HarmonyLogTag, "%{public}s", message);
        }
        catch
        {
            // P/Invoke failed (e.g. symbol renamed in a future SDK); fall back to nothing safe.
        }

        return true;
    }
#endif

    public static void If(bool condition, string message = "")
    {
        if (condition)
            throw new Exception(message);
    }

    public static void IfNull(object value, string message = "")
    {
        if (value is null)
            throw new Exception(message);
    }

    public static void IfNullOrEmpty(string value, string message = "")
    {
        if (string.IsNullOrEmpty(value))
            throw new Exception(message);
    }

    internal static void ErrorGuard(Action value)
    {
        try
        {
            value();
        }
        catch (Exception e)
        {
            LogError(e.Message);
        }
    }

    public static void Assert(bool condition, string? message)
        => System.Diagnostics.Debug.Assert(condition, message);

    public static void Assert(bool condition)
        => System.Diagnostics.Debug.Assert(condition);

    #region Gizmos

    private static readonly GizmoBuilder s_gizmoBuilder = new();

    /// <summary>
    /// Optional editor-supplied predicate consulted before a component's <see cref="MonoBehaviour.DrawGizmos"/>
    /// runs. Returning false skips that component, which is how the Scene view's Gizmos dropdown hides
    /// gizmos per component type. Null (the default, and always the case in a player build) draws everything.
    /// </summary>
    public static Func<MonoBehaviour, bool>? GizmoFilter;

    /// <summary>
    /// Optional editor-supplied predicate for a component's billboarded icon, consulted separately
    /// from <see cref="GizmoFilter"/>. The Gizmos window has an icon column and a gizmo column per
    /// type, so a component can draw its wire gizmo with its icon hidden and vice versa.
    /// </summary>
    public static Func<MonoBehaviour, bool>? GizmoIconFilter;

    /// <summary>Whether <see cref="DrawIcon"/> records anything (Scene view "3D Icons" toggle).</summary>
    public static bool GizmoIconsVisible = true;

    /// <summary>Multiplier applied to every <see cref="DrawIcon"/> scale (Scene view "3D Icons" size).</summary>
    public static float GizmoIconScale = 1f;

    /// <summary>
    /// Uniform scale applied to each component's gizmo geometry about that component's own transform
    /// position (Scene view "Gizmo Scale"). 1 draws the authored size. Only takes effect between
    /// <see cref="BeginGizmoObject"/> / <see cref="EndGizmoObject"/>, so editor-owned handles that
    /// draw outside a component scope keep their exact size.
    /// </summary>
    public static float GizmoScale = 1f;

    /// <summary>
    /// Optional editor hook implementing Unity's "Fade Gizmos": given the world bounds a component
    /// just drew (center + radius), return an alpha multiplier in 0..1 for that component's gizmo.
    /// Null (and always in a player build) leaves every gizmo at full opacity.
    /// </summary>
    public static Func<Float3, float, float>? GizmoFadeEvaluator;

    /// <summary>The component whose <c>DrawGizmos</c> is currently running, or null outside a scope.</summary>
    internal static MonoBehaviour? CurrentGizmoComponent;

    /// <summary>
    /// Open a per-component gizmo scope: applies <see cref="GizmoScale"/> about the component's own
    /// position and marks the vertex range so <see cref="GizmoFadeEvaluator"/> can dim exactly what
    /// this component drew. Always pair with <see cref="EndGizmoObject"/> in a finally.
    /// </summary>
    public static void BeginGizmoObject(MonoBehaviour component)
    {
        CurrentGizmoComponent = component;
        s_gizmoBuilder.BeginObject(component.Transform.Position, GizmoScale);
    }

    /// <summary>Close the scope opened by <see cref="BeginGizmoObject"/> and apply the fade.</summary>
    public static void EndGizmoObject()
    {
        s_gizmoBuilder.EndObject(GizmoFadeEvaluator);
        CurrentGizmoComponent = null;
    }

    public static void ClearGizmos()
    {
        s_gizmoBuilder.Clear();
    }

    public static (Mesh? wire, Mesh? solid) GetGizmoDrawData()
    {
        return s_gizmoBuilder.UpdateMesh();
    }

    internal static bool HasGizmos => s_gizmoBuilder.HasContent;

    public static List<GizmoBuilder.IconDrawCall> GetGizmoIcons()
    {
        return s_gizmoBuilder.GetIcons();
    }

    public static void PushMatrix(Float4x4 matrix)
    {
        s_gizmoBuilder.PushMatrix(matrix);
    }

    public static void PopMatrix()
    {
        s_gizmoBuilder.PopMatrix();
    }

    public static void DrawLine(Float3 start, Float3 end, Color color) => s_gizmoBuilder.DrawLine(start, end, color);
    public static void DrawTriangle(Float3 a, Float3 b, Float3 c, Color color) => s_gizmoBuilder.DrawTriangle(a, b, c, color);

    /// <summary>
    /// Batch-submit filled triangles. When <paramref name="indices"/> is empty, <paramref name="vertices"/>
    /// is treated as a triangle list. Prefer this over per-triangle <see cref="DrawTriangle"/> for large meshes.
    /// </summary>
    public static void DrawTriangles(ReadOnlySpan<Float3> vertices, ReadOnlySpan<int> indices, Color color)
        => s_gizmoBuilder.DrawTriangles(vertices, indices, color);

    /// <summary>
    /// Batch-submit line segments. When <paramref name="indices"/> is empty, <paramref name="vertices"/>
    /// is treated as a line list (pairs). Prefer this over per-segment <see cref="DrawLine"/> for large meshes.
    /// </summary>
    public static void DrawLines(ReadOnlySpan<Float3> vertices, ReadOnlySpan<int> indices, Color color)
        => s_gizmoBuilder.DrawLines(vertices, indices, color);
    public static void DrawWireCube(Float3 center, Float3 halfExtents, Color color) => s_gizmoBuilder.DrawWireCube(center, halfExtents, color);
    public static void DrawCube(Float3 center, Float3 halfExtents, Color color) => s_gizmoBuilder.DrawCube(center, halfExtents, color);
    public static void DrawWireCircle(Float3 center, Float3 normal, float radius, Color color, int segments = 16) => s_gizmoBuilder.DrawCircle(center, normal, radius, color, segments);
    public static void DrawWireSphere(Float3 center, float radius, Color color, int segments = 16) => s_gizmoBuilder.DrawWireSphere(center, radius, color, segments);
    public static void DrawSphere(Float3 center, float radius, Color color, int segments = 16) => s_gizmoBuilder.DrawSphere(center, radius, color, segments);
    public static void DrawWireCone(Float3 start, Float3 direction, float radius, Color color, int segments = 16) => s_gizmoBuilder.DrawWireCone(start, direction, radius, color, segments);
    public static void DrawWireCapsule(Float3 point1, Float3 point2, float radius, Color color, int segments = 16) => s_gizmoBuilder.DrawWireCapsule(point1, point2, radius, color, segments);
    public static void DrawWireCylinder(Float3 center, Quaternion rotation, float radius, float height, Color color, int segments = 16) => s_gizmoBuilder.DrawWireCylinder(center, rotation, radius, height, color, segments);
    public static void DrawArrow(Float3 start, Float3 direction, Color color) => s_gizmoBuilder.DrawArrow(start, direction, color);

    /// <param name="isComponentIcon">
    /// True for a component's billboard, which the Gizmos window's "3D Icons" checkbox, its size
    /// slider and the per-type icon column all govern. False for editor-owned handles whose size is
    /// already derived from the view and must stay aligned with their own hit test — those draw at
    /// exactly <paramref name="scale"/> and ignore the icon settings.
    /// </param>
    public static void DrawIcon(Texture2D icon, Float3 center, float scale, Color color,
        bool isComponentIcon = true)
    {
        if (!isComponentIcon)
        {
            s_gizmoBuilder.DrawIcon(icon, center, scale, color);
            return;
        }

        // Filter first: it doubles as the Gizmos window's discovery pass for "this type has an
        // icon", which must still happen while 3D Icons is switched off.
        if (CurrentGizmoComponent is { } c && GizmoIconFilter is { } iconFilter && !iconFilter(c)) return;
        if (!GizmoIconsVisible) return;
        s_gizmoBuilder.DrawIcon(icon, center, scale * GizmoIconScale, color);
    }

    #endregion

}

public class GizmoBuilder
{
    private struct MeshData
    {
        public List<Float3> Vertices = [];
        public List<Float2> Uvs = [];
        public List<Color32> Colors = [];
        public List<int> Indices = [];

        public MeshData()
        {
        }

        public readonly void Clear()
        {
            Vertices.Clear();
            Uvs.Clear();
            Colors.Clear();
            Indices.Clear();
        }
    }

    private MeshData _wireData = new();
    private MeshData _solidData = new();
    private Mesh? _wire;
    private Mesh? _solid;

    public struct IconDrawCall
    {
        public Texture2D Texture;
        public Float3 Center;
        public float Scale;
        public Color Color;
    }

    private List<IconDrawCall> _icons = [];

    private Stack<Float4x4> _matrix4X4s = new();

    // Per-component scope (Debug.BeginGizmoObject/EndGizmoObject). The object matrix is composed
    // *after* the PushMatrix stack rather than pushed onto it: components push their own local
    // space and AddLine only peeks the top, so pushing here would be overwritten by the first
    // collider that calls PushMatrix.
    private Float4x4 _objectMatrix = Float4x4.Identity;
    private bool _hasObjectMatrix;
    private Float3 _objectPivot;
    private int _objWireStart, _objSolidStart, _objIconStart;
    private bool _inObject;

    internal bool HasContent => _wireData.Vertices.Count > 0
        || _solidData.Vertices.Count > 0
        || _icons.Count > 0;


    public void Clear()
    {
        _wireData.Clear();
        _solidData.Clear();

        //_wire?.Clear();
        //_solid?.Clear();

        _icons.Clear();

        _matrix4X4s.Clear();
        _hasObjectMatrix = false;
        _inObject = false;
    }

    /// <summary>Start a per-component scope; see <see cref="Debug.BeginGizmoObject"/>.</summary>
    internal void BeginObject(Float3 pivot, float scale)
    {
        _objectPivot = pivot;
        _hasObjectMatrix = MathF.Abs(scale - 1f) > 0.0001f;
        // Column-vector convention (p' = M * p): rightmost factor applies first, so this reads
        // "move the pivot to the origin, scale, move back".
        if (_hasObjectMatrix)
            _objectMatrix = Float4x4.CreateTranslation(pivot)
                * Float4x4.CreateScale(scale)
                * Float4x4.CreateTranslation(new Float3(-pivot.X, -pivot.Y, -pivot.Z));
        _objWireStart = _wireData.Vertices.Count;
        _objSolidStart = _solidData.Vertices.Count;
        _objIconStart = _icons.Count;
        _inObject = true;
    }

    /// <summary>
    /// Close the scope and, when <paramref name="fade"/> is set, multiply the alpha of everything
    /// the component drew by the factor it returns for that gizmo's world bounds.
    /// </summary>
    internal void EndObject(Func<Float3, float, float>? fade)
    {
        _hasObjectMatrix = false;
        if (!_inObject) return;
        _inObject = false;
        if (fade == null) return;

        if (!TryGetObjectBounds(out Float3 center, out float radius)) return;
        float alpha = fade(center, radius);
        if (alpha >= 0.999f) return;
        if (alpha < 0f) alpha = 0f;

        ScaleAlpha(_wireData.Colors, _objWireStart, alpha);
        ScaleAlpha(_solidData.Colors, _objSolidStart, alpha);
        for (int i = _objIconStart; i < _icons.Count; i++)
        {
            IconDrawCall call = _icons[i];
            call.Color = new Color(call.Color.R, call.Color.G, call.Color.B, call.Color.A * alpha);
            _icons[i] = call;
        }
    }

    private bool TryGetObjectBounds(out Float3 center, out float radius)
    {
        center = Float3.Zero;
        radius = 0f;
        Float3 min = new(float.MaxValue), max = new(float.MinValue);
        bool any = false;
        for (int i = _objWireStart; i < _wireData.Vertices.Count; i++) { Grow(_wireData.Vertices[i], ref min, ref max); any = true; }
        for (int i = _objSolidStart; i < _solidData.Vertices.Count; i++) { Grow(_solidData.Vertices[i], ref min, ref max); any = true; }
        for (int i = _objIconStart; i < _icons.Count; i++)
        {
            // The billboard's own extent, not just its anchor: components like AudioSource draw an
            // icon and nothing else, and a point has zero radius — they would fade out at any
            // threshold. Half-size matches GizmoIcon.shader (halfSize = _IconScale * 0.5).
            IconDrawCall call = _icons[i];
            float half = MathF.Abs(call.Scale) * 0.5f;
            Grow(new Float3(call.Center.X - half, call.Center.Y - half, call.Center.Z - half), ref min, ref max);
            Grow(new Float3(call.Center.X + half, call.Center.Y + half, call.Center.Z + half), ref min, ref max);
            any = true;
        }
        if (!any) return false;

        center = (min + max) * 0.5f;
        radius = Float3.Length(max - min) * 0.5f;
        return true;

        static void Grow(Float3 p, ref Float3 min, ref Float3 max)
        {
            min = new Float3(MathF.Min(min.X, p.X), MathF.Min(min.Y, p.Y), MathF.Min(min.Z, p.Z));
            max = new Float3(MathF.Max(max.X, p.X), MathF.Max(max.Y, p.Y), MathF.Max(max.Z, p.Z));
        }
    }

    private static void ScaleAlpha(List<Color32> colors, int start, float alpha)
    {
        for (int i = start; i < colors.Count; i++)
        {
            Color32 c = colors[i];
            c.A = (byte)Maths.Clamp(c.A * alpha, 0f, 255f);
            colors[i] = c;
        }
    }

    /// <summary>Stack matrix first (component-local space), then the per-component object matrix.</summary>
    private Float3 TransformVertex(Float3 p)
    {
        if (_matrix4X4s.Count > 0)
            p = Float4x4.TransformPoint(p, _matrix4X4s.Peek());
        if (_hasObjectMatrix)
            p = Float4x4.TransformPoint(p, _objectMatrix);
        return p;
    }

    private void AddLine(Float3 a, Float3 b, Color color)
    {
        a = TransformVertex(a);
        b = TransformVertex(b);

        int index = _wireData.Vertices.Count;
        _wireData.Vertices.Add(a);
        _wireData.Vertices.Add(b);

        _wireData.Colors.Add(color);
        _wireData.Colors.Add(color);

        _wireData.Indices.Add(index);
        _wireData.Indices.Add(index + 1);
    }

    private void AddTriangle(Float3 a, Float3 b, Float3 c, Float2 a_uv, Float2 b_uv, Float2 c_uv, Color color)
    {
        a = TransformVertex(a);
        b = TransformVertex(b);
        c = TransformVertex(c);

        int index = _solidData.Vertices.Count;

        _solidData.Vertices.Add(a);
        _solidData.Vertices.Add(b);
        _solidData.Vertices.Add(c);

        _solidData.Uvs.Add(a_uv);
        _solidData.Uvs.Add(b_uv);
        _solidData.Uvs.Add(c_uv);

        _solidData.Colors.Add(color);
        _solidData.Colors.Add(color);
        _solidData.Colors.Add(color);

        _solidData.Indices.Add(index);
        _solidData.Indices.Add(index + 1);
        _solidData.Indices.Add(index + 2);
    }

    public void PushMatrix(Float4x4 matrix)
    {
        _matrix4X4s.Push(matrix);
    }

    public void PopMatrix()
    {
        _matrix4X4s.Pop();
    }

    public void DrawLine(Float3 start, Float3 end, Color color) => AddLine(start, end, color);

    public void DrawTriangle(Float3 a, Float3 b, Float3 c, Color color) => AddTriangle(a, b, c, Float2.Zero, Float2.Zero, Float2.Zero, color);

    public void DrawTriangles(ReadOnlySpan<Float3> vertices, ReadOnlySpan<int> indices, Color color)
    {
        if (vertices.Length == 0) return;
        if (indices.Length == 0)
        {
            int limit = vertices.Length - 2;
            for (int i = 0; i < limit; i += 3)
                AddTriangle(vertices[i], vertices[i + 1], vertices[i + 2], Float2.Zero, Float2.Zero, Float2.Zero, color);
            return;
        }

        int last = indices.Length - 2;
        for (int i = 0; i < last; i += 3)
        {
            int i0 = indices[i], i1 = indices[i + 1], i2 = indices[i + 2];
            if ((uint)i0 >= (uint)vertices.Length || (uint)i1 >= (uint)vertices.Length || (uint)i2 >= (uint)vertices.Length)
                continue;
            AddTriangle(vertices[i0], vertices[i1], vertices[i2], Float2.Zero, Float2.Zero, Float2.Zero, color);
        }
    }

    public void DrawLines(ReadOnlySpan<Float3> vertices, ReadOnlySpan<int> indices, Color color)
    {
        if (vertices.Length == 0) return;
        if (indices.Length == 0)
        {
            int limit = vertices.Length - 1;
            for (int i = 0; i < limit; i += 2)
                AddLine(vertices[i], vertices[i + 1], color);
            return;
        }

        int last = indices.Length - 1;
        for (int i = 0; i < last; i += 2)
        {
            int i0 = indices[i], i1 = indices[i + 1];
            if ((uint)i0 >= (uint)vertices.Length || (uint)i1 >= (uint)vertices.Length)
                continue;
            AddLine(vertices[i0], vertices[i1], color);
        }
    }

    public void DrawWireCube(Float3 center, Float3 halfExtents, Color color)
    {
        Float3[] vertices = [
            new Float3(center.X - halfExtents.X, center.Y - halfExtents.Y, center.Z - halfExtents.Z),
            new Float3(center.X + halfExtents.X, center.Y - halfExtents.Y, center.Z - halfExtents.Z),
            new Float3(center.X + halfExtents.X, center.Y - halfExtents.Y, center.Z + halfExtents.Z),
            new Float3(center.X - halfExtents.X, center.Y - halfExtents.Y, center.Z + halfExtents.Z),
            new Float3(center.X - halfExtents.X, center.Y + halfExtents.Y, center.Z - halfExtents.Z),
            new Float3(center.X + halfExtents.X, center.Y + halfExtents.Y, center.Z - halfExtents.Z),
            new Float3(center.X + halfExtents.X, center.Y + halfExtents.Y, center.Z + halfExtents.Z),
            new Float3(center.X - halfExtents.X, center.Y + halfExtents.Y, center.Z + halfExtents.Z),
        ];

        AddLine(vertices[0], vertices[1], color);
        AddLine(vertices[1], vertices[2], color);
        AddLine(vertices[2], vertices[3], color);
        AddLine(vertices[3], vertices[0], color);

        AddLine(vertices[4], vertices[5], color);
        AddLine(vertices[5], vertices[6], color);
        AddLine(vertices[6], vertices[7], color);
        AddLine(vertices[7], vertices[4], color);

        AddLine(vertices[0], vertices[4], color);
        AddLine(vertices[1], vertices[5], color);
        AddLine(vertices[2], vertices[6], color);
        AddLine(vertices[3], vertices[7], color);
    }

    public void DrawCube(Float3 center, Float3 halfExtents, Color color)
    {
        Float3[] vertices = [
            new Float3(center.X - halfExtents.X, center.Y - halfExtents.Y, center.Z - halfExtents.Z),
            new Float3(center.X + halfExtents.X, center.Y - halfExtents.Y, center.Z - halfExtents.Z),
            new Float3(center.X + halfExtents.X, center.Y - halfExtents.Y, center.Z + halfExtents.Z),
            new Float3(center.X - halfExtents.X, center.Y - halfExtents.Y, center.Z + halfExtents.Z),
            new Float3(center.X - halfExtents.X, center.Y + halfExtents.Y, center.Z - halfExtents.Z),
            new Float3(center.X + halfExtents.X, center.Y + halfExtents.Y, center.Z - halfExtents.Z),
            new Float3(center.X + halfExtents.X, center.Y + halfExtents.Y, center.Z + halfExtents.Z),
            new Float3(center.X - halfExtents.X, center.Y + halfExtents.Y, center.Z + halfExtents.Z),
        ];

        Float2[] uvs = [
            new Float2(0, 0),
            new Float2(1, 0),
            new Float2(1, 1),
            new Float2(0, 1),
        ];

        AddTriangle(vertices[0], vertices[1], vertices[2], uvs[0], uvs[1], uvs[2], color);
        AddTriangle(vertices[0], vertices[2], vertices[3], uvs[0], uvs[2], uvs[3], color);

        AddTriangle(vertices[4], vertices[6], vertices[5], uvs[0], uvs[1], uvs[2], color);
        AddTriangle(vertices[4], vertices[7], vertices[6], uvs[0], uvs[2], uvs[3], color);

        AddTriangle(vertices[0], vertices[3], vertices[7], uvs[0], uvs[1], uvs[2], color);
        AddTriangle(vertices[0], vertices[7], vertices[4], uvs[0], uvs[2], uvs[3], color);

        AddTriangle(vertices[1], vertices[5], vertices[6], uvs[0], uvs[1], uvs[2], color);
        AddTriangle(vertices[1], vertices[6], vertices[2], uvs[0], uvs[2], uvs[3], color);

        AddTriangle(vertices[3], vertices[2], vertices[6], uvs[0], uvs[1], uvs[2], color);
        AddTriangle(vertices[3], vertices[6], vertices[7], uvs[0], uvs[2], uvs[3], color);

        AddTriangle(vertices[0], vertices[4], vertices[5], uvs[0], uvs[1], uvs[2], color);
        AddTriangle(vertices[0], vertices[5], vertices[1], uvs[0], uvs[2], uvs[3], color);
    }

    public void DrawWireSphere(Float3 center, float radius, Color color, int segments = 16)
    {
        float step = MathF.PI * 2 / segments;

        for (int i = 0; i < segments; i++)
        {
            float angle1 = i * step;
            float angle2 = (i + 1) * step;

            Float3 a = new(Maths.Cos(angle1) * radius + center.X,
                            Maths.Sin(angle1) * radius + center.Y,
                            center.Z
                        );

            Float3 b = new(Maths.Cos(angle2) * radius + center.X,
                            Maths.Sin(angle2) * radius + center.Y,
                            center.Z
                        );

            AddLine(a, b, color);
        }

        for (int i = 0; i < segments; i++)
        {
            float angle1 = i * step;
            float angle2 = (i + 1) * step;

            Float3 a = new(Maths.Cos(angle1) * radius + center.X,
                            center.Y,
                            Maths.Sin(angle1) * radius + center.Z
                        );

            Float3 b = new(Maths.Cos(angle2) * radius + center.X,
                            center.Y,
                            Maths.Sin(angle2) * radius + center.Z
                        );

            AddLine(a, b, color);
        }

        for (int i = 0; i < segments; i++)
        {
            float angle1 = i * step;
            float angle2 = (i + 1) * step;

            Float3 a = new(center.X,
                            Maths.Cos(angle1) * radius + center.Y,
                            Maths.Sin(angle1) * radius + center.Z
                        );

            Float3 b = new(center.X,
                            Maths.Cos(angle2) * radius + center.Y,
                            Maths.Sin(angle2) * radius + center.Z
                        );

            AddLine(a, b, color);
        }
    }

    public void DrawCircle(Float3 center, Float3 normal, float radius, Color color, int segments)
    {
        Float3 u = Float3.Normalize(Float3.Cross(normal, Float3.UnitY));
        Float3 v = Float3.Normalize(Float3.Cross(u, normal));
        float step = MathF.PI * 2 / segments;
        for (int i = 0; i < segments; i++)
        {
            float angle1 = i * step;
            float angle2 = (i + 1) * step;
            Float3 a = center + radius * (Maths.Cos(angle1) * u + Maths.Sin(angle1) * v);
            Float3 b = center + radius * (Maths.Cos(angle2) * u + Maths.Sin(angle2) * v);
            AddLine(a, b, color);
        }
    }

    public void DrawSphere(Float3 center, float radius, Color color, int segments = 16)
    {
        int latitudeSegments = segments;
        int longitudeSegments = segments * 2;

        for (int lat = 0; lat < latitudeSegments; lat++)
        {
            float theta1 = lat * MathF.PI / latitudeSegments;
            float theta2 = (lat + 1) * MathF.PI / latitudeSegments;

            for (int lon = 0; lon < longitudeSegments; lon++)
            {
                float phi1 = lon * 2 * MathF.PI / longitudeSegments;
                float phi2 = (lon + 1) * 2 * MathF.PI / longitudeSegments;

                Float3 v1 = CalculatePointOnSphere(theta1, phi1, radius, center);
                Float3 v2 = CalculatePointOnSphere(theta1, phi2, radius, center);
                Float3 v3 = CalculatePointOnSphere(theta2, phi1, radius, center);
                Float3 v4 = CalculatePointOnSphere(theta2, phi2, radius, center);

                // First triangle
                AddTriangle(v1, v2, v3, Float2.Zero, Float2.Zero, Float2.Zero, color);

                // Second triangle
                AddTriangle(v2, v4, v3, Float2.Zero, Float2.Zero, Float2.Zero, color);
            }
        }
    }

    private Float3 CalculatePointOnSphere(float theta, float phi, float radius, Float3 center)
    {
        float x = Maths.Sin(theta) * Maths.Cos(phi);
        float y = Maths.Cos(theta);
        float z = Maths.Sin(theta) * Maths.Sin(phi);

        return new Float3(
            x * radius + center.X,
            y * radius + center.Y,
            z * radius + center.Z
        );
    }

    public void DrawWireCone(Float3 start, Float3 direction, float radius, Color color, int segments = 16)
    {
        float step = MathF.PI * 2 / segments;
        Float3 tip = start + direction;

        // Normalize the direction vector
        Float3 dir = Float3.Normalize(direction);

        // Find perpendicular vectors
        Float3 u = GetPerpendicularVector(dir);
        Float3 v = Float3.Cross(dir, u);

        for (int i = 0; i < segments; i++)
        {
            float angle1 = i * step;
            float angle2 = (i + 1) * step;

            // Calculate circle points using the perpendicular vectors
            Float3 a = start + radius * (Maths.Cos(angle1) * u + Maths.Sin(angle1) * v);
            Float3 b = start + radius * (Maths.Cos(angle2) * u + Maths.Sin(angle2) * v);

            AddLine(a, b, color);
            if (i == 0 || i == segments / 4 || i == segments / 2 || i == segments * 3 / 4)
                AddLine(a, tip, color);
        }
    }

    public void DrawWireCapsule(Float3 point1, Float3 point2, float radius, Color color, int segments = 16)
    {
        // Calculate the axis of the capsule
        Float3 axis = point2 - point1;
        float height = Float3.Length(axis);

        if (height < 1e-6)
        {
            // Degenerate case: draw a sphere
            DrawWireSphere(point1, radius, color, segments);
            return;
        }

        Float3 dir = axis / height;

        // Find perpendicular vectors
        Float3 u = GetPerpendicularVector(dir);
        Float3 v = Float3.Cross(dir, u);

        float step = MathF.PI * 2 / segments;

        // Draw the cylindrical body (circles at both ends and connecting lines)
        for (int i = 0; i < segments; i++)
        {
            float angle1 = i * step;
            float angle2 = (i + 1) * step;

            // Circle at point1
            Float3 a1 = point1 + radius * (Maths.Cos(angle1) * u + Maths.Sin(angle1) * v);
            Float3 b1 = point1 + radius * (Maths.Cos(angle2) * u + Maths.Sin(angle2) * v);

            // Circle at point2
            Float3 a2 = point2 + radius * (Maths.Cos(angle1) * u + Maths.Sin(angle1) * v);
            Float3 b2 = point2 + radius * (Maths.Cos(angle2) * u + Maths.Sin(angle2) * v);

            AddLine(a1, b1, color);
            AddLine(a2, b2, color);

            // Connecting lines every quarter
            if (i % (segments / 4) == 0)
            {
                AddLine(a1, a2, color);
            }
        }

        // Draw hemisphere at point1 (bottom cap)
        for (int i = 0; i < segments / 2; i++)
        {
            float theta1 = MathF.PI / 2 + i * MathF.PI / segments;
            float theta2 = MathF.PI / 2 + (i + 1) * MathF.PI / segments;

            for (int j = 0; j < segments; j++)
            {
                float phi1 = j * 2 * MathF.PI / segments;
                float phi2 = (j + 1) * 2 * MathF.PI / segments;

                Float3 v1 = point1 + radius * (Maths.Sin(theta1) * Maths.Cos(phi1) * u + Maths.Sin(theta1) * Maths.Sin(phi1) * v + Maths.Cos(theta1) * dir);
                Float3 v2 = point1 + radius * (Maths.Sin(theta1) * Maths.Cos(phi2) * u + Maths.Sin(theta1) * Maths.Sin(phi2) * v + Maths.Cos(theta1) * dir);
                Float3 v3 = point1 + radius * (Maths.Sin(theta2) * Maths.Cos(phi1) * u + Maths.Sin(theta2) * Maths.Sin(phi1) * v + Maths.Cos(theta2) * dir);

                if (j % (segments / 4) == 0)
                {
                    AddLine(v1, v3, color);
                }
                if (i == 0 || i == segments / 2 - 1)
                {
                    AddLine(v1, v2, color);
                }
            }
        }

        // Draw hemisphere at point2 (top cap)
        for (int i = 0; i < segments / 2; i++)
        {
            float theta1 = i * MathF.PI / segments;
            float theta2 = (i + 1) * MathF.PI / segments;

            for (int j = 0; j < segments; j++)
            {
                float phi1 = j * 2 * MathF.PI / segments;
                float phi2 = (j + 1) * 2 * MathF.PI / segments;

                Float3 v1 = point2 + radius * (Maths.Sin(theta1) * Maths.Cos(phi1) * u + Maths.Sin(theta1) * Maths.Sin(phi1) * v + Maths.Cos(theta1) * dir);
                Float3 v2 = point2 + radius * (Maths.Sin(theta1) * Maths.Cos(phi2) * u + Maths.Sin(theta1) * Maths.Sin(phi2) * v + Maths.Cos(theta1) * dir);
                Float3 v3 = point2 + radius * (Maths.Sin(theta2) * Maths.Cos(phi1) * u + Maths.Sin(theta2) * Maths.Sin(phi1) * v + Maths.Cos(theta2) * dir);

                if (j % (segments / 4) == 0)
                {
                    AddLine(v1, v3, color);
                }
                if (i == 0 || i == segments / 2 - 1)
                {
                    AddLine(v1, v2, color);
                }
            }
        }
    }

    public void DrawWireCylinder(Float3 center, Quaternion rotation, float radius, float height, Color color, int segments)
    {
        Float3 up = rotation * Float3.UnitY;
        Float3 forward = rotation * Float3.UnitZ;
        Float3 right = rotation * Float3.UnitX;
        Float3 topCenter = center + (up * (height / 2));
        Float3 bottomCenter = center - (up * (height / 2));
        float step = MathF.PI * 2 / segments;
        // Draw top and bottom circles
        for (int i = 0; i < segments; i++)
        {
            float angle1 = i * step;
            float angle2 = (i + 1) * step;
            Float3 topA = topCenter + radius * (Maths.Cos(angle1) * right + Maths.Sin(angle1) * forward);
            Float3 topB = topCenter + radius * (Maths.Cos(angle2) * right + Maths.Sin(angle2) * forward);
            Float3 bottomA = bottomCenter + radius * (Maths.Cos(angle1) * right + Maths.Sin(angle1) * forward);
            Float3 bottomB = bottomCenter + radius * (Maths.Cos(angle2) * right + Maths.Sin(angle2) * forward);
            AddLine(topA, topB, color);
            AddLine(bottomA, bottomB, color);
            // Connecting lines every quarter
            if (i % (segments / 4) == 0)
            {
                AddLine(topA, bottomA, color);
            }
        }
    }

    private Float3 GetPerpendicularVector(Float3 v)
    {
        Float3 result;
        if (Maths.Abs(v.X) > 0.1f)
            result = new Float3(v.Y, -v.X, 0);
        else if (Maths.Abs(v.Y) > 0.1f)
            result = new Float3(0, v.Z, -v.Y);
        else
            result = new Float3(-v.Z, 0, v.X);
        return Float3.Normalize(result);
    }

    public void DrawArrow(Float3 start, Float3 direction, Color color)
    {
        Float3 axis = Float3.Normalize(direction);
        Float3 end = start + direction;
        AddLine(start, end, color);

        DrawWireCone(start + (direction * 0.9f), axis * 0.1f, 0.1f, color, 4);

    }

    // The object matrix moves the icon with the rest of the component's gizmo; the icon's own size
    // stays on the "3D Icons" slider (Debug.GizmoIconScale), not the gizmo scale.
    public void DrawIcon(Texture2D icon, Float3 center, float scale, Color color)
        => _icons.Add(new IconDrawCall
        {
            Texture = icon,
            Center = _hasObjectMatrix ? Float4x4.TransformPoint(center, _objectMatrix) : center,
            Scale = scale,
            Color = color,
        });

    public (Mesh? wire, Mesh? solid) UpdateMesh()
    {
        bool hasWire = _wireData.Vertices.Count > 0;
        if (hasWire)
        {
            _wire ??= new()
            {
                MeshTopology = Topology.Lines,
                IndexFormat = IndexFormat.UInt32,
            };

            // No LINQ (PR0001): manual array build for gizmo mesh upload.
            {
                int vc = _wireData.Vertices.Count;
                var verts = new Float3[vc];
                for (int i = 0; i < vc; i++) verts[i] = _wireData.Vertices[i];
                _wire.Vertices = verts;

                int cc = _wireData.Colors.Count;
                var cols = new Color[cc];
                for (int i = 0; i < cc; i++) cols[i] = _wireData.Colors[i];
                _wire.Colors = cols;

                int ic = _wireData.Indices.Count;
                var idx = new uint[ic];
                for (int i = 0; i < ic; i++) idx[i] = (uint)_wireData.Indices[i];
                _wire.Indices = idx;
            }
        }

        bool hasSolid = _solidData.Vertices.Count > 0;
        if (hasSolid)
        {
            _solid ??= new()
            {
                MeshTopology = Topology.Triangles,
                IndexFormat = IndexFormat.UInt32,
            };

            // No LINQ (PR0001): manual array build for gizmo mesh upload.
            {
                int vc = _solidData.Vertices.Count;
                var verts = new Float3[vc];
                for (int i = 0; i < vc; i++) verts[i] = _solidData.Vertices[i];
                _solid.Vertices = verts;

                int cc = _solidData.Colors.Count;
                var cols = new Color[cc];
                for (int i = 0; i < cc; i++) cols[i] = _solidData.Colors[i];
                _solid.Colors = cols;

                int uc = _solidData.Uvs.Count;
                var uvs = new Float2[uc];
                for (int i = 0; i < uc; i++) uvs[i] = _solidData.Uvs[i];
                _solid.UV = uvs;

                int ic = _solidData.Indices.Count;
                var idx = new uint[ic];
                for (int i = 0; i < ic; i++) idx[i] = (uint)_solidData.Indices[i];
                _solid.Indices = idx;
            }
        }

        return (
            hasWire ? _wire : null,
            hasSolid ? _solid : null
            );
    }

    public List<IconDrawCall> GetIcons()
    {
        return _icons;
    }
}
