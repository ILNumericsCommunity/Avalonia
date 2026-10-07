using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ILNumerics.Community.Avalonia.Render;
using ILNumerics.Core.DeviceManagement;
using ILNumerics.Drawing;
using Color = System.Drawing.Color;
using Point = Avalonia.Point;

namespace ILNumerics.Community.Avalonia;

public sealed partial class Panel
{
    #region State

    private static int _useBackgroundRendering;
    private BackgroundRenderWorker? _worker;
    private readonly List<Visual> _backgroundAncestors = new List<Visual>();
    private FrameGeometry _backgroundGeometry;
    private Scene? _globalScene;
    private Task _sceneAssignment = Task.CompletedTask;
    private long _sceneRevision;

    #endregion

    #region Presentation state

    private int _notificationPending;
    private Color _backgroundColor = Color.White;
    private int _backgroundFps;
    private long _fpsStarted;
    private long _fpsFrames;
    private bool _releasingBackgroundCapture;
    private bool _frameNotificationPending;

    #endregion

    #region Properties and events

    /// <summary>Selects background rendering for newly constructed panels. Defaults to false; browsers use the UI path.</summary>
    /// <remarks>Existing panels keep their mode. Global scene assignment/configuration works in both modes.</remarks>
    public static bool UseBackgroundRendering
    {
        get => Volatile.Read(ref _useBackgroundRendering) != 0;
        set => Volatile.Write(ref _useBackgroundRendering, value ? 1 : 0);
    }

    /// <summary>The mode captured at construction, including platform fallback.</summary>
    public bool IsBackgroundRendering { get; }

    /// <summary>Raised on the UI thread outside the render pass after bitmap copying, before compositor display/scanout.</summary>
    /// <remarks>UI-mode notifications are posted and coalesced; disposal cancels pending notifications.</remarks>
    public event EventHandler? FramePresented;

    /// <summary>Copied frames in either mode.</summary>
    public long PresentedFrames { get; private set; }

    /// <summary>Completed rasterizations, including obsolete background geometry.</summary>
    public long RenderedFrames => _worker?.RenderedFrames ?? PresentedFrames;

    /// <summary>Last background render/staging time or UI render/copy time.</summary>
    public double LastRenderMilliseconds { get; private set; }

    /// <summary>Background request-to-copy latency; zero in synchronous mode.</summary>
    public double LastFrameLatencyMilliseconds { get; private set; }

    /// <summary>Background command queue high-water mark, bounded at 256.</summary>
    public int QueueHighWater => _worker?.QueueHighWater ?? 0;

    /// <summary>Coalesced background pointer moves.</summary>
    public long CoalescedPointerMoves => _worker?.CoalescedMoves ?? 0;

    /// <summary>Worker cleanup completion. Never synchronously wait on the UI thread.</summary>
    public Task Completion => _worker?.Completion ?? Task.CompletedTask;

    #endregion

    #region Notifications and access

    private void QueueFramePresented()
    {
        if (_frameNotificationPending || _disposed)
            return;

        _frameNotificationPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _frameNotificationPending = false;
            if (!_disposed)
                FramePresented?.Invoke(this, EventArgs.Empty);
        }, DispatcherPriority.Background);
    }

    private GDIDriver Driver
    {
        get
        {
            VerifyAccess();
            ObjectDisposedException.ThrowIf(_disposed, this);
            
            if (IsBackgroundRendering)
                throw new InvalidOperationException("Driver state belongs to a worker. Use UpdateDriverSceneAsync for its synchronized copy or PickAsync for picking.");

            return _driver;
        }
    }

    #endregion

    #region Operations

    /// <summary>Optional convenience: constructs/configures a global scene on the UI thread, awaits assignment and requests a frame.</summary>
    public Task SetSceneAsync(Func<Scene> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        try
        {
            VerifyAccess();

            var scene = factory();
            scene.Configure();
            Scene = scene;
            
            RequestRender();

            return _sceneAssignment;
        }
        catch (Exception error)
        {
            return Task.FromException(error);
        }
    }

    /// <summary>
    /// Convenience addition to the ILNumerics driver API: manipulates the driver's synchronized scene copy
    /// on its render-owner thread and requests a frame. Completion precedes presentation.
    /// </summary>
    /// <remarks>
    /// Use the supplied driver's SceneSyncRoot or LocalSceneSyncRoot for panel-specific state such as the camera.
    /// Ordinary global-scene changes use Panel.Scene and Configure instead. Do not mutate/configure/replace the
    /// global scene here, retain driver references, or synchronously dispatch to UI from a worker callback.
    /// </remarks>
    public Task UpdateDriverSceneAsync(Action<GDIDriver> update)
    {
        ArgumentNullException.ThrowIfNull(update);

        if (IsBackgroundRendering)
        {
            return _worker!.InvokeAsync(driver =>
            {
                update(driver);

                return true;
            });
        }

        try
        {
            update(Driver);
            _driver.Configure();
            
            RequestRender();

            return Task.CompletedTask;
        }
        catch (Exception error)
        {
            return Task.FromException(error);
        }
    }

    /// <summary>Invalidates the panel and requests fresh scene content in either rendering mode.</summary>
    /// <remarks>
    /// Avalonia's Visual.InvalidateVisual is non-virtual. This Panel-specific method hides it; calls through a
    /// Visual or Control reference only invalidate Avalonia presentation. Call on the UI thread after Configure.
    /// </remarks>
    public new void InvalidateVisual()
    {
        VerifyAccess();
        if (_disposed)
            return;

        if (IsBackgroundRendering)
            _worker!.RequestRender();

        base.InvalidateVisual();
    }

    /// <summary>Requests fresh content in either rendering mode.</summary>
    public void RequestRender()
    {
        if (IsBackgroundRendering)
            _worker!.RequestRender();
        else
            Render(0);
    }

    /// <summary>Picks logical coordinates on the owner thread. The result describes state at execution time.</summary>
    public Task<int?> PickAsync(Point point)
    {
        VerifyAccess();
        
        if (_disposed || Bounds.Width <= 0 || Bounds.Height <= 0)
            return Task.FromResult<int?>(null);
        
        if (!IsBackgroundRendering)
            return Task.FromResult(PickAt(new System.Drawing.Point((int) point.X, (int) point.Y), 0));
        
        if (!_backgroundGeometry.Active)
            return Task.FromResult<int?>(null);

        UpdateBackgroundGeometry();

        return _worker!.PickAsync(new System.Drawing.Point((int) point.X, (int) point.Y), 0, _backgroundGeometry.Scaling);
    }

    #endregion

    #region Initialization and scenes

    /// <summary>Starts UI-thread shutdown and asynchronously awaits worker cleanup.</summary>
    public ValueTask DisposeAsync()
    {
        Dispose();

        return new ValueTask(Completion);
    }

    private void InitializeBackground()
    {
        // ILNumerics 7.4.16 creates its process-wide host pool with MaxSize=0 when initialized off
        // its main thread. Initialize on UI before starting the owner to preserve default pool reuse.
        _ = DeviceManager.GetDevice(0).MemoryPool;

        _fpsStarted = Stopwatch.GetTimestamp();
        _worker = new BackgroundRenderWorker(() =>
        {
            if (Interlocked.Exchange(ref _notificationPending, 1) == 0)
                Dispatcher.UIThread.Post(PresentBackground, DispatcherPriority.Background);
        });

        var scene = new Scene();
        scene.Configure();
        AssignGlobalScene(scene);
        
        QueueBackgroundUpdate(driver =>
        {
            driver.BeginRenderFrame += (_, e) => OnBeginRenderFrame(e.Parameter);
            driver.EndRenderFrame += (_, e) => OnEndRenderFrame(e.Parameter);
        });
    }

    private void AssignGlobalScene(Scene scene)
    {
        if (ReferenceEquals(scene, _globalScene))
            return;

        // Panel ownership keeps the published global scene alive, including before the worker applies it.
        lock (BackgroundRenderWorker.SceneReferenceGate)
        {
            scene.IncreaseReference();
        }

        var previous = _globalScene;
        _globalScene = scene;
        _sceneRevision++;
        _sceneAssignment = _worker!.AssignSceneAsync(scene, _sceneRevision);
        if (previous != null)
        {
            lock (BackgroundRenderWorker.SceneReferenceGate)
            {
                previous.DecreaseReference();
            }
        }

        ObserveAssignment(_sceneAssignment);
    }

    private async void ObserveAssignment(Task assignment)
    {
        try
        {
            await assignment;
        }
        catch (Exception error)
        {
            if (!_disposed)
                OnRenderingFailed(error);
        }
    }

    private async void QueueBackgroundUpdate(Action<GDIDriver> update)
    {
        try
        {
            await UpdateDriverSceneAsync(update);
        }
        catch (Exception error)
        {
            if (!_disposed)
                OnRenderingFailed(error);
        }
    }

    #endregion

    #region Presentation

    private void PresentBackground()
    {
        if (_disposed || Interlocked.Exchange(ref _notificationPending, 0) == 0)
            return;

        UpdateBackgroundGeometry();
        var (frame, error) = _worker!.TakeNotification();
        var presented = false;

        try
        {
            if (frame != null && frame.SceneRevision == _sceneRevision && frame.Geometry == _backgroundGeometry && _backgroundGeometry.Active)
            {
                var size = new PixelSize(frame.Geometry.Width, frame.Geometry.Height);
                var dpi = new Vector(96 * frame.Geometry.Scaling, 96 * frame.Geometry.Scaling);
                if (_bitmap == null || _bitmap.PixelSize != size || _bitmap.Dpi != dpi)
                {
                    var bitmap = new WriteableBitmap(size, dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
                    _bitmap?.Dispose();
                    _bitmap = bitmap;
                }

                using (var target = _bitmap.Lock())
                {
                    var rowBytes = checked(size.Width * 4);
                    if (target.RowBytes < rowBytes)
                        throw new InvalidOperationException("Invalid presentation stride.");

                    if (target.RowBytes == rowBytes)
                        Marshal.Copy(frame.Pixels, 0, target.Address, checked(rowBytes * size.Height));
                    else
                    {
                        // Preserve destination padding when the platform framebuffer is not tightly packed.
                        for (var row = 0; row < size.Height; row++)
                            Marshal.Copy(frame.Pixels, row * rowBytes, target.Address + (row * target.RowBytes), rowBytes);
                    }
                }

                PresentedFrames++;
                LastRenderMilliseconds = frame.RenderMilliseconds;
                LastFrameLatencyMilliseconds = Stopwatch.GetElapsedTime(frame.RequestedAt).TotalMilliseconds;
                presented = true;
                
                // Presentation must not schedule another rasterization.
                base.InvalidateVisual();
            }
        }
        catch (Exception copyError)
        {
            error = copyError;
        }
        finally
        {
            if (frame != null)
                _worker.AcknowledgeFrame();
        }

        if (error != null && !_disposed)
            OnRenderingFailed(error);
        if (presented && !_disposed)
        {
            _fpsFrames++;
            var elapsed = Stopwatch.GetElapsedTime(_fpsStarted).TotalSeconds;
            if (elapsed >= 1)
            {
                _backgroundFps = (int) Math.Round(_fpsFrames / elapsed);
                _fpsFrames = 0;
                _fpsStarted = Stopwatch.GetTimestamp();
                OnFPSChanged();
            }

            if (!_disposed)
                FramePresented?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RenderBackground(DrawingContext context)
    {
        if (!_disposed && _bitmap != null && _backgroundGeometry.Active)
            context.DrawImage(_bitmap, new Rect(0, 0, _bitmap.PixelSize.Width, _bitmap.PixelSize.Height), new Rect(Bounds.Size));

        base.Render(context);
    }

    #endregion

    #region Visual lifecycle

    private void AttachBackground()
    {
        foreach (var ancestor in this.GetVisualAncestors())
        {
            _backgroundAncestors.Add(ancestor);
            ancestor.PropertyChanged += BackgroundAncestorChanged;
        }

        if (_topLevel != null)
            _topLevel.ScalingChanged += BackgroundScalingChanged;
        UpdateBackgroundGeometry();
    }

    private void BackgroundScalingChanged(object? sender, EventArgs e) => UpdateBackgroundGeometry();

    private void BackgroundAncestorChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty)
            UpdateBackgroundGeometry();
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (IsBackgroundRendering && change.Property == IsVisibleProperty)
            UpdateBackgroundGeometry();
    }

    private void DetachBackground()
    {
        if (_topLevel != null)
            _topLevel.ScalingChanged -= BackgroundScalingChanged;
        _topLevel = null;
        
        foreach (var ancestor in _backgroundAncestors)
            ancestor.PropertyChanged -= BackgroundAncestorChanged;
        _backgroundAncestors.Clear();
        
        UpdateBackgroundGeometry();
    }

    private void UpdateBackgroundGeometry()
    {
        if (_disposed || _worker == null)
            return;

        var scale = _topLevel?.RenderScaling ?? 1;
        var width = _topLevel != null && IsEffectivelyVisible && Bounds.Width > 0 ? checked((int) Math.Ceiling(Bounds.Width * scale)) : 0;
        var height = width > 0 && Bounds.Height > 0 ? checked((int) Math.Ceiling(Bounds.Height * scale)) : 0;
        if ((long) width * height > 16 * 1024 * 1024)
            throw new InvalidOperationException("Background frame exceeds the 16-megapixel budget.");
        
        if (_backgroundGeometry.Width == width && _backgroundGeometry.Height == height && _backgroundGeometry.Scaling == scale)
            return;

        _backgroundGeometry = new FrameGeometry(width, height, scale, _backgroundGeometry.Generation + 1);
        _worker.SetGeometry(_backgroundGeometry);
        
        // SetGeometry already requests a worker frame.
        base.InvalidateVisual();
    }

    #endregion

    #region Disposal

    private void DisposeBackground()
    {
        DetachBackground();
        _worker!.Stop();
        
        if (_globalScene != null)
        {
            lock (BackgroundRenderWorker.SceneReferenceGate)
            {
                _globalScene.DecreaseReference();
            }
        }

        _globalScene = null;
        _bitmap?.Dispose();
        _bitmap = null;
    }

    #endregion

    #region Input

    private void ProcessInput(PointerAction action, MouseEventArgs args)
    {
        if (IsBackgroundRendering)
        {
            if (_backgroundGeometry.Active)
                _worker!.Pointer(action, args, _backgroundGeometry.Generation);

            return;
        }

        switch (action)
        {
            case PointerAction.Move:
                _inputController.OnMouseMove(args);
                break;
            case PointerAction.Down:
                _inputController.OnMouseDown(args);
                break;
            case PointerAction.Up:
                _inputController.OnMouseUp(args);
                break;
            case PointerAction.Wheel:
                _inputController.OnMouseWheel(args);
                break;
            case PointerAction.Click:
                _inputController.OnMouseClick(args);
                break;
            case PointerAction.DoubleClick:
                _inputController.OnMouseDoubleClick(args);
                break;
            case PointerAction.Enter:
                _inputController.OnMouseEnter(args);
                break;
            case PointerAction.Leave:
                _inputController.OnMouseLeave(args);
                break;
        }
    }

    /// <inheritdoc />
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        if (IsBackgroundRendering && !_disposed && !_releasingBackgroundCapture)
            _worker!.CancelGesture();

        base.OnPointerCaptureLost(e);
    }

    #endregion
}
