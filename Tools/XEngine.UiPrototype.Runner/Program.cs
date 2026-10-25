using XEngine.PaperUI;
using XEngine.Runtime;
using XEngine.Runtime.GUI;
using XEngine.Runtime.Resources;
using XEngine.Scribe;

namespace XEngine.UiPrototype;

internal static class Program
{
    private static void Main(string[] args)
    {
        int retainedTree = Array.IndexOf(args, "--validate-retained-tree");
        if (retainedTree >= 0)
        {
            if (retainedTree + 1 >= args.Length) throw new ArgumentException("--validate-retained-tree needs an output directory.");
            new UiIndexedTreeValidationGame(Path.GetFullPath(args[retainedTree + 1]), true).Run("N4 retained tree", 360, 240);
            return;
        }
        int tree = Array.IndexOf(args, "--validate-indexed-tree");
        if (tree >= 0)
        {
            if (tree + 1 >= args.Length) throw new ArgumentException("--validate-indexed-tree needs an output directory.");
            new UiIndexedTreeValidationGame(Path.GetFullPath(args[tree + 1])).Run("N4 indexed tree", 360, 240);
            return;
        }
        int paperRegion = Array.IndexOf(args, "--validate-paper-region");
        if (paperRegion >= 0)
        {
            if (paperRegion + 1 >= args.Length) throw new ArgumentException("--validate-paper-region needs an output directory.");
            new UiPaperRegionValidationGame(Path.GetFullPath(args[paperRegion + 1])).Run("N4 Paper element regions", 360, 240);
            return;
        }
        int mixed = Array.IndexOf(args, "--validate-ui-mixed-paper");
        if (mixed >= 0)
        {
            if (mixed + 1 >= args.Length) throw new ArgumentException("--validate-ui-mixed-paper needs an output directory.");
            new UiMixedPaperValidationGame(Path.GetFullPath(args[mixed + 1])).Run("N4 mixed Paper layers", 320, 220);
            return;
        }
        int composition = Array.IndexOf(args, "--validate-ui-composition");
        if (composition >= 0)
        {
            if (composition + 1 >= args.Length) throw new ArgumentException("--validate-ui-composition needs an output directory.");
            new UiCompositionValidationGame(Path.GetFullPath(args[composition + 1])).Run("N4 ordered UI composition", 320, 220);
            return;
        }
        int memory = Array.IndexOf(args, "--validate-ui-memory");
        if (memory >= 0)
        {
            if (memory + 1 >= args.Length) throw new ArgumentException("--validate-ui-memory needs an output directory.");
            new UiMemoryValidationGame(Path.GetFullPath(args[memory + 1])).Run("N3 UI memory lifecycle", 1280, 720);
            return;
        }
        int cjk = Array.IndexOf(args, "--validate-ui-cjk");
        if (cjk >= 0)
        {
            if (cjk + 2 >= args.Length) throw new ArgumentException("--validate-ui-cjk needs an output directory and fallback font path.");
            new CjkPaintValidationGame(Path.GetFullPath(args[cjk + 1]), Path.GetFullPath(args[cjk + 2])).Run("N3 CJK fallback", 640, 180);
            return;
        }
        int recreation = Array.IndexOf(args, "--validate-ui-device-recreation");
        if (recreation >= 0)
        {
            if (recreation + 1 >= args.Length) throw new ArgumentException("--validate-ui-device-recreation needs an output directory.");
            UiDeviceRecreationValidation.Run(Path.GetFullPath(args[recreation + 1]));
            return;
        }
        int retirement = Array.IndexOf(args, "--validate-ui-retirement");
        if (retirement >= 0)
        {
            if (retirement + 1 >= args.Length) throw new ArgumentException("--validate-ui-retirement needs an output directory.");
            new UiRetirementValidationGame(Path.GetFullPath(args[retirement + 1])).Run("N3 UI resource retirement", 640, 260);
            return;
        }
        int quillAtlas = Array.IndexOf(args, "--validate-quill-atlas");
        if (quillAtlas >= 0)
        {
            if (quillAtlas + 1 >= args.Length) throw new ArgumentException("--validate-quill-atlas needs an output directory.");
            new QuillPaintValidationGame(Path.GetFullPath(args[quillAtlas + 1]), true).Run("N3 retained atlas lifecycle", 640, 260);
            return;
        }
        int quillPaint = Array.IndexOf(args, "--validate-quill-paint");
        if (quillPaint >= 0)
        {
            if (quillPaint + 1 >= args.Length) throw new ArgumentException("--validate-quill-paint needs an output directory.");
            new QuillPaintValidationGame(Path.GetFullPath(args[quillPaint + 1])).Run("N3 retained text and brushes", 640, 260);
            return;
        }
        int uploads = Array.IndexOf(args, "--validate-ui-uploads");
        if (uploads >= 0)
        {
            if (uploads + 1 >= args.Length) throw new ArgumentException("--validate-ui-uploads needs an output directory.");
            new PartialUploadValidationGame(Path.GetFullPath(args[uploads + 1])).Run("N3 partial UI uploads", 300, 100);
            return;
        }
        int kernel = Array.IndexOf(args, "--validate-kernel");
        if (kernel >= 0)
        {
            if (kernel + 1 >= args.Length) throw new ArgumentException("--validate-kernel needs an output directory.");
            new KernelValidationGame(Path.GetFullPath(args[kernel + 1])).Run("N2 UI kernel validation", 800, 450);
            return;
        }
        string? capture = null;
        string adapter = "retained";
        string? validation = null;
        string? benchmark = null;
        bool replay = Array.IndexOf(args, "--replay-model") >= 0;
        bool replayInput = Array.IndexOf(args, "--replay-input") >= 0;
        bool replayCycle = Array.IndexOf(args, "--replay-cycle") >= 0;
        bool dynamicInput = Array.IndexOf(args, "--dynamic-input") >= 0;
        bool vsync = Array.IndexOf(args, "--vsync") >= 0;
        bool dynamicViewport = Array.IndexOf(args, "--dynamic-viewport") >= 0;
        bool queuedViewport = Array.IndexOf(args, "--queued-viewport") >= 0;
        bool effects = Array.IndexOf(args, "--effects") >= 0;
        if (queuedViewport && !dynamicViewport) throw new ArgumentException("--queued-viewport requires --dynamic-viewport.");
        if (replayCycle && (replay || replayInput)) throw new ArgumentException("Cycle replay must run alone.");
        if (replay && replayInput) throw new ArgumentException("Choose model replay or input replay, not both.");
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--smoke-capture") capture = Path.GetFullPath(args[++i]);
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--adapter") adapter = args[++i];
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--validate-renderer") validation = Path.GetFullPath(args[++i]);
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--benchmark") benchmark = Path.GetFullPath(args[++i]);
        if (benchmark != null && (capture != null || validation != null || replay || replayInput)) throw new ArgumentException("Benchmark uses its own isolated workload; do not combine smoke flags.");
        if (adapter != "retained" && adapter != "imgui" && adapter != "paper" && adapter != "cached") throw new ArgumentException("Adapter must be retained, cached, imgui or paper.");
        if (replayCycle && (capture == null || benchmark != null || validation != null)) throw new ArgumentException("Cycle replay requires only --smoke-capture.");
        if (dynamicInput && benchmark == null) throw new ArgumentException("--dynamic-input requires --benchmark.");
        new PrototypeGame(capture, adapter, replay, replayInput, validation, benchmark, replayCycle, dynamicInput, vsync, dynamicViewport, queuedViewport, effects).Run("N1 UI prototype: " + adapter, 1280, 720);
    }
}

internal sealed class PrototypeGame : Game
{
    private readonly string? _capture;
    private int _frames;
    private readonly string _adapterName;
    private readonly ReplayTimeline? _replay;
    private readonly bool _replayInput;
    private readonly string? _validation;
    private readonly PerformanceCapture? _performance;
    private readonly TimedInputReplay? _cycle;
    private readonly System.Diagnostics.Stopwatch _inputClock = new();
    private readonly List<object> _cycleResults = new();
    private double _lastInputTime;
    private readonly bool _vsync;
    private readonly bool _effects;
    private readonly ViewportReplay? _viewportReplay;
    private readonly ViewportSample[]? _viewportSamples;
    private int _viewportSampleCount, _viewportSamplesDropped;
    private readonly record struct ViewportSample(double Seconds, double IntervalMs, int SourceTick, bool Uploaded, double UploadMs, long UploadBytes, int Skipped);
    public PrototypeGame(string? capture, string adapter, bool replay, bool replayInput, string? validation, string? benchmark, bool replayCycle, bool dynamicInput, bool vsync, bool dynamicViewport, bool queuedViewport, bool effects)
    {
        _capture = capture; _adapterName = adapter; _replayInput = replayInput; _validation = validation;
        _vsync = vsync;
        _effects = effects;
        if (dynamicViewport) { _viewportReplay = new ViewportReplay(queuedViewport); if (benchmark == null) _viewportSamples = new ViewportSample[100000]; }
        if (replayCycle) _cycle = new TimedInputReplay();
        if (benchmark != null) _performance = new PerformanceCapture(benchmark, adapter, dynamicInput, _viewportReplay, effects);
        if (replay) _replay = new ReplayTimeline([
            new(30, ReplayAction.ToggleBranch, 0), new(30, ReplayAction.SelectNode, 105),
            new(30, ReplayAction.ScrollTree, Value: 44), new(30, ReplayAction.ScrollProperties, Value: 44),
            new(30, ReplayAction.SetProperty, 2, Text: "Property 0002: edited"),
            new(30, ReplayAction.SetInputText, Text: "N1 replay changed"), new(30, ReplayAction.DragSplit, Value: .6),
            new(60, ReplayAction.ToggleBranch, 0), new(60, ReplayAction.SelectNode, -1),
            new(60, ReplayAction.ScrollTree), new(60, ReplayAction.ScrollProperties),
            new(60, ReplayAction.SetProperty, 2, Text: "Property 0002: 2"),
            new(60, ReplayAction.SetInputText, Text: "N1 rendering smoke"), new(60, ReplayAction.DragSplit, Value: .5)]);
    }
    private IPrototypeAdapter? _adapter;
    private Texture2D? _texture;
    private FontFile? _font;
    private PrototypeWorkload _model = null!;

    public override void InitializeWindow(string title, int width, int height)
        => Window.InitWindow(title, width, height, Silk.NET.Windowing.WindowState.Normal, _vsync);

    public override void Initialize()
    {
        var labels = new string[10000]; var parents = new int[labels.Length];
        for (int i = 0; i < labels.Length; i++)
        { labels[i] = $"Node {i:D5}"; parents[i] = i % 100 == 0 ? -1 : i / 100 * 100; }
        var properties = new string[1000];
        for (int i = 0; i < properties.Length; i++) properties[i] = $"Property {i:D4}: {i}";
        _model = new PrototypeWorkload(new TreeDocument(labels, parents), new RowDocument(properties));
        _model.VisualEffects = _effects;
        _model.Apply(new ReplayEvent(0, ReplayAction.SetInputText, Text: "N1 rendering smoke"));
        var size = Window.InternalWindow.FramebufferSize;
        _font = new FontFile(Path.Combine(AppContext.BaseDirectory, "Geist-Regular.ttf"));
        _texture = new Texture2D(_viewportReplay == null ? 2u : ViewportReplay.Size, _viewportReplay == null ? 2u : ViewportReplay.Size);
        if (_viewportReplay != null) _viewportReplay.Advance(0, _texture);
        else _texture.SetData(new Memory<byte>([40, 70, 100, 255, 60, 100, 130, 255,
            60, 100, 130, 255, 40, 70, 100, 255]), 0, 0, 2, 2);
        if (_validation == null) _adapter = CreateAdapter();
    }

    private IPrototypeAdapter CreateAdapter()
    {
        if (_adapterName == "imgui") return new ImGuiAdapter(_model);
        var renderer = new PaperRenderer(); var size = Window.InternalWindow.FramebufferSize;
        renderer.Initialize(size.X, size.Y);
        return _adapterName == "paper" ? new PaperAdapter(renderer, _font!, _model)
            : new RetainedCanvasAdapter(renderer, _font!, _model, cacheGpu: _adapterName == "cached");
    }

    public override void OnGui(Scene? scene, Paper paper)
    {
        if (_validation != null)
        {
            try { if (!RendererValidation.Run(_validation, _adapterName, CreateAdapter)) Environment.ExitCode = 2; }
            catch (Exception exception) { Console.Error.WriteLine(exception); Environment.ExitCode = 2; }
            Quit(); return;
        }
        var size = Window.InternalWindow.FramebufferSize;
        float scale = Math.Max(.01f, Window.ContentScale);
        if (size.X / scale < 160 || size.Y / scale < 160) return;
        _replay?.AdvanceTo(_frames, _model);
        if (!_inputClock.IsRunning) _inputClock.Start();
        double inputTime = _inputClock.Elapsed.TotalSeconds;
        bool viewportUploaded = _performance == null && (_viewportReplay?.Advance(inputTime, _texture!) ?? false);
        if (_viewportReplay != null && _performance == null)
        {
            if (_viewportSampleCount < _viewportSamples!.Length)
                _viewportSamples[_viewportSampleCount++] = new(inputTime, (inputTime - _lastInputTime) * 1000,
                    _viewportReplay.SourceTick, viewportUploaded, _viewportReplay.LastUploadMs,
                    _viewportReplay.LastUploadAllocatedBytes, _viewportReplay.SkippedSourceFrames);
            else _viewportSamplesDropped++;
        }
        float inputDelta = (float)Math.Max(.000001, inputTime - _lastInputTime);
        _lastInputTime = inputTime;
        PrototypeInput input = _replayInput ? InputReplay.At(_frames, size.X / scale, size.Y / scale)
            : _cycle != null ? _cycle.Advance(inputTime, size.X / scale, size.Y / scale)
            : _capture != null || _replay != null || _performance != null ? new PrototypeInput(-1, -1) : ReadNativeInput() with { DeltaSeconds = inputDelta };
        if (_performance != null) _performance.Draw(_frames, _adapter!, input, size.X / scale, size.Y / scale, scale, _texture!, _model);
        else
        {
            _adapter!.SetInput(input);
            _adapter!.Prepare(size.X / scale, size.Y / scale, scale, _texture!);
            _adapter.Render();
        }
        if (_cycle != null && _cycle.Advanced && _cycle.CurrentTick % CyclicInputReplay.Period == 179)
        {
            bool passed = _model.Tree.Count == 10000 && _model.Tree.SelectedIdentity == 1
                && _model.Properties.GetText(0) == "Property 0000: 0" && _model.InputText == "N1 rendering smoke"
                && _model.TreeScroll == 0 && _model.PropertyScroll == 0 && Math.Abs(_model.SplitRatio - .5) < .00001
                && _model.EditingProperty == -1;
            _cycleResults.Add(new { Tick = _cycle.CurrentTick, Passed = passed, VisibleTree = _model.Tree.Count,
                Selected = _model.Tree.SelectedIdentity, Property0 = _model.Properties.GetText(0), _model.InputText,
                _model.TreeScroll, _model.PropertyScroll, _model.SplitRatio, _model.EditingProperty });
            if (!passed) Environment.ExitCode = 2;
            if (_cycleResults.Count == 3)
            {
                Capture(size.X, size.Y, _capture!);
                if (_viewportReplay != null) SaveViewportSamples();
                File.WriteAllText(Path.ChangeExtension(_capture!, ".cycle.json"), System.Text.Json.JsonSerializer.Serialize(
                    new { Adapter = _adapterName, Frames = _frames, Seconds = inputTime, VSync = Window.VSync,
                        ViewportUpdates = _viewportReplay?.Updates ?? 0, SkippedSourceFrames = _viewportReplay?.SkippedSourceFrames ?? 0,
                        ViewportUploadMode = _viewportReplay?.UploadMode,
                        ViewportSamplesDropped = _viewportSamplesDropped, _cycle.MaximumBacklogTicks, Cycles = _cycleResults },
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                Quit(); return;
            }
        }
        if (_capture != null && _replay != null && (_frames == 29 || _frames == 59))
        {
            string stage = Path.Combine(Path.GetDirectoryName(_capture)!, Path.GetFileNameWithoutExtension(_capture) + $"-tick{_frames}.png");
            Capture(size.X, size.Y, stage);
            Console.WriteLine($"N1 replay snapshot: tick={_frames}, visibleTree={_model.Tree.Count}, treeScroll={_model.TreeScroll}, propertyScroll={_model.PropertyScroll}, split={_model.SplitRatio}, input={_model.InputText}");
        }
        if (_capture != null && _replayInput && (_frames == 39 || _frames == 59 || _frames == 69))
        {
            string stage = Path.Combine(Path.GetDirectoryName(_capture)!, Path.GetFileNameWithoutExtension(_capture) + $"-tick{_frames}.png");
            Capture(size.X, size.Y, stage);
            Console.WriteLine($"N1 input snapshot: tick={_frames}, selected={_model.Tree.SelectedIdentity}, property0={_model.Properties.GetText(0)}, input={_model.InputText}, editingProperty={_model.EditingProperty}");
        }
        _frames++;
        if (_performance != null && _performance.Finished(_frames))
        {
            _performance.Save(size.X / scale, size.Y / scale, scale);
            Console.WriteLine("N1 performance capture saved: " + _adapterName);
            Quit(); return;
        }
        if (_capture != null && _cycle == null && _frames == 90)
        {
            Capture(size.X, size.Y, _capture);
            Console.WriteLine($"N1 smoke: adapter={_adapterName}, frames={_frames}, builds={_adapter!.BuildCount}, draws={_adapter.Stats.UiDrawCalls}, uploadBytes={_adapter.Stats.BufferUploadBytes}");
            if (_replayInput) VerifyInput(size.X / scale);
            Quit();
        }
    }

    private PrototypeInput ReadNativeInput()
    {
        var pointer = GetPaperMousePosition(); PrototypeKeys keys = PrototypeKeys.None;
        if (Input.IsCtrlPressed) keys |= PrototypeKeys.Control;
        if (Input.IsShiftPressed) keys |= PrototypeKeys.Shift;
        if (Input.GetKey(KeyCode.A)) keys |= PrototypeKeys.A;
        if (Input.GetKey(KeyCode.Backspace)) keys |= PrototypeKeys.Backspace;
        if (Input.GetKey(KeyCode.Delete)) keys |= PrototypeKeys.Delete;
        if (Input.GetKey(KeyCode.Left)) keys |= PrototypeKeys.Left;
        if (Input.GetKey(KeyCode.Right)) keys |= PrototypeKeys.Right;
        if (Input.GetKey(KeyCode.Home)) keys |= PrototypeKeys.Home;
        if (Input.GetKey(KeyCode.End)) keys |= PrototypeKeys.End;
        if (Input.GetKey(KeyCode.Enter)) keys |= PrototypeKeys.Enter;
        if (Input.GetKey(KeyCode.Escape)) keys |= PrototypeKeys.Escape;
        return new PrototypeInput(pointer.X, pointer.Y, Input.GetMouseButton(0), Input.MouseWheelDelta, keys, Input.InputString);
    }

    private void SaveViewportSamples()
    {
        using var writer = new StreamWriter(Path.ChangeExtension(_capture!, ".viewport.csv"));
        writer.WriteLine("frame,seconds,interval_ms,source_tick,uploaded,upload_ms,upload_allocated_bytes,skipped_source_frames");
        for (int i = 0; i < _viewportSampleCount; i++)
        {
            var sample = _viewportSamples![i];
            writer.WriteLine(FormattableString.Invariant($"{i},{sample.Seconds:R},{sample.IntervalMs:R},{sample.SourceTick},{(sample.Uploaded ? 1 : 0)},{sample.UploadMs:R},{sample.UploadBytes},{sample.Skipped}"));
        }
    }

    private void VerifyInput(float width)
    {
        double expectedSplit = .5 + 80 / (double)(width - 12);
        bool passed = _model.Tree.Count == 9901 && _model.Tree.SelectedIdentity == 105
            && _model.Properties.GetText(0) == "Edited property" && _model.InputText == "Typed input"
            && _model.TreeScroll == 44 && _model.PropertyScroll == 66 && Math.Abs(_model.SplitRatio - expectedSplit) < .00001;
        var result = new { Adapter = _adapterName, Passed = passed, VisibleTree = _model.Tree.Count,
            Selected = _model.Tree.SelectedIdentity, Property0 = _model.Properties.GetText(0), _model.InputText,
            _model.TreeScroll, _model.PropertyScroll, _model.SplitRatio, ExpectedSplit = expectedSplit,
            Scope = "Adapter-level pointer/key/character replay; excludes OS event delivery and IME" };
        string json = System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.ChangeExtension(_capture!, ".input.json"), json);
        Console.WriteLine(json);
        if (!passed) Environment.ExitCode = 2;
    }

    private void Capture(int width, int height, string destination)
    {
        using var target = new RenderTexture(width, height, true, [TextureImageFormat.Color4b]);
        _adapter!.SetTarget(target.frameBuffer);
        try
        {
            using (var clear = Graphics.GetCommandBuffer("N1 capture clear"))
            {
                clear.SetRenderTarget(target.frameBuffer);
                clear.SetViewport(0, 0, (uint)width, (uint)height);
                clear.DisableScissor();
                clear.ClearRenderTarget(ClearFlags.Color | ClearFlags.Depth | ClearFlags.Stencil,
                    new XEngine.Vector.Color(30 / 255f, 32 / 255f, 37 / 255f, 1));
                Graphics.Submit(clear);
            }
            _adapter!.Render();
            // The readback opcode can execute before the current frame's recorded
            // rendering is submitted. Flush first; this is outside timed captures.
            using (var flush = Graphics.GetCommandBuffer("N1 capture flush")) Graphics.SubmitAndWait(flush);
            var bytes = new byte[target.MainTexture.GetSize()];
            target.MainTexture.GetData<byte>(bytes.AsMemory());
            using var picture = new ImageMagick.MagickImage();
            picture.ReadPixels(bytes, new ImageMagick.PixelReadSettings((uint)width, (uint)height,
                ImageMagick.StorageType.Char, ImageMagick.PixelMapping.RGBA));
            picture.Write(destination, ImageMagick.MagickFormat.Png32);
        }
        finally { _adapter.SetTarget(null); }
    }

    public override void Closing()
    {
        _adapter?.Dispose();
        _texture?.Dispose();
    }
}
