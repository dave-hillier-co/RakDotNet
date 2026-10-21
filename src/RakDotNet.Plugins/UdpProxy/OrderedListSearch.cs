namespace RakDotNet.Plugins;

/// <summary>
/// DataStructures::OrderedList's binary search (DS_OrderedList.h:119-170), for the plugins whose comparison functions
/// are not consistent orders: where a lookup or an insertion lands depends on the search itself.
/// </summary>
internal static class OrderedListSearch
{
    /// <summary>
    /// GetIndexFromKey: the index of the element <paramref name="compare"/> says is equal to the key, or where the key
    /// would be inserted.
    /// </summary>
    public static int GetIndexFromKey<TKey, TData>(IReadOnlyList<TData> list, TKey key, Func<TKey, TData, int> compare, out bool objectExists)
    {
        objectExists = false;
        if (list.Count == 0)
            return 0;
        var upperBound = list.Count - 1;
        var lowerBound = 0;
        var index = list.Count / 2;
        while (true)
        {
            var res = compare(key, list[index]);
            if (res == 0)
            {
                objectExists = true;
                return index;
            }
            if (res < 0)
                upperBound = index - 1;
            else
                lowerBound = index + 1;
            index = lowerBound + ((upperBound - lowerBound) / 2);
            if (lowerBound > upperBound)
                return lowerBound;
            // Only an inconsistent comparison gets here (RakNet asserts).
            if (index < 0 || index >= list.Count)
                return 0;
        }
    }

    /// <summary>Insert (DS_OrderedList.h:173-199): false, inserting nothing, when an equal element is there.</summary>
    public static bool Insert<TKey, TData>(List<TData> list, TKey key, TData data, Func<TKey, TData, int> compare)
    {
        var index = GetIndexFromKey(list, key, compare, out var exists);
        if (exists)
            return false;
        list.Insert(Math.Min(index, list.Count), data);
        return true;
    }
}
