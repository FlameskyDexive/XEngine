// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XEngine.Editor.Build.Web;

/// <summary>
/// Turns official Mono-WASM ESM glue (<c>export</c> / <c>import.meta</c> / <c>import()</c>)
/// into CommonJS that WeChat <c>require()</c> can load. Does not copy stock
/// <c>dotnet.js</c> into the mini-game as an ES module.
/// </summary>
internal static class WeChatEsmCommonJs
{
    private static readonly Regex s_exportDefault = new(
        @"export\s+default\s+([^;]+);",
        RegexOptions.CultureInvariant);

    private static readonly Regex s_exportNamed = new(
        @"export\s*\{([^}]+)\};",
        RegexOptions.CultureInvariant);

    private static readonly Regex s_sourceMappingUrl = new(
        @"^[ \t]*//[#@][ \t]*sourceMappingURL=.*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    internal const string ImportMetaUrlPrefix = "https://xengine.local/";

    internal static string ToCommonJs(string source, string importMetaUrl)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(importMetaUrl);
        source = s_sourceMappingUrl.Replace(source, string.Empty);
        if (source.Contains("__xengineImportMetaUrl", StringComparison.Ordinal))
            return PatchEmscriptenCanvasLookup(source);

        var builder = new StringBuilder(source.Length + 512);
        builder.Append("var __xengineImportMetaUrl = ");
        builder.Append(JsonSerializer.Serialize(importMetaUrl));
        builder.Append(';');
        builder.Append('\n');
        builder.Append("function __xengineImport(spec) {");
        builder.Append(" var s = String(spec || '');");
        builder.Append(" if (s.indexOf('dotnet.native') >= 0) return Promise.resolve(require('./dotnet.native.js'));");
        builder.Append(" if (s.indexOf('dotnet.runtime') >= 0) return Promise.resolve(require('./dotnet.runtime.js'));");
        builder.Append(" if (s.indexOf('dotnet.diagnostics') >= 0) return Promise.resolve({});");
        builder.Append(" return Promise.reject(new Error('WeChat path boot: no dynamic import for ' + s));");
        builder.Append(" }");
        builder.Append('\n');

        string body = source.Replace("import.meta.url", "__xengineImportMetaUrl", StringComparison.Ordinal);
        body = body.Replace("import.meta", "({ url: __xengineImportMetaUrl })", StringComparison.Ordinal);
        body = body.Replace("import(", "__xengineImport(", StringComparison.Ordinal);
        body = s_exportDefault.Replace(
            body,
            "module.exports = $1; module.exports.default = $1;");
        body = s_exportNamed.Replace(body, ReplaceNamedExport);
        builder.Append(body);
        return PatchEmscriptenCanvasLookup(builder.ToString());
    }

    /// <summary>
    /// WeChat's <c>document</c> is a frozen host object: assigning <c>querySelector</c> is a
    /// no-op. Emscripten 3.1.56 <c>findEventTarget</c> must resolve <c>#canvas</c> from
    /// <c>Module.canvas</c> instead.
    /// </summary>
    internal static string PatchEmscriptenCanvasLookup(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        const string already = "globalThis.__xengineCanvas";
        if (source.Contains(already, StringComparison.Ordinal))
            return source;

        const string needle =
            "var domElement = specialHTMLTargets[target] || (typeof document != 'undefined' ? document.querySelector(target) : undefined);";
        const string replacement =
            "var domElement = specialHTMLTargets[target] || ((target === '#canvas' || target === 'canvas') ? ((typeof Module !== 'undefined' && Module.canvas) || globalThis.__xengineCanvas || null) : (typeof document != 'undefined' && document.querySelector ? document.querySelector(target) : undefined));";
        return source.Contains(needle, StringComparison.Ordinal)
            ? source.Replace(needle, replacement, StringComparison.Ordinal)
            : source;
    }

    internal static string ImportMetaUrlFor(string packageRelativeFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRelativeFile);
        string rel = packageRelativeFile.Replace('\\', '/').TrimStart('/');
        return ImportMetaUrlPrefix + rel;
    }

    private static string ReplaceNamedExport(Match match)
    {
        string list = match.Groups[1].Value;
        var builder = new StringBuilder();
        builder.Append("module.exports = module.exports || {}");
        foreach (string raw in list.Split(','))
        {
            string part = raw.Trim();
            if (part.Length == 0)
                continue;

            string local;
            string exported;
            int asIndex = part.IndexOf(" as ", StringComparison.Ordinal);
            if (asIndex >= 0)
            {
                local = part.Substring(0, asIndex).Trim();
                exported = part.Substring(asIndex + 4).Trim();
            }
            else
            {
                local = exported = part;
            }

            builder.Append("; module.exports[");
            builder.Append(JsonSerializer.Serialize(exported));
            builder.Append("] = ");
            builder.Append(local);
        }

        builder.Append(';');
        return builder.ToString();
    }
}
