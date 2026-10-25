// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Diagnostics;

using XEngine.Editor.Inspector;
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
/// Inspector for imported <see cref="VideoClip"/> assets: probe metadata plus a docked
/// bottom preview with transport controls. Selecting the asset never auto-plays and the
/// preview starts muted; the timeline slider merges scrub moves and issues a single seek
/// on release. Playback is pumped from the preview GUI (no scene involvement).
/// </summary>
[CustomAssetEditor(typeof(VideoClip))]
public class VideoClipAssetEditor : AssetImporterEditor
{
    private Guid _currentGuid = Guid.Empty;
    private VideoPreviewSession? _preview;
    private bool _playing;
    private bool _muted = true;
    private bool _loop;
    private float _speed = 1f;
    private float _volume = 1f;
    private bool _scrubbing;
    private double _scrubTime;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastTick;

    public override void OnGUI(Paper paper, string id, AssetEntry entry, EngineObject? asset)
    {
        if (asset is not VideoClip clip)
        {
            Origami.Label(paper, $"{id}_missing", "VideoClip failed to load.").Show();
            return;
        }

        id = $"{id}_{entry.Guid:N}";
        Origami.Header(paper, $"{id}_h", "Video").Show();

        (int w, int h) = clip.DisplaySize;
        string audio = clip.HasAudio ? $"{clip.AudioCodec} {clip.AudioSampleRate}Hz {clip.AudioChannels}ch" : "silent";
        Origami.Label(paper, $"{id}_info",
            $"{clip.VideoCodec}{(string.IsNullOrEmpty(clip.VideoProfile) ? "" : " " + clip.VideoProfile)} · {w}×{h} · {clip.Fps:F0}fps · {clip.DurationSeconds:F2}s · {audio} · {(clip.CanSeek ? "seekable" : "stream")}")
            .Show();
        Origami.Label(paper, $"{id}_meta", $"Container: {clip.Container} · {clip.ContentHash[..Math.Min(12, clip.ContentHash.Length)]}…")
            .Show();
    }

    public override bool HasPreview(EngineObject? asset) => asset is VideoClip;

    public override void OnPreviewGUI(Paper paper, string id, EngineObject asset, float width, float height)
    {
        if (asset is not VideoClip clip)
        {
            return;
        }

        if (_currentGuid != clip.AssetID)
        {
            // Selection changed to a different asset: stop + release the previous preview.
            TeardownPreview();
            _currentGuid = clip.AssetID;
        }

        float viewH = height * 0.62f;
        float controlsH = height - viewH;

        // ── video view ──
        Texture2D? frame = _preview?.Texture;
        if (frame is null && !_playing)
        {
            frame = clip.Cover; // poster frame before the first play
        }

        paper.Box($"{id}_view").Width(width).Height(viewH)
            .BackgroundColor(EditorTheme.Neutral600)
            .OnPostLayout((handle, rect) => paper.Draw(ref handle, (canvas, r) =>
            {
                if (frame is null || frame.IsDisposed)
                {
                    return;
                }

                DrawVideoFrame(canvas, r, frame);
            }));

        // ── transport ──
        using (paper.Row($"{id}_transport").Height(controlsH * 0.55f).Enter())
        {
            Origami.Button(paper, $"{id}_play", _playing ? "⏸" : "▶", () =>
            {
                if (_preview is null)
                {
                    StartPreview(clip);
                }

                if (_playing)
                {
                    _preview?.Pause();
                    _playing = false;
                }
                else
                {
                    _preview?.Play();
                    _playing = true;
                }
            }).Show();

            Origami.Button(paper, $"{id}_stop", "⏹", () =>
            {
                TeardownPreview();
                _playing = false;
            }).Show();

            Origami.Button(paper, $"{id}_loop", _loop ? "🔁" : "➡", () =>
            {
                _loop = !_loop;
                if (_preview is not null)
                {
                    _preview.IsLooping = _loop;
                }
            }).Show();

            Origami.Button(paper, $"{id}_mute", _muted ? "🔇" : "🔊", () =>
            {
                _muted = !_muted;
                _preview?.SetMuted(_muted);
            }).Show();

            Origami.Slider(paper, $"{id}_vol", _volume, v =>
            {
                _volume = v;
                _preview?.SetMuted(_muted || _volume <= 0.001f);
            }, 0f, 1f).Width(70f).Show();

            Origami.Slider(paper, $"{id}_spd", _speed, v =>
            {
                _speed = v;
                if (_preview is not null)
                {
                    _preview.Speed = v;
                }
            }, 0.5f, 2f).Width(70f).Show();
        }

        // ── timeline: scrub merges moves locally, release issues a single seek ──
        double previewTime = _preview?.Time ?? 0;
        double duration = Math.Max(0.05, _preview?.Duration ?? clip.DurationSeconds);
        double shownTime = _scrubbing ? _scrubTime : previewTime;
        using (paper.Row($"{id}_tl").Height(controlsH * 0.25f).Enter())
        {
            Origami.Slider(paper, $"{id}_tl_s", (float)(shownTime / duration),
                v => { if (_scrubbing) _scrubTime = v * duration; }, 0f, 1f)
                .OnDragStart(() =>
                {
                    _scrubbing = true;
                    _scrubTime = _preview?.Time ?? 0;
                })
                .OnDragEnd(() =>
                {
                    _scrubbing = false;
                    _preview?.Seek(_scrubTime);
                })
                .Width(width - 90f)
                .Show();

            Origami.Label(paper, $"{id}_tl_t", $"{FormatTime(shownTime)} / {FormatTime(duration)}").Show();
        }

        // ── status line ──
        string status = _preview is null
            ? "preview stopped"
            : _scrubbing ? "scrubbing…" : _preview.State.ToString();
        using (paper.Row($"{id}_status").Height(controlsH * 0.2f).Enter())
        {
            Origami.Label(paper, $"{id}_status_l", status).Show();
        }

        // Playback tick: pumped from the preview GUI, never the scene loop.
        double now = _clock.Elapsed.TotalSeconds;
        float delta = (float)(now - _lastTick);
        _lastTick = now;
        _preview?.Tick();
        if (_preview is not null && !_preview.IsPlaying)
        {
            _preview.Tick(); // paused previews still present poster/state frames
        }

        _ = delta;
    }

    public override void OnPreviewCleanup()
    {
        TeardownPreview();
    }

    private void StartPreview(VideoClip clip)
    {
        TeardownPreview();
        _currentGuid = clip.AssetID;
        _preview = VideoPreviewSession.CreateForClip(clip, withAudio: !_muted, loop: _loop, speed: _speed);
        _preview.SetMuted(_muted);
        _lastTick = _clock.Elapsed.TotalSeconds;
    }

    private void TeardownPreview()
    {
        _preview?.Dispose();
        _preview = null;
        _playing = false;
        _scrubbing = false;
    }

    private static string FormatTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}:{t.Seconds:D2}" : $"{t.Seconds}.{t.Milliseconds / 100:D1}s";
    }

    /// <summary>Aspect-fit blit of the live video texture (Y-flip: textures are Y-up).</summary>
    internal static void DrawVideoFrame(Canvas canvas, Rect rect, Texture2D texture)
    {
        if (texture.IsDisposed)
        {
            return;
        }

        float rx = (float)rect.Min.X, ry = (float)rect.Min.Y;
        float rw = (float)(rect.Max.X - rect.Min.X), rh = (float)(rect.Max.Y - rect.Min.Y);
        float maxW = rw - 8f, maxH = rh - 8f;
        float aspect = texture.Width / MathF.Max(1f, texture.Height);
        float drawW = maxW, drawH = drawW / aspect;
        if (drawH > maxH)
        {
            drawH = maxH;
            drawW = drawH * aspect;
        }

        float drawX = rx + (rw - drawW) / 2f;
        float drawY = ry + (rh - drawH) / 2f;

        canvas.SetBrushTexture(texture);
        canvas.SetBrushTextureTransform(
            XEngine.Vector.Spatial.Transform2D.CreateTranslation(drawX, drawY + drawH) *
            XEngine.Vector.Spatial.Transform2D.CreateScale(drawW, -drawH));
        canvas.RectFilled(drawX, drawY, drawW, drawH, XEngine.Vector.Color32.FromArgb(255, 255, 255, 255));
        canvas.ClearBrushTexture();
    }
}
