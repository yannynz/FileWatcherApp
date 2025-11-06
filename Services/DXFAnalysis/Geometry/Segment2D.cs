namespace FileWatcherApp.Services.DXFAnalysis.Geometry;

/// <summary>
/// Represents a line segment extracted from DXF entities for broad-phase operations.
/// </summary>
public readonly record struct Segment2D(Point2D Start, Point2D End, string Layer, bool IsCurve, double? RadiusHint)
{
    /// <summary>Gets the axis aligned bounding box for the segment.</summary>
    public BoundingBox2D Bounds
    {
        get
        {
            var minX = Math.Min(Start.X, End.X);
            var maxX = Math.Max(Start.X, End.X);
            var minY = Math.Min(Start.Y, End.Y);
            var maxY = Math.Max(Start.Y, End.Y);
            return new BoundingBox2D(minX, minY, maxX, maxY);
        }
    }
}

/// <summary>
/// Represents a 2D point used by the DXF analysis pipeline.
/// </summary>
public readonly record struct Point2D(double X, double Y)
{
    /// <summary>Computes the squared distance to another point.</summary>
    public double DistanceSquared(Point2D other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        return dx * dx + dy * dy;
    }
}

/// <summary>
/// Represents an axis aligned bounding box.
/// </summary>
public readonly record struct BoundingBox2D(double MinX, double MinY, double MaxX, double MaxY)
{
    /// <summary>Determines whether the bounding box intersects another.</summary>
    public bool Intersects(in BoundingBox2D other)
    {
        return !(other.MinX > MaxX ||
                 other.MaxX < MinX ||
                 other.MinY > MaxY ||
                 other.MaxY < MinY);
    }

    /// <summary>Gets the diagonal length of the bounding box.</summary>
    public double DiagonalLength()
    {
        var dx = MaxX - MinX;
        var dy = MaxY - MinY;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
