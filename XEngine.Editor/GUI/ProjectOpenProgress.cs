using System;

namespace XEngine.Editor.GUI;

/// <summary>
/// Frame-paced project-open steps drawn on the launcher. The currently displayed step is painted
/// for one frame before its work runs, so the status text stays visible during a blocking load.
/// </summary>
internal static class ProjectOpenProgress
{
    internal static readonly string[] StepKeys =
    [
        // Ordered to match ExecuteProjectOpenStep: step 0 = LoadAssemblies (scripts), step 1 =
        // ReinitializeRegistries (registries), step 2 = EditorAssetBackend.Initialize (assets),
        // step 3 = dock layout, step 4 = scene. The old order showed "asset database" during the
        // (potentially minutes-long) script compile, so the status text lied about what was running.
        "launcher.progress.scripts",
        "launcher.progress.registries",
        "launcher.progress.assets",
        "launcher.progress.layout",
        "launcher.progress.scene",
    ];

    public static int StepCount => StepKeys.Length;

    public static bool IsActive { get; private set; }

    /// <summary>Index of the step currently shown (and, after the display frame, executed).</summary>
    public static int DisplayedStep { get; private set; }

    public static string StepKey =>
        DisplayedStep >= 0 && DisplayedStep < StepKeys.Length
            ? StepKeys[DisplayedStep]
            : StepKeys[^1];

    /// <summary>Detail line for the current step — e.g. "正在编译 XEngine.XGUI.Runtime (3/18)…" during
    /// the scripts step, empty otherwise. Written by ScriptAssemblyManager from the compile loop and
    /// read by the launcher's progress footer. Never null.</summary>
    public static string Detail { get; private set; } = string.Empty;

    /// <summary>Sets the detail line; safe to call from the compile worker thread (volatile field).</summary>
    public static void SetDetail(string detail) => Detail = detail ?? string.Empty;

    /// <summary>Determinate fill in [0,1] for the launcher progress bar.</summary>
    public static float Fraction
    {
        get
        {
            if (!IsActive)
                return 0f;
            return Math.Clamp((DisplayedStep + 1f) / (StepCount + 1f), 0.08f, 0.95f);
        }
    }

    private static bool _skipNextWork;

    public static void Begin()
    {
        IsActive = true;
        DisplayedStep = 0;
        Detail = string.Empty;
        _skipNextWork = true;
    }

    /// <summary>
    /// Returns true when the currently displayed step should execute this frame.
    /// The first call after <see cref="Begin"/> is display-only so the bar can paint first.
    /// </summary>
    public static bool TryBeginWork()
    {
        if (!IsActive)
            return false;
        if (_skipNextWork)
        {
            _skipNextWork = false;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Advance after work for <see cref="DisplayedStep"/> completed.
    /// Returns true when every step has finished.
    /// </summary>
    public static bool CompleteCurrentStep()
    {
        DisplayedStep++;
        if (DisplayedStep >= StepCount)
        {
            IsActive = false;
            return true;
        }

        return false;
    }

    public static void Cancel()
    {
        IsActive = false;
        DisplayedStep = 0;
        Detail = string.Empty;
        _skipNextWork = false;
    }
}
