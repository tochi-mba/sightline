namespace Sightline.Core.Sentry;

/// <summary>
/// A small picture of brightness: <see cref="Width"/> by <see cref="Height"/> cells, each 0 to 255. Sentry
/// looks at the live view through one of these, fine enough to see a person cross it and coarse enough that
/// sensor noise and JPEG blocks average out.
/// </summary>
public sealed class LumaGrid
{
    /// <summary>Creates a grid, checking it is one.</summary>
    public LumaGrid(int width, int height, int[] cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException("A grid needs at least one cell.");
        }

        if (cells.Length != width * height)
        {
            throw new ArgumentException($"A {width} by {height} grid has {width * height} cells, not {cells.Length}.");
        }

        if (cells.Any(c => c is < 0 or > 255))
        {
            throw new ArgumentException("Brightness runs from 0 to 255.");
        }

        Width = width;
        Height = height;
        Cells = cells;
    }

    /// <summary>Cells across.</summary>
    public int Width { get; }

    /// <summary>Cells down.</summary>
    public int Height { get; }

    /// <summary>Each cell's brightness, row by row.</summary>
    public IReadOnlyList<int> Cells { get; }
}

/// <summary>How much it takes to count as motion: a cell's change out of 255, and the share of watched cells changing at once.</summary>
public enum Sensitivity
{
    /// <summary>A person close to the camera; ignores pets, leaves and rain.</summary>
    Low,

    /// <summary>A person anywhere in view.</summary>
    Medium,

    /// <summary>Anything that moves, a cat included.</summary>
    High,
}

/// <summary>The numbers behind each <see cref="Sensitivity"/>, the same as the Android app's.</summary>
public static class Sensitivities
{
    /// <summary>How far a cell must move from the learnt background, out of 255.</summary>
    public static int CellChange(this Sensitivity sensitivity) => sensitivity switch
    {
        Sensitivity.Low => 40,
        Sensitivity.High => 18,
        _ => 28,
    };

    /// <summary>What share of the watched cells must change at once.</summary>
    public static double ChangedFraction(this Sensitivity sensitivity) => sensitivity switch
    {
        Sensitivity.Low => 0.08,
        Sensitivity.High => 0.01,
        _ => 0.03,
    };
}

/// <summary>Which cells of the picture Sentry watches: a doorway, not the road beyond it.</summary>
public sealed class Zone
{
    private readonly bool[] watched;

    /// <summary>Creates a zone over a <paramref name="width"/> by <paramref name="height"/> picture.</summary>
    public Zone(int width, int height, bool[] watched)
    {
        ArgumentNullException.ThrowIfNull(watched);
        if (watched.Length != width * height)
        {
            throw new ArgumentException($"A {width} by {height} zone has {width * height} cells.");
        }

        if (!watched.Any(w => w))
        {
            throw new ArgumentException("A zone must watch at least one cell.");
        }

        Width = width;
        Height = height;
        this.watched = watched;
    }

    /// <summary>Cells across.</summary>
    public int Width { get; }

    /// <summary>Cells down.</summary>
    public int Height { get; }

    /// <summary>How many cells are watched.</summary>
    public int Size => watched.Count(w => w);

    /// <summary>Whether the cell at <paramref name="index"/> is watched.</summary>
    public bool Contains(int index) => watched[index];

    /// <summary>Every cell of a picture.</summary>
    public static Zone All(int width, int height) => new(width, height, Enumerable.Repeat(true, width * height).ToArray());

    /// <summary>The cells inside a rectangle given as fractions of the picture, 0 to 1 from the top left.</summary>
    public static Zone Rectangle(int width, int height, double left, double top, double right, double bottom)
    {
        var inside = new[] { left, top, right, bottom }.All(v => v is >= 0 and <= 1);
        if (!inside || left >= right || top >= bottom)
        {
            throw new ArgumentException("A zone is a rectangle inside the picture.");
        }

        return new Zone(width, height, Enumerable.Range(0, width * height).Select(index =>
        {
            var x = ((index % width) + 0.5) / width;
            var y = ((index / width) + 0.5) / height;
            return x >= left && x <= right && y >= top && y <= bottom;
        }).ToArray());
    }
}

/// <summary>
/// Tells motion from a still scene with no machine learning: a slowly learnt background follows slow change,
/// a shift shared by the whole picture (the camera adjusting its exposure) is taken out before cells are
/// compared, and what is left, a part of the picture changing on its own, is motion. The Android app's
/// detector, line for line.
/// </summary>
public sealed class MotionDetector(Sensitivity sensitivity, Zone? zone = null)
{
    /// <summary>How much of each grid the background takes in.</summary>
    private const double LearningRate = 0.05;

    private double[]? background;
    private int[] watched = [];

    /// <summary>Compares <paramref name="grid"/> with the background, then learns from it.</summary>
    /// <returns>The share of watched cells that changed; the first grid, and one of a new size, return 0.</returns>
    public double Observe(LumaGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        if (zone is not null && (zone.Width != grid.Width || zone.Height != grid.Height))
        {
            throw new ArgumentException("The zone was drawn on a picture of a different size.");
        }

        var learnt = background;
        if (learnt is null || learnt.Length != grid.Cells.Count)
        {
            background = grid.Cells.Select(c => (double)c).ToArray();
            watched = Enumerable.Range(0, grid.Cells.Count).Where(i => zone?.Contains(i) != false).ToArray();
            return 0;
        }

        var shift = watched.Sum(i => grid.Cells[i] - learnt[i]) / watched.Length;
        var changed = watched.Count(i => Math.Abs(grid.Cells[i] - learnt[i] - shift) >= sensitivity.CellChange());
        for (var i = 0; i < learnt.Length; i++)
        {
            learnt[i] += LearningRate * (grid.Cells[i] - learnt[i]);
        }

        return (double)changed / watched.Length;
    }

    /// <summary>Whether <paramref name="score"/> is motion at this sensitivity.</summary>
    public bool IsMotion(double score) => score >= sensitivity.ChangedFraction();

    /// <summary>Forgets the background, so the next grid starts it again.</summary>
    public void Reset() => background = null;
}
