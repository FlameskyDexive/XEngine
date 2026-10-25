using System;
using System.Collections.Generic;

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.NodePlay.Dialogue;

/// <summary>Actor facade consumed by dialogue nodes and UI.</summary>
public interface INPDialogueActor
{
    string DisplayName { get; }
    string PortraitKey { get; }
    string DialogueColor { get; }
    Transform Transform { get; }
}

/// <summary>Component facade: put on any GameObject to make it a dialogue actor.</summary>
public class NPDialogueActor : MonoBehaviour, INPDialogueActor
{
    public string ActorName = "Actor";
    public string Portrait = "";
    public string ColorHex = "#ffffff";

    public string DisplayName => ActorName;
    public string PortraitKey => Portrait;
    public string DialogueColor => ColorHex;
    Transform INPDialogueActor.Transform => base.Transform;
}

/// <summary>One spoken line (+ optional audio key and metadata).</summary>
public class NPStatement
{
    public string Text = "";
    public string AudioKey = "";
    public string Meta = "";

    public NPStatement() { }
    public NPStatement(string text) => Text = text;
    public NPStatement(string text, string audioKey, string meta)
    { Text = text; AudioKey = audioKey; Meta = meta; }

    /// <summary>Replace [varName] / [Global/varName] tokens from a blackboard chain.</summary>
    public NPStatement InterpolateInto(INPBlackboard? bb) => new NPStatement(Interpolate(bb), AudioKey, Meta);

    public string Interpolate(INPBlackboard? bb)
    {
        if (bb is not NPBlackboardSource source || Text.IndexOf('[') < 0)
            return Text;

        string result = Text;
        int guard = 0;
        while (guard++ < 16)
        {
            int open = result.IndexOf('[');
            if (open < 0)
                break;
            int close = result.IndexOf(']', open);
            if (close < 0)
                break;

            string token = result[(open + 1)..close];
            string replacement = source.Get(token)?.Value?.ToString() ?? "";
            result = result[..open] + replacement + result[(close + 1)..];
        }

        return result;
    }
}

/// <summary>Subtitles request — the UI answers by invoking <see cref="Continue"/> when done.</summary>
public sealed class NPSubtitlesRequest
{
    public required INPDialogueActor Actor;
    public required NPStatement Statement;
    public required Action Continue;

    /// <summary>Set by a UI handler; without it AutoPump advances the conversation.</summary>
    public bool Handled;
}

public sealed class NPChoiceOption
{
    public required NPStatement Text;
    /// <summary>Only offered when this condition passes (null = always).</summary>
    public NPConditionTask? Condition;
    /// <summary>Set to speak the picked option back as a subtitle.</summary>
    public bool SaySelection;
}

/// <summary>Multiple-choice request — the UI invokes <see cref="Pick"/> with the chosen index.</summary>
public sealed class NPMultipleChoiceRequest
{
    public required INPDialogueActor Actor;
    public required List<NPChoiceOption> Options;
    public float AvailableTime;
    public required Action<int> Pick;

    public bool Handled;
}
