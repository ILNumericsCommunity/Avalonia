using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ILNumerics.Drawing;
using Point = System.Drawing.Point;

namespace ILNumerics.Community.Avalonia.Render;

// Only mailbox state is locked. Driver operations, callbacks and copies never hold the gate.
internal sealed class BackgroundRenderWorker
{
    #region Synchronization

    private const int QueueCapacity = 256;

    // ILNumerics Scene's driver-reference counter is not atomic in 7.4.16.
    internal static readonly object SceneReferenceGate = new object();

    private readonly object _gate = new object();
    private readonly LinkedList<Command> _commands = new LinkedList<Command>();
    private readonly Action _notify;
    private readonly TaskCompletionSource _completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    #endregion

    #region Mailbox

    private FrameGeometry _geometry;
    private bool _stopping;
    private bool _dirty;
    private bool _resetInput;
    private bool _frameOutstanding;
    private CompletedFrame? _frame;
    private Exception? _error;
    private long _requestedAt;
    private long _latestSceneRevision;

    #endregion

    #region Driver state

    private int _ownerThreadId;
    private GDIDriver? _ownedDriver;
    private double _appliedScaling = 1;
    private bool _rasterizing;
    private long _sceneRevision;
    private long _lastPublishedSceneRevision = -1;

    #endregion

    #region Diagnostics

    private long _rendered;
    private long _coalescedMoves;
    private int _queueHighWater;

    #endregion

    #region Command

    private sealed record Command(Action<GDIDriver, InputController> Execute, Action Cancel, bool IsMove = false, long? Generation = null);

    #endregion

    #region Initialization

    public BackgroundRenderWorker(Action notify)
    {
        _notify = notify;
        new Thread(Run) { IsBackground = true, Name = "ILNumerics render owner" }.Start();
    }

    public Task Completion => _completion.Task;

    public long RenderedFrames => Interlocked.Read(ref _rendered);

    public long CoalescedMoves => Interlocked.Read(ref _coalescedMoves);

    public int QueueHighWater => Volatile.Read(ref _queueHighWater);

    public bool CheckAccess() => Environment.CurrentManagedThreadId == Volatile.Read(ref _ownerThreadId);

    #endregion

    #region Picking

    public int? PickAt(Point logicalPoint, long timeMs, double? scaling = null)
    {
        if (CheckAccess())
        {
            if (_stopping)
                return null;
            if (_rasterizing)
                throw new InvalidOperationException("Picking cannot reenter an active render callback on the same driver.");

            return Pick(_ownedDriver!, logicalPoint, timeMs, scaling ?? _appliedScaling);
        }

        return InvokeAsync(driver => Pick(driver, logicalPoint, timeMs, scaling ?? _appliedScaling)).GetAwaiter().GetResult();
    }

    public Task<int?> PickAsync(Point logicalPoint, long timeMs, double scaling) => InvokeAsync(driver => Pick(driver, logicalPoint, timeMs, scaling));

    private static int? Pick(GDIDriver driver, Point point, long timeMs, double scaling)
    {
        if (driver.Size.Width <= 0 || driver.Size.Height <= 0)
            return null;

        return driver.PickAt(new Point((int) (point.X * scaling), (int) (point.Y * scaling)), timeMs);
    }

    #endregion

    #region Requests

    public void SetGeometry(FrameGeometry geometry)
    {
        lock (_gate)
        {
            if (_stopping || _geometry == geometry)
                return;

            _geometry = geometry;
            _resetInput = true;
            MarkDirty();
        }
    }

    public void RequestRender()
    {
        lock (_gate)
        {
            if (!_stopping)
                MarkDirty();
        }
    }

    public void CancelGesture()
    {
        lock (_gate)
        {
            if (_stopping)
                return;

            RemoveQueuedInput();
            _resetInput = true;
            MarkDirty();
        }
    }

    private void RemoveQueuedInput()
    {
        for (var node = _commands.First; node != null;)
        {
            var next = node.Next;
            if (node.Value.Generation != null)
                _commands.Remove(node);

            node = next;
        }
    }

    private void MarkDirty()
    {
        if (!_dirty)
            _requestedAt = Stopwatch.GetTimestamp();
        _dirty = true;
        Monitor.Pulse(_gate);
    }

    public Task<T> InvokeAsync<T>(Func<GDIDriver, T> action)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new Command((driver, _) =>
        {
            try
            {
                result.TrySetResult(action(driver));
            }
            catch (Exception error)
            {
                result.TrySetException(error);
            }
        }, () => result.TrySetCanceled());

        lock (_gate)
        {
            if (_stopping)
                result.TrySetException(new ObjectDisposedException(nameof(Panel)));
            else if (_commands.Count >= QueueCapacity)
                result.TrySetException(new InvalidOperationException("Background queue is full. Await updates before submitting more."));
            else
            {
                _commands.AddLast(command);
                _queueHighWater = Math.Max(_queueHighWater, _commands.Count);
                MarkDirty();
            }
        }

        return result.Task;
    }

    public Task AssignSceneAsync(Scene scene, long revision = 0)
    {
        lock (_gate)
        {
            _latestSceneRevision = revision;
        }

        // Retain the pending assignment independently of panel and driver references.
        lock (SceneReferenceGate)
        {
            scene.IncreaseReference();
        }

        return ReleaseAssignmentAsync(InvokeAsync(driver =>
        {
            lock (SceneReferenceGate)
            {
                driver.Scene = scene;
            }

            _sceneRevision = revision;

            return true;
        }), scene);
    }

    private static async Task ReleaseAssignmentAsync(Task assignment, Scene scene)
    {
        try
        {
            await assignment.ConfigureAwait(false);
        }
        finally
        {
            lock (SceneReferenceGate)
            {
                scene.DecreaseReference();
            }
        }
    }

    public void Pointer(PointerAction action, MouseEventArgs args, long generation)
    {
        var command = new Command((driver, input) =>
        {
            args.Location = new Point((int) (args.LocationF.X * driver.Size.Width), (int) (args.LocationF.Y * driver.Size.Height));

            switch (action)
            {
                case PointerAction.Move:
                    input.OnMouseMove(args);
                    break;
                case PointerAction.Down:
                    input.OnMouseMove(args);
                    input.OnMouseDown(args);
                    break;
                case PointerAction.Up:
                    input.OnMouseUp(args);
                    break;
                case PointerAction.Wheel:
                    input.OnMouseMove(args);
                    input.OnMouseWheel(args);
                    break;
                case PointerAction.Click:
                    input.OnMouseClick(args);
                    break;
                case PointerAction.DoubleClick:
                    input.OnMouseDoubleClick(args);
                    break;
                case PointerAction.Enter:
                    input.OnMouseEnter(args);
                    break;
                case PointerAction.Leave:
                    input.OnMouseLeave(args);
                    break;
            }
        }, () => { }, action == PointerAction.Move, generation);

        var notify = false;
        lock (_gate)
        {
            if (_stopping || !_geometry.Active || generation != _geometry.Generation)
                return;

            if (command.IsMove && _commands.Last?.Value.IsMove == true && _commands.Last.Value.Generation == generation)
            {
                _commands.Last.Value = command;
                Interlocked.Increment(ref _coalescedMoves);
            }
            else if (_commands.Count >= QueueCapacity)
            {
                RemoveQueuedInput();
                _resetInput = true;
                _error = new InvalidOperationException("Input queue saturated; the current gesture was cancelled.");
                notify = true;
            }
            else
                _commands.AddLast(command);

            _queueHighWater = Math.Max(_queueHighWater, _commands.Count);
            MarkDirty();
        }

        if (notify)
            _notify();
    }

    #endregion

    #region Frames and shutdown

    public (CompletedFrame? Frame, Exception? Error) TakeNotification()
    {
        lock (_gate)
        {
            var result = (_frame, _error);
            _frame = null;
            _error = null;

            return result; // UI owns the buffer until acknowledgement.
        }
    }

    public void AcknowledgeFrame()
    {
        lock (_gate)
        {
            _frameOutstanding = false;
            Monitor.Pulse(_gate);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _stopping = true;
            _frame = null;
            foreach (var command in _commands)
                command.Cancel();

            _commands.Clear();
            Monitor.Pulse(_gate);
        }
    }

    #endregion

    #region Render loop

    private void Run()
    {
        GDIDriver? driver = null;

        try
        {
            driver = new GDIDriver(new CommonBackBuffer());
            _ownedDriver = driver;
            Volatile.Write(ref _ownerThreadId, Environment.CurrentManagedThreadId);
            var input = new InputController(driver, RequestRender);
            var applied = default(FrameGeometry);
            var staging = Array.Empty<byte>();
            driver.BeginRenderFrame += (_, e) => e.Parameter.DPIScaling = applied.Scaling;
            Exception? renderError = null;
            var lastStart = 0L;
            driver.RenderingFailed += (_, e) => renderError = e.Exception ?? new TimeoutException("Scene rasterization timed out.");

            while (true)
            {
                FrameGeometry geometry;
                Command[] commands;
                bool resetInput;
                bool render;
                long requestedAt;

                lock (_gate)
                {
                    // Commands (including synchronous picks) must progress even if UI has not consumed the frame.
                    // Only production/staging reuse waits for acknowledgement.
                    while (!_stopping && _commands.Count == 0 && (_frameOutstanding || !_dirty || !_geometry.Active))
                        Monitor.Wait(_gate);
                    if (_stopping)
                        break;

                    geometry = _geometry;
                    commands = new Command[_commands.Count];
                    _commands.CopyTo(commands, 0);
                    _commands.Clear();
                    resetInput = _resetInput;
                    _resetInput = false;
                    render = !_frameOutstanding && geometry.Active && (_dirty || commands.Length > 0);
                    requestedAt = _requestedAt;
                    if (render)
                        _dirty = false;
                }

                try
                {
                    using var scope = Scope.Enter();
                    if (resetInput)
                        input = new InputController(driver, RequestRender);
                    if (geometry.Active && applied != geometry)
                    {
                        driver.Size = new System.Drawing.Size(geometry.Width, geometry.Height);
                        applied = geometry;
                        _appliedScaling = geometry.Scaling;
                    }

                    var sceneChanged = false;
                    foreach (var command in commands)
                    {
                        lock (_gate)
                        {
                            if (_stopping)
                            {
                                command.Cancel();
                                continue;
                            }
                        }

                        var previousScene = driver.Scene;
                        if (sceneChanged && command.Generation != null && geometry.Active)
                        {
                            Rasterize(driver);
                            sceneChanged = false;
                        }
                        if (command.Generation == null || command.Generation == geometry.Generation)
                            command.Execute(driver, input);
                        if (!ReferenceEquals(previousScene, driver.Scene))
                        {
                            input = new InputController(driver, RequestRender);
                            sceneChanged = true;
                        }
                    }

                    if (!render)
                        continue;

                    lock (_gate)
                    {
                        // Assignment arrived after this command batch: apply it before starting obsolete work.
                        if (_sceneRevision != _latestSceneRevision)
                            continue;
                    }

                    var remaining = 16.667 - Stopwatch.GetElapsedTime(lastStart).TotalMilliseconds;
                    if (_sceneRevision == _lastPublishedSceneRevision && remaining > 0)
                    {
                        lock (_gate)
                        {
                            while (!_stopping && _sceneRevision == _latestSceneRevision
                                              && (remaining = 16.667 - Stopwatch.GetElapsedTime(lastStart).TotalMilliseconds) > 0)
                                Monitor.Wait(_gate, (int) Math.Ceiling(remaining));
                            if (_stopping)
                                break;
                            if (_sceneRevision != _latestSceneRevision)
                                continue;
                        }
                    }

                    var start = Stopwatch.GetTimestamp();
                    lastStart = start;
                    renderError = null;
                    Rasterize(driver);
                    if (renderError != null)
                        throw renderError;

                    var length = checked(geometry.Width * geometry.Height * 4);
                    if (staging.Length != length)
                        staging = new byte[length];
                    CopyPixels(driver, geometry, staging, length);
                    var frame = new CompletedFrame(staging, geometry, Interlocked.Increment(ref _rendered),
                                                   Stopwatch.GetElapsedTime(start).TotalMilliseconds, requestedAt, _sceneRevision);

                    lock (_gate)
                    {
                        if (_stopping)
                            break;
                        if (_geometry != geometry || _sceneRevision != _latestSceneRevision)
                            continue;

                        _frame = frame;
                        _frameOutstanding = true;
                        _lastPublishedSceneRevision = _sceneRevision;
                    }

                    _notify();
                }
                catch (Exception error)
                {
                    foreach (var command in commands)
                        command.Cancel();

                    lock (_gate)
                    {
                        if (_stopping)
                            break;

                        _error = error;
                        _dirty = false;
                    }

                    _notify();
                }
            }
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                _error = error;
            }

            _notify();
            Stop();
        }
        finally
        {
            try
            {
                if (driver != null)
                {
                    driver.SceneSyncRoot.Dispose();
                    driver.LocalSceneSyncRoot?.Dispose();
                    driver.LocalScene.Dispose();
                    lock (SceneReferenceGate)
                    {
                        driver.Scene.DecreaseReference();
                    }
                    driver.Dispose();
                }

                _completion.TrySetResult();
            }
            catch (Exception error)
            {
                _completion.TrySetException(error);
            }
        }
    }

    #endregion

    #region Rendering

    private static void CopyPixels(GDIDriver driver, FrameGeometry geometry, byte[] staging, int length)
    {
        using Array<int> pixels = ((CommonBackBuffer) driver.BackBuffer).PixelBuffer;
        if (pixels.S.NumberOfElements != (long) geometry.Width * geometry.Height)
            throw new InvalidOperationException("Worker commands must not change the driver's frame geometry.");

        Marshal.Copy(pixels.GetHostPointerForRead(), staging, 0, length);
        GC.KeepAlive(pixels);
    }

    private void Rasterize(GDIDriver driver)
    {
        _rasterizing = true;

        try
        {
            driver.Render();
        }
        finally
        {
            _rasterizing = false;
        }
    }

    #endregion
}
