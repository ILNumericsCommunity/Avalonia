using System;
using System.Diagnostics;
using System.Drawing;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ILNumerics.Community.Avalonia.Render;
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
/// GDI+ is explicitly disabled to ensure consistent rendering across platforms (incl. Windows).
/// <para>
/// UseBackgroundRendering selects the owner thread at construction. UI mode supports synchronous IDriver access;
/// the global Scene remains application-facing in both modes; background rendering owns its synchronized copy.
/// </para>
/// <para>
/// The <see cref="BeginRenderFrame" />, <see cref="EndRenderFrame" />, <see cref="FPSChanged" />
/// events run on the selected render owner (UI or worker). Worker callbacks must not access UI objects or block on UI dispatch.
/// RenderingFailed, FPSChanged and FramePresented notifications remain on the UI thread in both modes.
/// </para>
/// </remarks>
public sealed partial class Panel : Control, IDriver, IDisposable, IAsyncDisposable
{
    private readonly Clock _clock;
    private readonly GDIDriver _driver;
    private readonly InputController _inputController;

    private WriteableBitmap? _bitmap;
    private bool _disposed;
    private TopLevel? _topLevel;
    private double _renderScaling = 1;

    // Defer disposal requested by a render callback until the driver has returned.
    private bool _rendering;

    static Panel()
    {
        // Disable GDI+ to ensure consistent rendering across all Avalonia platforms (incl. Windows)
        GDIDriver.IsGDIPlusSupported = false;
    }

    /// <summary>Creates a software-rendered, per-monitor-DPI-aware panel.</summary>
    public Panel()
    {
        _clock = new Clock { Running = false };

        IsBackgroundRendering = UseBackgroundRendering && !OperatingSystem.IsBrowser();
        if (IsBackgroundRendering)
        {
            _driver = null!;
            _inputController = null!;
            InitializeBackground();
            return;
        }

        _driver = new GDIDriver(new CommonBackBuffer());
        _driver.FPSChanged += (_, _) => OnFPSChanged();
        _driver.BeginRenderFrame += (_, a) =>
        {
            a.Parameter.DPIScaling = _renderScaling;
            OnBeginRenderFrame(a.Parameter);
        };
        _driver.EndRenderFrame += (_, a) => OnEndRenderFrame(a.Parameter);
        _driver.RenderingFailed += (_, a) => OnRenderingFailed(a.Exception, a.Timeout);

        _inputController = new InputController(this);
    }

    /// <summary>Gets or sets the background color in Avalonia format.</summary>
    /// <value>The background color as an Avalonia <see cref="Color" />.</value>
    public Color Background
    {
        get => new Color(BackColor.A, BackColor.R, BackColor.G, BackColor.B);
        set => BackColor = System.Drawing.Color.FromArgb(value.A, value.R, value.G, value.B);
    }

    #region IDisposable

    /// <summary>
    /// Releases all resources used by the <see cref="Panel" />.
    /// </summary>
    public void Dispose()
    {
        VerifyAccess();
        if (_disposed)
            return;

        _disposed = true;
        if (IsBackgroundRendering)
        {
            DisposeBackground();
            return;
        }
        if (_topLevel != null)
            _topLevel.ScalingChanged -= OnScalingChanged;
        _topLevel = null;

        if (!_rendering)
            DisposeResources();
    }

    private void DisposeResources()
    {
        _bitmap?.Dispose();
        _bitmap = null;
        _driver.Dispose();
    }

    #endregion

    #region Implementation of IDriver

    /// <summary>Raised synchronously on the UI thread when the frames-per-second counter changes.</summary>
    public event EventHandler? FPSChanged;

    /// <summary>Raised on the render owner thread before a frame is rasterized.</summary>
    public event EventHandler<RenderEventArgs>? BeginRenderFrame;

    /// <summary>Raised on the render owner thread after a frame is rasterized.</summary>
    public event EventHandler<RenderEventArgs>? EndRenderFrame;

    /// <summary>Raised synchronously on the UI thread when rendering fails.</summary>
    public event EventHandler<RenderErrorEventArgs>? RenderingFailed;

    /// <inheritdoc />
    [Obsolete("Use Scene.First<Camera>() instead!")]
    public Camera Camera => Driver.Camera;

    /// <inheritdoc />
    public System.Drawing.Color BackColor
    {
        get => IsBackgroundRendering ? _backgroundColor : _driver.BackColor;
        set
        {
            VerifyAccess();
            if (IsBackgroundRendering)
            {
                _backgroundColor = value;
                QueueBackgroundUpdate(driver => driver.BackColor = value);
            }
            else
                _driver.BackColor = value;
        }
    }

    /// <inheritdoc />
    public int FPS => IsBackgroundRendering ? _backgroundFps : _driver.FPS;

    /// <inheritdoc />
    /// <remarks>Invalidates the visual to request a UI-thread render pass.</remarks>
    public void Render(long timeMs)
    {
        VerifyAccess();
        if (IsBackgroundRendering)
        {
            RequestRender();
            return;
        }
        if (!_disposed)
            InvalidateVisual();
    }

    /// <inheritdoc />
    public void Configure()
    {
        VerifyAccess();
        if (!_disposed)
            Scene.Configure();
    }

    /// <inheritdoc />
    public Scene Scene
    {
        get
        {
            VerifyAccess();
            ObjectDisposedException.ThrowIf(_disposed, this);
            return IsBackgroundRendering ? _globalScene! : _driver.Scene;
        }
        set
        {
            VerifyAccess();
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(value);
            if (IsBackgroundRendering)
                AssignGlobalScene(value);
            else
            {
                if (!ReferenceEquals(_driver.Scene, value))
                    _sceneRevision++;
                _driver.Scene = value;
            }
        }
    }

    /// <inheritdoc />
    public Scene LocalScene => Driver.LocalScene;

    /// <inheritdoc />
    public Group SceneSyncRoot => Driver.SceneSyncRoot;

    /// <inheritdoc />
    public Group LocalSceneSyncRoot => Driver.LocalSceneSyncRoot;

    /// <inheritdoc />
    public RectangleF Rectangle
    {
        get => Driver.Rectangle;
        set => Driver.Rectangle = value;
    }

    /// <inheritdoc />
    public bool Supports(Capabilities capability) => Driver.Supports(capability);

    /// <inheritdoc />
    public Matrix4 ViewTransform => Driver.ViewTransform;

    /// <inheritdoc />
    public RendererTypes RendererType => RendererTypes.GDI;

    /// <inheritdoc />
    public Scene GetCurrentScene(long ms = 0) => Driver.GetCurrentScene(ms);

    /// <inheritdoc />
    /// <remarks>
    /// Background mode queues picking to the owner and synchronously waits, including preceding driver work.
    /// Owner-thread calls execute directly; reentry from an active render callback is rejected.
    /// Worker callbacks must never synchronously wait for UI dispatch. Use PickAsync to avoid blocking UI callers.
    /// </remarks>
    public int? PickAt(Point screenCoords, long timeMs)
    {
        if (IsBackgroundRendering)
        {
            if (_worker!.CheckAccess())
                return _worker.PickAt(screenCoords, timeMs);
            if (!Dispatcher.UIThread.CheckAccess())
            {
                if (_disposed)
                    return null;
                return _worker.PickAt(screenCoords, timeMs);
            }
            VerifyAccess();
            if (_disposed || Bounds.Width <= 0 || Bounds.Height <= 0)
                return null;
            UpdateBackgroundGeometry();
            if (!_backgroundGeometry.Active)
                return null;
            return _worker.PickAt(screenCoords, timeMs, _backgroundGeometry.Scaling);
        }
        VerifyAccess();
        if (_disposed || _rendering || Bounds.Width <= 0 || Bounds.Height <= 0)
            return null;

        UpdateRenderSize();
        _rendering = true;
        try
        {
            // Convert logical input coordinates to physical backbuffer pixels.
            return _driver.PickAt(new Point((int) (screenCoords.X * _renderScaling), (int) (screenCoords.Y * _renderScaling)), timeMs);
        }
        finally
        {
            _rendering = false;
            if (_disposed)
                DisposeResources();
        }
    }

    /// <inheritdoc />
    public System.Drawing.Size Size
    {
        get => Driver.Size;
        set => Driver.Size = value;
    }

    /// <inheritdoc />
    public uint Timeout
    {
        get => Driver.Timeout;
        set => Driver.Timeout = value;
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

    #region RenderingPipeline

    /// <summary>
    /// Copies the current ILNumerics backbuffer pixels into the presentation bitmap.
    /// Must be called on the UI thread after rasterization completes.
    /// </summary>
    private WriteableBitmap CopyBackBufferToBitmap(Vector dpi)
    {
        if (_driver.BackBuffer is not CommonBackBuffer backBuffer)
            throw new InvalidOperationException($"BackBuffer is not of type {nameof(CommonBackBuffer)}.");

        // PixelBuffer returns a RetArray; release our local wrapper after the copy.
        using Array<int> pixelBuffer = backBuffer.PixelBuffer;
        var pixelSize = new PixelSize(backBuffer.Size.Width, backBuffer.Size.Height);

        // A monitor change can alter DPI even when the rounded pixel dimensions match.
        if (_bitmap == null || _bitmap.PixelSize != pixelSize || _bitmap.Dpi != dpi)
        {
            var bitmap = new WriteableBitmap(pixelSize, dpi, Platform_PixelFormat.Bgra8888, AlphaFormat.Premul);
            _bitmap?.Dispose();
            _bitmap = bitmap;
        }

        // Copy pixel data to the bitmap
        using (var frameBuffer = _bitmap.Lock())
        {
            var rowBytes = checked(pixelSize.Width * sizeof(int));
            if (frameBuffer.RowBytes < rowBytes || pixelBuffer.S.NumberOfElements < (long) pixelSize.Width * pixelSize.Height)
                throw new InvalidOperationException("The pixel buffer does not match the frame dimensions.");

            var sourcePtr = pixelBuffer.GetHostPointerForRead();
            if (sourcePtr == IntPtr.Zero)
                throw new InvalidOperationException("The pixel buffer is not available in host memory.");

            unsafe
            {
                if (frameBuffer.RowBytes == rowBytes)
                {
                    var byteCount = checked((long) rowBytes * pixelSize.Height);
                    Buffer.MemoryCopy(sourcePtr.ToPointer(), frameBuffer.Address.ToPointer(), byteCount, byteCount);
                }
                else
                {
                    // Preserve destination padding when the platform framebuffer is not tightly packed.
                    for (var row = 0; row < pixelSize.Height; row++)
                    {
                        var source = (byte*) sourcePtr + ((long) row * rowBytes);
                        var destination = (byte*) frameBuffer.Address + ((long) row * frameBuffer.RowBytes);
                        Buffer.MemoryCopy(source, destination, frameBuffer.RowBytes, rowBytes);
                    }
                }
            }
            GC.KeepAlive(pixelBuffer);
        }
        return _bitmap;
    }

    #endregion

    #region Overrides

    /// <inheritdoc />
    /// <remarks>Rasterizes and copies the current scene synchronously on the UI thread.</remarks>
    public override void Render(DrawingContext context)
    {
        VerifyAccess();
        if (IsBackgroundRendering)
        {
            RenderBackground(context);
            return;
        }
        if (_disposed || _rendering || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;
        UpdateRenderSize();

        _rendering = true;
        try
        {
            _driver.Configure();
            var started = Stopwatch.GetTimestamp();
            _driver.Render();
            if (_disposed)
                return;

            var bitmap = CopyBackBufferToBitmap(new Vector(96.0 * _renderScaling, 96.0 * _renderScaling));

            // Source coordinates are physical pixels; destination coordinates are logical units.
            context.DrawImage(bitmap, new Rect(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height), new Rect(0, 0, Bounds.Width, Bounds.Height));
            LastRenderMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            PresentedFrames++;
            QueueFramePresented();
        }
        finally
        {
            _rendering = false;
            if (_disposed)
                DisposeResources();
        }

        base.Render(context);
    }

    /// <inheritdoc />
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);

        UpdateRenderSize();
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (_disposed)
            return;

        _topLevel = TopLevel.GetTopLevel(this);
        if (IsBackgroundRendering)
        {
            AttachBackground();
            return;
        }
        if (_topLevel != null)
            _topLevel.ScalingChanged += OnScalingChanged;

        UpdateRenderSize();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (IsBackgroundRendering)
        {
            DetachBackground();
            base.OnDetachedFromVisualTree(e);
            return;
        }
        if (_topLevel != null)
            _topLevel.ScalingChanged -= OnScalingChanged;
        _topLevel = null;

        base.OnDetachedFromVisualTree(e);
    }

    private void OnScalingChanged(object? sender, EventArgs e)
    {
        UpdateRenderSize();
        InvalidateVisual();
    }

    private void UpdateRenderSize()
    {
        if (IsBackgroundRendering)
        {
            UpdateBackgroundGeometry();
            return;
        }
        _renderScaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1d;
        if (_disposed || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        var size = new System.Drawing.Size(Math.Max(1, checked((int) Math.Ceiling(Bounds.Width * _renderScaling))),
                                           Math.Max(1, checked((int) Math.Ceiling(Bounds.Height * _renderScaling))));
        if (_driver.Size != size)
            _driver.Size = size;
    }

    /// <inheritdoc />
    protected override void OnPointerEntered(PointerEventArgs e)
    {
        if (!_disposed)
            ProcessInput(PointerAction.Enter, MouseEventArgs.Empty);

        base.OnPointerEntered(e);
    }

    /// <inheritdoc />
    protected override void OnPointerExited(PointerEventArgs e)
    {
        if (!_disposed)
            ProcessInput(PointerAction.Leave, MouseEventArgs.Empty);

        base.OnPointerExited(e);
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (CanProcessPointer())
            ProcessInput(PointerAction.Move, PointerEvent(e, Bounds, _clock.TimeMilliseconds));

        base.OnPointerMoved(e);
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (IsBackgroundRendering && CanProcessPointer())
            e.Pointer.Capture(this);
        if (CanProcessPointer())
            ProcessInput(PointerAction.Down, PointerEvent(e, Bounds, _clock.TimeMilliseconds));

        base.OnPointerPressed(e);
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (CanProcessPointer())
            ProcessInput(PointerAction.Up, PointerEvent(e, Bounds, _clock.TimeMilliseconds));
        if (IsBackgroundRendering)
        {
            _releasingBackgroundCapture = true;
            try
            {
                e.Pointer.Capture(null);
            }
            finally
            {
                _releasingBackgroundCapture = false;
            }
        }

        base.OnPointerReleased(e);
    }

    /// <inheritdoc />
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (CanProcessPointer())
            ProcessInput(PointerAction.Wheel, PointerEvent(e, Bounds, _clock.TimeMilliseconds));

        base.OnPointerWheelChanged(e);
    }

    /// <inheritdoc />
    protected override void OnTapped(TappedEventArgs e)
    {
        if (CanProcessPointer())
            ProcessInput(PointerAction.Click, TappedMouseEvent(e, 1, Bounds, _clock.TimeMilliseconds));

        base.OnTapped(e);
    }

    /// <inheritdoc />
    protected override void OnDoubleTapped(TappedEventArgs e)
    {
        if (CanProcessPointer())
            ProcessInput(PointerAction.DoubleClick, TappedMouseEvent(e, 2, Bounds, _clock.TimeMilliseconds));

        base.OnDoubleTapped(e);
    }

    #endregion

    #region MouseEventConversion

    private bool CanProcessPointer() => !_disposed && !_rendering && Bounds.Width > 0 && Bounds.Height > 0;

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
            if (pointerReleasedEventArgs.InitialPressMouseButton == MouseButton.Left)
                buttons = MouseButtons.Left;
            else if (pointerReleasedEventArgs.InitialPressMouseButton == MouseButton.Middle)
                buttons = MouseButtons.Center;
            else if (pointerReleasedEventArgs.InitialPressMouseButton == MouseButton.Right)
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
