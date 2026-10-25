// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.PaperUI.Events;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Quill;
using XEngine.Vector;

namespace XEngine.PaperUI
{
    // ────────────────────────────────────────────────────────────────────────────────
    //  Stateful (captured-value) event bridges.
    //
    //  The On*<T>(capturedValue, handler) fluent overloads used to synthesize a fresh
    //  closure + delegate per element per presented frame — one overnight editor dump held
    //  539,748 dead Action<ClickEvent> instances. They now rent a pooled bridge: a mutable
    //  closure whose State/Handler fields are rewritten during each frame's build while the
    //  delegate instance survives across frames. Dispatch invokes the bridge straight from
    //  a dedicated ElementData slot, so no wrapper allocation reaches the GC heap in
    //  steady state.
    //
    //  Lifecycle: CreateElement drains the previous slot's bridges back to these pools
    //  before installing the fresh struct (see PaperElementStorage / ElementData.ReturnBridges).
    // ────────────────────────────────────────────────────────────────────────────────

    internal interface IStatefulHandler
    {
        /// <summary>Return this bridge instance to its per-type pool.</summary>
        void ReturnToPool();
    }

    internal static class StatefulPools<TInterface> where TInterface : class, IStatefulHandler
    {
        private static readonly Dictionary<Type, Stack<object>> s_pools = new();

        public static TConcrete Rent<TConcrete>() where TConcrete : TInterface, new()
        {
            lock (s_pools)
            {
                if (s_pools.TryGetValue(typeof(TConcrete), out var stack))
                {
                    while (stack.Count > 0)
                        return (TConcrete)stack.Pop()!;
                }
            }
            return new TConcrete();
        }

        public static void Return<TConcrete>(TConcrete bridge) where TConcrete : class, TInterface
        {
            lock (s_pools)
            {
                if (!s_pools.TryGetValue(typeof(TConcrete), out var stack))
                    s_pools[typeof(TConcrete)] = stack = new Stack<object>();
                if (stack.Count < 4096)
                    stack.Push(bridge);
            }
        }
    }

    #region Handler interfaces

    internal interface IClickStateful : IStatefulHandler { void Invoke(ClickEvent e); }
    internal interface IDragStateful : IStatefulHandler { void Invoke(DragEvent e); }
    internal interface IScrollStateful : IStatefulHandler { void Invoke(ScrollEvent e); }
    internal interface IKeyStateful : IStatefulHandler { void Invoke(KeyEvent e); }
    internal interface ITextStateful : IStatefulHandler { void Invoke(TextInputEvent e); }
    internal interface IFocusStateful : IStatefulHandler { void Invoke(FocusEvent e); }
    internal interface IElementStateful : IStatefulHandler { void Invoke(ElementEvent e); }
    internal interface IPostLayoutStateful : IStatefulHandler { void Invoke(ElementHandle handle, Rect rect); }

    #endregion

    #region Bridges

    internal sealed class ClickBridge<T> : IClickStateful
    {
        public T State = default!;
        public Action<T, ClickEvent> Handler = default!;
        public void Invoke(ClickEvent e) => Handler(State, e);
        public void ReturnToPool() => StatefulPools<IClickStateful>.Return(this);
    }

    internal sealed class DragBridge<T> : IDragStateful
    {
        public T State = default!;
        public Action<T, DragEvent> Handler = default!;
        public void Invoke(DragEvent e) => Handler(State, e);
        public void ReturnToPool() => StatefulPools<IDragStateful>.Return(this);
    }

    internal sealed class ScrollBridge<T> : IScrollStateful
    {
        public T State = default!;
        public Action<T, ScrollEvent> Handler = default!;
        public void Invoke(ScrollEvent e) => Handler(State, e);
        public void ReturnToPool() => StatefulPools<IScrollStateful>.Return(this);
    }

    internal sealed class KeyBridge<T> : IKeyStateful
    {
        public T State = default!;
        public Action<T, KeyEvent> Handler = default!;
        public void Invoke(KeyEvent e) => Handler(State, e);
        public void ReturnToPool() => StatefulPools<IKeyStateful>.Return(this);
    }

    internal sealed class TextBridge<T> : ITextStateful
    {
        public T State = default!;
        public Action<T, TextInputEvent> Handler = default!;
        public void Invoke(TextInputEvent e) => Handler(State, e);
        public void ReturnToPool() => StatefulPools<ITextStateful>.Return(this);
    }

    internal sealed class FocusBridge<T> : IFocusStateful
    {
        public T State = default!;
        public Action<T, FocusEvent> Handler = default!;
        public void Invoke(FocusEvent e) => Handler(State, e);
        public void ReturnToPool() => StatefulPools<IFocusStateful>.Return(this);
    }

    internal sealed class ElementBridge<T> : IElementStateful
    {
        public T State = default!;
        public Action<T, ElementEvent> Handler = default!;
        public void Invoke(ElementEvent e) => Handler(State, e);
        public void ReturnToPool() => StatefulPools<IElementStateful>.Return(this);
    }

    internal sealed class PostLayoutBridge<T> : IPostLayoutStateful
    {
        public T State = default!;
        public Action<T, ElementHandle, Rect> Handler = default!;
        public void Invoke(ElementHandle handle, Rect rect) => Handler(State, handle, rect);
        public void ReturnToPool() => StatefulPools<IPostLayoutStateful>.Return(this);
    }

    #endregion
}
