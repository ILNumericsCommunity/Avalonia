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

namespace ILNumerics.Community.Avalonia;

/// <summary>
/// Avalonia rendering panel for ILNumerics (based on GDI driver)
/// </summary>
public sealed class Panel : Control, IDriver
{
    private readonly Clock _clock;
    private readonly GDIDriver _driver;
    private readonly InputController _inputController;

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

    #region Implementation of IDriver

    /// <inheritdoc />
    public event EventHandler? FPSChanged;

    private void OnFPSChanged()
    {
        FPSChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public event EventHandler<RenderEventArgs>? BeginRenderFrame;

    private void OnBeginRenderFrame(RenderParameter parameter)
    {
        BeginRenderFrame?.Invoke(this, new RenderEventArgs(parameter));
    }

    /// <inheritdoc />
    public event EventHandler<RenderEventArgs>? EndRenderFrame;

    private void OnEndRenderFrame(RenderParameter parameter)
    {
        EndRenderFrame?.Invoke(this, new RenderEventArgs(parameter));
    }

    /// <inheritdoc />
    public event EventHandler<RenderErrorEventArgs>? RenderingFailed;

    private void OnRenderingFailed(Exception exc, bool timeout = false)
    {
        RenderingFailed?.Invoke(this, new RenderErrorEventArgs { Exception = exc, Timeout = timeout });
    }

    /// <inheritdoc />
    [Obsolete("Use Scene.First<Camera>() instead!")]
    public Camera Camera
    {
        get { return _driver.Camera; }
    }

    /// <inheritdoc />
    public System.Drawing.Color BackColor
    {
        get { return _driver.BackColor; }
        set { _driver.BackColor = value; }
    }

    /// <summary>Set and gets the background color in Avalonia.</summary>
    public Color Background
    {
        get { return new Color(_driver.BackColor.A, _driver.BackColor.R, _driver.BackColor.G, _driver.BackColor.B); }
        set { _driver.BackColor = System.Drawing.Color.FromArgb(value.A, value.R, value.G, value.B); }
    }

    /// <inheritdoc />
    public int FPS
    {
        get { return _driver.FPS; }
    }

    /// <inheritdoc />
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
        get { return _driver.Scene; }
        set { _driver.Scene = value; }
    }

    /// <inheritdoc />
    public Scene LocalScene
    {
        get { return _driver.LocalScene; }
    }

    /// <inheritdoc />
    public Group SceneSyncRoot
    {
        get { return _driver.SceneSyncRoot; }
    }

    /// <inheritdoc />
    public Group LocalSceneSyncRoot
    {
        get { return _driver.LocalSceneSyncRoot; }
    }

    /// <inheritdoc />
    public RectangleF Rectangle
    {
        get { return _driver.Rectangle; }
        set { _driver.Rectangle = value; }
    }

    /// <inheritdoc />
    public bool Supports(Capabilities capability)
    {
        return _driver.Supports(capability);
    }

    /// <inheritdoc />
    public Matrix4 ViewTransform
    {
        get { return _driver.ViewTransform; }
    }

    /// <inheritdoc />
    public RendererTypes RendererType
    {
        get { return RendererTypes.GDI; }
    }

    /// <inheritdoc />
    public Scene GetCurrentScene(long ms = 0)
    {
        return _driver.GetCurrentScene(ms);
    }

    /// <inheritdoc />
    public int? PickAt(System.Drawing.Point screenCoords, long timeMs)
    {
        // Consider high DPI: transform requested logical screen coords into actual back buffer pixel coords
        var scaling = VisualRoot?.RenderScaling ?? 1.0;

        return _driver.PickAt(new System.Drawing.Point((int) (screenCoords.X * scaling), (int) (screenCoords.Y * scaling)), timeMs);
    }

    /// <inheritdoc />
    public System.Drawing.Size Size
    {
        get { return _driver.Size; }
        set { _driver.Size = value; }
    }

    /// <inheritdoc />
    public uint Timeout
    {
        get { return _driver.Timeout; }
        set { _driver.Timeout = value; }
    }

    #endregion

    #region Render

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

            var bitmap = new WriteableBitmap(Platform_PixelFormat.Rgb32, AlphaFormat.Premul, pixelBuffer.GetHostPointerForRead(), pixelSize, dpi, pixelSize.Width * 4);
            context.DrawImage(bitmap, new Rect(0, 0, backBuffer.Size.Width, backBuffer.Size.Height));
        }
        else
            throw new InvalidOperationException($"BackBuffer is not of type {nameof(CommonBackBuffer)}.");

        base.Render(context);
    }

    #endregion

    #region Overrides

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
        var location = new System.Drawing.Point((int) point.Position.X, (int) point.Position.Y);

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
        var location = new System.Drawing.Point((int) point.X, (int) point.Y);

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
