// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Runtime;

namespace XEngine.XUI;

/// <summary>
/// Base for the generated per-screen binding: the only layer that knows element ids.
/// </summary>
/// <remarks>
/// <para>
/// The generated subclass exposes one lazily-cached property per <c>id</c> in the document, typed by
/// the tag. Business code reads <c>View.BtnLogin</c> and never touches a GameObject, a component
/// lookup, or a string — which is what makes a controller reviewable without the document open beside
/// it, and what makes a renamed id a compile error instead of a runtime null.
/// </para>
/// <para>
/// A hot reload rebuilds the screen and calls <see cref="Rebind"/>, so the cached references cannot
/// outlive the objects they point at.
/// </para>
/// </remarks>
public abstract class XuiView : IDisposable
{
    private XuiScreenInstance _screen;

    protected XuiView(XuiScreenInstance screen)
    {
        _screen = screen ?? throw new ArgumentNullException(nameof(screen));
    }

    /// <summary>The assembled screen this binding reads from.</summary>
    public XuiScreenInstance Screen => _screen;

    /// <summary>
    /// Points the binding at a freshly assembled screen and drops every cached reference. Called after
    /// a document hot reload, so a controller survives an edit without being torn down.
    /// </summary>
    public void Rebind(XuiScreenInstance screen)
    {
        _screen = screen ?? throw new ArgumentNullException(nameof(screen));
        ClearCache();
    }

    /// <summary>
    /// Resolves an element, or explains what to do about it. The generated properties call this, so a
    /// document that no longer declares an id fails with the id's name and the fix, not a null.
    /// </summary>
    protected T Require<T>(string id) where T : MonoBehaviour => _screen.Require<T>(id);

    /// <summary>Generated override that nulls every cached field.</summary>
    protected virtual void ClearCache() { }

    public void Dispose()
    {
        ClearCache();
        GC.SuppressFinalize(this);
    }
}
