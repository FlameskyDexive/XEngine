using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Ai.Protocol;

namespace XEngine.Ai.Host;

internal static class NavCli
{
    public static Task<object> Invoke(HostCommand command, IReadOnlyList<string> args, IServiceProvider services)
    {
        object boxed = command switch
        {
            HostCommand.NavInspect => NavTools.Inspect(services, Asset(args)),
            HostCommand.NavQueryPath => NavTools.QueryPath(
                services, Asset(args),
                Vec(args, "--from").X, Vec(args, "--from").Y, Vec(args, "--from").Z,
                Vec(args, "--to").X, Vec(args, "--to").Y, Vec(args, "--to").Z),
            HostCommand.NavExport => NavTools.Export(
                services, Asset(args), Req(args, "--format"), Req(args, "--output")),
            _ => null!,
        };
        if (boxed is not null) return Task.FromResult(boxed);
        return InvokeWrite(command, args, services);
    }

    static async Task<object> InvokeWrite(HostCommand command, IReadOnlyList<string> args, IServiceProvider services)
    {
        if (command != HostCommand.NavSettingsPatch)
            throw new InvalidOperationException($"Not a nav CLI command: {command}");

        return await NavTools.PatchSettings(
            services, Asset(args), Patches(args), Req(args, "--idempotency-key"),
            Opt(args, "--intent"), Opt(args, "--expected-before-hash"),
            Flag(args, "--dry-run"), CancellationToken.None).ConfigureAwait(false);
    }

    static string Asset(IReadOnlyList<string> args)
        => Opt(args, "--navmesh") ?? Opt(args, "--asset")
           ?? throw new ArgumentException("--navmesh (or --asset) requires an argument.");

    static NavVec3 Vec(IReadOnlyList<string> args, string name)
        => NavTools.ParseVec3(Req(args, name), name);

    static string Req(IReadOnlyList<string> args, string name)
    {
        string? v = Opt(args, name);
        if (string.IsNullOrEmpty(v)) throw new ArgumentException($"{name} requires an argument.");
        return v;
    }

    static string? Opt(IReadOnlyList<string> args, string name)
    {
        for (int i = 0; i < args.Count; i++)
        {
            if (!string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) continue;
            if (i + 1 >= args.Count || args[i + 1].StartsWith("-", StringComparison.Ordinal)) return null;
            return args[i + 1];
        }

        return null;
    }

    static bool Flag(IReadOnlyList<string> args, string name)
    {
        for (int i = 0; i < args.Count; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static SceneFieldPatch[] Patches(IReadOnlyList<string> args)
    {
        using JsonDocument doc = JsonDocument.Parse(Req(args, "--patches"));
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("--patches must be a JSON array of {path, value}.");
        var list = new List<SceneFieldPatch>();
        foreach (JsonElement el in doc.RootElement.EnumerateArray())
        {
            list.Add(new SceneFieldPatch(
                el.GetProperty("path").GetString() ?? "",
                el.GetProperty("value").GetString() ?? ""));
        }

        return list.ToArray();
    }
}
