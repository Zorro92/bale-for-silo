namespace Bale.Core.Browse;

public sealed record LibraryCollectionCell(LibraryCollection Collection, int Index, int GridIndex, int Row, int Column);
public sealed record LibraryCollectionRow(string? Header, IReadOnlyList<LibraryCollectionCell> Cells);

/// <summary>
/// screens/library/TvLibraryDetailScreen.kt CollectionsTab: non-empty named groups consume a full-span
/// header slot; unnamed buckets have no header. Track both lazy-grid slots and visual rows for focus.
/// </summary>
public sealed class LibraryCollectionsGrid
{
    public const int Columns = 6;
    public IReadOnlyList<LibraryCollectionRow> Rows { get; }
    public IReadOnlyList<LibraryCollectionCell> Cells { get; }

    public LibraryCollectionsGrid(IReadOnlyList<LibraryCollectionSection> sections)
    {
        var rows = new List<LibraryCollectionRow>();
        var cells = new List<LibraryCollectionCell>();
        List<LibraryCollectionCell>? row = null;
        var gridIndex = 0;
        foreach (var section in sections)
        {
            if (section.Collections.Count == 0) continue;
            if (section.Name.Length > 0)
            {
                rows.Add(new LibraryCollectionRow(section.Name, []));
                gridIndex++;
                row = null;
            }

            foreach (var collection in section.Collections)
            {
                if (row is null || row.Count == Columns)
                {
                    row = [];
                    rows.Add(new LibraryCollectionRow(null, row));
                }
                var cell = new LibraryCollectionCell(collection, cells.Count, gridIndex++, rows.Count - 1, row.Count);
                row.Add(cell);
                cells.Add(cell);
            }
        }
        Rows = rows;
        Cells = cells;
    }

    // CollectionsTab's remembered ID is used only while it still exists; otherwise enter at the first card.
    public LibraryCollectionCell? Entry(string? rememberedId) =>
        Cells.FirstOrDefault(cell => cell.Collection.Id == rememberedId) ?? Cells.FirstOrDefault();

    /// <summary>theme/Layout.kt rememberTvGridBringIntoViewSpec: 12% leading / 22% trailing gutters, respecting the shell inset.</summary>
    public static double ScrollDistance(double offset, double size, double viewport, double topInset)
    {
        var start = Math.Max(viewport * 0.12, topInset);
        var end = viewport * 0.78;
        return offset < start ? offset - start : offset + size > end ? offset + size - end : 0;
    }

    public int Move(int index, GridDirection direction)
    {
        if (index < 0 || index >= Cells.Count) return index;
        var cell = Cells[index];
        if (direction is GridDirection.Left or GridDirection.Right)
        {
            var column = cell.Column + (direction == GridDirection.Left ? -1 : 1);
            return column >= 0 && column < Rows[cell.Row].Cells.Count ? Rows[cell.Row].Cells[column].Index : index;
        }

        // Focus skips the full-span headers, but never shifts sideways at a short row's missing cell.
        var step = direction == GridDirection.Up ? -1 : 1;
        for (var row = cell.Row + step; row >= 0 && row < Rows.Count; row += step)
        {
            if (Rows[row].Header is not null) continue;
            return cell.Column < Rows[row].Cells.Count ? Rows[row].Cells[cell.Column].Index : index;
        }
        return index;
    }
}
