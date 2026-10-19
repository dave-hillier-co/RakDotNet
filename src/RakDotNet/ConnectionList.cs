using System.Collections;

namespace RakDotNet;

/// <summary>
/// An immutable snapshot of the connection table, rebuilt only when a connection is added or removed. Enumerates with a
/// struct enumerator, without allocating.
/// </summary>
public readonly struct ConnectionList : IReadOnlyList<RakConnection>
{
    public int Count => throw new NotImplementedException();

    public RakConnection this[int index] => throw new NotImplementedException();

    public Enumerator GetEnumerator() => throw new NotImplementedException();

    IEnumerator<RakConnection> IEnumerable<RakConnection>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator : IEnumerator<RakConnection>
    {
        public readonly RakConnection Current => throw new NotImplementedException();

        readonly object IEnumerator.Current => Current;

        public bool MoveNext() => throw new NotImplementedException();

        public void Reset() => throw new NotImplementedException();

        public readonly void Dispose() { }
    }
}
