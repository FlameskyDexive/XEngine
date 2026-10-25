using System.Collections.Generic;

namespace XEngine.Hudmesh;

/// <summary>Static per-type pool for HUD widgets and per-entity objects (same pattern as UIMeshBuilder.Rent).</summary>
[Pool]
internal static class HudPool<T> where T : class, new()
{
    private static readonly Stack<T> Pool = new();

    public static T Pop() => Pool.TryPop(out T? item) ? item : new T();

    public static void Push(T item) => Pool.Push(item);
}
