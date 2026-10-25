// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Runtime;
using XEngine.XUI.Core;

namespace XEngine.XUI;

/// <summary>
/// One assembled screen: the Canvas root plus the id → GameObject index.
/// <para>
/// The index is the whole contract between the document and the code. A generated view binding looks
/// ids up here, <c>runtime_xui_tree</c> walks it to report what actually exists, and a hot reload
/// swaps the instance while the controller keeps running — so a stale binding surfaces as a named
/// missing id rather than a null nobody can trace.
/// </para>
/// </summary>
public sealed class XuiScreenInstance
{
    private readonly Dictionary<string, GameObject> _byId;

    internal XuiScreenInstance(
        string name,
        GameObject root,
        XuiLayerKind layer,
        Dictionary<string, GameObject> byId)
    {
        Name = name;
        Root = root;
        Layer = layer;
        _byId = byId;
    }

    /// <summary>Screen name from the document; also the controller type name.</summary>
    public string Name { get; }

    /// <summary>The Canvas GameObject this screen owns.</summary>
    public GameObject Root { get; }

    public XuiLayerKind Layer { get; }

    /// <summary>Every id declared in the document, in creation order.</summary>
    public IReadOnlyCollection<string> Ids => _byId.Keys;

    public GameObject? Find(string id)
        => _byId.TryGetValue(id, out GameObject? go) ? go : null;

    /// <summary>
    /// Looks up a component by document id. Returns null when the id is gone or carries a different
    /// component than the caller expects — both are real cases after an edit, and both are things the
    /// caller should be able to report rather than crash on.
    /// </summary>
    public T? Get<T>(string id) where T : MonoBehaviour
    {
        return Find(id) is { IsDisposed: false } go ? go.GetComponent<T>() : null;
    }

    /// <summary>
    /// Same as <see cref="Get{T}"/> but states plainly what went wrong, for controller code that
    /// genuinely cannot continue without the control.
    /// </summary>
    public T Require<T>(string id) where T : MonoBehaviour
    {
        if (Find(id) is not { IsDisposed: false } go)
        {
            throw new InvalidOperationException(
                "Screen \"" + Name + "\" has no element with id \"" + id
                + "\". The document changed — re-run xui_scaffold to regenerate the view binding.");
        }

        if (go.GetComponent<T>() is not { IsDisposed: false } component)
        {
            throw new InvalidOperationException(
                "Element \"" + id + "\" on screen \"" + Name + "\" has no " + typeof(T).Name
                + ". The tag in the document no longer matches the generated binding — re-run xui_scaffold.");
        }

        return component;
    }
}
