namespace RakDotNet.Plugins;

/// <summary>
/// GridSectorizer: a uniform grid over a rectangle of the world, each cell listing the entries whose bounding rectangles
/// touch it, for finding what is near a point or area.
/// </summary>
/// <remarks>
/// <para>
/// Ported from GridSectorizer.cpp as RakNet builds it, without <c>_USE_ORDERED_LIST</c>, so there is no RemoveEntry or
/// MoveEntry. The arithmetic is single precision, as in the C++, so entries fall in the same cells. Coordinates outside
/// the world are clamped into its edge cells. An entry added to several cells, or added twice, is returned once for each.
/// </para>
/// <para>Not thread-safe, as in RakNet.</para>
/// </remarks>
/// <typeparam name="T">The entries; RakNet's are <c>void*</c>.</typeparam>
public sealed class GridSectorizer<T>
{
    private List<T>[] _grid = [];
    private float _cellOriginX;
    private float _cellOriginY;
    private float _cellWidth;
    private float _invCellWidth;
    private float _invCellHeight;
    private int _gridCellWidthCount;
    private int _gridCellHeightCount;

    /// <summary>
    /// Init: divides the world from (<paramref name="minX"/>, <paramref name="minY"/>) to (<paramref name="maxX"/>,
    /// <paramref name="maxY"/>) into the fewest cells no larger than <paramref name="maxCellWidth"/> by
    /// <paramref name="maxCellHeight"/>, forgetting every entry.
    /// </summary>
    public void Init(float maxCellWidth, float maxCellHeight, float minX, float minY, float maxX, float maxY)
    {
        if (!(maxCellWidth > 0f))
            throw new ArgumentOutOfRangeException(nameof(maxCellWidth), maxCellWidth, "The cell width must be positive.");
        if (!(maxCellHeight > 0f))
            throw new ArgumentOutOfRangeException(nameof(maxCellHeight), maxCellHeight, "The cell height must be positive.");

        // GridSectorizer.cpp:28-37: the cells are made slightly smaller so they fit the world exactly.
        _cellOriginX = minX;
        _cellOriginY = minY;
        var gridWidth = maxX - minX;
        var gridHeight = maxY - minY;
        _gridCellWidthCount = (int)MathF.Ceiling(gridWidth / maxCellWidth);
        _gridCellHeightCount = (int)MathF.Ceiling(gridHeight / maxCellHeight);
        _cellWidth = gridWidth / _gridCellWidthCount;
        var cellHeight = gridHeight / _gridCellHeightCount;
        _invCellWidth = 1.0f / _cellWidth;
        _invCellHeight = 1.0f / cellHeight;

        var count = checked(_gridCellWidthCount * _gridCellHeightCount);
        _grid = new List<T>[count];
        for (var i = 0; i < count; i++)
            _grid[i] = [];
    }

    /// <summary>AddEntry: lists <paramref name="entry"/> in every cell its bounding rectangle touches.</summary>
    public void AddEntry(T entry, float minX, float minY, float maxX, float maxY)
    {
        if (!(_cellWidth > 0f))
            throw new InvalidOperationException("Init has not been called.");
        if (!(minX < maxX && minY < maxY))
            throw new ArgumentException("The rectangle is empty: its minimum must be below its maximum in both directions.");

        // GridSectorizer.cpp:43-66.
        int xStart = CellX(minX), yStart = CellY(minY), xEnd = CellX(maxX), yEnd = CellY(maxY);
        for (var x = xStart; x <= xEnd; ++x)
        {
            for (var y = yStart; y <= yEnd; ++y)
                _grid[(y * _gridCellWidthCount) + x].Add(entry);
        }
    }

    /// <summary>
    /// GetEntries: replaces the contents of <paramref name="intersectionList"/> with the entries of every cell the
    /// rectangle touches, cell by cell, x outer and y inner.
    /// </summary>
    public void GetEntries(List<T> intersectionList, float minX, float minY, float maxX, float maxY)
    {
        ArgumentNullException.ThrowIfNull(intersectionList);
        intersectionList.Clear();
        if (_grid.Length == 0)
            return;

        // GridSectorizer.cpp:166-191.
        int xStart = CellX(minX), yStart = CellY(minY), xEnd = CellX(maxX), yEnd = CellY(maxY);
        for (var x = xStart; x <= xEnd; ++x)
        {
            for (var y = yStart; y <= yEnd; ++y)
                intersectionList.AddRange(_grid[(y * _gridCellWidthCount) + x]);
        }
    }

    /// <summary>Clear: empties every cell.</summary>
    public void Clear()
    {
        foreach (var cell in _grid)
            cell.Clear();
    }

    // WorldToCellXOffsetAndClamped (GridSectorizer.cpp:206-212).
    private int CellX(float input) => Math.Min(_gridCellWidthCount - 1, Math.Max((int)((input - _cellOriginX) * _invCellWidth), 0));

    // WorldToCellYOffsetAndClamped (GridSectorizer.cpp:213-219).
    private int CellY(float input) => Math.Min(_gridCellHeightCount - 1, Math.Max((int)((input - _cellOriginY) * _invCellHeight), 0));
}
