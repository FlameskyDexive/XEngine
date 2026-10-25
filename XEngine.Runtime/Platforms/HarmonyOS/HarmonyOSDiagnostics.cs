// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Threading;

namespace XEngine.Runtime.Platforms.HarmonyOS;

/// <summary>Immutable identity emitted by the HarmonyOS player diagnostics endpoint.</summary>
public sealed record HarmonyOSDiagnosticsInfo
{
    public HarmonyOSDiagnosticsInfo(string buildId, string bundleFingerprint, int apiLevel, string abi, string renderer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildId);
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleFingerprint);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(apiLevel);
        ArgumentException.ThrowIfNullOrWhiteSpace(abi);
        ArgumentException.ThrowIfNullOrWhiteSpace(renderer);

        BuildId = buildId;
        BundleFingerprint = bundleFingerprint;
        ApiLevel = apiLevel;
        Abi = abi;
        Renderer = renderer;
    }

    public string BuildId { get; }
    public string BundleFingerprint { get; }
    public int ApiLevel { get; }
    public string Abi { get; }
    public string Renderer { get; }
}

/// <summary>A detached, safe-to-persist HarmonyOS player diagnostics snapshot.</summary>
public sealed record HarmonyOSDiagnosticsSnapshot(
    string BuildId,
    string BundleFingerprint,
    int ApiLevel,
    string Abi,
    string Renderer,
    int SurfaceAttachCount,
    int SurfaceDetachCount,
    int ResumeCount,
    int PauseCount,
    IReadOnlyList<string> Errors);

/// <summary>
/// Bounded diagnostics for a HarmonyOS player. Callers must provide all sensitive values that
/// appear in an error; the raw message is never retained after <see cref="RecordError"/> returns.
/// </summary>
public sealed class HarmonyOSDiagnostics
{
    private const int ErrorCapacity = 16;

    private readonly HarmonyOSDiagnosticsInfo _info;
    private readonly object _errorLock = new();
    private readonly string[] _errors = new string[ErrorCapacity];
    private int _errorCount;
    private int _pauseCount;
    private int _resumeCount;
    private int _surfaceAttachCount;
    private int _surfaceDetachCount;

    public HarmonyOSDiagnostics(HarmonyOSDiagnosticsInfo info)
    {
        _info = info ?? throw new ArgumentNullException(nameof(info));
    }

    public void RecordSurfaceAttached() => Interlocked.Increment(ref _surfaceAttachCount);

    public void RecordSurfaceDetached() => Interlocked.Increment(ref _surfaceDetachCount);

    public void RecordResumed() => Interlocked.Increment(ref _resumeCount);

    public void RecordPaused() => Interlocked.Increment(ref _pauseCount);

    /// <summary>Records a bounded error after replacing every supplied sensitive value.</summary>
    public void RecordError(string message, IReadOnlyList<string>? sensitiveValues = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        string redacted = Redact(message, sensitiveValues);

        lock (_errorLock)
        {
            if (_errorCount == ErrorCapacity)
            {
                Array.Copy(_errors, 1, _errors, 0, ErrorCapacity - 1);
                _errors[ErrorCapacity - 1] = redacted;
                return;
            }

            _errors[_errorCount++] = redacted;
        }
    }

    public HarmonyOSDiagnosticsSnapshot Snapshot()
    {
        string[] errors;
        lock (_errorLock)
        {
            errors = new string[_errorCount];
            Array.Copy(_errors, errors, _errorCount);
        }

        return new HarmonyOSDiagnosticsSnapshot(
            _info.BuildId,
            _info.BundleFingerprint,
            _info.ApiLevel,
            _info.Abi,
            _info.Renderer,
            Volatile.Read(ref _surfaceAttachCount),
            Volatile.Read(ref _surfaceDetachCount),
            Volatile.Read(ref _resumeCount),
            Volatile.Read(ref _pauseCount),
            Array.AsReadOnly(errors));
    }

    private static string Redact(string message, IReadOnlyList<string>? sensitiveValues)
    {
        if (sensitiveValues is null)
            return message;

        string result = message;
        for (int i = 0; i < sensitiveValues.Count; i++)
        {
            string? value = sensitiveValues[i];
            if (!string.IsNullOrEmpty(value))
                result = result.Replace(value, "[REDACTED]", StringComparison.Ordinal);
        }
        return result;
    }
}
