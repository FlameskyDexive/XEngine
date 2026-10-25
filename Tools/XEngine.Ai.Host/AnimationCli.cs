using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;
using XEngine.Ai.Protocol;

namespace XEngine.Ai.Host;

internal static class AnimationCli
{
    public static Task<object> Invoke(HostCommand command, IReadOnlyList<string> args, IServiceProvider services)
    {
        object boxed = command switch
        {
            HostCommand.AnimatorInspect => AnimationTools.InspectAnimator(services, Req(args, "--controller")),
            HostCommand.AnimatorValidate => AnimationTools.ValidateAnimator(services, Req(args, "--controller")),
            HostCommand.ClipInspect => AnimationTools.InspectClip(
                services, Req(args, "--clip"), Opt(args, "--scene"), Opt(args, "--object")),
            HostCommand.BlendTreeInspect => AnimationTools.InspectBlendTree(
                services, Req(args, "--asset"), Opt(args, "--state")),
            HostCommand.OverrideControllerInspect => AnimationTools.InspectOverrideController(
                services, Req(args, "--asset")),
            HostCommand.AvatarMaskInspect => AnimationTools.InspectAvatarMask(
                services, Req(args, "--asset")),
            _ => null!,
        };
        if (boxed is not null) return Task.FromResult(boxed);
        return InvokeWrite(command, args, services);
    }

    static async Task<object> InvokeWrite(HostCommand command, IReadOnlyList<string> args, IServiceProvider services)
    {
        string key = Req(args, "--idempotency-key");
        string? intent = Opt(args, "--intent");
        string? hash = Opt(args, "--expected-before-hash");
        bool dry = Flag(args, "--dry-run");
        CancellationToken ct = CancellationToken.None;

        return command switch
        {
            HostCommand.AnimatorStateCreate => await AnimationTools.CreateState(
                services, Req(args, "--controller"), Req(args, "--name"), key, intent,
                Opt(args, "--layer"), Opt(args, "--clip-guid"), hash, dry, ct).ConfigureAwait(false),
            HostCommand.AnimatorStatePatch => await AnimationTools.PatchState(
                services, Req(args, "--controller"), Req(args, "--state"), Patches(args), key, intent,
                Opt(args, "--layer"), hash, dry, ct).ConfigureAwait(false),
            HostCommand.AnimatorStateDelete => await AnimationTools.DeleteState(
                services, Req(args, "--controller"), Req(args, "--state"), key, intent,
                Opt(args, "--layer"), hash, dry, ct).ConfigureAwait(false),
            HostCommand.AnimatorTransitionCreate => await AnimationTools.CreateTransition(
                services, Req(args, "--controller"), Req(args, "--from"), Req(args, "--to"), key,
                Opt(args, "--name"), intent, Opt(args, "--layer"), hash, dry, ct).ConfigureAwait(false),
            HostCommand.AnimatorTransitionPatch => await AnimationTools.PatchTransition(
                services, Req(args, "--controller"), Req(args, "--transition"), Patches(args), key, intent,
                Opt(args, "--layer"), hash, dry, ct).ConfigureAwait(false),
            HostCommand.AnimatorTransitionDelete => await AnimationTools.DeleteTransition(
                services, Req(args, "--controller"), Req(args, "--transition"), key, intent,
                Opt(args, "--layer"), hash, dry, ct).ConfigureAwait(false),
            HostCommand.AnimatorTransitionReorder => await AnimationTools.ReorderTransitions(
                services, Req(args, "--controller"), Req(args, "--state"),
                (Opt(args, "--names") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                key, intent, Opt(args, "--layer"), hash, dry, ct).ConfigureAwait(false),
            HostCommand.AnimatorParameterAdd => await AnimationTools.AddParameter(
                services, Req(args, "--controller"), Req(args, "--name"), key,
                Opt(args, "--type") ?? "Float", intent, hash, dry, ct).ConfigureAwait(false),
            HostCommand.AnimatorParameterRemove => await AnimationTools.RemoveParameter(
                services, Req(args, "--controller"), Req(args, "--name"), key, intent, hash, dry, ct).ConfigureAwait(false),
            HostCommand.AnimatorLayerAdd => await AnimationTools.AddLayer(
                services, Req(args, "--controller"), Req(args, "--name"), key, intent, hash, dry, ct).ConfigureAwait(false),
            HostCommand.AnimatorLayerPatch => await AnimationTools.PatchLayer(
                services, Req(args, "--controller"), Req(args, "--layer"), Patches(args), key, intent, hash, dry, ct).ConfigureAwait(false),
            HostCommand.AnimatorStateMachineCreate => await AnimationStateMachineTools.CreateStateMachine(
                services, Req(args, "--controller"), Req(args, "--name"), key, intent,
                Opt(args, "--layer"), Opt(args, "--parent"),
                OptFloat(args, "--x") ?? 200f, OptFloat(args, "--y") ?? 80f, hash, dry, ct).ConfigureAwait(false),
            HostCommand.AnimatorStateMachineDelete => await AnimationStateMachineTools.DeleteStateMachine(
                services, Req(args, "--controller"), Req(args, "--name"), key, intent,
                Opt(args, "--layer"), Opt(args, "--parent"), hash, dry, ct).ConfigureAwait(false),
            HostCommand.AnimatorStateMachineMove => await AnimationStateMachineTools.MoveStateIntoMachine(
                services, Req(args, "--controller"), Req(args, "--state"), Req(args, "--target"), key, intent,
                Opt(args, "--layer"), Opt(args, "--path"), hash, dry, ct).ConfigureAwait(false),
            HostCommand.ClipRetarget => await AnimationTools.RetargetClip(
                services, Req(args, "--clip"), key, Opt(args, "--like"), intent, hash, dry, ct)
                .ConfigureAwait(false),
            HostCommand.ClipPatch => await AnimationTools.PatchClip(
                services, Req(args, "--clip"), Patches(args), key, intent, hash, dry, ct).ConfigureAwait(false),
            HostCommand.ClipCurvePatch => await AnimationTools.PatchCurve(
                services, Req(args, "--clip"),
                Opt(args, "--type") != null ? (Opt(args, "--bone") ?? "") : Req(args, "--bone"),
                Req(args, "--channel"), Req(args, "--op"),
                Float(args, "--time"), key, OptFloat(args, "--value"), Opt(args, "--type"), intent, hash, dry, ct).ConfigureAwait(false),
            HostCommand.ClipEventAdd => await AnimationTools.AddClipEvent(
                services, Req(args, "--clip"), Float(args, "--time"), Req(args, "--function"), key,
                OptFloat(args, "--float") ?? 0f, OptInt(args, "--int") ?? 0,
                Opt(args, "--string"), Opt(args, "--object"), intent, hash, dry, ct).ConfigureAwait(false),
            HostCommand.ClipEventPatch => await AnimationTools.PatchClipEvent(
                services, Req(args, "--clip"), Int(args, "--index"), Patches(args), key, intent, hash, dry, ct)
                .ConfigureAwait(false),
            HostCommand.ClipEventRemove => await AnimationTools.RemoveClipEvent(
                services, Req(args, "--clip"), Int(args, "--index"), key, intent, hash, dry, ct)
                .ConfigureAwait(false),
            HostCommand.BlendTreePatch => await AnimationTools.PatchBlendTree(
                services, Req(args, "--asset"), Patches(args), key, Opt(args, "--state"), intent, hash, dry, ct)
                .ConfigureAwait(false),
            HostCommand.OverrideControllerPatch => await AnimationTools.PatchOverrideController(
                services, Req(args, "--asset"), Patches(args), key, intent, hash, dry, ct)
                .ConfigureAwait(false),
            HostCommand.AvatarMaskPatch => await AnimationTools.PatchAvatarMask(
                services, Req(args, "--asset"), Patches(args), key, intent, hash, dry, ct)
                .ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Not an animation CLI command: {command}"),
        };
    }

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

    static float Float(IReadOnlyList<string> args, string name)
        => float.Parse(Req(args, name), CultureInfo.InvariantCulture);

    static int Int(IReadOnlyList<string> args, string name)
        => int.Parse(Req(args, name), CultureInfo.InvariantCulture);

    static int? OptInt(IReadOnlyList<string> args, string name)
    {
        string? v = Opt(args, name);
        return v is null ? null : int.Parse(v, CultureInfo.InvariantCulture);
    }

    static float? OptFloat(IReadOnlyList<string> args, string name)
    {
        string? v = Opt(args, name);
        return v is null ? null : float.Parse(v, CultureInfo.InvariantCulture);
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
