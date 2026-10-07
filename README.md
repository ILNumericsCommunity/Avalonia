# ILNumerics.Community.Avalonia

[![Nuget](https://img.shields.io/nuget/v/ILNumerics.Community.Avalonia?style=flat-square&logo=nuget&color=blue)](https://www.nuget.org/packages/ILNumerics.Community.Avalonia)

Integration package for ILNumerics (http://ilnumerics.net/) scene graphs and plot cubes with Avalonia (https://avaloniaui.net/platforms) cross-platform UI framework. `ILNumerics.Community.Avalonia` provides an ILNumerics panel implementation for Avalonia and a set of helper / convenience functions to make embedding ILNumerics scenes into Avalonia apps straightforward. This package makes it easy to host ILNumerics scene graphs and 2D/3D plot cubes inside Avalonia applications. The panel acts as a bridge between Avalonia's UI system and ILNumerics rendering, allowing you to build interactive visualizations that run cross-platform.

Note: This package currently uses a software renderer on all platforms. It generally provides a smooth rendering experience for moderate data sizes, but performance may vary per platform and scene complexity.

## Compatibility

- .NET: targets `.NET 10`.
- ILNumerics: `ILNumerics 7.4+`
- Avalonia: targets `Avalonia 12.1.3`.

> Note: Desktop platforms (Windows, Linux, macOS) are working well. There are currently some outstanding issues on mobile platforms; please refer to the issue tracker for details and status updates.

## Usage

Add the ILNumerics panel to your user interface (in XAML or in code). The example below shows a simple XAML usage; adjust XML namespaces as appropriate for your project:

```xml
<avalonia:Panel Background="White" x:Name="ilPanel" />
```

Assign a scene to the panel to render it. A minimal example in C#:

```csharp
// Create a Scene containing a PlotCube and a Surface. Replace 'B' with your data array.
ilPanel.Scene = new Scene
{
    new PlotCube(twoDMode: false)
    {
        new Surface(tosingle(B), colormap: Colormaps.Jet) { new Colorbar() }
    }
};

// Call Configure so ILNumerics computes bounds and internal state required for rendering.
ilPanel.Scene.Configure();
ilPanel.InvalidateVisual();
```

Notes:

- Assign or modify the global scene on the Avalonia UI thread.
- Call `Scene.Configure()` (or `ilPanel.Configure()`) after changes, then `ilPanel.InvalidateVisual()` to request a frame.
- The same global-scene workflow works with either rendering mode.

## Rendering modes

The panel uses the ILNumerics software renderer in both modes. **UI-thread rendering is the default:** scene rasterization, picking, and interaction execute on the UI thread. **Background rendering is optional:** a dedicated worker performs rasterization, picking, and interaction while the UI copies and draws completed frames. Background rendering can improve responsiveness for complex scenes, but does not guarantee higher frame rates or shorter time to the first frame.

Choose the mode **before constructing panels or loading their XAML**, for example during application startup:

```csharp
using ILNumerics.Community.Avalonia;

Panel.UseBackgroundRendering = true;  // Background rendering for new panels.
// Panel.UseBackgroundRendering = false; // Default: UI-thread rendering.
```

The static setting affects new panels only; changing it does not switch existing instances or transfer their scenes between threads. `panel.IsBackgroundRendering` reports an instance's mode. Browser targets always fall back to UI-thread rendering. The desktop demo's renderer selector recreates its panel to compare the two paths.

Background render requests and consecutive pointer moves are coalesced, with bounded command and completed-frame storage. The worker never overwrites a published pixel buffer until the UI has finished copying it. Resize and monitor-DPI changes request frames with matching physical dimensions and scaling. New-scene first frames bypass normal pacing; obsolete scene/geometry results are discarded. Native rendering already in progress finishes normally.

### Global scenes and render requests

`panel.Scene` is the application-facing global scene in both modes. Assigning it immediately changes the getter's result; background mode applies the assignment to the driver through its ordered command queue. ILNumerics maintains a separate synchronized scene copy for rendering and panel-specific interaction.

Configure global-scene changes on the UI thread and request rendering:

```csharp
ilPanel.Scene = CreateScene();
ilPanel.Configure();
ilPanel.InvalidateVisual();

// After changing the existing global scene:
// ... modify ilPanel.Scene ...
ilPanel.Scene.Configure();
ilPanel.InvalidateVisual();
```

`Configure()` publishes global-scene changes; it does not request a frame. The background driver picks up configured changes when synchronizing its rendering copy. `ilPanel.InvalidateVisual()` requests fresh content in either mode and returns before presentation. `Render(0)` remains available for the ILNumerics driver API, and `RequestRender()` is an alternative explicit request.

Avalonia's `Visual.InvalidateVisual()` is non-virtual, so `Panel` provides a method with the same name rather than an override. Call it on a reference typed as `ILNumerics.Community.Avalonia.Panel`. Calls through a `Visual` or `Control` reference—and framework-internal invalidations—use Avalonia's base method, which only redraws cached pixels in background mode. Use `RequestRender()` if an explicit content request is needed. Completed-frame presentation uses the base method internally to avoid a rendering feedback loop.

`SetSceneAsync` is an optional convenience for creating/configuring a global scene and awaiting its driver assignment:

```csharp
await ilPanel.SetSceneAsync(() => CreateScene());
```

Call it on the UI thread. The factory also runs on the UI thread, including in background mode; expensive scene construction/configuration therefore still occupies the UI. Completion indicates assignment, not frame presentation. Ordinary scene updates do not require async methods.

### Picking

`PickAt` accepts logical panel coordinates and works synchronously in both modes:

```csharp
int? hit = ilPanel.PickAt(new System.Drawing.Point(100, 100), 0);
```

In background mode, non-owner calls queue the query and wait for the worker, including preceding work; owner-thread calls execute directly. The picking render itself can be expensive. Use `PickAsync` when the UI should remain available while waiting:

```csharp
int? hit = await ilPanel.PickAsync(new Avalonia.Point(100, 100));
```

Call `PickAsync` on the UI thread. Its result describes the scene when the owner executes the query. Coordinates are scaled to physical pixels internally. Picking from an active render callback is rejected to prevent reentry into the same driver.

### Driver-owned scenes and API differences

Mouse interaction changes the driver's synchronized scene copy, such as its camera, rather than the global scene. `UpdateDriverSceneAsync` is an optional convenience addition to the ILNumerics driver API for accessing this panel-specific state on the selected render-owner thread:

```csharp
await ilPanel.UpdateDriverSceneAsync(driver =>
{
    var camera = driver.SceneSyncRoot.First<ILNumerics.Drawing.Camera>();
    // Apply panel-specific camera changes here.
});
```

The method requests a frame after the delegate executes. Await completion to keep submitted operations bounded; completion does not mean the frame has been displayed. In background delegates, do not access Avalonia controls, synchronously wait for UI dispatch, retain mutable driver/scene references, replace/configure the global scene, change the driver size, or dispose the driver. Use `panel.Scene` for ordinary global-scene changes.

The common `Scene`/`Configure`/`InvalidateVisual`/`PickAt` workflow is supported in both modes, but the complete synchronous `IDriver` surface is not interchangeable:

| API                                                                                      | UI-thread mode                  | Background mode                                          |
| ---------------------------------------------------------------------------------------- | ------------------------------- | -------------------------------------------------------- |
| `Scene`, `Configure()`                                                                   | Application-facing global scene | Same global-scene workflow; driver assignment is queued  |
| `Render(long)`, `RequestRender()`                                                        | Request a UI render pass        | Request worker rasterization                             |
| `Panel.InvalidateVisual()`                                                               | Request a UI render pass        | Request worker rasterization and invalidate presentation |
| `PickAt`, `PickAsync`                                                                    | Execute picking on UI           | Execute picking on worker; `PickAt` waits                |
| `Background`, `BackColor`                                                                | Apply directly                  | Queue color changes; getter returns the requested color  |
| `SceneSyncRoot`, `LocalSceneSyncRoot`, `LocalScene`                                      | Direct driver access            | Direct access rejected; use `UpdateDriverSceneAsync`     |
| `Camera`, `Size`, `Rectangle`, `Timeout`, `ViewTransform`, `GetCurrentScene`, `Supports` | Direct driver access            | Direct access rejected                                   |
| `FPS`                                                                                    | ILNumerics driver FPS           | Rate of frames copied for presentation                   |

`PresentedFrames`, `RenderedFrames`, `LastRenderMilliseconds`, and `LastFrameLatencyMilliseconds` expose rendering diagnostics. These measure rendering/copying rather than physical display scanout; UI-mode `LastFrameLatencyMilliseconds` is zero.

### Events and threading

`BeginRenderFrame`, `EndRenderFrame`, and ILNumerics scene mouse handlers run on the render-owner thread: UI in synchronous mode, worker in background mode. Existing handlers that access Avalonia controls must marshal those updates:

```csharp
ilPanel.EndRenderFrame += (_, _) =>
    Avalonia.Threading.Dispatcher.UIThread.Post(() => statusText.Text = "Rendered");
```

Never synchronously wait for UI dispatch from a worker callback: the UI may already be waiting for synchronous picking. In UI mode, render callbacks occur inside the render operation; avoid changing visual/layout state directly from them as well.

`RenderingFailed` and `FPSChanged` are UI-thread notifications. `FramePresented` also runs on UI, outside the render pass, and is suitable for UI status updates. UI-mode notifications are posted/coalesced and suppressed after disposal. The event indicates completed bitmap copying, not compositor display or scanout.

### Cleanup and memory

Dispose the panel on the UI thread when its owner is permanently closed. `Dispose()` requests background shutdown without blocking; `await ilPanel.DisposeAsync()` or awaiting `Completion` waits asynchronously for the active driver operation and cleanup. Do not synchronously wait for worker termination on the UI thread. Temporary detach does not dispose the panel; background frame production pauses while detached, hidden, or empty-sized.

ILNumerics uses unmanaged array storage and retains released memory in a reusable pool. Private memory need not fall immediately after scene replacement or disposal; see [ILNumerics memory management](https://ilnumerics.net/memory-management.html).

## Examples and demos

This repository includes demo projects under the `Demo/` folder showcasing usage across desktop, browser and mobile targets. Run the demos to see concrete usage and to experiment with different scenes and rendering configurations.

### License

ILNumerics.Community.Avalonia is licensed under the terms of the MIT license (<http://opensource.org/licenses/MIT>, see LICENSE.txt).
