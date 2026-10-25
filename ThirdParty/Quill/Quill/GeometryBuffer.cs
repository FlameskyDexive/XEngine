// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace XEngine.Quill
{
    /// <summary>
    /// A growable array that hands out its backing store directly, so renderer backends can upload
    /// canvas geometry without copying it out first. List&lt;T&gt; cannot do this on netstandard2.1,
    /// which has no CollectionsMarshal.
    /// </summary>
    /// <remarks>
    /// XEngine addition: implements <see cref="IReadOnlyList{T}"/> (explicitly, so the backing
    /// <see cref="Count"/> field stays nameable) and gains <see cref="CopyTo"/>, so code written
    /// against the previous List-backed canvas store — read-only views and bulk copies — works
    /// on this buffer unchanged.
    /// </remarks>
    internal sealed class GeometryBuffer<T> : IReadOnlyList<T>
    {
        private const int MinimumCapacity = 256;

        internal T[] Array;
        internal int Count;

        internal GeometryBuffer(int capacity = MinimumCapacity)
        {
            Array = new T[capacity < MinimumCapacity ? MinimumCapacity : capacity];
        }

        internal ReadOnlySpan<T> AsSpan() => new ReadOnlySpan<T>(Array, 0, Count);

        /// <summary>Bulk-copies the valid entries into <paramref name="destination"/> (which must
        /// be at least <see cref="Count"/> long).</summary>
        internal void CopyTo(T[] destination) => System.Array.Copy(Array, destination, Count);

        T IReadOnlyList<T>.this[int index] => Array[index];

        int IReadOnlyCollection<T>.Count => Count;

        IEnumerator<T> IEnumerable<T>.GetEnumerator()
        {
            for (int i = 0; i < Count; i++)
                yield return Array[i];
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            => ((IEnumerable<T>)this).GetEnumerator();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Add(in T item)
        {
            if (Count == Array.Length)
                Grow(Count + 1);

            Array[Count++] = item;
        }

        internal void AddRange(List<T> items)
        {
            int count = items.Count;
            Reserve(count);
            items.CopyTo(Array, Count);
            Count += count;
        }

        /// <summary>Ensures room for <paramref name="additional"/> more items without reallocating.</summary>
        internal void Reserve(int additional)
        {
            int needed = Count + additional;
            if (needed > Array.Length)
                Grow(needed);
        }

        internal void Clear() => Count = 0;

        private void Grow(int needed)
        {
            int capacity = Array.Length;
            while (capacity < needed)
                capacity *= 2;

            System.Array.Resize(ref Array, capacity);
        }
    }
}
