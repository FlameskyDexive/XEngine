using System;
using System.Collections.Generic;

using XEngine.Runtime;

namespace XEngine.NodePlay.Dialogue;

/// <summary>
/// Minimal sample UI: subscribes the static request events and plays the conversation through
/// engine logs, auto-answering choices after a short delay (or first option). Swap for any real
/// UI by handling the same events — the runtime never talks to this class.
/// </summary>
public class NPSimpleDialogueUI
{
    private readonly List<(NPSubtitlesRequest request, DateTime shownAt)> _pendingChoices = new();

    public bool Enabled = true;

    public NPSimpleDialogueUI() => Attach();

    public void Attach()
    {
        NPDialogueAsset.OnSubtitlesRequest += OnSubtitles;
        NPDialogueAsset.OnMultipleChoiceRequest += OnChoice;
    }

    public void Detach()
    {
        NPDialogueAsset.OnSubtitlesRequest -= OnSubtitles;
        NPDialogueAsset.OnMultipleChoiceRequest -= OnChoice;
    }

    private void OnSubtitles(NPSubtitlesRequest request)
    {
        if (!Enabled)
            return;
        request.Handled = true;
        Debug.Log("[Dialogue] " + request.Actor.DisplayName + ": " + request.Statement.Text);
        request.Continue();
    }

    private void OnChoice(NPMultipleChoiceRequest request)
    {
        if (!Enabled)
            return;
        request.Handled = true;
        for (int i = 0; i < request.Options.Count; i++)
            Debug.Log($"[Dialogue] {i + 1}. {request.Options[i].Text.Text}");
        Debug.Log("[Dialogue] (auto-selecting 1 — sample UI)");
        request.Pick(0);
    }
}
