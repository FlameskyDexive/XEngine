/*
 * Jitter2 Physics Library
 * (c) Thorben Linneweber and contributors
 * SPDX-License-Identifier: MIT
 */

using System.Runtime.CompilerServices;
using Jitter2.LinearMath;

namespace Jitter2.Dynamics;

/// <summary>
/// How two per-shape material coefficients are reduced to the single value a contact solves with.
/// </summary>
/// <remarks>
/// XENGINE: added for per-shape materials. The declaration order is significant — see
/// <see cref="MaterialCombineExtensions.Resolve"/> — and matches PhysX so that values authored against
/// Unity's <c>PhysicsMaterialCombine</c> transfer unchanged.
/// </remarks>
public enum MaterialCombine
{
    /// <summary>The arithmetic mean of the two coefficients.</summary>
    Average = 0,

    /// <summary>The smaller of the two coefficients.</summary>
    Minimum = 1,

    /// <summary>The product of the two coefficients.</summary>
    Multiply = 2,

    /// <summary>The larger of the two coefficients.</summary>
    Maximum = 3,
}

/// <summary>
/// Resolution of a pair of material coefficients into the one the solver uses.
/// </summary>
public static class MaterialCombineExtensions
{
    /// <summary>
    /// The mode a pair solves with, given what each side asks for.
    /// </summary>
    /// <remarks>
    /// A pair has two modes and must solve with one. PhysX resolves the conflict by ordinal: the
    /// numerically larger <see cref="MaterialCombine"/> wins, so <see cref="MaterialCombine.Maximum"/>
    /// beats <see cref="MaterialCombine.Multiply"/> beats <see cref="MaterialCombine.Minimum"/> beats
    /// <see cref="MaterialCombine.Average"/>. That ordering is why the enum values are pinned. The
    /// alternative — averaging the modes, or letting the lower shape id win — makes the result depend on
    /// authoring order, which is worse to debug than an arbitrary but stable precedence.
    /// <para>
    /// XENGINE: a shape may express <see langword="null"/>, meaning it has no material and no opinion.
    /// It then defers to the other side instead of voting. Without this, a shape with no material would
    /// have to default to some concrete mode, and since <see cref="MaterialCombine.Maximum"/> is the
    /// dominant ordinal, defaulting to it would silently override every material a pair could name.
    /// Defaulting to <see cref="MaterialCombine.Average"/> instead would change how bodies without
    /// materials behave, which is upstream's <c>max</c>. Abstaining satisfies both.
    /// </para>
    /// </remarks>
    /// <param name="modeA">The first shape's mode, or <see langword="null"/> to abstain.</param>
    /// <param name="modeB">The second shape's mode, or <see langword="null"/> to abstain.</param>
    /// <returns>The mode to combine with. <see cref="MaterialCombine.Maximum"/> when both abstain.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static MaterialCombine Reconcile(MaterialCombine? modeA, MaterialCombine? modeB)
    {
        if (modeA.HasValue)
            return modeB.HasValue && modeB.Value > modeA.Value ? modeB.Value : modeA.Value;

        return modeB ?? MaterialCombine.Maximum;
    }

    /// <summary>
    /// Combines two coefficients, deferring to whichever side asks for the more dominant mode.
    /// </summary>
    /// <param name="a">The first shape's coefficient.</param>
    /// <param name="modeA">The first shape's combine mode, or <see langword="null"/> to abstain.</param>
    /// <param name="b">The second shape's coefficient.</param>
    /// <param name="modeB">The second shape's combine mode, or <see langword="null"/> to abstain.</param>
    /// <returns>The coefficient the contact solves with.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Real Resolve(Real a, MaterialCombine? modeA, Real b, MaterialCombine? modeB)
        => Combine(a, b, Reconcile(modeA, modeB));

    /// <summary>
    /// Applies a resolved mode to two coefficients.
    /// </summary>
    /// <param name="a">The first coefficient.</param>
    /// <param name="b">The second coefficient.</param>
    /// <param name="mode">The mode to apply.</param>
    /// <returns>The combined coefficient.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Real Combine(Real a, Real b, MaterialCombine mode)
        => mode switch
        {
            MaterialCombine.Minimum => MathR.Min(a, b),
            MaterialCombine.Multiply => a * b,
            MaterialCombine.Maximum => MathR.Max(a, b),
            _ => (a + b) * (Real)0.5,
        };
}
