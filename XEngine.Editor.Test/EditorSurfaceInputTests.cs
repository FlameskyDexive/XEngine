using System.Numerics;

using NSubstitute;
using Silk.NET.Input;
using XEngine.Editor.Core;
using XEngine.PaperUI;
using XEngine.Quill;
using XEngine.Scribe;
using XEngine.Vector;
using Xunit;

namespace XEngine.Editor.Test;

public sealed class EditorSurfaceInputTests
{
    [Fact]
    public void CompleteDragBetweenFramesPreservesOriginHeldMovementAndRelease()
    {
        using var f = new Fixture();
        f.Move(10, 20);
        f.Button(MouseButton.Left, true);
        f.Move(80, 90);
        f.Button(MouseButton.Left, false);
        Assert.True(f.Input.HasPendingInput);

        f.Frame(p => { Assert.True(p.IsPointerPressed(PaperMouseBtn.Left)); Assert.Equal(new Float2(10, 20), p.PointerPos); });
        f.Frame(p =>
        {
            Assert.True(p.IsPointerDown(PaperMouseBtn.Left));
            Assert.False(p.IsPointerReleased(PaperMouseBtn.Left));
            Assert.Equal(new Float2(70, 70), p.PointerDelta);
        });
        f.Frame(p => { Assert.True(p.IsPointerReleased(PaperMouseBtn.Left)); Assert.Equal(new Float2(80, 90), p.PointerPos); });
        Assert.False(f.Input.HasPendingInput);
        f.Frame(p => Assert.False(p.IsPointerReleased(PaperMouseBtn.Left)));
    }

    [Fact]
    public void GetterAheadOfCallbackDoesNotReplacePressOrigin()
    {
        using var f = new Fixture();
        f.Move(10, 20);
        f.PolledPosition(80, 90);
        f.Button(MouseButton.Left, true);
        f.Move(80, 90);
        f.Frame(p => Assert.Equal(new Float2(10, 20), p.PointerPos));
        Assert.True(f.Input.HasPendingInput);
        f.Frame(p => { Assert.True(p.IsPointerDown(PaperMouseBtn.Left)); Assert.Equal(new Float2(80, 90), p.PointerPos); });
        Assert.False(f.Input.HasPendingInput);
    }

    [Fact]
    public void QueuedClicksKeepTheirOwnCoordinatesAndDoNotRepeatEdges()
    {
        using var f = new Fixture();
        for (int click = 0; click < 2; click++)
        {
            f.Move(10 + 70 * click, 20 + 70 * click);
            f.Button(MouseButton.Left, true);
            f.Button(MouseButton.Left, false);
        }
        for (int frame = 0; frame < 4; frame++)
        {
            int current = frame;
            f.Frame(p =>
            {
                Assert.Equal(current % 2 == 0, p.IsPointerPressed(PaperMouseBtn.Left));
                Assert.Equal(current % 2 == 1, p.IsPointerReleased(PaperMouseBtn.Left));
                Assert.Equal(current < 2 ? new Float2(10, 20) : new Float2(80, 90), p.PointerPos);
            });
        }
        Assert.False(f.Input.HasPendingInput);
    }

    [Fact]
    public void DoubleClickBetweenFramesReachesPaperDoubleClickDetection()
    {
        using var f = new Fixture();
        for (int i = 0; i < 2; i++) { f.Button(MouseButton.Left, true); f.Button(MouseButton.Left, false); }
        f.Frame(p => Assert.False(p.IsPointerDoubleClick(PaperMouseBtn.Left)));
        f.Frame();
        f.Frame(p => Assert.True(p.IsPointerDoubleClick(PaperMouseBtn.Left)));
        f.Frame();
        Assert.False(f.Input.HasPendingInput);
    }

    [Fact]
    public void AllThreeButtonsRetainIndependentStateAndChronologicalPositions()
    {
        using var f = new Fixture();
        f.Move(10, 20); f.Button(MouseButton.Left, true);
        f.Move(30, 40); f.Button(MouseButton.Middle, true);
        f.Move(50, 60); f.Button(MouseButton.Right, true);
        f.Button(MouseButton.Middle, false);
        f.Frame(p => { Assert.True(p.IsPointerPressed(PaperMouseBtn.Left)); Assert.False(p.IsPointerDown(PaperMouseBtn.Middle)); });
        f.Frame(p => { Assert.True(p.IsPointerDown(PaperMouseBtn.Left)); Assert.True(p.IsPointerPressed(PaperMouseBtn.Middle)); Assert.Equal(new Float2(30, 40), p.PointerPos); });
        f.Frame(p => { Assert.True(p.IsPointerPressed(PaperMouseBtn.Right)); Assert.Equal(new Float2(50, 60), p.PointerPos); });
        f.Frame(p => { Assert.True(p.IsPointerReleased(PaperMouseBtn.Middle)); Assert.True(p.IsPointerDown(PaperMouseBtn.Left)); Assert.True(p.IsPointerDown(PaperMouseBtn.Right)); });
    }

    [Fact]
    public void CapturedNativeCoordinatesUseCurrentIndependentScales()
    {
        using var f = new Fixture();
        f.Move(10, 20); f.Button(MouseButton.Right, true);
        f.Move(80, 90); f.Button(MouseButton.Right, false);
        f.Frame(p => Assert.Equal(new Float2(5, 40), p.PointerPos), .5f, 2f);
        f.Frame(p => Assert.Equal(new Float2(40, 180), p.PointerPos), .5f, 2f);
        f.Frame(p => Assert.True(p.IsPointerReleased(PaperMouseBtn.Right)), .5f, 2f);
    }

    [Fact]
    public void KeyboardTapAndModifiersSurviveBetweenFramesWithoutRepeatEdges()
    {
        using var f = new Fixture();
        f.Key(Key.ControlLeft, true); f.Key(Key.A, true); f.Key(Key.A, true);
        f.Key(Key.A, false); f.Key(Key.ControlLeft, false);
        Assert.True(f.Input.HasPendingInput);
        f.Frame(p => { Assert.True(p.IsKeyPressed(PaperKey.A)); Assert.True(p.IsKeyDown(PaperKey.LeftControl)); });
        Assert.True(f.Input.HasPendingInput);
        f.Frame(p => { Assert.True(p.IsKeyReleased(PaperKey.A)); Assert.True(p.IsKeyReleased(PaperKey.LeftControl)); });
        Assert.False(f.Input.HasPendingInput);
        f.Frame(p => { Assert.False(p.IsKeyReleased(PaperKey.A)); Assert.False(p.IsKeyPressed(PaperKey.A)); });
    }

    [Fact]
    public void TextAndScrollCallbacksScheduleOneDeliveryThenDrain()
    {
        using var f = new Fixture();
        foreach (char c in "中A\r") f.Character(c);
        f.Scroll(1); f.Scroll(2);
        Assert.True(f.Input.HasPendingInput);
        f.Frame(p => { Assert.Equal("中A\n", new string(p.InputString.ToArray())); Assert.Equal(3f, p.PointerWheel); });
        Assert.False(f.Input.HasPendingInput);
        f.Frame(p => { Assert.Empty(p.InputString); Assert.Equal(0f, p.PointerWheel); });
    }

    [Fact]
    public void PollOnlyDevicesStillDeliverButtonsKeysMovementAndWheel()
    {
        using var f = new Fixture();
        f.PolledPosition(12, 24);
        f.HeldButtons[(int)MouseButton.Middle] = true;
        f.HeldKeys.Add(Key.Escape);
        f.Mouse.ScrollWheels.Returns(new[] { new ScrollWheel(0, 2) });
        f.Frame(p =>
        {
            Assert.True(p.IsPointerPressed(PaperMouseBtn.Middle)); Assert.True(p.IsKeyPressed(PaperKey.Escape));
            Assert.Equal(new Float2(12, 24), p.PointerPos); Assert.Equal(2f, p.PointerWheel);
        });
        f.HeldButtons[(int)MouseButton.Middle] = false; f.HeldKeys.Clear();
        f.Frame(p => { Assert.True(p.IsPointerReleased(PaperMouseBtn.Middle)); Assert.True(p.IsKeyReleased(PaperKey.Escape)); });
    }

    [Fact]
    public void BurstDrainsEveryEdgeAndReleasesLargeRetainedBuffer()
    {
        using var f = new Fixture();
        for (int i = 0; i < 1100; i++) { f.Button(MouseButton.Left, true); f.Button(MouseButton.Left, false); }
        for (int i = 0; i < 2200; i++)
        {
            bool pressed = i % 2 == 0;
            f.Frame(p => Assert.Equal(pressed, p.IsPointerPressed(PaperMouseBtn.Left)));
        }
        Assert.False(f.Input.HasPendingInput);
        var queue = typeof(EditorSurfaceInput).GetField("_mouseTransitions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(f.Input)!;
        Assert.InRange((int)queue.GetType().GetProperty("Capacity")!.GetValue(queue)!, 4, 1024);
    }

    [Fact]
    public void DisposeUnsubscribesCallbacksClearsPendingInputAndOwnsContextOnce()
    {
        using var f = new Fixture();
        f.Button(MouseButton.Left, true); f.Key(Key.A, true); f.Character('A'); f.Scroll(1);
        f.Input.Dispose(); f.Input.Dispose();
        f.Move(20, 30); f.Button(MouseButton.Left, false); f.Key(Key.A, false); f.Character('B'); f.Scroll(2);
        Assert.False(f.Input.HasPendingInput);
        f.Context.Received(1).Dispose();
        Assert.Throws<ObjectDisposedException>(() => f.Input.Apply(f.Paper, 1, 1));
    }

    [Fact]
    public void MissingDevicesLeaveAnEmptyBridge()
    {
        using var f = new Fixture(false);
        f.Frame(p => { Assert.False(p.IsPointerDown(PaperMouseBtn.Left)); Assert.False(p.IsKeyDown(PaperKey.A)); });
        Assert.False(f.Input.HasPendingInput);
        Assert.Equal("", f.Input.GetClipboardText());
        f.Input.SetClipboardText("ignored without a keyboard");
    }

    [Fact]
    public void ClipboardUsesThisSurfaceKeyboardAndStopsAfterDispose()
    {
        using var f = new Fixture();
        f.Keyboard.ClipboardText.Returns("Camera");
        Assert.Equal("Camera", f.Paper.GetClipboard());
        f.Paper.SetClipboard("Light");
        f.Keyboard.Received().ClipboardText = "Light";
        f.Input.Dispose();
        Assert.Equal("", f.Paper.GetClipboard());
        f.Paper.SetClipboard("after disposal");
        f.Keyboard.DidNotReceive().ClipboardText = "after disposal";
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly IInputContext Context = Substitute.For<IInputContext>();
        internal readonly IMouse Mouse = Substitute.For<IMouse>();
        internal readonly IKeyboard Keyboard = Substitute.For<IKeyboard>();
        internal readonly bool[] HeldButtons = new bool[12];
        internal readonly HashSet<Key> HeldKeys = new();
        internal readonly Paper Paper = new(new Renderer(), 100, 100, new FontAtlasSettings { AtlasSize = 32 });
        internal readonly EditorSurfaceInput Input;
        internal Fixture(bool devices = true)
        {
            Context.Mice.Returns(devices ? new[] { Mouse } : Array.Empty<IMouse>());
            Context.Keyboards.Returns(devices ? new[] { Keyboard } : Array.Empty<IKeyboard>());
            Mouse.Position.Returns(Vector2.Zero);
            Mouse.ScrollWheels.Returns(Array.Empty<ScrollWheel>());
            Mouse.IsButtonPressed(Arg.Any<MouseButton>()).Returns(c => HeldButtons[(int)c.Arg<MouseButton>()]);
            Keyboard.IsKeyPressed(Arg.Any<Key>()).Returns(c => HeldKeys.Contains(c.Arg<Key>()));
            Input = new EditorSurfaceInput(Context);
            Paper.SetClipboardHandler(Input);
        }
        internal void PolledPosition(float x, float y) => Mouse.Position.Returns(new Vector2(x, y));
        internal void Move(float x, float y)
        {
            PolledPosition(x, y);
            Mouse.MouseMove += Raise.Event<Action<IMouse, Vector2>>(Mouse, new Vector2(x, y));
        }
        internal void Button(MouseButton button, bool down)
        {
            HeldButtons[(int)button] = down;
            if (down) Mouse.MouseDown += Raise.Event<Action<IMouse, MouseButton>>(Mouse, button);
            else Mouse.MouseUp += Raise.Event<Action<IMouse, MouseButton>>(Mouse, button);
        }
        internal void Key(Key key, bool down)
        {
            if (down) { HeldKeys.Add(key); Keyboard.KeyDown += Raise.Event<Action<IKeyboard, Key, int>>(Keyboard, key, 0); }
            else { HeldKeys.Remove(key); Keyboard.KeyUp += Raise.Event<Action<IKeyboard, Key, int>>(Keyboard, key, 0); }
        }
        internal void Character(char c) => Keyboard.KeyChar += Raise.Event<Action<IKeyboard, char>>(Keyboard, c);
        internal void Scroll(float y) => Mouse.Scroll += Raise.Event<Action<IMouse, ScrollWheel>>(Mouse, new ScrollWheel(0, y));
        internal void Frame(Action<Paper>? inspect = null, float xScale = 1, float yScale = 1)
        {
            Paper.BeginFrame(1f / 60);
            Input.Apply(Paper, xScale, yScale);
            inspect?.Invoke(Paper);
            Paper.EndFrame();
        }
        public void Dispose() { Input.Dispose(); Paper.Canvas.Dispose(); }
    }

    private sealed class Renderer : ICanvasRenderer
    {
        public object CreateTexture(uint width, uint height) => new Int2((int)width, (int)height);
        public Int2 GetTextureSize(object texture) => (Int2)texture;
        public void SetTextureData(object texture, IntRect bounds, byte[] data) { }
        public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> calls) { }
        public void Dispose() { }
    }
}
