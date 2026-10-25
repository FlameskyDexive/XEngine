// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Vector;

namespace XEngine.OrigamiUI;

/// <summary>One desktop menu session, shared by its owner and mirrored surfaces.</summary>
internal sealed class MenuNavigation
{
    private sealed class Level
    {
        internal int Selected = -1, Open = -1;
        internal string Label = "";
        internal int Kind;
        internal bool Reveal;
        internal readonly MenuScroll Scroll = new();
        internal readonly MenuWidthCache Width = new();
    }

    private sealed class FocusSlot(Paper paper)
    {
        internal readonly Paper Paper = paper;
        internal readonly int Previous = MenuFocusRestore.PreviousFocus(paper);
        internal readonly bool SkipNavigation = paper.SkipKeyboardNavigation;
        internal int Menu;
        internal Float2 Pointer = paper.PointerPos;
        internal bool PendingPointerMove;
    }

    private readonly List<Level> _levels = [];
    private readonly List<FocusSlot> _focus = [];
    private bool _keyboard, _pointerMoved;
    private bool _hoverReady;
    private bool _selectFirst;
    internal Action<int>? SwitchRoot;

    internal void Reset(bool selectFirst = false)
    {
        _levels.Clear();
        _keyboard = selectFirst;
        _selectFirst = selectFirst;
    }

    private Level At(int depth)
    {
        while (_levels.Count <= depth) _levels.Add(new Level());
        return _levels[depth];
    }

    private FocusSlot Slot(Paper paper)
    {
        for (int i = 0; i < _focus.Count; i++)
            if (ReferenceEquals(_focus[i].Paper, paper)) return _focus[i];
        var slot = new FocusSlot(paper);
        _focus.Add(slot);
        return slot;
    }

    internal void RememberFocus(Paper? paper) { if (paper != null) Slot(paper); }

    internal void Focus(Paper paper, ElementHandle menu)
    {
        var slot = Slot(paper);
        slot.Menu = menu.Data.ID;
        paper.SkipKeyboardNavigation = true;
        paper.SetFocus(menu);
    }

    internal void ReleaseFocus()
    {
        for (int i = 0; i < _focus.Count; i++)
        {
            var slot = _focus[i];
            slot.Paper.SkipKeyboardNavigation = slot.SkipNavigation;
            if (slot.Menu != 0 && slot.Paper.FocusedElementId == slot.Menu)
                MenuFocusRestore.Queue(slot.Paper, slot.Previous, slot.Menu);
        }
        _focus.Clear();
    }

    internal bool Selected(int depth, int index) => At(depth).Selected == index;
    internal bool Open(int depth, int index) => At(depth).Open == index;
    internal MenuScroll ScrollAt(int depth) => At(depth).Scroll;
    internal MenuWidthCache WidthAt(int depth) => At(depth).Width;
    internal int RevealAt(int depth)
    {
        var level = At(depth);
        bool reveal = level.Reveal;
        level.Reveal = false;
        return reveal ? level.Selected : -1;
    }

    internal void Scrolled(int depth)
    {
        At(depth).Open = -1;
        Trim(depth + 1);
        _keyboard = false;
        _pointerMoved = true;
    }
    internal bool CanHover(Paper paper)
    {
        if (ContextMenu.PointerSurfaceFilter?.Invoke(paper) == false) return false;
        var slot = Slot(paper);
        return slot.Pointer == paper.PointerPos && (!_keyboard || slot.PendingPointerMove);
    }

    internal void Hover(Paper paper, ContextBuilder builder, int depth, int index)
    {
        if (!_hoverReady || !paper.IsParentHovered || (_keyboard && !_pointerMoved)
            || ContextMenu.PointerSurfaceFilter?.Invoke(paper) == false) return;
        var level = At(depth);
        int open = builder.Items[index] is ContextBuilder.CtxSubmenu ? index : -1;
        if (level.Selected == index && level.Open == open) return;
        Select(builder, depth, index);
        level.Open = open;
        Trim(depth + 1);
        _keyboard = false;
    }

    private void Trim(int count)
    {
        if (_levels.Count > count) _levels.RemoveRange(count, _levels.Count - count);
    }

    private static int Kind(IContextItem item) => item switch
    {
        ContextBuilder.CtxItem { Enabled: true } => 1,
        ContextBuilder.CtxToggle { Enabled: true } => 2,
        ContextBuilder.CtxSubmenu { Sub: not null } => 3,
        _ => 0,
    };

    private static string Label(IContextItem item) => item switch
    {
        ContextBuilder.CtxItem itemRow => itemRow.Label,
        ContextBuilder.CtxToggle toggle => toggle.Label,
        ContextBuilder.CtxSubmenu sub => sub.Label,
        _ => "",
    };

    private void Select(ContextBuilder builder, int depth, int index)
    {
        var level = At(depth);
        level.Selected = index;
        level.Label = index >= 0 ? Label(builder.Items[index]) : "";
        level.Kind = index >= 0 ? Kind(builder.Items[index]) : 0;
    }

    private void Move(ContextBuilder builder, int depth, int delta)
    {
        var level = At(depth);
        int count = builder.Items.Count;
        int start = level.Selected < 0 ? (delta > 0 ? -1 : 0) : level.Selected;
        for (int n = 1; n <= count; n++)
        {
            int index = (start + delta * n % count + count) % count;
            if (Kind(builder.Items[index]) == 0) continue;
            Select(builder, depth, index);
            level.Open = -1;
            level.Reveal = true;
            Trim(depth + 1);
            break;
        }
        _keyboard = true;
    }

    private bool Activate(ContextBuilder builder, int depth, Action close)
    {
        int index = At(depth).Selected;
        if (index < 0 || index >= builder.Items.Count) return false;
        switch (builder.Items[index])
        {
            case ContextBuilder.CtxSubmenu { Sub: { } sub }:
                At(depth).Open = index;
                Trim(depth + 1);
                Move(sub, depth + 1, 1);
                return false;
            case ContextBuilder.CtxItem { Enabled: true } item:
                close(); item.OnClick?.Invoke(); return true;
            case ContextBuilder.CtxToggle { Enabled: true } toggle:
                close(); toggle.OnClick?.Invoke(); return true;
            default: return false;
        }
    }

    // Return false when this session closed, or a menu-bar root switch was requested.
    internal bool Process(Paper paper, ContextBuilder root, Action close)
    {
        var slot = Slot(paper);
        bool moved = slot.Pointer != paper.PointerPos;
        // IsParentHovered belongs to the previous layout pass. On the movement frame it
        // can still name a different parent row and close the child we are moving into.
        _hoverReady = !moved;
        // Hover is resolved after this build. Carry movement into the next build too.
        _pointerMoved = moved || slot.PendingPointerMove;
        slot.PendingPointerMove = moved;
        slot.Pointer = paper.PointerPos;
        paper.CaptureKeyboard();
        // Paper pulses IsKeyPressed once for each auto-repeat interval. The wider
        // IsKeyPressedOrRepeating query stays true for half that interval.
        if (paper.IsKeyPressed(PaperKey.Down) || paper.IsKeyPressed(PaperKey.Up)
            || paper.IsKeyPressed(PaperKey.Left) || paper.IsKeyPressed(PaperKey.Right)
            || paper.IsKeyPressed(PaperKey.Escape) || paper.IsKeyPressed(PaperKey.Enter)
            || paper.IsKeyPressed(PaperKey.KeypadEnter) || paper.InputString.Count > 0)
        {
            _pointerMoved = false;
            slot.PendingPointerMove = false;
        }
        int depth = 0;
        var builder = root;
        while (true)
        {
            var level = At(depth);
            int selected = level.Selected;
            if (selected >= 0 && (selected >= builder.Items.Count
                || level.Kind != Kind(builder.Items[selected])
                || !string.Equals(level.Label, Label(builder.Items[selected]), StringComparison.Ordinal)))
            {
                Select(builder, depth, -1); level.Open = -1; Trim(depth + 1);
            }
            if (level.Open < 0 || level.Open != level.Selected
                || builder.Items[level.Open] is not ContextBuilder.CtxSubmenu { Sub: { } child }) break;
            builder = child; depth++;
        }
        if (_selectFirst) { _selectFirst = false; Move(builder, depth, 1); }
        if (paper.IsKeyPressed(PaperKey.Down)) Move(builder, depth, 1);
        else if (paper.IsKeyPressed(PaperKey.Up)) Move(builder, depth, -1);
        else if (paper.IsKeyPressed(PaperKey.Escape) || paper.IsKeyPressed(PaperKey.Left))
        {
            if (depth > 0) { At(depth - 1).Open = -1; Trim(depth); _keyboard = true; }
            else if (paper.IsKeyPressed(PaperKey.Escape)) { close(); paper.InputString.Clear(); return false; }
            else if (SwitchRoot != null) { SwitchRoot(-1); paper.InputString.Clear(); return false; }
        }
        else if (paper.IsKeyPressed(PaperKey.Right))
        {
            if (At(depth).Selected >= 0 && builder.Items[At(depth).Selected] is ContextBuilder.CtxSubmenu)
                Activate(builder, depth, close);
            else if (depth == 0 && SwitchRoot != null) { SwitchRoot(1); paper.InputString.Clear(); return false; }
        }
        else if (paper.IsKeyPressed(PaperKey.Enter) || paper.IsKeyPressed(PaperKey.KeypadEnter))
        {
            if (Activate(builder, depth, close)) { paper.InputString.Clear(); return false; }
        }

        bool modified = paper.IsKeyDown(PaperKey.LeftControl) || paper.IsKeyDown(PaperKey.RightControl)
            || paper.IsKeyDown(PaperKey.LeftAlt) || paper.IsKeyDown(PaperKey.RightAlt);
        while (paper.InputString.Count > 0)
        {
            char c = paper.InputString.Dequeue();
            if (modified || char.IsControl(c) || char.IsWhiteSpace(c)) continue;
            int matches = 0, next = -1, first = -1;
            int selected = At(depth).Selected;
            for (int i = 0; i < builder.Items.Count; i++)
            {
                string label = Label(builder.Items[i]);
                if (Kind(builder.Items[i]) == 0 || label.Length == 0
                    || char.ToUpperInvariant(label[0]) != char.ToUpperInvariant(c)) continue;
                matches++;
                if (first < 0) first = i;
                if (i > selected && next < 0) next = i;
            }
            if (matches == 0) continue;
            Select(builder, depth, next >= 0 ? next : first);
            At(depth).Open = -1; Trim(depth + 1); _keyboard = true;
            At(depth).Reveal = true;
            if (matches == 1)
            {
                bool invoked = Activate(builder, depth, close);
                paper.InputString.Clear();
                return !invoked;
            }
        }
        return true;
    }
}

/// <summary>Bounded, session-owned scroll state for one menu depth.</summary>
internal sealed class MenuScroll
{
    internal float Offset, Maximum;
    private float _nextHover;
    private int _hoverDirection;

    internal bool By(float delta)
    {
        float next = Math.Clamp(Offset + delta, 0, Maximum);
        if (next == Offset) return false;
        Offset = next;
        return true;
    }

    internal bool Hover(float time, int direction, float rowHeight)
    {
        if (_hoverDirection != direction || time > _nextHover + .1f)
        {
            _hoverDirection = direction;
            _nextHover = time + .3f;
            return false;
        }
        if (time < _nextHover) return false;
        _nextHover = time + .08f;
        return By(direction * rowHeight);
    }

    internal void Leave() => _hoverDirection = 0;
}

/// <summary>One pending restoration per live Paper, consumed only after the closing frame.</summary>
internal static class MenuFocusRestore
{
    private readonly record struct Pending(int Previous, int Menu, float ClosedAt);
    private sealed class State { internal Pending Pending; }
    // Paper's root storage is pruned at EndFrame. Weak keys preserve the next-frame
    // handoff without retaining destroyed surfaces or allocating on idle frames.
    private static readonly ConditionalWeakTable<Paper, State> States = new();

    internal static int PreviousFocus(Paper paper)
        => States.TryGetValue(paper, out var state) && state.Pending.Menu != 0
            && state.Pending.Menu == paper.FocusedElementId ? state.Pending.Previous : paper.FocusedElementId;

    internal static void Queue(Paper paper, int previous, int menu)
        => States.GetValue(paper, static _ => new State()).Pending = new Pending(previous, menu, paper.Time);

    internal static void Apply(Paper paper)
    {
        if (!States.TryGetValue(paper, out var state)) return;
        var pending = state.Pending;
        if (pending.Menu == 0 || pending.ClosedAt >= paper.Time) return;
        state.Pending = default;
        if (paper.FocusedElementId != 0 && paper.FocusedElementId != pending.Menu) return;
        var previous = paper.FindElementByID(pending.Previous);
        if (pending.Previous != 0 && previous.IsValid) paper.SetFocus(previous);
        else paper.ClearFocus();
    }
}
