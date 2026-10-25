using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Quill;
using XEngine.Scribe;
using XEngine.Vector;
using Xunit;

namespace XEngine.Editor.Test;

public sealed class EditorMenuPresentationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MenuDensityAndGutterFollowHostOptIn(bool editor)
    {
        using var f = new Fixture(editor);
        void Draw() => ContextMenu.Preview(f.Paper, "menu", m => m
            .Item("Plain", static () => { })
            .Item("Checked", static () => { }, on: true)
            .Toggle("Toggle", static () => { }, static () => true));
        f.Frame(Draw);
        var plain = f.Text("Plain");
        var check = f.Text("Checked");
        var toggle = f.Text("Toggle");
        Assert.Equal(editor ? 22 : f.Theme.Metrics.RowHeight + 6, plain.GetParentHandle().Data.LayoutHeight, 3);
        Assert.Equal(editor ? 22 : f.Theme.Metrics.RowHeight + 6, plain.Data.LayoutHeight, 3);
        Assert.Equal(plain.Data.X, check.Data.X, 3);
        Assert.Equal(plain.Data.X, toggle.Data.X, 3);
        Assert.Equal(editor ? 2 : 1, plain.GetParentHandle().Data.ChildIndices.Count);
        Assert.True(check.Data.X + check.Data.LayoutWidth <= check.GetParentHandle().Data.X + check.GetParentHandle().Data.LayoutWidth);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ToggleInvokesOnceAndDesktopChoiceCloses(bool editor)
    {
        using var f = new Fixture(editor);
        bool open = true, value = false;
        int calls = 0;
        void Draw()
        {
            if (open) ContextMenu.Panel(f.Paper, "menu", 10, 10, Layer.Topmost,
                () => open = false, m => m.Toggle("Choice", () => { value = !value; calls++; }, () => value));
        }
        f.Frame(Draw);
        f.Click(f.Text("Choice").GetParentHandle(), Draw);
        Assert.Equal(1, calls);
        Assert.True(value);
        Assert.Equal(!editor, open);
    }

    [Fact]
    public void DisabledItemDoesNotInvokeOrDismiss()
    {
        using var f = new Fixture();
        bool open = true;
        int calls = 0;
        void Draw() => ContextMenu.Panel(f.Paper, "menu", 10, 10, Layer.Topmost,
            () => open = false, m => m.Item("Disabled", () => calls++, enabled: false));
        f.Frame(Draw);
        f.Click(f.Text("Disabled").GetParentHandle(), Draw);
        Assert.Equal(0, calls);
        Assert.True(open);
    }

    [Theory]
    [InlineData(640)]
    [InlineData(260)]
    public void LongLabelAndShortcutHaveSeparateColumns(float width)
    {
        using var f = new Fixture(width: width);
        const string label = "Create Empty Parent and Preserve Transform";
        const string shortcut = "Ctrl+Shift+G";
        f.Frame(() => ContextMenu.Preview(f.Paper, "menu",
            m => m.Item(label, static () => { }, shortcut: shortcut)));
        var text = f.Text(label).Data;
        var hint = f.Text(shortcut).Data;
        var row = f.Text(label).GetParentHandle().Data;
        Assert.True(row.LayoutWidth > 200, $"row={row.LayoutWidth}, text={text.LayoutWidth}, hint={hint.LayoutWidth}, measure={f.Paper.MeasureText(label, 12, f.Theme.Font).X}");
        Assert.True(text.X + text.LayoutWidth <= hint.X);
        Assert.True(hint.X + hint.LayoutWidth <= row.X + row.LayoutWidth + .01f);
        Assert.True(row.X + row.LayoutWidth <= width);
        Assert.Equal(22, hint.LayoutHeight, 3);
        if (width > 300)
            Assert.True(text.LayoutWidth >= f.Paper.MeasureText(label, 12, f.Theme.Font).X);
    }

    [Theory]
    [InlineData(10, false)]
    [InlineData(590, true)]
    public void SubmenuUsesActualWidthsAndFlipsAtRightEdge(float x, bool flips)
    {
        using var f = new Fixture(width: 640);
        void Draw() => ContextMenu.Panel(f.Paper, "menu", x, 20, Layer.Topmost,
            static () => { }, m => m.Submenu("Create", s => s.Item("A longer submenu entry", static () => { })));
        f.Frame(Draw);
        var parent = f.Text("Create").GetParentHandle().Data;
        f.Paper.SetPointerPosition(parent.X + 40, parent.Y + 11);
        // Hover state is resolved after layout; the next build opens the child.
        f.Frame(Draw); f.Frame(Draw); f.Frame(Draw);
        var child = f.Text("A longer submenu entry").GetParentHandle().Data;
        Assert.True(child.X >= 0);
        Assert.True(child.X + child.LayoutWidth <= 640);
        if (flips) Assert.True(child.X < parent.X);
        else Assert.True(child.X > parent.X);
        // Cross the seam and retain the submenu, rather than falling through a gap.
        f.Paper.SetPointerPosition(child.X + child.LayoutWidth / 2, child.Y + 11);
        f.Frame(Draw); f.Frame(Draw);
        Assert.True(f.Text("A longer submenu entry").Data.LayoutWidth > 0);
    }

    [Fact]
    public void MenuBarEscapeDismissesAndReleasesGlobalOpenState()
    {
        using var f = new Fixture();
        void Draw() => Origami.MenuBar(f.Paper, "bar").Menu("File", m => m.Item("Open", static () => { })).Show();
        f.Frame(Draw);
        f.Click(f.Text("File").GetParentHandle(), Draw);
        Assert.True(MenuBarBuilder.HasOpenDropdown);
        f.Frame(Draw, () => f.Paper.SetKeyState(PaperKey.Escape, true));
        Assert.False(MenuBarBuilder.HasOpenDropdown);
        f.Frame(Draw, () => f.Paper.SetKeyState(PaperKey.Escape, false));
    }

    [Fact]
    public void ContextEscapeDismissesWithoutCommandInvocation()
    {
        using var f = new Fixture();
        int calls = 0;
        f.Frame(() => ContextMenu.Show(10, 10, m => m.Item("Command", () => calls++)));
        Assert.True(ContextMenu.IsOpen);
        f.Frame(static () => { }, () => f.Paper.SetKeyState(PaperKey.Escape, true));
        Assert.False(ContextMenu.IsOpen);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void EscapeOnMirroredSurfaceClosesOwnerMenu()
    {
        using var f = new Fixture();
        void Draw() => Origami.MenuBar(f.Paper, "bar").Menu("File", m => m.Item("Open", static () => { })).Show();
        f.Frame(Draw);
        f.Click(f.Text("File").GetParentHandle(), Draw);
        var previousMap = ContextMenu.MapPointBetweenPapers;
        var mirror = new Paper(new Renderer(), 640, 400, new FontAtlasSettings { AtlasSize = 32 });
        try
        {
            ContextMenu.MapPointBetweenPapers = static (_, _, x, y) => (x, y);
            mirror.BeginFrame(1f / 60); Origami.BeginFrame(mirror, 1f / 60);
            mirror.SetKeyState(PaperKey.Escape, true);
            Origami.EndFrame(mirror); mirror.EndFrame();
            Assert.False(MenuBarBuilder.HasOpenDropdown);
            f.Frame(Draw);
            Assert.False(MenuBarBuilder.HasOpenDropdown);
        }
        finally
        {
            ContextMenu.MapPointBetweenPapers = previousMap;
            mirror.Canvas.Dispose();
        }
    }

    [Fact]
    public void ThemeCloneAndTransitionRetainValueProfile()
    {
        var a = EditorTheme.BuildOrigamiTheme();
        var b = a.Clone();
        Assert.Equal(a.Menus, b.Menus);
        b.Menus = b.Menus!.Value with { RowHeight = 30 };
        Assert.Equal(22, a.Menus!.Value.RowHeight);
        Assert.Equal(26, OrigamiTheme.Lerp(a, b, .5f).Menus!.Value.RowHeight);
        b.Menus = null;
        Assert.Equal(a.Menus, OrigamiTheme.Lerp(a, b, .25f).Menus);
        Assert.Null(OrigamiTheme.Lerp(a, b, .75f).Menus);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ComponentMenuEligibilityIsSilentAndRemovalRemainsGuarded(bool required)
    {
        using var f = new Fixture();
        using var go = new XEngine.Runtime.GameObject("Menu dependency");
        var target = go.AddComponent<MenuRequiredComponent>();
        if (required) go.AddComponent<MenuDependentComponent>();
        var build = typeof(XEngine.Editor.Inspector.GameObjectInspector).GetMethod("BuildComponentContextMenu",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        int errors = 0;
        XEngine.Runtime.OnLog handler = (_, _, severity) =>
        {
            if (severity == XEngine.Runtime.LogSeverity.Error) errors++;
        };
        XEngine.Runtime.Debug.OnLog += handler;
        try
        {
            for (int i = 0; i < 3; i++)
                f.Frame(() => ContextMenu.Preview(f.Paper, "menu", m => build.Invoke(null, [m, go, target, 1])));
            Assert.Equal(0, errors);
            var row = f.Text(XEngine.Rosetta.Loc.Get("inspector.remove_component")).GetParentHandle().Data;
            var statefulClick = typeof(ElementData).GetField("SfClick",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(row);
            Assert.Equal(!required, row.OnClick != null || statefulClick != null);

            go.RemoveComponent(target);
            Assert.Equal(required ? 1 : 0, errors);
            Assert.Equal(required, go.GetComponent<MenuRequiredComponent>() != null);
        }
        finally { XEngine.Runtime.Debug.OnLog -= handler; }
    }

    public sealed class MenuRequiredComponent : XEngine.Runtime.MonoBehaviour { }

    [RequireComponent(typeof(MenuRequiredComponent))]
    public sealed class MenuDependentComponent : XEngine.Runtime.MonoBehaviour { }

    [Theory]
    [InlineData(false, "Beta")]
    [InlineData(true, "Omega")]
    public void KeyboardSkipsNonCommandsAndWraps(bool up, string expected)
    {
        using var f = new Fixture();
        string chosen = "";
        f.Frame(() => ContextMenu.Show(10, 10, m => m.Title("Subject").Header("Commands")
            .Item("Disabled", () => chosen = "Disabled", enabled: false).Separator()
            .Item("Alpha", () => chosen = "Alpha").Item("Beta", () => chosen = "Beta")
            .Item("Omega", () => chosen = "Omega")));
        f.Key(up ? PaperKey.Up : PaperKey.Down);
        if (!up) f.Key(PaperKey.Down);
        f.Key(PaperKey.Enter);
        Assert.Equal(expected, chosen);
        Assert.False(ContextMenu.IsOpen);
    }

    [Fact]
    public void RepeatedLetterCyclesAndUniqueLetterInvokesOnce()
    {
        using var f = new Fixture();
        string chosen = "";
        int calls = 0;
        void Build(ContextBuilder m) => m.Item("Blocked", () => calls++, enabled: false)
            .Item("Beta", () => { chosen = "Beta"; calls++; })
            .Item("Bravo", () => { chosen = "Bravo"; calls++; })
            .Item("Omega", () => { chosen = "Omega"; calls++; });
        f.Frame(() => ContextMenu.Show(10, 10, Build));
        f.Character('b'); f.Character('B');
        Assert.Equal(0, calls);
        f.Key(PaperKey.Enter);
        Assert.Equal("Bravo", chosen);
        Assert.Equal(1, calls);
        f.Frame(() => ContextMenu.Show(10, 10, Build));
        f.Character('o');
        Assert.False(ContextMenu.IsOpen);
        Assert.Equal("Omega", chosen);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void ModifiedTextAndWhitespaceDoNotActivate()
    {
        using var f = new Fixture();
        int calls = 0;
        f.Frame(() => ContextMenu.Show(10, 10, m => m.Item("Omega", () => calls++)));
        f.Frame(static () => { }, () => { f.Paper.SetKeyState(PaperKey.LeftControl, true); f.Paper.PushInputText('o'); });
        f.Frame(static () => { }, () => f.Paper.SetKeyState(PaperKey.LeftControl, false));
        f.Character(' ');
        Assert.Equal(0, calls);
        Assert.True(ContextMenu.IsOpen);
    }

    [Theory]
    [InlineData(PaperKey.Right)]
    [InlineData(PaperKey.Enter)]
    public void UniqueSubmenuLetterSelectsChildAndEscapeClosesOneLevel(PaperKey reopen)
    {
        using var f = new Fixture();
        string chosen = "";
        f.Frame(() => ContextMenu.Show(10, 10, m => m.Submenu("Create", s => s
            .Item("One", () => chosen = "One").Item("Two", () => chosen = "Two"))));
        f.Character('c');
        Assert.True(f.HasText("One"));
        f.Key(PaperKey.Escape);
        Assert.True(ContextMenu.IsOpen);
        Assert.False(f.HasText("One"));
        f.Key(reopen);
        Assert.True(f.HasText("One"));
        f.Key(PaperKey.Down); f.Key(PaperKey.KeypadEnter);
        Assert.Equal("Two", chosen);
        Assert.False(ContextMenu.IsOpen);
    }

    [Fact]
    public void LeftReturnsToParentAndRootEscapeCloses()
    {
        using var f = new Fixture();
        f.Frame(() => ContextMenu.Show(10, 10, m => m.Submenu("Create", s => s.Item("One", static () => { }))));
        f.Key(PaperKey.Down); f.Key(PaperKey.Right);
        f.Key(PaperKey.Left);
        Assert.True(ContextMenu.IsOpen);
        Assert.False(f.HasText("One"));
        f.Key(PaperKey.Escape);
        Assert.False(ContextMenu.IsOpen);
    }

    [Fact]
    public void HeldDownRepeatsAcrossEnabledRows()
    {
        using var f = new Fixture();
        f.Paper.AutoRepeatDelay = .1f;
        f.Paper.AutoRepeatRate = .1f;
        string chosen = "";
        f.Frame(() => ContextMenu.Show(10, 10, m => m.Item("Alpha", () => chosen = "Alpha")
            .Item("Beta", () => chosen = "Beta").Item("Gamma", () => chosen = "Gamma")));
        f.Frame(static () => { }, () => f.Paper.SetKeyState(PaperKey.Down, true));
        for (int i = 0; i < 13; i++) f.Frame(static () => { });
        f.Frame(static () => { }, () => f.Paper.SetKeyState(PaperKey.Down, false));
        f.Key(PaperKey.Enter);
        Assert.Equal("Beta", chosen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangedOrDisabledSelectedCommandIsNotInvoked(bool disable)
    {
        using var f = new Fixture();
        int calls = 0;
        bool changed = false;
        f.Frame(() => ContextMenu.Show(10, 10, m => m.Item(changed ? "Replacement" : "Original",
            () => calls++, enabled: !changed || !disable)));
        f.Key(PaperKey.Down);
        changed = true;
        f.Key(PaperKey.Enter);
        Assert.Equal(0, calls);
        Assert.True(ContextMenu.IsOpen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CallbackMayOpenAnotherMenu(bool pointer)
    {
        using var f = new Fixture();
        int calls = 0;
        f.Frame(() => ContextMenu.Show(10, 10, m => m.Item("Original", () =>
        {
            calls++;
            ContextMenu.Show(10, 10, next => next.Item("Replacement", static () => { }));
        })));
        if (pointer) f.Click(f.Text("Original").GetParentHandle(), static () => { });
        else { f.Key(PaperKey.Down); f.Key(PaperKey.Enter); }
        Assert.Equal(1, calls);
        Assert.True(ContextMenu.IsOpen);
        Assert.True(f.HasText("Replacement"));
        Assert.True(f.Paper.FindElementByID(f.Paper.FocusedElementId).IsValid);
    }

    [Fact]
    public void StationaryPointerDoesNotReplaceKeyboardAndMovementResumesHover()
    {
        using var f = new Fixture();
        string chosen = "";
        void Build(ContextBuilder m) => m.Item("Alpha", () => chosen = "Alpha").Item("Beta", () => chosen = "Beta");
        f.Frame(() => ContextMenu.Show(10, 10, Build));
        var alpha = f.Text("Alpha").GetParentHandle().Data;
        f.Paper.SetPointerPosition(alpha.X + 40, alpha.Y + 11);
        f.Frame(static () => { }); f.Frame(static () => { });
        f.Key(PaperKey.Down); f.Key(PaperKey.Enter);
        Assert.Equal("Beta", chosen);
        chosen = "";
        f.Frame(() => ContextMenu.Show(10, 10, Build));
        f.Key(PaperKey.Up);
        f.Paper.SetPointerPosition(alpha.X + 42, alpha.Y + 11);
        f.Frame(static () => { }); f.Frame(static () => { }); f.Frame(static () => { });
        f.Key(PaperKey.Enter);
        Assert.Equal("Alpha", chosen);
    }

    [Theory]
    [InlineData(PaperKey.Enter, false)]
    [InlineData(PaperKey.Escape, false)]
    [InlineData(PaperKey.Escape, true)]
    public void MenuOwnsKeyboardAndRestoresOnlySurvivingPriorFocus(PaperKey close, bool removePrior)
    {
        using var f = new Fixture();
        int prior = 0, keys = 0, text = 0;
        bool showField = true;
        void Draw()
        {
            if (!showField) return;
            var field = f.Paper.Box("prior").Size(180, 25).TabIndex(0)
                .OnKeyPressed(_ => keys++).OnTextInput(_ => text++);
            using (field.Enter())
            {
                prior = f.Paper.CurrentParent.Data.ID;
                if (f.Paper.FocusedElementId == 0) f.Paper.SetFocus();
            }
        }
        f.Frame(Draw);
        Assert.Equal(prior, f.Paper.FocusedElementId);
        f.Frame(() => { Draw(); ContextMenu.Show(10, 40, m => m.Item("Beta", static () => { }).Item("Bravo", static () => { })); });
        Assert.NotEqual(prior, f.Paper.FocusedElementId);
        f.Character('b', Draw);
        f.Key(PaperKey.Tab, Draw);
        Assert.Equal(0, text);
        Assert.Equal(0, keys);
        showField = !removePrior;
        f.Frame(Draw, () => f.Paper.SetKeyState(close, true));
        Assert.False(ContextMenu.IsOpen);
        Assert.NotEqual(prior, f.Paper.FocusedElementId);
        Assert.Equal(0, keys);
        f.Frame(Draw, () => f.Paper.SetKeyState(close, false));
        Assert.Equal(removePrior ? 0 : prior, f.Paper.FocusedElementId);
        Assert.False(f.Paper.SkipKeyboardNavigation);
        f.Character('z', Draw);
        Assert.Equal(removePrior ? 0 : 1, text);
    }

    [Fact]
    public void ExplicitCallbackFocusWinsOverRestoration()
    {
        using var f = new Fixture();
        ElementHandle prior = default, next = default;
        void Draw()
        {
            prior = f.Paper.Box("prior").Size(100, 20)._handle;
            next = f.Paper.Box("next").Size(100, 20)._handle;
        }
        f.Frame(() => { Draw(); f.Paper.SetFocus(prior); });
        f.Frame(() => { Draw(); ContextMenu.Show(10, 50, m => m.Item("Choose", () => f.Paper.SetFocus(next))); });
        f.Character('c', Draw);
        f.Frame(Draw);
        Assert.Equal(next.Data.ID, f.Paper.FocusedElementId);
    }

    [Fact]
    public void MenuBarKeyboardSwitchIsNotOverriddenByStationaryButtonHover()
    {
        using var f = new Fixture();
        string chosen = "";
        void Draw() => Origami.MenuBar(f.Paper, "bar")
            .Menu("File", m => m.Item("Open", () => chosen = "Open"))
            .Menu("Edit", m => m.Item("Copy", () => chosen = "Copy")).Show();
        f.Frame(Draw);
        f.Click(f.Text("File").GetParentHandle(), Draw);
        f.Key(PaperKey.Right, Draw);
        f.Key(PaperKey.Enter, Draw);
        Assert.Equal("Copy", chosen);
        Assert.False(MenuBarBuilder.HasOpenDropdown);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PointerDismissalRestoresPriorFocus(bool backdrop)
    {
        using var f = new Fixture();
        ElementHandle field = default;
        int calls = 0;
        void Draw() => field = f.Paper.Box("field").Size(120, 20)._handle;
        f.Frame(() => { Draw(); f.Paper.SetFocus(field); });
        f.Frame(() => { Draw(); ContextMenu.Show(10, 40, m => m.Item("Choose", () => calls++)); });
        var target = backdrop ? f.Paper.RootElement : f.Text("Choose").GetParentHandle();
        // Root center is outside the small menu, so it hits only the transparent backdrop.
        f.Click(target, Draw);
        Assert.False(ContextMenu.IsOpen);
        Assert.Equal(backdrop ? 0 : 1, calls);
        Assert.Equal(field.Data.ID, f.Paper.FocusedElementId);
    }

    [Fact]
    public void ReentrantMenuRestoresOriginalFieldRatherThanPriorMenu()
    {
        using var f = new Fixture();
        ElementHandle field = default;
        void Draw() => field = f.Paper.Box("field").Size(120, 20)._handle;
        f.Frame(() => { Draw(); f.Paper.SetFocus(field); });
        f.Frame(() => { Draw(); ContextMenu.Show(10, 40, m => m.Item("Choose", () =>
            ContextMenu.Show(10, 40, next => next.Item("Replacement", static () => { })))); });
        f.Character('c', Draw);
        f.Frame(Draw);
        Assert.True(ContextMenu.IsOpen);
        f.Key(PaperKey.Escape, Draw);
        Assert.Equal(field.Data.ID, f.Paper.FocusedElementId);
    }

    [Fact]
    public void MenuBarRestoresFieldFocusAfterKeyboardDismissal()
    {
        using var f = new Fixture();
        ElementHandle field = default;
        void Draw()
        {
            Origami.MenuBar(f.Paper, "bar").Menu("File", m => m.Item("Open", static () => { })).Show();
            field = f.Paper.Box("field").Size(100, 20)._handle;
        }
        f.Frame(() => { Draw(); f.Paper.SetFocus(field); });
        f.Click(f.Text("File").GetParentHandle(), Draw);
        f.Key(PaperKey.Escape, Draw);
        Assert.Equal(field.Data.ID, f.Paper.FocusedElementId);
    }

    [Fact]
    public void KeyboardToggleInvokesOnceAndCloses()
    {
        using var f = new Fixture();
        int calls = 0;
        bool on = false;
        f.Frame(() => ContextMenu.Show(10, 10, m => m.Toggle("Choice", () => { calls++; on = !on; }, () => on)));
        f.Key(PaperKey.Down); f.Key(PaperKey.Enter);
        Assert.True(on);
        Assert.Equal(1, calls);
        Assert.False(ContextMenu.IsOpen);
    }

    [Fact]
    public void MirroredSurfaceNavigatesAndInvokesOwnerCommandOnce()
    {
        using var f = new Fixture();
        int calls = 0;
        void Draw() => Origami.MenuBar(f.Paper, "bar").Menu("File", m => m.Item("Open", () => calls++)).Show();
        f.Frame(Draw);
        f.Click(f.Text("File").GetParentHandle(), Draw);
        var previousMap = ContextMenu.MapPointBetweenPapers;
        var mirror = new Paper(new Renderer(), 640, 400, new FontAtlasSettings { AtlasSize = 32 });
        void Mirror(PaperKey key, bool down)
        {
            mirror.BeginFrame(1f / 60); Origami.BeginFrame(mirror, 1f / 60);
            mirror.SetKeyState(key, down);
            Origami.EndFrame(mirror); mirror.EndFrame();
        }
        try
        {
            ContextMenu.MapPointBetweenPapers = static (_, _, x, y) => (x, y);
            Mirror(PaperKey.Down, true); Mirror(PaperKey.Down, false);
            f.Frame(Draw);
            Mirror(PaperKey.Enter, true); Mirror(PaperKey.Enter, false);
            f.Frame(Draw);
            Assert.Equal(1, calls);
            Assert.False(MenuBarBuilder.HasOpenDropdown);
            Assert.False(mirror.SkipKeyboardNavigation);
            Assert.Equal(0, mirror.FocusedElementId);
        }
        finally
        {
            ContextMenu.MapPointBetweenPapers = previousMap;
            mirror.Canvas.Dispose();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ClickOpenedMenuKeepsSurfaceProfileAfterBuildScopeEnds(bool editor)
    {
        using var f = new Fixture(editor, scopedFrames: true);
        int calls = 0;
        void Draw() => f.Paper.Box("launcher").Size(100, 20)
            .Text("Launcher", f.Theme.Font).OnClick(_ =>
                ContextMenu.Show(10, 40, m => m.Item("Choose", () => calls++)));
        f.Frame(Draw);
        f.Click(f.Text("Launcher"), Draw);
        Assert.True(ContextMenu.IsOpen);
        f.Character('c', Draw);
        Assert.Equal(editor ? 1 : 0, calls);
        Assert.Equal(!editor, ContextMenu.IsOpen);
    }

    [Fact]
    public void OffscreenPassRestoresPopupProfileAlongWithOwner()
    {
        using var f = new Fixture(scopedFrames: true);
        f.Frame(static () => { });
        using (Origami.PushOffscreenSurface())
        using (Origami.PushTheme(OrigamiTheme.CreateDefaults()))
        {
            var offscreen = new Paper(new Renderer(), 100, 100, new FontAtlasSettings { AtlasSize = 32 });
            try { Origami.BeginFrame(offscreen, 1f / 60); }
            finally { offscreen.Canvas.Dispose(); }
        }
        int calls = 0;
        ContextMenu.Show(10, 40, m => m.Item("Choose", () => calls++));
        f.Character('c');
        Assert.Equal(1, calls);
        Assert.False(ContextMenu.IsOpen);
    }

    [Theory]
    [InlineData(100, 640, 400)]
    [InlineData(1000, 640, 400)]
    [InlineData(100, 260, 180)]
    [InlineData(100, 260, 90)]
    [InlineData(100, 260, 40)]
    public void OversizedMenuBoundsVirtualizesAndRevealsWrappedSelection(int count, float width, float height)
    {
        using var f = new Fixture(width: width, height: height);
        int chosen = -1, calls = 0;
        f.Frame(() => ContextMenu.Show(width - 10, height - 10, m => {
            for (int i = 0; i < count; i++)
            {
                int index = i;
                m.Item("Entry " + i.ToString("D3"), () => { chosen = index; calls++; });
            }
        }));
        Assert.True(f.HasText("Entry 000"));
        Assert.False(f.HasText("Entry " + (count - 1).ToString("D3")));
        Assert.True(f.Paper.ElementCount < 200);
        f.Key(PaperKey.Up);
        var last = f.Text("Entry " + (count - 1).ToString("D3")).GetParentHandle().Data;
        Assert.InRange(last.Y, 0, height - last.LayoutHeight);
        var root = MenuRoot(f.Text("Entry " + (count - 1).ToString("D3"))).Data;
        Assert.InRange(root.LayoutHeight, 1, height - 8);
        Assert.InRange(root.Y, 0, height - root.LayoutHeight);
        f.Key(PaperKey.Down);
        Assert.True(f.HasText("Entry 000"));
        Assert.False(f.HasText("Entry " + (count - 1).ToString("D3")));
        f.Key(PaperKey.Enter);
        Assert.Equal(0, chosen);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void WheelScrollDoesNotSnapBackAndVisibleClickKeepsItemIdentity()
    {
        using var f = new Fixture();
        int chosen = -1, calls = 0;
        f.Frame(() => ContextMenu.Show(10, 10, m => {
            for (int i = 0; i < 100; i++)
            {
                int index = i;
                m.Item("Entry " + i.ToString("D3"), () => { chosen = index; calls++; });
            }
        }));
        f.Key(PaperKey.Down);
        var first = f.Text("Entry 000").Data;
        f.Paper.SetPointerPosition(first.X + 30, first.Y + 11);
        f.Frame(static () => { }, () => f.Paper.SetPointerWheel(-1));
        f.Frame(static () => { }); f.Frame(static () => { });
        Assert.False(f.HasText("Entry 000"));
        f.Click(f.Text("Entry 003").GetParentHandle(), static () => { });
        Assert.Equal(3, chosen);
        Assert.Equal(1, calls);
        Assert.False(ContextMenu.IsOpen);
    }

    [Fact]
    public void IndicatorClickAndHoverScrollWithoutInvokingCommands()
    {
        using var f = new Fixture();
        int calls = 0;
        f.Frame(() => ContextMenu.Show(10, 10, m => {
            for (int i = 0; i < 100; i++) m.Item("Entry " + i.ToString("D3"), () => calls++);
        }));
        var root = MenuRoot(f.Text("Entry 000"));
        var column = new ElementHandle(f.Paper, root.Data.ChildIndices[0]);
        var down = new ElementHandle(f.Paper, column.Data.ChildIndices[2]);
        f.Click(down, static () => { });
        Assert.False(f.HasText("Entry 000"));
        for (int i = 0; i < 40; i++) f.Frame(static () => { });
        Assert.False(f.HasText("Entry 001"));
        Assert.True(ContextMenu.IsOpen);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void ShrinkingMenuResetsScrollBeforeGrowingAgain()
    {
        using var f = new Fixture();
        int count = 100;
        f.Frame(() => ContextMenu.Show(10, 10, m => {
            for (int i = 0; i < count; i++) m.Item("Entry " + i.ToString("D3"), static () => { });
        }));
        f.Key(PaperKey.Up);
        Assert.True(f.HasText("Entry 099"));
        count = 5; f.Frame(static () => { });
        Assert.True(f.HasText("Entry 000"));
        Assert.True(f.HasText("Entry 004"));
        count = 100; f.Frame(static () => { });
        Assert.True(f.HasText("Entry 000"));
        Assert.False(f.HasText("Entry 099"));
    }

    [Fact]
    public void CallerOwnedPanelResetsScrollAfterShrinking()
    {
        using var f = new Fixture();
        int count = 100;
        void Draw() => ContextMenu.Panel(f.Paper, "panel", 10, 10, Layer.Topmost,
            static () => { }, m => {
                for (int i = 0; i < count; i++) m.Item("Entry " + i.ToString("D3"), static () => { });
            });
        f.Frame(Draw);
        var first = f.Text("Entry 000").Data;
        f.Paper.SetPointerPosition(first.X + 30, first.Y + 11);
        f.Frame(Draw, () => f.Paper.SetPointerWheel(-100));
        f.Frame(Draw);
        Assert.True(f.HasText("Entry 099"));
        count = 5; f.Frame(Draw);
        Assert.True(f.HasText("Entry 000"));
        count = 100; f.Frame(Draw);
        Assert.True(f.HasText("Entry 000"));
        Assert.False(f.HasText("Entry 099"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpenMenuWidthTracksChangedLabelsShortcutsAndFontMetrics(bool panel)
    {
        using var f = new Fixture(width: 1200);
        string label = "Short", shortcut = "";
        void Build(ContextBuilder m) => m.Item(label, static () => { }, shortcut: shortcut);
        void Draw()
        {
            if (panel) ContextMenu.Panel(f.Paper, "panel", 10, 10, Layer.Topmost, static () => { }, Build);
        }
        f.Frame(() => { Draw(); if (!panel) ContextMenu.Show(10, 10, Build); });
        float initial = MenuRoot(f.Text(label)).Data.LayoutWidth;
        label = "A changed label that must expand the open menu";
        f.Frame(Draw);
        float expanded = MenuRoot(f.Text(label)).Data.LayoutWidth;
        Assert.True(expanded > initial);
        shortcut = "Ctrl+Shift+Alt+Command"; f.Frame(Draw);
        float withShortcut = MenuRoot(f.Text(label)).Data.LayoutWidth;
        Assert.True(withShortcut > expanded);
        f.Theme.Menus = f.Theme.Menus!.Value with { FontSize = 20 };
        f.Frame(Draw);
        Assert.True(MenuRoot(f.Text(label)).Data.LayoutWidth > withShortcut);
        label = "Short"; shortcut = ""; f.Frame(Draw);
        Assert.Equal(initial, MenuRoot(f.Text(label)).Data.LayoutWidth);
    }

    [Fact]
    public void StableThousandItemMenuDoesNotRecreateAllTextLayoutsEachFrame()
    {
        using var f = new Fixture();
        string[] labels = new string[1000];
        for (int i = 0; i < labels.Length; i++) labels[i] = "Entry " + i.ToString("D3");
        f.Frame(() => ContextMenu.Show(10, 10, m => {
            for (int i = 0; i < labels.Length; i++) m.Item(labels[i], static () => { });
        }));
        for (int i = 0; i < 30; i++) f.Frame(static () => { });
        long layoutsBefore = f.Paper.Canvas.Text.FontEngine.LayoutCreationCount;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 30; i++) f.Frame(static () => { });
        long allocated = (GC.GetAllocatedBytesForCurrentThread() - before) / 30;
        Assert.True(allocated < 400_000, $"Allocated {allocated} B/frame for a stable menu");
        Assert.Equal(layoutsBefore, f.Paper.Canvas.Text.FontEngine.LayoutCreationCount);
        Assert.True(f.Paper.ElementCount < 200);
    }

    [Fact]
    public void LongChildRevealsIndependentlyAfterScrollingParent()
    {
        using var f = new Fixture();
        int chosen = -1;
        f.Frame(() => ContextMenu.Show(500, 350, m => {
            for (int i = 0; i < 99; i++) m.Item("Entry " + i.ToString("D3"), static () => { });
            m.Submenu("Zebra", sub => {
                for (int i = 0; i < 100; i++)
                {
                    int index = i;
                    sub.Item("Child " + i.ToString("D3"), () => chosen = index);
                }
            });
        }));
        f.Key(PaperKey.Up); f.Key(PaperKey.Right);
        Assert.True(f.HasText("Zebra"));
        Assert.True(f.HasText("Child 000"));
        f.Key(PaperKey.Up);
        var last = f.Text("Child 099").GetParentHandle().Data;
        Assert.InRange(last.Y, 0, 400 - last.LayoutHeight);
        Assert.True(f.HasText("Zebra"));
        f.Key(PaperKey.Enter);
        Assert.Equal(99, chosen);
    }

    private static ElementHandle MenuRoot(ElementHandle child)
    {
        while (child.IsValid && child.Data.Layer == Layer.Base) child = child.GetParentHandle();
        Assert.True(child.IsValid);
        return child;
    }

    [Fact]
    public void ChildWheelIgnoresStalePointerOnAnotherMirroredSurface()
    {
        using var f = new Fixture();
        var mirror = new Paper(new Renderer(), 640, 400, new FontAtlasSettings { AtlasSize = 32 });
        var previousMap = ContextMenu.MapPointBetweenPapers;
        var previousFilter = ContextMenu.PointerSurfaceFilter;
        void MirrorFrame()
        {
            mirror.BeginFrame(1f / 60); Origami.BeginFrame(mirror, 1f / 60);
            Origami.EndFrame(mirror); mirror.EndFrame();
        }
        try
        {
            ContextMenu.MapPointBetweenPapers = static (_, _, x, y) => (x, y);
            ContextMenu.PointerSurfaceFilter = paper => ReferenceEquals(paper, f.Paper);
            f.Frame(() => ContextMenu.Show(400, 10, m => {
                for (int i = 0; i < 99; i++) m.Item("Entry " + i.ToString("D3"), static () => { });
                m.Submenu("Zebra", sub => {
                    for (int i = 0; i < 100; i++) sub.Item("Child " + i.ToString("D3"), static () => { });
                });
            }));
            f.Key(PaperKey.Up);
            var stale = f.Text("Entry 088").GetParentHandle().Data;
            f.Paper.SetPointerPosition(stale.X + 50, stale.Y + 11);
            f.Key(PaperKey.Right);
            mirror.SetPointerPosition(stale.X + 50, stale.Y + 11);
            MirrorFrame(); MirrorFrame();
            var child = f.Text("Child 003").GetParentHandle().Data;
            f.Paper.SetPointerPosition(child.X + 50, child.Y + 11);
            f.Frame(static () => { }, () => f.Paper.SetPointerWheel(-1000));
            MirrorFrame();
            f.Frame(static () => { }); MirrorFrame();
            f.Frame(static () => { });
            Assert.True(f.HasText("Zebra"));
            Assert.True(f.HasText("Child 099"));
            Assert.False(f.HasText("Child 000"));
        }
        finally
        {
            ContextMenu.MapPointBetweenPapers = previousMap;
            ContextMenu.PointerSurfaceFilter = previousFilter;
            mirror.Canvas.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MirroredLongMenuKeepsOwnerViewportAndClampedScreenPosition(bool menuBar)
    {
        using var f = new Fixture();
        void Build(ContextBuilder m)
        {
            for (int i = 0; i < 100; i++) m.Item("Entry " + i.ToString("D3"), static () => { });
        }
        void Draw()
        {
            if (menuBar) Origami.MenuBar(f.Paper, "bar").Menu("File", Build).Show();
        }
        f.Frame(() => { Draw(); if (!menuBar) ContextMenu.Show(600, 350, Build); });
        if (menuBar) f.Click(f.Text("File").GetParentHandle(), Draw);
        var previousMap = ContextMenu.MapPointBetweenPapers;
        var mirror = new Paper(new Renderer(), 300, 220, new FontAtlasSettings { AtlasSize = 32 });
        ElementHandle FindText(string text)
        {
            for (int i = 0; i < mirror.ElementCount; i++)
                if (mirror.GetElementData(i).Paragraph == text) return new ElementHandle(mirror, i);
            throw new InvalidOperationException("Missing mirror item " + text);
        }
        void Compare(string text)
        {
            var owner = MenuRoot(f.Text(text)).Data;
            mirror.BeginFrame(1f / 60); Origami.BeginFrame(mirror, 1f / 60);
            Origami.EndFrame(mirror); mirror.EndFrame();
            var foreign = MenuRoot(FindText(text)).Data;
            Assert.Equal(owner.LayoutWidth, foreign.LayoutWidth, 3);
            Assert.Equal(owner.LayoutHeight, foreign.LayoutHeight, 3);
            Assert.Equal(owner.X - 200, foreign.X, 3);
            Assert.Equal(owner.Y - 50, foreign.Y, 3);
            Assert.Equal(f.Text(text).Data.Y - 50, FindText(text).Data.Y, 3);
        }
        try
        {
            ContextMenu.MapPointBetweenPapers = static (_, _, x, y) => (x - 200, y - 50);
            Compare("Entry 000");
            f.Key(PaperKey.Up, Draw);
            Compare("Entry 099");
        }
        finally { ContextMenu.MapPointBetweenPapers = previousMap; mirror.Canvas.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StandalonePopupAndInlinePreviewRetainUnboundedDefaults(bool preview)
    {
        using var f = new Fixture(editor: preview);
        void Build(ContextBuilder m)
        {
            for (int i = 0; i < 100; i++) m.Item("Entry " + i.ToString("D3"), static () => { });
        }
        f.Frame(() => {
            if (preview) ContextMenu.Preview(f.Paper, "preview", Build);
            else ContextMenu.Show(10, 10, Build);
        });
        Assert.True(f.HasText("Entry 099"));
        Assert.True(f.Text("Entry 099").Data.Y > 400);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly Paper Paper;
        internal readonly OrigamiTheme Theme;
        private readonly IDisposable? _scope;
        private readonly IDisposable _surfaceScope = Origami.PushOffscreenSurface();
        private readonly bool _scopedFrames;
        private readonly FontFile? _previousFont;
        private readonly float _width;
        private readonly float _height;

        internal Fixture(bool editor = true, float width = 640, bool scopedFrames = false, float height = 400)
        {
            ContextMenu.Close(); MenuBarBuilder.ClearMirroredMenu();
            _width = width;
            _height = height;
            Paper = new Paper(new Renderer(), width, height, new FontAtlasSettings { AtlasSize = 32 });
            _previousFont = EditorTheme.DefaultFont;
            string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            EditorTheme.DefaultFont = new FontFile(Path.Combine(root, "XEngine.Editor", "Resources", "Geist-Regular.ttf"));
            Theme = editor ? EditorTheme.BuildOrigamiTheme() : OrigamiTheme.CreateDefaults();
            Theme.Font = EditorTheme.DefaultFont;
            _scopedFrames = scopedFrames;
            if (!scopedFrames) _scope = Origami.PushTheme(Theme);
        }

        internal ElementHandle Text(string text)
        {
            for (int i = 0; i < Paper.ElementCount; i++)
                if (Paper.GetElementData(i).Paragraph == text) return new ElementHandle(Paper, i);
            throw new InvalidOperationException("Missing menu text: " + text);
        }

        internal bool HasText(string text)
        {
            for (int i = 0; i < Paper.ElementCount; i++)
                if (Paper.GetElementData(i).Paragraph == text) return true;
            return false;
        }

        internal void Key(PaperKey key, Action? draw = null)
        {
            draw ??= static () => { };
            Frame(draw, () => Paper.SetKeyState(key, true));
            Frame(draw, () => Paper.SetKeyState(key, false));
            Frame(draw);
        }

        internal void Character(char value, Action? draw = null)
            => Frame(draw ?? (static () => { }), () => Paper.PushInputText(value));

        internal void Frame(Action draw, Action? input = null)
        {
            Paper.BeginFrame(1f / 60);
            using (_scopedFrames ? Origami.PushTheme(Theme) : null)
            {
                Origami.BeginFrame(Paper, 1f / 60);
                input?.Invoke();
                using (Paper.Column("host").Size(_width, _height).Enter()) draw();
                Origami.EndFrame(Paper);
            }
            Paper.EndFrame();
        }

        internal void Click(ElementHandle row, Action draw)
        {
            float x = row.Data.X + row.Data.LayoutWidth / 2, y = row.Data.Y + row.Data.LayoutHeight / 2;
            Paper.SetPointerPosition(x, y);
            Frame(draw, () => Paper.SetPointerState(PaperMouseBtn.Left, x, y, true, false));
            Frame(draw, () => Paper.SetPointerState(PaperMouseBtn.Left, x, y, false, false));
            Frame(draw);
        }

        public void Dispose()
        {
            ContextMenu.Close(); MenuBarBuilder.ClearMirroredMenu();
            _scope?.Dispose(); _surfaceScope.Dispose(); EditorTheme.DefaultFont = _previousFont;
            Paper.Canvas.Dispose();
        }
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
