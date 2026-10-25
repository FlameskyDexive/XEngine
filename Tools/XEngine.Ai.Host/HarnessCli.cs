using Microsoft.Extensions.DependencyInjection;
using XEngine.Ai.Harness;
using XEngine.Ai.Protocol;

namespace XEngine.Ai.Host;

/// <summary>
/// harness install / status / upgrade verbs — the CLI entry to the same installer the editor uses
/// at project creation. Install/upgrade are idempotent: template outputs are rewritten and skills
/// re-copied; the .xengine/harness.json stamp records what shipped. Allowed in every edition (an
/// engine-dev host may seed a user project's harness); provenance is the stamp itself — the files
/// are scaffolding outside Assets/, so they do not go through the asset operation journal.
/// </summary>
internal static class HarnessCli
{
    public static HarnessInstallResult Install(IReadOnlyList<string> args, IServiceProvider services)
    {
        string projectRoot = services.GetRequiredService<ProjectDescriptor>().ProjectRoot;
        return HarnessInstaller.Install(projectRoot, GetOptionalOption(args, "--engine-root"));
    }

    public static HarnessStatus Status(IServiceProvider services)
    {
        return HarnessInstaller.Status(services.GetRequiredService<ProjectDescriptor>().ProjectRoot);
    }

    public static object Upgrade(IReadOnlyList<string> args, IServiceProvider services)
    {
        string projectRoot = services.GetRequiredService<ProjectDescriptor>().ProjectRoot;
        HarnessStatus before = HarnessInstaller.Status(projectRoot);
        HarnessInstallResult result = HarnessInstaller.Install(projectRoot, GetOptionalOption(args, "--engine-root"));
        return new HarnessUpgradeResult(before.Installed, before.HarnessVersion, result);
    }

    private static string? GetOptionalOption(IReadOnlyList<string> args, string name)
    {
        for (int index = 0; index < args.Count; ++index)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return index + 1 < args.Count ? args[index + 1] : null;
            }
        }
        return null;
    }
}

/// <summary>upgrade = install over an existing (or absent) harness; reports what was there before.</summary>
internal sealed record HarnessUpgradeResult(
    bool WasInstalled,
    string? PreviousVersion,
    HarnessInstallResult Result);
