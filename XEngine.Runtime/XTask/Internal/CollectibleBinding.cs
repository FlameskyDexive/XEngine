// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Reflection;

namespace XEngine.Async.Internal;

/// <summary>
/// True when the item transitively references a type from a collectible (hot-reloadable)
/// AssemblyLoadContext, so it must be dropped from the frame loop when that context unloads.
/// </summary>
internal interface ICollectibleBoundItem
{
    bool IsCollectibleBound { get; }
}

/// <summary>
/// Implemented by engine-side combinator promises (<c>WhenAll</c>/<c>WhenAny</c>) that sit BETWEEN a
/// frame-loop source (e.g. <c>DelayPromise</c>) and the ultimate awaiter. The combinator registers an
/// engine closure on the child source, so the child's direct delegate check comes back
/// "not collectible" while the combinator's own core still holds the user continuation. This
/// interface lets <see cref="CollectibleBinding.IsChainBound"/> see through that middleman.
/// </summary>
internal interface IChainCollectibleSource
{
    /// <summary>True when this combinator's pending awaiter belongs to a collectible assembly.</summary>
    bool HasCollectibleAwaiter { get; }
}

/// <summary>
/// Shared helpers for detecting references into collectible assemblies. A delegate or state object
/// that belongs to a collectible ALC keeps the whole script context alive as long as anything in a
/// long-lived assembly references it — the root cause of failed hot-reload unloads.
/// </summary>
/// <remarks>
/// The check must follow CONSTRUCTED GENERIC types: XTask's pooled async runners are engine types
/// instantiated over the user's state machine (<c>AsyncXTaskVoid&lt;UserStateMachine&gt;</c>), so the
/// delegate that reaches the frame loop is an engine method on an engine object whose generic
/// argument — and through it the runner's <c>_stateMachine</c> field — lives in the script ALC.
/// </remarks>
internal static class CollectibleBinding
{
    /// <summary>A delegate is collectible-bound when its method is declared by, or its target
    /// instance (including generic instantiation arguments) belongs to, a collectible assembly.</summary>
    public static bool IsBound(Delegate? action)
    {
        if (action == null) return false;
        if (IsTypeBound(action.Method.DeclaringType)) return true;
        return IsTypeBound(action.Target?.GetType());
    }

    /// <summary>A state object boxed from a collectible assembly (async state machine boxes) is
    /// itself a strong reference into that assembly.</summary>
    public static bool IsBound(object? state) => state != null && IsTypeBound(state.GetType());

    /// <summary>
    /// Transitive variant of <see cref="IsBound(Delegate)"/> for reload scrubs: follows a delegate
    /// into compiler-generated closures and any <see cref="IChainCollectibleSource"/> middleman they
    /// capture, so <c>Delay → WhenAll → user continuation</c> chains are recognized and the whole
    /// chain can be dropped. Uses reflection (closure fields); only called from unload-time scrub
    /// paths, never per frame.
    /// </summary>
    public static bool IsChainBound(Delegate? action, object? state)
    {
        if (IsBound(action) || IsBound(state)) return true;

        if (action != null)
        {
            if (action.Target is IChainCollectibleSource { HasCollectibleAwaiter: true }) return true;
            if (action.Target is IChainCollectibleSource) return false; // engine awaiter only - chain ends here

            // Compiler-generated closures capture the combinator promise in an instance field.
            object? target = action.Target;
            if (target != null && target.GetType().IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false))
            {
                var fields = target.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                foreach (var field in fields)
                {
                    try
                    {
                        if (field.GetValue(target) is IChainCollectibleSource { HasCollectibleAwaiter: true })
                            return true;
                    }
                    catch { /* skip unreadable fields */ }
                }
            }
        }

        if (state is IChainCollectibleSource { HasCollectibleAwaiter: true }) return true;
        return false;
    }

    private static bool IsTypeBound(Type? type)
    {
        for (Type? current = type; current != null; current = current.DeclaringType)
        {
            // A generic type parameter carries no assembly of its own, and its DeclaringType is the
            // generic definition whose arguments include that very parameter: following it recursed
            // without end. That is exactly what a lambda declared inside a generic class produces -
            // its compiler-generated <>c host reports the OPEN definition as DeclaringType (e.g.
            // AsyncXTask<TStateMachine>.<>c -> AsyncXTask<TStateMachine> -> TStateMachine -> ...),
            // so TaskPool.RemoveCollectibleSizeGetters overflowed the stack on every hot-reload
            // that followed a Play session. The parameter's constraints are not inspected; the
            // definition itself is still checked through its own assembly on the way here.
            if (current.IsGenericParameter) return false;
            if (current.Assembly.IsCollectible) return true;

            if (current.IsArray)
            {
                Type? element = current.GetElementType();
                if (element != null && IsTypeBound(element)) return true;
                continue;
            }

            if (current.IsGenericType)
            {
                foreach (Type argument in current.GetGenericArguments())
                {
                    if (!argument.IsGenericParameter && IsTypeBound(argument)) return true;
                }
            }
        }

        return false;
    }
}
