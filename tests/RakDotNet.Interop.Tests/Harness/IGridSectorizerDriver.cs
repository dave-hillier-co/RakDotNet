namespace RakDotNet.Interop.Tests.Harness;

/// <summary>Drives a GridSectorizer of integer entries: the grid* commands of docs/interop-protocol.md.</summary>
public interface IGridSectorizerDriver
{
    /// <summary>Init</summary>
    Task InitGridAsync(float maxCellWidth, float maxCellHeight, GridRectangle world, CancellationToken cancellationToken = default);

    /// <summary>AddEntry of a positive integer.</summary>
    Task AddGridEntryAsync(int entry, GridRectangle bounds, CancellationToken cancellationToken = default);

    /// <summary>GetEntries</summary>
    Task<IReadOnlyList<int>> GetGridEntriesAsync(GridRectangle bounds, CancellationToken cancellationToken = default);

    /// <summary>Clear</summary>
    Task ClearGridAsync(CancellationToken cancellationToken = default);
}

public readonly record struct GridRectangle(float MinX, float MinY, float MaxX, float MaxY);
