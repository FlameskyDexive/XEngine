// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace XEngine.SpineGenerator.Editor;

/// <summary>
/// Runs one spinegen CLI command at a time as a child process and surfaces progress through
/// <see cref="Poll"/> (panel per-frame pattern, NavMeshBakeService style). JSON stdout of the last
/// command is kept for the wizard to render result summaries.
/// </summary>
public sealed class SpineGenCliRunner
{
    public const string MockProvider = "mock";

    public bool IsRunning { get; private set; }
    public string Status { get; private set; } = "Idle";
    public string? LastCommand { get; private set; }
    public string? LastJson { get; private set; }
    public string? LastError { get; private set; }
    public bool LastSucceeded { get; private set; }

    private Process? _process;
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();

    /// <summary>Locates xengine-cli: shipped beside the editor exe, or the dev-tools layout two
    /// repos up (Build/Editor/&lt;cfg&gt;/net10.0 → repo root → Tools/XEngine.Ai.Host).</summary>
    public static string? LocateCli()
    {
        string editorDir = AppContext.BaseDirectory;
        foreach (string candidate in new[]
                 {
                     Path.Combine(editorDir, "xengine-cli.exe"),
                     Path.Combine(editorDir, "xengine-cli"),
                     Path.Combine(editorDir, "..", "..", "..", "..", "Tools", "XEngine.Ai.Host", "bin",
                         "Debug", "net10.0", "xengine-cli.exe"),
                     Path.Combine(editorDir, "..", "..", "..", "..", "Tools", "XEngine.Ai.Host", "bin",
                         "Release", "net10.0", "xengine-cli.exe"),
                 })
        {
            string full = Path.GetFullPath(candidate);
            if (File.Exists(full))
                return full;
        }
        return null;
    }

    public void Run(string projectRoot, IReadOnlyList<string> arguments)
    {
        if (IsRunning)
            return;
        string? cli = LocateCli();
        if (cli == null)
        {
            LastError = "xengine-cli not found (expected beside the editor or under Tools/XEngine.Ai.Host).";
            LastSucceeded = false;
            return;
        }

        var args = new List<string> { "--project", projectRoot };
        args.AddRange(arguments);

        _stdout.Clear();
        _stderr.Clear();
        LastCommand = Path.GetFileNameWithoutExtension(cli) + " " + string.Join(' ', args);
        LastError = null;
        LastJson = null;
        Status = "Running…";
        IsRunning = true;

        var start = new ProcessStartInfo
        {
            FileName = cli,
            Arguments = string.Join(' ', args.Select(Quote)),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        _process = new Process { StartInfo = start };
        _process.OutputDataReceived += (_, e) => { if (e.Data != null) _stdout.AppendLine(e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data != null) _stderr.AppendLine(e.Data); };
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    /// <summary>Drains finished-process state; call once per frame from the panel.</summary>
    public void Poll()
    {
        if (!IsRunning || _process == null)
            return;
        if (!_process.HasExited)
            return;

        int code = _process.ExitCode;
        LastJson = _stdout.ToString().Trim();
        string errors = _stderr.ToString().Trim();
        LastSucceeded = code == 0;
        LastError = LastSucceeded ? null : (errors.Length > 0 ? errors : $"exit code {code}");
        Status = LastSucceeded ? "Done" : "Failed";
        IsRunning = false;
        _process.Dispose();
        _process = null;
    }

    public void ResetResult()
    {
        LastJson = null;
        LastError = null;
        LastSucceeded = false;
        Status = IsRunning ? Status : "Idle";
    }

    private static string Quote(string value) =>
        value.Contains(' ') ? "\"" + value + "\"" : value;
}
