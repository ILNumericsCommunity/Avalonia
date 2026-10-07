namespace ILNumerics.Community.Avalonia.Render;

internal sealed record CompletedFrame(byte[] Pixels, FrameGeometry Geometry, long Sequence, double RenderMilliseconds, long RequestedAt,
                                      long SceneRevision);
