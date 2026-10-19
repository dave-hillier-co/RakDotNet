using System.Collections;

namespace RakDotNet;

/// <summary>
/// An immutable snapshot of the connected systems, rebuilt only when a connection is added or removed or stops being
/// Connected. Enumerates with a struct enumerator, without allocating.
/// </summary>
public readonly struct ConnectionList : IReadOnlyList<RakConnection>
{
    private readonly RakConnection[]? _items;

    internal ConnectionList(RakConnection[] items) => _items = items;

    public int Count => _items?.Length ?? 0;

    public RakConnection this[int index] => (_items ?? [])[index];

    public Enumerator GetEnumerator() => new(_items ?? []);

    IEnumerator<RakConnection> IEnumerable<RakConnection>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator : IEnumerator<RakConnection>
    {
        private readonly RakConnection[] _items;
        private int _index;

        internal Enumerator(RakConnection[] items)
        {
            _items = items;
            _index = -1;
        }

        public readonly RakConnection Current => _items[_index];

        readonly object IEnumerator.Current => Current;

        public bool MoveNext() => ++_index < _items.Length;

        public void Reset() => _index = -1;

        public readonly void Dispose() { }
    }
}
