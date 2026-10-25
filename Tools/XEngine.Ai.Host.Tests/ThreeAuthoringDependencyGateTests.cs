using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>
/// A6 — dependency-closure gate for the three.js authoring chain: the runtime (and therefore
/// every Player build) must never reference the authoring host, its WebView2 dependency, the
/// recipe tooling or the host client. Authoring stays in the editor/CLI surface; a runtime csproj
/// that grows any of these references fails this test before it can ship.
/// </summary>
public sealed class ThreeAuthoringDependencyGateTests
{
    private static string RuntimeCsproj => Path.Combine(RepoRootLocator.Find(),
        "XEngine.Runtime", "XEngine.Runtime.csproj");

    private static readonly string[] ForbiddenInRuntime =
    [
        "Microsoft.Web.WebView2",
        "XEngine.Ai.ThreeHost",
        "XEngine.Ai.ThreeFx",
    ];

    [Fact]
    public void Runtime_NeverReferencesAuthoringToolchain()
    {
        string csproj = File.ReadAllText(RuntimeCsproj);
        foreach (string forbidden in ForbiddenInRuntime)
        {
            Assert.True(!csproj.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                $"XEngine.Runtime.csproj must not reference '{forbidden}': the Player build " +
                "must not carry the three.js authoring host or its dependencies.");
        }
    }

    [Fact]
    public void Runtime_HasNoThreeAuthoringAssembliesInOutput()
    {
        // The desktop Debug output the editor consumes doubles as the shipped assembly set for
        // this gate; walk the runtime's own build output and assert no authoring assembly landed.
        string runtimeDir = Path.Combine(RepoRootLocator.Find(), "Build", "Runtime", "Debug", "net10.0");
        if (!Directory.Exists(runtimeDir))
            return; // not built yet on this machine — the csproj gate above is the authoritative check

        foreach (string forbidden in ForbiddenInRuntime)
        {
            string hit = Array.Find(
                Directory.GetFiles(runtimeDir, "*.dll"),
                f => Path.GetFileName(f).Contains(forbidden, StringComparison.OrdinalIgnoreCase));
            Assert.True(hit is null,
                $"Authoring assembly '{Path.GetFileName(hit)}' found in the runtime output; " +
                "the Player dependency closure must exclude the authoring toolchain.");
        }
    }

    [Fact]
    public void AuthoringHost_ClientReferences_AreConfinedToTooling()
    {
        // The editor may reference the host *client* (it drives the authoring host process);
        // the runtime must not. Scan every csproj in the repo and enforce the split. Generated
        // game-project script csprojs (OutputPath=Library\ScriptAssemblies) are editor-domain:
        // the script compiler reference-feeds them every editor output DLL by design, and they
        // are not part of any Player dependency closure.
        string repoRoot = RepoRootLocator.Find();
        foreach (string csproj in Directory.GetFiles(repoRoot, "*.csproj", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(repoRoot, csproj);
            if (relative.StartsWith("Build", StringComparison.Ordinal)
                || relative.Contains("Library", StringComparison.Ordinal)
                || relative.Contains("obj", StringComparison.Ordinal))
            {
                continue;
            }

            string content = File.ReadAllText(csproj);
            if (content.Contains("Library\\ScriptAssemblies", StringComparison.Ordinal)
                || content.Contains("Library/ScriptAssemblies", StringComparison.Ordinal))
            {
                continue;
            }

            bool referencesClient = content.Contains("XEngine.Ai.ThreeHost.Client", StringComparison.Ordinal);
            bool isTooling = relative.Contains("XEngine.Editor", StringComparison.Ordinal)
                || relative.StartsWith("Tools", StringComparison.Ordinal);

            Assert.True(!referencesClient || isTooling,
                $"'{relative}' references the authoring host client; only the editor and CLI " +
                "tooling projects may do so.");
        }
    }
}
