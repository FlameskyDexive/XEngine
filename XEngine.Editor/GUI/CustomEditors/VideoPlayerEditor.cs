// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Diagnostics;

using XEngine.Editor.Inspector;
using XEngine.Editor.Core;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Quill;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Runtime.Video;
using XEngine.Vector;

namespace XEngine.Editor.GUI;

/// <summary>
/// Custom inspector for the <see cref="VideoPlayer"/> component: the default property grid
/// plus a preview foldout. In EDIT mode the preview runs an independent
/// <see cref="VideoPreviewSession"/> (the real component is never started, PlayOnAwake is
/// not honoured, nothing writes to the scene); a URL source connects only when the user
/// presses Play. In PLAY mode the foldout shows the real player and its live texture.
/// Selection changes, fold-out collapse, and cleanup all stop preview audio/decoding.
/// </summary>
[CustomEditor(typeof(VideoPlayer))]
public class VideoPlayerEditor : CustomEditor
{
    private object? _activeTarget;
    private VideoPreviewSession? _preview;
    private bool _playing;
    private bool _muted = true;
    private bool _loop;
    private float _speed = 1f;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastTick;
    private bool _cleanupHooked;

    public override void OnGUI(Paper paper, string id, object target)
    {
        var player = target as VideoPlayer;
        HookSelectionCleanup();
        TrackTarget(target);

        GUI.PropertyGridUtils.Draw(paper, $"{id}_props", target);

        if (player is null)
        {
            return;
        }

        id = $"{id}_vp";
        bool playMode = Application.IsPlaying && Application.IsEditor;

        Origami.Header(paper, $"{id}_pv_h", "Video Preview").Show();

        if (playMode)
        {
            // Play mode: the real player owns playback — show its state and live texture.
            DrawLivePreview(paper, $"{id}_pv", player.Texture);
            using (paper.Row($"{id}_pv_row").Enter())
            {
                Origami.Label(paper, $"{id}_pv_st",
                    $"{player.State} · {FormatTime(player.Time)} / {FormatTime(player.Length)}").Show();
            }

            using (paper.Row($"{id}_pv_ctl").Enter())
            {
                Origami.Button(paper, $"{id}_pv_play", player.IsPlaying ? "⏸" : "▶", () =>
                {
                    if (player.IsPlaying) player.Pause(); else player.Play();
                }).Show();
                Origami.Button(paper, $"{id}_pv_stop", "⏹", player.Stop).Show();
            }

            if (_preview is not null)
            {
                // The real player took over: drop any edit-mode preview.
                TeardownPreview();
            }

            return;
        }

        // ── edit mode: independent preview, no scene writes ──
        using (paper.Row($"{id}_pv_row").Enter())
        {
            Origami.Button(paper, $"{id}_pv_play", _playing ? "⏸" : "▶", () =>
            {
                if (_preview is null)
                {
                    StartPreview(player);
                    return;
                }

                if (_playing)
                {
                    _preview.Pause();
                    _playing = false;
                }
                else
                {
                    _preview.Play();
                    _playing = true;
                }
            }).Show();

            Origami.Button(paper, $"{id}_pv_stop", "⏹", () =>
            {
                TeardownPreview();
                _playing = false;
            }).Show();

            Origami.Label(paper, $"{id}_pv_note",
                _preview is null ? "edit-mode preview (muted) — press ▶" : $"{_preview.State} · {FormatTime(_preview.Time)}").Show();
        }

        // Playback tick from the GUI (never the game SimulationStep).
        double now = _clock.Elapsed.TotalSeconds;
        _lastTick = now;
        _preview?.Tick();
        if (_preview is not null && !_preview.IsPlaying)
        {
            _preview.Tick();
        }
    }

    private void StartPreview(VideoPlayer player)
    {
        if (!TryResolveUri(player, out string uri))
        {
            return;
        }

        TeardownPreview();
        _preview = VideoPreviewSession.Create(uri, withAudio: !_muted, loop: player.IsLooping, speed: player.PlaybackSpeed);
        _preview.SetMuted(_muted);
        _playing = true;
        _lastTick = _clock.Elapsed.TotalSeconds;
    }

    private static bool TryResolveUri(VideoPlayer player, out string uri)
    {
        uri = string.Empty;
        if (player.Source == VideoSource.Url)
        {
            uri = player.Url ?? string.Empty;
            return uri.Length > 0;
        }

        return player.Clip is not null && player.Clip.TryGetPayloadPath(out uri);
    }

    private void DrawLivePreview(Paper paper, string id, Texture2D? texture)
    {
        paper.Box($"{id}_view").Width(240).Height(135)
            .BackgroundColor(EditorTheme.Neutral600)
            .OnPostLayout((handle, rect) => paper.Draw(ref handle, (canvas, r) =>
            {
                if (texture is null || texture.IsDisposed)
                {
                    return;
                }

                VideoClipAssetEditor.DrawVideoFrame(canvas, r, texture);
            }));
    }

    // ---------------------------------------------------------------- target / cleanup

    private void TrackTarget(object target)
    {
        if (!ReferenceEquals(_activeTarget, target))
        {
            // Selection/instance changed: release the previous preview immediately.
            TeardownPreview();
            _activeTarget = target;
        }
    }

    private void HookSelectionCleanup()
    {
        if (_cleanupHooked)
        {
            return;
        }

        _cleanupHooked = true;
        Selection.OnSelectionChanged += TeardownPreview;
    }

    private void TeardownPreview()
    {
        _preview?.Dispose();
        _preview = null;
        _playing = false;
    }

    private static string FormatTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}:{t.Seconds:D2}" : $"{t.Seconds}.{t.Milliseconds / 100:D1}s";
    }
}
