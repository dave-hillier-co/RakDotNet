namespace RakDotNet.Plugins.Tests.GridSectorizer;

public sealed class GridSectorizerTests
{
    private readonly GridSectorizer<int> _grid = new();
    private readonly List<int> _found = [];

    [Fact]
    public void Entries_are_returned_once_per_cell_column_by_column()
    {
        // Native RakNet gives the same list for these calls (tools/interop-peer's grid* commands). A 95-unit world with
        // cells of at most 10 has ten cells of 9.5 a side (GridSectorizer.cpp:28-37).
        _grid.Init(10, 10, 0, 0, 95, 95);
        _grid.AddEntry(1, 9.5f, 9.5f, 19, 19);
        _grid.AddEntry(2, -50, -50, 1, 1);
        _grid.AddEntry(3, 28.5f, 0, 200, 5);

        _grid.GetEntries(_found, 0, 0, 95, 95);

        // Entry 1 covers cells 1 and 2 in each direction, entry 2 is clamped into cell 0, and entry 3 runs from cell 3 to
        // the last; GetEntries walks x, then y within it (GridSectorizer.cpp:166-191).
        Assert.Equal([2, 1, 1, 1, 1, 3, 3, 3, 3, 3, 3, 3], _found);
    }

    [Fact]
    public void A_query_returns_the_entries_of_the_cells_it_touches()
    {
        _grid.Init(10, 10, 0, 0, 95, 95);
        _grid.AddEntry(1, 9.5f, 9.5f, 19, 19);
        _grid.AddEntry(3, 28.5f, 0, 200, 5);

        _grid.GetEntries(_found, 9.5f, 9.5f, 9.6f, 9.6f);
        Assert.Equal([1], _found);

        // 19 is past the end of cell 1 in x, but y 0 to 1 is cell 0, which entry 1 does not reach.
        _grid.GetEntries(_found, 18.9f, 0, 19, 1);
        Assert.Empty(_found);

        // 28.4 is still in cell 2, below entry 3's first cell.
        _grid.GetEntries(_found, 28.4f, 0, 28.4f, 1);
        Assert.Empty(_found);
    }

    [Fact]
    public void GetEntries_replaces_what_the_list_held()
    {
        _grid.Init(10, 10, 0, 0, 100, 100);
        _grid.AddEntry(5, 1, 1, 2, 2);
        _found.Add(99);

        _grid.GetEntries(_found, 0, 0, 5, 5);

        Assert.Equal([5], _found);
    }

    [Fact]
    public void Clear_empties_every_cell()
    {
        _grid.Init(10, 10, 0, 0, 100, 100);
        _grid.AddEntry(5, 1, 1, 50, 50);

        _grid.Clear();
        _grid.GetEntries(_found, 0, 0, 100, 100);

        Assert.Empty(_found);
    }

    [Fact]
    public void Init_again_starts_an_empty_grid()
    {
        _grid.Init(10, 10, 0, 0, 100, 100);
        _grid.AddEntry(5, 1, 1, 50, 50);

        _grid.Init(20, 20, 0, 0, 100, 100);
        _grid.GetEntries(_found, 0, 0, 100, 100);

        Assert.Empty(_found);
    }

    [Fact]
    public void An_entry_added_twice_is_returned_twice()
    {
        _grid.Init(10, 10, 0, 0, 100, 100);
        _grid.AddEntry(5, 1, 1, 2, 2);
        _grid.AddEntry(5, 1, 1, 2, 2);

        _grid.GetEntries(_found, 0, 0, 5, 5);

        Assert.Equal([5, 5], _found);
    }

    [Fact]
    public void A_cell_size_must_be_positive_and_a_rectangle_not_empty()
    {
        // RakNet asserts these (GridSectorizer.cpp:26, 45-46).
        Assert.Throws<ArgumentOutOfRangeException>(() => _grid.Init(0, 10, 0, 0, 100, 100));
        Assert.Throws<InvalidOperationException>(() => _grid.AddEntry(1, 0, 0, 1, 1));
        _grid.Init(10, 10, 0, 0, 100, 100);
        Assert.Throws<ArgumentException>(() => _grid.AddEntry(1, 5, 0, 5, 1));
    }
}
