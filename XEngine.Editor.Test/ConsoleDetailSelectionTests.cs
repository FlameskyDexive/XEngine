// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;

using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Quill;
using XEngine.Scribe;
using XEngine.Vector;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// The console's log detail pane renders its message and stack trace through a read-only
/// <c>TextArea</c> so the text can be dragged over and copied, the way Unity's console allows. These
/// tests drive that widget headlessly: a selection must copy, and read-only must mean the keyboard
/// cannot edit what it selected.
/// </summary>
public class ConsoleDetailSelectionTests
{
    private const string DetailText = "Could not create asset from Assets/Textures/Missing.png";

    [Fact]
    public void DragSelection_CopiesTheSelectedRange()
    {
        var harness = new DetailPaneHarness(DetailText);

        harness.DragSelect(fromX: 0f, toX: 1000f);
        harness.PressCopy();

        Assert.Equal(DetailText, harness.Clipboard.Text);
    }

    [Fact]
    public void SelectAllShortcut_CopiesEverything()
    {
        var harness = new DetailPaneHarness(DetailText);

        harness.Click();
        harness.PressKeyWithControl(PaperKey.A);
        harness.PressCopy();

        Assert.Equal(DetailText, harness.Clipboard.Text);
    }

    [Fact]
    public void ReadOnlyPane_KeyboardCannotEditTheText()
    {
        var harness = new DetailPaneHarness(DetailText);
        harness.Clipboard.Text = "pasted";

        harness.DragSelect(fromX: 0f, toX: 1000f);
        harness.PressKey(PaperKey.Backspace);
        harness.PressKey(PaperKey.Delete);
        harness.PressKeyWithControl(PaperKey.V);
        harness.PressKeyWithControl(PaperKey.X);
        harness.PressKey(PaperKey.Enter);

        Assert.Null(harness.LastChange);
        Assert.Equal(DetailText, harness.RenderedText);
    }

    /// <summary>
    /// Drives the same element tree the console detail pane builds - a focusable wrapper with a hooked
    /// read-only multi-line text element - against a headless Paper.
    /// </summary>
    private sealed class DetailPaneHarness
    {
        private const float TextTop = 12f;
        private readonly Paper _paper;
        private readonly FontFile _font;
        private readonly string _text;

        public FakeClipboard Clipboard { get; } = new();

        /// <summary>Value the text element reports back, i.e. the edit the widget tried to make.</summary>
        public string? LastChange { get; private set; }

        /// <summary>Value the widget holds after the last frame, selection and edits included.</summary>
        public string RenderedText { get; private set; }

        public DetailPaneHarness(string text)
        {
            _text = text;
            RenderedText = text;
            _font = new FontFile(FontPath());
            _paper = new Paper(new NullRenderer(), 600, 200, new FontAtlasSettings());
            _paper.SetClipboardHandler(Clipboard);
            Frame();
        }

        /// <summary>Runs one frame; the caller sets input state through the other helpers first.</summary>
        private void Frame()
        {
            _paper.BeginFrame(0.016f);
            var settings = ElementBuilder.TextInputSettings.Default;
            settings.Font = _font;
            settings.ReadOnly = true;
            settings.DoWrap = true;

            using (_paper.Box("detail_sel").Width(UnitValue.Stretch()).Height(UnitValue.Auto)
                .Padding(0, 0, TextTop, 0).TabIndex(0).Enter())
            {
                _paper.Box("detail_txt").Width(UnitValue.Stretch()).Height(UnitValue.Auto)
                    .HookToParent().IsNotInteractable()
                    .FontSize(12f)
                    .TextArea(RenderedText, settings, onChange: v =>
                    {
                        LastChange = v;
                        RenderedText = v;
                    });
            }
            _paper.EndFrame();
        }

        public void Click()
        {
            _paper.SetPointerState(PaperMouseBtn.Left, 4f, TextTop + 4f, true, false);
            Frame();
            _paper.SetPointerState(PaperMouseBtn.Left, 4f, TextTop + 4f, false, false);
            Frame();
        }

        /// <summary>Press, sweep right across the line, release - a mouse text selection.</summary>
        public void DragSelect(float fromX, float toX)
        {
            _paper.SetPointerState(PaperMouseBtn.Left, fromX, TextTop + 4f, true, false);
            Frame();
            for (float x = fromX; x < toX; x += MathF.Max(1f, (toX - fromX) / 4f))
            {
                _paper.SetPointerState(PaperMouseBtn.Left, x, TextTop + 4f, true, true);
                Frame();
            }
            _paper.SetPointerState(PaperMouseBtn.Left, toX, TextTop + 4f, true, true);
            Frame();
            _paper.SetPointerState(PaperMouseBtn.Left, toX, TextTop + 4f, false, false);
            Frame();
        }

        public void PressCopy() => PressKeyWithControl(PaperKey.C);

        public void PressKey(PaperKey key)
        {
            _paper.SetKeyState(key, true);
            Frame();
            _paper.SetKeyState(key, false);
            Frame();
        }

        public void PressKeyWithControl(PaperKey key)
        {
            _paper.SetKeyState(PaperKey.LeftControl, true);
            Frame();
            PressKey(key);
            _paper.SetKeyState(PaperKey.LeftControl, false);
            Frame();
        }

        private static string FontPath()
        {
            string relative = Path.Combine("Packages", "com.xengine.xgui", "Runtime", "Resources", "DefaultFont.ttf");
            string local = Path.Combine(AppContext.BaseDirectory, relative);
            if (File.Exists(local)) return local;

            string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            return Path.Combine(repo, relative);
        }
    }

    private sealed class FakeClipboard : IClipboardHandler
    {
        public string Text = "";
        public string GetClipboardText() => Text;
        public void SetClipboardText(string text) => Text = text;
    }

    private sealed class NullRenderer : ICanvasRenderer
    {
        public void Dispose() { }
        public object CreateTexture(uint width, uint height) => new Int2((int)width, (int)height);
        public Int2 GetTextureSize(object texture) => (Int2)texture;
        public void SetTextureData(object texture, IntRect bounds, byte[] data) { }
        public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> drawCalls) { }
    }
}
