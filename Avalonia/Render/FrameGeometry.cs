namespace ILNumerics.Community.Avalonia.Render;

internal readonly record struct FrameGeometry(int Width, int Height, double Scaling, long Generation)
{
    public bool Active => Width > 0 && Height > 0;
}
