using System;

namespace MMUCS.Runtime;

/// <summary>A point in game units (the canvas, or the game's 640×480 page where said).</summary>
public readonly record struct GamePoint(double X, double Y)
{
    public static GamePoint operator +(GamePoint p, GamePoint d) => new(p.X + d.X, p.Y + d.Y);
    public static GamePoint operator -(GamePoint p, GamePoint d) => new(p.X - d.X, p.Y - d.Y);
}

/// <summary>A size in game units.</summary>
public readonly record struct GameSize(double Width, double Height);

/// <summary>A rectangle in game units (right and bottom edges excluded from <see cref="Contains"/>).</summary>
public readonly record struct GameRect(double X, double Y, double Width, double Height)
{
    public GameRect(GamePoint topLeft, GameSize size) : this(topLeft.X, topLeft.Y, size.Width, size.Height) { }

    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public GamePoint TopLeft => new(X, Y);
    public GamePoint BottomRight => new(Right, Bottom);
    public GamePoint Center => new(X + Width / 2, Y + Height / 2);
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(GamePoint p) => p.X >= X && p.X < Right && p.Y >= Y && p.Y < Bottom;
    public GameRect Translate(GamePoint by) => new(X + by.X, Y + by.Y, Width, Height);
}
