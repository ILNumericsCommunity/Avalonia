using System;
using System.Drawing;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ILNumerics.Drawing;
using Color = Avalonia.Media.Color;
using Control = Avalonia.Controls.Control;
using Platform_PixelFormat = Avalonia.Platform.PixelFormat;
using Point = System.Drawing.Point;

namespace ILNumerics.Community.Avalonia;

/// <summary>
/// Avalonia rendering panel for ILNumerics (based on GDI driver).
/// </summary>
/// <remarks>
/// This panel uses the GDI driver for rendering and supports all Avalonia platforms.
/// GDI+ is explicitly disabled to ensure consistent rendering across platforms.
/// </remarks>
public sealed class Panel : Control, IDriver, IDisposable
{
    private readonly Clock _clock;
    private readonly GDIDriver _driver;
    private readonly InputController _inputController;

    private WriteableBitmap? _bitmap;
    private bool _disposed;

    static Panel()
    {
        // Disable GDI+ to ensure consistent rendering across all Avalonia platforms (incl. Windows)
        GDIDriver.IsGDIPlusSupported = false;
    }

    public Panel()
    {
        _clock = new Clock { Running = false };

        _driver = new GDIDriver(new CommonBackBuffer());
        _driver.FPSChanged += (_, _) => OnFPSChanged();
        _driver.BeginRenderFrame += (_, a) => OnBeginRenderFrame(a.Parameter);
        _driver.EndRenderFrame += (_, a) => OnEndRenderFrame(a.Parameter);
        _driver.RenderingFailed += (_, a) => OnRenderingFailed(a.Exception, a.Timeout);

        _inputController = new InputController(this);
        Tapped += (_, a) => OnTapped(a);
        DoubleTapped += (_, a) => OnDoubleTapped(a);
    }

    /// <summary>Gets or sets the background color in Avalonia format.</summary>
    /// <value>The background color as an Avalonia <see cref="Color" />.</value>
    public Color Background
    {
        get => new(_driver.BackColor.A, _driver.BackColor.R, _driver.BackColor.G, _driver.BackColor.B);
        set => _driver.BackColor = System.Drawing.Color.FromArgb(value.A, value.R, value.G, value.B);
    }

    #region IDisposable

    /// <summary>
    /// Releases all resources used by the <see cref="Panel" />.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _bitmap?.Dispose();
        _bitmap = null;

        _driver?.Dispose();

        _disposed = true;
    }

    #endregion

    #region Implementation of IDriver

    /// <inheritdoc />
    public event EventHandler? FPSChanged;

    /// <inheritdoc />
    public event EventHandler<RenderEventArgs>? BeginRenderFrame;

    /// <inheritdoc />
    public event EventHandler<RenderEventArgs>? EndRenderFrame;

    /// <inheritdoc />
    public event EventHandler<RenderErrorEventArgs>? RenderingFailed;

    /// <inheritdoc />
    [Obsolete("Use Scene.First<Camera>() instead!")]
    public Camera Camera => _driver.Camera;

    /// <inheritdoc />
    public System.Drawing.Color BackColor
    {
        get => _driver.BackColor;
        set => _driver.BackColor = value;
    }

    /// <inheritdoc />
    public int FPS => _driver.FPS;

    /// <inheritdoc />
    /// <remarks>This method triggers an Avalonia visual invalidation to request a re-render.</remarks>
    public void Render(long timeMs)
    {
        InvalidateVisual();
    }

    /// <inheritdoc />
    public void Configure()
    {
        _driver.Configure();
    }

    /// <inheritdoc />
    public Scene Scene
    {
        get => _driver.Scene;
        set => _driver.Scene = value;
    }

    /// <inheritdoc />
    public Scene LocalScene => _driver.LocalScene;

    /// <inheritdoc />
    public Group SceneSyncRoot => _driver.SceneSyncRoot;

    /// <inheritdoc />
    public Group LocalSceneSyncRoot => _driver.LocalSceneSyncRoot;

    /// <inheritdoc />
    public RectangleF Rectangle
    {
        get => _driver.Rectangle;
        set => _driver.Rectangle = value;
    }

    /// <inheritdoc />
    public bool Supports(Capabilities capability) => _driver.Supports(capability);

    /// <inheritdoc />
    public Matrix4 ViewTransform => _driver.ViewTransform;

    /// <inheritdoc />
    public RendererTypes RendererType => RendererTypes.GDI;

    /// <inheritdoc />
    public Scene GetCurrentScene(long ms = 0) => _driver.GetCurrentScene(ms);

    /// <inheritdoc />
    public int? PickAt(Point screenCoords, long timeMs)
    {
        // Consider high DPI: transform requested logical screen coords into actual back buffer pixel coords
        var scaling = VisualRoot?.RenderScaling ?? 1.0;

        return _driver.PickAt(new Point((int) (screenCoords.X * scaling), (int) (screenCoords.Y * scaling)), timeMs);
    }

    /// <inheritdoc />
    public System.Drawing.Size Size
    {
        get => _driver.Size;
        set => _driver.Size = value;
    }

    /// <inheritdoc />
    public uint Timeout
    {
        get => _driver.Timeout;
        set => _driver.Timeout = value;
    }

    #endregion

    private void OnFPSChanged()
    {
        FPSChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnBeginRenderFrame(RenderParameter parameter)
    {
        BeginRenderFrame?.Invoke(this, new RenderEventArgs(parameter));
    }

    private void OnEndRenderFrame(RenderParameter parameter)
    {
        EndRenderFrame?.Invoke(this, new RenderEventArgs(parameter));
    }

    private void OnRenderingFailed(Exception exc, bool timeout = false)
    {
        RenderingFailed?.Invoke(this, new RenderErrorEventArgs { Exception = exc, Timeout = timeout });
    }

    #region Overrides

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        // Safeguard: do not render if back buffer size is empty
        // (panel detached from tree or not yet properly initialized)
        if (_driver.BackBuffer.Size.IsEmpty)
            return;

        // Render using 'GDI' driver (now also works on non-Windows platforms)
        _driver.Configure();
        _driver.Render();

        // Copy pixel buffer to Avalonia WriteableBitmap and draw it
        if (_driver.BackBuffer is CommonBackBuffer backBuffer)
        {
            Array<int> pixelBuffer = backBuffer.PixelBuffer;
            var pixelSize = new PixelSize(backBuffer.Size.Width, backBuffer.Size.Height);
            var scaling = VisualRoot?.RenderScaling ?? 1.0;
            var dpi = new Vector(96.0 / scaling, 96.0 / scaling);

            // Recreate bitmap only when size changes to avoid allocations per frame
            if (_bitmap == null || _bitmap.PixelSize != pixelSize)
            {
                _bitmap?.Dispose();
                _bitmap = new WriteableBitmap(pixelSize, dpi, Platform_PixelFormat.Bgra8888, AlphaFormat.Premul);
            }

            // Copy pixel data to the bitmap
            using (var frameBuffer = _bitmap.Lock())
            {
                var byteCount = pixelSize.Width * pixelSize.Height * 4;
                var sourcePtr = pixelBuffer.GetHostPointerForRead();
                unsafe
                {
                    Buffer.MemoryCopy(sourcePtr.ToPointer(), frameBuffer.Address.ToPointer(), byteCount, byteCount);
                }
            }

            context.DrawImage(_bitmap, new Rect(0, 0, backBuffer.Size.Width, backBuffer.Size.Height));
        }
        else
            throw new InvalidOperationException($"BackBuffer is not of type {nameof(CommonBackBuffer)}.");

        base.Render(context);
    }

    /// <inheritdoc />
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        // Consider high DPI: transform requested logical size into actual back buffer pixel size
        var scaling = VisualRoot?.RenderScaling ?? 1.0;
        var scaledSize = new System.Drawing.Size((int) (scaling * e.NewSize.Width), (int) (scaling * e.NewSize.Height));
        if (scaledSize.Width <= 0 || scaledSize.Height <= 0)
            return;

        // Update driver size (also updates back buffer size)
        _driver.Size = scaledSize;

        base.OnSizeChanged(e);
    }

    /// <inheritdoc />
    protected override void OnPointerEntered(PointerEventArgs e)
    {
        _inputController.OnMouseEnter(MouseEventArgs.Empty);

        base.OnPointerEntered(e);
    }

    /// <inheritdoc />
    protected override void OnPointerExited(PointerEventArgs e)
    {
        _inputController.OnMouseLeave(MouseEventArgs.Empty);

        base.OnPointerExited(e);
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        _inputController.OnMouseMove(PointerEvent(e, Bounds, _clock.TimeMilliseconds));

        base.OnPointerMoved(e);
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        _inputController.OnMouseDown(PointerEvent(e, Bounds, _clock.TimeMilliseconds));

        base.OnPointerPressed(e);
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _inputController.OnMouseUp(PointerEvent(e, Bounds, _clock.TimeMilliseconds));

        base.OnPointerReleased(e);
    }

    /// <inheritdoc />
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        _inputController.OnMouseWheel(PointerEvent(e, Bounds, _clock.TimeMilliseconds));

        base.OnPointerWheelChanged(e);
    }

    private void OnTapped(TappedEventArgs e)
    {
        _inputController.OnMouseClick(TappedMouseEvent(e, 1, Bounds, _clock.TimeMilliseconds));
    }

    private void OnDoubleTapped(TappedEventArgs e)
    {
        _inputController.OnMouseDoubleClick(TappedMouseEvent(e, 2, Bounds, _clock.TimeMilliseconds));
    }

    #endregion

    #region MouseEventConversion

    private MouseEventArgs PointerEvent(PointerEventArgs args, Rect rect, long timeMS)
    {
        // Get pointer position (normalized to current control size)
        var point = args.GetCurrentPoint(this);
        var location = new Point((int) point.Position.X, (int) point.Position.Y);

        var x = point.Position.X / rect.Width;
        var y = point.Position.Y / rect.Height;
        var locationF = new PointF((float) x, (float) y);

        // Key modifiers
        var shift = (args.KeyModifiers & KeyModifiers.Shift) != 0;
        var alt = (args.KeyModifiers & KeyModifiers.Alt) != 0;
        var ctrl = (args.KeyModifiers & KeyModifiers.Control) != 0;

        var buttons = MouseButtons.None;
        if (args is PointerReleasedEventArgs pointerReleasedEventArgs)
        {
            // Use the initially pressed button for released events
            if (pointerReleasedEventArgs.InitialPressMouseButton.HasFlag(MouseButton.Left))
                buttons = MouseButtons.Left;
            else if (pointerReleasedEventArgs.InitialPressMouseButton.HasFlag(MouseButton.Middle))
                buttons = MouseButtons.Center;
            else if (pointerReleasedEventArgs.InitialPressMouseButton.HasFlag(MouseButton.Right))
                buttons = MouseButtons.Right;
        }
        else
        {
            // Use the currently pressed button for other events
            if (point.Properties.IsLeftButtonPressed)
                buttons = MouseButtons.Left;
            else if (point.Properties.IsMiddleButtonPressed)
                buttons = MouseButtons.Center;
            else if (point.Properties.IsRightButtonPressed)
                buttons = MouseButtons.Right;
        }

        // Handle wheel events separately
        if (args is PointerWheelEventArgs pointerWheelEventArgs)
            return new MouseEventArgs(locationF, location, shift, alt, ctrl) { TimeMS = timeMS, Button = buttons, Delta = (int) pointerWheelEventArgs.Delta.Y };

        return new MouseEventArgs(locationF, location, shift, alt, ctrl) { TimeMS = timeMS, Button = buttons };
    }

    private MouseEventArgs TappedMouseEvent(TappedEventArgs args, int clickCount, Rect rect, long timeMS)
    {
        // Get pointer position (normalized to current control size)
        var point = args.GetPosition(this);
        var location = new Point((int) point.X, (int) point.Y);

        var x = point.X / rect.Width;
        var y = point.Y / rect.Height;
        var locationF = new PointF((float) x, (float) y);

        // Key modifiers
        var shift = (args.KeyModifiers & KeyModifiers.Shift) != 0;
        var alt = (args.KeyModifiers & KeyModifiers.Alt) != 0;
        var ctrl = (args.KeyModifiers & KeyModifiers.Control) != 0;

        return new MouseEventArgs(locationF, location, shift, alt, ctrl) { TimeMS = timeMS, Button = MouseButtons.Left, Clicks = clickCount };
    }

    #endregion
}
