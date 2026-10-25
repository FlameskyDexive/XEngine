// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

using XEngine.Runtime;
using XEngine.Runtime.Resources;

namespace XEngine.Editor.Core;

/// <summary>
/// ★ runtime_eval — A C# REPL service that evaluates arbitrary expressions/statements in the live
/// editor context (Unity CLI <c>eval</c> equivalent). Uses Roslyn CSharpScript with ScriptOptions
/// referencing the editor + runtime + loaded user-script assemblies. <see cref="EvalGlobals"/> are
/// injected so common engine state (Scene.Current, Application, EditorApplication.Instance) is
/// reachable without qualification.
///
/// <para><b>Security:</b> eval has full engine API access — it is a credential-grade capability (same
/// as Unity CLI's eval, which is token-gated). Only exposed via the local in-process MCP server
/// (editor --serve), never in shipping builds.</para>
/// </summary>
public static class ScriptEvalService
{
    private static ScriptOptions? _options;

    /// <summary>Evaluate a C# expression or statement. Returns the result (or null for statements).</summary>
    public static async Task<EvalResult> EvaluateAsync(string code)
    {
        try
        {
            ScriptOptions options = GetOptions();
            var globals = new EvalGlobals();

            // EvaluateAsync infers the return type as object — works for both expressions
            // (returns a value) and statements (returns null).
            object? result = await CSharpScript.EvaluateAsync(code, options, globals, typeof(EvalGlobals));
            return new EvalResult(
                Succeeded: true,
                Result: result?.ToString() ?? "null",
                ResultType: result?.GetType().FullName ?? "null",
                Error: null);
        }
        catch (CompilationErrorException cex)
        {
            return new EvalResult(false, null, null, string.Join("\n", cex.Diagnostics.Select(d => d.ToString())));
        }
        catch (Exception ex)
        {
            return new EvalResult(false, null, null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static ScriptOptions GetOptions()
    {
        if (_options is not null) return _options;

        // NOTE: user-script assemblies are deliberately NOT referenced here. They live in a
        // collectible ALC; the CLR refuses to resolve collectible assemblies into the default
        // context (FileLoadException 0x80131515), and referencing their on-disk path makes each
        // submission load a private copy whose types then never match live scene components.
        // Eval code that must touch user components resolves them via reflection from
        // ScriptAssemblyManager.GetScriptAssemblies() (the authoritative, identity-stable list).
        var assemblies = new System.Collections.Generic.List<Assembly>
        {
            typeof(EditorApplication).Assembly,   // XEngine.Editor
            typeof(Scene).Assembly,                // XEngine.Runtime
            typeof(Console).Assembly,              // System.Console
            typeof(System.Linq.Enumerable).Assembly, // System.Linq
        };
        foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (loaded.IsCollectible) continue;
            string? name = loaded.GetName().Name;
            // "Vector" (XEngine.Vector) and "Echo" are referenced by public engine types
            // (Transform.Position is Float3, resources serialize as EchoObject); without them
            // any eval that declares a Vector/Echo local fails to compile.
            if (name != null && (name.StartsWith("XEngine.", StringComparison.Ordinal) || name is "Vector" or "Echo"))
                assemblies.Add(loaded);
        }

        var namespaces = new System.Collections.Generic.List<string>
        {
            "System", "System.Linq", "System.Collections.Generic",
            "XEngine.Runtime", "XEngine.Runtime.Resources", "XEngine.Runtime.Rendering",
            "XEngine.Editor", "XEngine.Editor.Core",
        };
        if (assemblies.Exists(a => a.GetType("XEngine.Timeline.Editor.TimelineWindow") != null))
        {
            namespaces.Add("XEngine.Timeline");
            namespaces.Add("XEngine.Timeline.Editor");
        }

        _options = ScriptOptions.Default
            .WithReferences(assemblies.Distinct())
            .WithImports(namespaces)
            .WithEmitDebugInformation(false);
        return _options;
    }

    /// <summary>Globals injected into every eval — common engine context reachable unqualified.</summary>
    public sealed class EvalGlobals
    {
        public Scene? Scene => Scene.Current;
        public EditorApplication? Editor => EditorApplication.Instance;
        public bool IsPlaying => Application.IsPlaying;
    }
}

/// <summary>Result of a C# eval.</summary>
public sealed record EvalResult(bool Succeeded, string? Result, string? ResultType, string? Error);
