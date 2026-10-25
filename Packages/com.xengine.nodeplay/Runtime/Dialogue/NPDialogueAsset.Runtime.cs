using System;
using System.Collections.Generic;

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.NodePlay.Dialogue;

/// <summary>
/// Dialogue playback runtime: actor resolution (SELF = instigator), node cursor, and the static
/// request events that decouple any UI. With no UI subscribed (AutoPump) statements auto-continue
/// and the first offered choice is taken — headless tests and demos just work.
/// </summary>
public partial class NPDialogueAsset : NPGraphAsset
{
    /// <summary>UI hooks (reference-design parity). Handlers set request.Handled = true.</summary>
    public static event Action<NPSubtitlesRequest>? OnSubtitlesRequest;
    public static event Action<NPMultipleChoiceRequest>? OnMultipleChoiceRequest;
    public static event Action<NPDialogueAsset, INPDialogueActor?>? OnDialogueStarted;
    public static event Action<NPDialogueAsset, bool>? OnDialogueFinished;

    /// <summary>When no UI handled a request, advance automatically (tests / simple demos).</summary>
    public static bool AutoPump = true;

    public INPDialogueActor? Instigator { get; private set; }
    public DTNode? CurrentNode { get; private set; }

    /// <summary>Last node the cursor executed (test/inspection surface).</summary>
    public DTNode? LastRouted { get; private set; }
    public bool IsFinished { get; private set; }

    private Action<bool>? _onFinished;
    private readonly Dictionary<string, INPDialogueActor> _actorOverrides = new(StringComparer.Ordinal);

    public void SetActorReference(string key, INPDialogueActor actor) => _actorOverrides[key] = actor;

    public INPDialogueActor? ResolveActor(string key, NPRunner runner)
    {
        if (string.Equals(key, InstigatorKey, StringComparison.OrdinalIgnoreCase))
            return Instigator ?? ResolveFromRunner(runner);
        if (_actorOverrides.TryGetValue(key, out var byKey))
            return byKey;

        var param = FindActorParameter(key);
        if (param == null)
            return ResolveFromRunner(runner, key);

        // resolve by name among the runner's dialogue-actor components, else synthesize
        if (runner != null)
        {
            foreach (MonoBehaviour component in runner.GetComponents(typeof(INPDialogueActor)))
            {
                if (component is INPDialogueActor actor && actor.DisplayName == param.Name)
                    return actor;
            }
        }

        return new NPSyntheticActor(param.Name);
    }

    private INPDialogueActor? ResolveFromRunner(NPRunner runner, string? preferName = null)
    {
        if (runner == null)
            return null;
        foreach (MonoBehaviour component in runner.GetComponents(typeof(INPDialogueActor)))
        {
            if (component is INPDialogueActor actor
                && (preferName == null || actor.DisplayName == preferName))
                return actor;
        }

        return null;
    }

    private NPActorParameter? FindActorParameter(string key)
    {
        for (int i = 0; i < ActorParameters.Count; i++)
        {
            if (string.Equals(ActorParameters[i].Key, key, StringComparison.OrdinalIgnoreCase))
                return ActorParameters[i];
        }

        return null;
    }

    // ---- playback -----------------------------------------------------------------------

    public void StartDialogue(NPRunner runner, INPDialogueActor? instigator, Action<bool>? onFinished = null)
    {
        OwnerRunner = runner;
        GraphBlackboard ??= LocalBlackboard;
        GraphBlackboard.Identifier = runner?.Name ?? "Dialogue";
        Instigator = instigator
                     ?? (runner is INPDialogueActor self ? self : null)
                     ?? ResolveFromRunner(runner);
        IsFinished = false;
        _onFinished = onFinished;
        BuildTopology();
        OnStarted(runner);
        OnDialogueStarted?.Invoke(this, Instigator);

        if (PrimeNode is DTNode prime)
            ExecuteNode(prime);
        else
            FinishDialogue(true);
    }

    internal void ExecuteNode(DTNode node) => ExecuteNode(node, OwnerRunner!);

    internal void ExecuteNode(DTNode node, NPRunner runner)
    {
        LastRouted = node;
        CurrentNode = node;
        node.Status = NPStatus.Running;
        node.Execute(this, runner);
    }

    /// <summary>Advance along the outgoing wire (index = choice outcome for choice nodes).</summary>
    public void ContinueFrom(DTNode from, int wireIndex = 0)
    {
        if (IsFinished)
            return;
        int taken = 0;
        for (int i = 0; i < from.OutConnections.Count; i++)
        {
            if (from.OutConnections[i].Disabled)
                continue;
            if (taken++ == wireIndex)
            {
                if (from.OutConnections[i].Target is DTNode next)
                    ExecuteNode(next);
                return;
            }
        }

        // no outgoing wire: treat as natural finish
        FinishDialogue(true);
    }

    public void SayCurrent(string text, string actorKey)
    {
        var request = new NPSubtitlesRequest
        {
            Actor = ResolveActor(actorKey, OwnerRunner) ?? new NPSyntheticActor(actorKey),
            Statement = new NPStatement(text),
            Continue = () => { },
        };
        NPDialogueAsset.RaiseSubtitlesRequest(request);
    }

    public void FinishDialogue(bool success)
    {
        if (IsFinished)
            return;
        IsFinished = true;
        CurrentNode = null;
        OnDialogueFinished?.Invoke(this, success);
        _onFinished?.Invoke(success);
    }

    // ---- static pumps -------------------------------------------------------------------

    internal static void RaiseSubtitlesRequest(NPSubtitlesRequest request)
        => OnSubtitlesRequest?.Invoke(request);

    internal static void RaiseMultipleChoiceRequest(NPMultipleChoiceRequest request)
        => OnMultipleChoiceRequest?.Invoke(request);

    private sealed class NPSyntheticActor : INPDialogueActor
    {
        private readonly string _name;
        public NPSyntheticActor(string name) => _name = name;
        public string DisplayName => _name;
        public string PortraitKey => "";
        public string DialogueColor => "#dddddd";
        public Transform Transform => null!;
    }
}
