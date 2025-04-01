using System;
using System.Drawing;
using System.Drawing.Imaging;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ILNumerics.Drawing;
using Color = Avalonia.Media.Color;
using Control = Avalonia.Controls.Control;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
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

    public Panel()
    {
        _clock = new Clock { Running = false };

        _driver = new GDIDriver(new BackBuffer { Rectangle = new Rectangle(0, 0, (int) Bounds.Width, (int) Bounds.Height) });
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
        return _driver.PickAt(screenCoords, timeMs);
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
        _driver.Configure();
        _driver.Render();

        BitmapData? srcBmpData = null;
        try
        {
            srcBmpData = _driver.BackBuffer.Bitmap.LockBits(new Rectangle(0, 0, (int) Bounds.Width, (int) Bounds.Height),
                                                            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var avWritableBitmap = CreateBitmapFromArgbPixelData(srcBmpData.Scan0, srcBmpData.Width, srcBmpData.Height);

            context.DrawImage(avWritableBitmap, new Rect(0, 0, (int) Bounds.Width, (int) Bounds.Height));
            base.Render(context);
        }
        finally
        {
            if (srcBmpData != null)
                _driver.BackBuffer.Bitmap.UnlockBits(srcBmpData);
        }
    }

    private static unsafe WriteableBitmap CreateBitmapFromArgbPixelData(nint pixelDateArgb, int pixelWidth, int pixelHeight)
    {
        var dpi = new Vector(96, 96); // Standard may need to change on some devices 
        var bitmap = new WriteableBitmap(new PixelSize(pixelWidth, pixelHeight), dpi, Platform_PixelFormat.Bgra8888, AlphaFormat.Premul);

        var lenBytes = pixelWidth * pixelHeight * 4;
        using var frameBuffer = bitmap.Lock();
        Buffer.MemoryCopy((void*) pixelDateArgb, (void*) frameBuffer.Address, lenBytes, lenBytes);

        return bitmap;
    }

    #endregion

    #region PointerEventHandlers

    /// <inheritdoc />
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        if (Bounds.Width > 0 && Bounds.Height > 0)
        {
            _driver.Size = new System.Drawing.Size((int) Bounds.Width, (int) Bounds.Height);
            InvalidateVisual();
        }

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
        _inputController.OnMouseMove(PointerMovedMouseEvent(e, Bounds, _clock.TimeMilliseconds));

        base.OnPointerMoved(e);
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        _inputController.OnMouseDown(PointerPressedMouseEvent(e, Bounds, _clock.TimeMilliseconds));

        base.OnPointerPressed(e);
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _inputController.OnMouseUp(PointerReleasedMouseEvent(e, Bounds, _clock.TimeMilliseconds));

        base.OnPointerReleased(e);
    }

    /// <inheritdoc />
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        _inputController.OnMouseWheel(WheelMouseEvent(e, Bounds, _clock.TimeMilliseconds));

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

    private MouseEventArgs PointerMovedMouseEvent(PointerEventArgs args, Rect rect, long timeMS)
    {
        var point = args.GetCurrentPoint(this);
        var location = new System.Drawing.Point((int) point.Position.X, (int) point.Position.Y);

        var x = point.Position.X / rect.Width;
        var y = point.Position.Y / rect.Height;
        var locationF = new PointF((float) x, (float) y);

        var shift = (args.KeyModifiers & KeyModifiers.Shift) != 0;
        var alt = (args.KeyModifiers & KeyModifiers.Alt) != 0;
        var ctrl = (args.KeyModifiers & KeyModifiers.Control) != 0;

        var buttons = MouseButtons.None;
        if (point.Properties.IsLeftButtonPressed)
            buttons = MouseButtons.Left;
        else if (point.Properties.IsMiddleButtonPressed)
            buttons = MouseButtons.Center;
        else if (point.Properties.IsRightButtonPressed)
            buttons = MouseButtons.Right;

        return new MouseEventArgs(locationF, location, shift, alt, ctrl) { TimeMS = timeMS, Button = buttons };
    }

    private MouseEventArgs PointerPressedMouseEvent(PointerPressedEventArgs args, Rect rect, long timeMS)
    {
        var point = args.GetCurrentPoint(this);
        var location = new System.Drawing.Point((int) point.Position.X, (int) point.Position.Y);

        var x = point.Position.X / rect.Width;
        var y = point.Position.Y / rect.Height;
        var locationF = new PointF((float) x, (float) y);

        var shift = (args.KeyModifiers & KeyModifiers.Shift) != 0;
        var alt = (args.KeyModifiers & KeyModifiers.Alt) != 0;
        var ctrl = (args.KeyModifiers & KeyModifiers.Control) != 0;

        var buttons = MouseButtons.None;
        if (point.Properties.IsLeftButtonPressed)
            buttons = MouseButtons.Left;
        else if (point.Properties.IsMiddleButtonPressed)
            buttons = MouseButtons.Center;
        else if (point.Properties.IsRightButtonPressed)
            buttons = MouseButtons.Right;

        return new MouseEventArgs(locationF, location, shift, alt, ctrl) { TimeMS = timeMS, Button = buttons, Clicks = args.ClickCount };
    }

    private MouseEventArgs PointerReleasedMouseEvent(PointerReleasedEventArgs args, Rect rect, long timeMS)
    {
        var point = args.GetCurrentPoint(this);
        var location = new System.Drawing.Point((int) point.Position.X, (int) point.Position.Y);

        var x = point.Position.X / rect.Width;
        var y = point.Position.Y / rect.Height;
        var locationF = new PointF((float) x, (float) y);

        var shift = (args.KeyModifiers & KeyModifiers.Shift) != 0;
        var alt = (args.KeyModifiers & KeyModifiers.Alt) != 0;
        var ctrl = (args.KeyModifiers & KeyModifiers.Control) != 0;

        var buttons = MouseButtons.None;
        if (point.Properties.IsLeftButtonPressed)
            buttons = MouseButtons.Left;
        else if (point.Properties.IsMiddleButtonPressed)
            buttons = MouseButtons.Center;
        else if (point.Properties.IsRightButtonPressed)
            buttons = MouseButtons.Right;

        return new MouseEventArgs(locationF, location, shift, alt, ctrl) { TimeMS = timeMS, Button = buttons };
    }

    private MouseEventArgs WheelMouseEvent(PointerWheelEventArgs args, Rect rect, long timeMS)
    {
        var point = args.GetCurrentPoint(this);
        var location = new System.Drawing.Point((int) point.Position.X, (int) point.Position.Y);

        var x = point.Position.X / rect.Width;
        var y = point.Position.Y / rect.Height;
        var locationF = new PointF((float) x, (float) y);

        var shift = (args.KeyModifiers & KeyModifiers.Shift) != 0;
        var alt = (args.KeyModifiers & KeyModifiers.Alt) != 0;
        var ctrl = (args.KeyModifiers & KeyModifiers.Control) != 0;

        return new MouseEventArgs(locationF, location, shift, alt, ctrl) { TimeMS = timeMS, Delta = (int) args.Delta.Y };
    }

    private MouseEventArgs TappedMouseEvent(TappedEventArgs args, int clickCount, Rect rect, long timeMS)
    {
        var point = args.GetPosition(this);
        var location = new System.Drawing.Point((int) point.X, (int) point.Y);

        var x = point.X / rect.Width;
        var y = point.Y / rect.Height;
        var locationF = new PointF((float) x, (float) y);

        var shift = (args.KeyModifiers & KeyModifiers.Shift) != 0;
        var alt = (args.KeyModifiers & KeyModifiers.Alt) != 0;
        var ctrl = (args.KeyModifiers & KeyModifiers.Control) != 0;

        return new MouseEventArgs(locationF, location, shift, alt, ctrl) { TimeMS = timeMS, Button = MouseButtons.Left, Clicks = clickCount };
    }

    #endregion
}
