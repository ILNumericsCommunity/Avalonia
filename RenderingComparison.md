# Integrated rendering comparison

Both paths now live in the main repository. `Panel.UseBackgroundRendering` is sampled at construction; the same demo recreates a `Panel` when its selector flips the flag. `Panel.cs` holds the synchronous driver path; `Panel.Background.cs` and `BackgroundRenderWorker.cs` hold the isolated owner and completed-frame path. There is no separate BackgroundPanel public control.

This document archives measurements taken during development. The temporary headless validation project, benchmark scripts and test-only instrumentation have been removed; the tables below are historical results. The desktop demo's renderer selector remains available for manual comparison.

## ILNumerics pooling

The vendor explicitly states that numerical arrays use unmanaged memory and that freed storage is normally retained in an internal pool for reuse instead of returned to the OS:

- https://ilnumerics.net/memory-management.html
- https://ilnumerics.net/low-level-memory-functions.html
- https://ilnumerics.net/array-memory-access.html

Inspection of 7.4.16 confirms size-class bins, pool Size/Count/MaxSize/ShrinkCount/OOMCount and reuse before native allocation. Pool maximums can adapt upward under workload. A pool `Shrink` operation exists, but this integration does not change process-wide pool policy or forcibly collect memory. Private-byte retention alone is not a leak diagnosis. Compare live buffers, reusable pool size, repeated-cycle growth and actual allocation failures.

The benchmark samples `DeviceManager.GetDevice(0).MemoryPool.ToString()` alongside managed/private memory. In the 60-second equal-churn pair (16 scene switches each), the host pool stored:

| Mode | Reusable pool after warmup | Final private memory | Errors |
|---|---:|---:|---:|
| UI | approximately 548–562 MiB | 861 MiB | none |
| Background | approximately 485–559 MiB | 887 MiB | none |

This directly explains a large part of the observed high footprint. The pool's OOM-handled counter was one in UI mode and zero in background in this run; an internal handled allocation failure is distinct from a surfaced render exception. These measurements do not prove all historical long-run growth is pooled.

Later full regression runs surfaced intermittent ILNumerics native allocation failures during the original UI-mode demo check under a memory-loaded machine, including when the native demo was running concurrently. Rerunning the same executable after closing the demo passed; an accelerator-enabled suite also passed. The vendor-generated `error.txt` preserves failures for investigation. Pooling explains retention; it does not make actual allocation failures harmless or eliminate memory-pressure testing.

## Run the same demo

```powershell
dotnet run --project Demo/AvaloniaDemo.Desktop -p:GeneratePackageOnBuild=false -p:ILNAcceleratorEnabled=false
```

The demo selector flips only the static property and reconstructs the panel, preserving the selected scene. The demo uses ordinary Scene assignment, Scene.Configure and Render calls in both modes. Benchmark processes run sequentially to avoid CPU competition and share the demo's deterministic scene factory; only the property value differs. Demand is nominally 60 Hz with 5 ms UI heartbeat requests, three seconds warmup, bounded pending benchmark jobs. Stress changes scene every four seconds. JSON SAMPLE lines include host-pool statistics.

## Same-Panel results

### Demo mode-switch crash fix

Switching background to UI exposed an invalidation during Avalonia's render pass: the demo updated status text from FramePresented, which UI mode previously invoked inside Panel.Render. UI-mode presentation notifications now post/coalesce outside the render pass and skip disposed panels. Demo status updates are generation-guarded and throttled to avoid status-layout/render feedback. Counts still increment at bitmap copy, but UI notification timestamps include dispatcher delay; earlier timing tables predate this notification change.

The focused `--mode-switch-check` repeatedly switches background/UI and changes status text from the notification, asserting callbacks execute outside Panel's render operation. It passed Release with project acceleration both disabled and enabled. A subsequent full accelerated rendering suite passed. Initial verification attempts failed separately in ILNumerics allocation under low available memory; checks passed after build-server shutdown.

### Release with ILNumerics project acceleration enabled

The preceding tables were already Release builds (`-c Release`), with project acceleration explicitly disabled. This rerun uses `ILNAcceleratorEnabled=true`, then executes the built Release assembly without a debugger. The rendering regression suite passed first. Windows/.NET 10, Headless/Skia, 800 x 600, sequential fresh processes, same workloads, three-second warmup and matching host-pool maximums:

| Workload | Mode | Copied frames/s | Heartbeat p95 | Simulated input p95 | CPU seconds | Final private MiB |
|---|---|---:|---:|---:|---:|---:|
| 50 x 60 surface interaction, 30 s | UI | 39.00 | 17.52 ms | 46.45 ms | 27.72 | 266.5 |
| Same | Background | 40.29 | 15.99 ms | 3.02 ms | 26.11 | 265.7 |
| 150 x 160 surface interaction, 30 s | UI | 13.42 | 64.50 ms | 143.01 ms | 36.55 | 269.9 |
| Same | Background | 14.84 | 16.13 ms | 3.18 ms | 38.17 | 276.9 |
| Scene switching, 60 s / 16 switches | UI | 8.64 | 218.47 ms | 261.79 ms | 74.63 | 875.2 |
| Same | Background | 5.82 | 16.09 ms | 2.46 ms | 102.89 | 910.6 |

All six runs completed with empty reported error lists and zero handled OOMs. Fixed-scene background throughput was slightly higher in this run, and complex-scene UI responsiveness substantially better. Scene churn still favored UI throughput and CPU use. These single-run results do not establish acceleration as the sole cause of differences from earlier runs; machine load, JIT/cache warmup and optimized code generation also matter. Background stress sampled an early 2,662.8 MiB private footprint before settling around 890–1,009 MiB, final 910.6 MiB. Reusable host-pool storage later ranged roughly 539–563 MiB; retention is not automatically a leak. Counts remain bitmap copies, not physical display FPS.

### Current results after first-frame pacing and obsolete-scene handling

Rerun on the latest implementation: Windows/.NET 10, Release, project accelerator disabled, Headless/Skia, 800 x 600, sequential fresh processes, three-second warmup. Each mode uses the same ordinary global-scene API. Pool maximum was identical (17,082,009,600 bytes), with no handled OOMs or pool shrinks reported in completed runs.

| Workload | Mode | Copied frames/s | Heartbeat p95 | Simulated input p95 | CPU seconds | Final private MiB |
|---|---|---:|---:|---:|---:|---:|
| 50 x 60 surface interaction, 30 s | UI | 25.55 | 29.68 ms | 77.86 ms | 31.36 | 261.7 |
| Same | Background | 32.16 | 16.00 ms | 4.51 ms | 30.69 | 263.0 |
| 150 x 160 surface interaction, 30 s | UI | 11.80 | 85.65 ms | 201.27 ms | 34.81 | 276.3 |
| Same | Background | 10.26 | 16.07 ms | 3.27 ms | 34.42 | 274.8 |
| Scene switching, 60 s / 16 switches | UI | 9.41 | 170.92 ms | 251.99 ms | 74.84 | 887.7 |
| Same | Background | 4.58 | 16.25 ms | 2.52 ms | 101.80 | 915.4 |

Compared with the preceding rerun, background scene-switching throughput was 4.58 versus 2.92 copied frames/s (about 57% higher); the UI counterpart was 9.41 versus 9.64. This is consistent with removing avoidable first-frame delay, but it is not an isolated causal measurement: machine load and rendering times varied. Fixed complex-scene throughput reversed the earlier ranking (UI 11.80 / background 10.26), underlining that single-run FPS is variable. The robust result remains much lower background UI/input blocking; scene churn still costs throughput and CPU.

The first UI stress attempt failed near the end with an ILNumerics ContourPlot construction NullReferenceException (ContourHelper.AddPoint/VisitFace). That attempt produced no complete RESULT and is excluded from the table, but must not be counted as a success. Both stress runs completed on the subsequent pair with empty reported error lists. The four fixed-scene runs completed without errors; the full rendering regression suite also passed before timing.

Background stress sampled a transient 1,423.8 MiB private-memory footprint at ten seconds before falling to approximately 883–959 MiB at later samples, final 915.4 MiB. Host-pool samples held approximately 529–561 MiB; the successful UI stress pair retained approximately 495–510 MiB. This is pooled-memory context, not proof of leak absence. These are bitmap-copy counts, not native compositor/scanout measurements.

### Earlier integrated baseline

Windows/.NET 10, Release, accelerator disabled, Headless/Skia, 800 x 600, single diagnostic runs:

| Workload | Mode | Copied frames/s | Heartbeat p95 | Simulated input p95 | CPU seconds |
|---|---|---:|---:|---:|---:|
| 150 x 160 surface interaction, 20 s | UI | 10.92 | 92.93 ms | 207.27 ms | 23.73 |
| Same | Background | 11.45 | 16.09 ms | 3.25 ms | 25.11 |
| Scene-switching interaction, 60 s | UI | 8.51 | 224.29 ms | 343.09 ms | 80.33 |
| Same | Background | 6.57 | 16.14 ms | 2.54 ms | 97.55 |

These are frame-copy notifications, not scanout FPS. Headless input simulation pumps render work internally, so handler time includes its synchronous render work. Heartbeat is the stronger UI responsiveness measure. Background mode greatly reduces UI blocking; it does not guarantee faster rasterization or higher throughput. Scene switching shows a throughput/CPU cost. The earlier separate-worktree experiment passed ten-minute fixed-scene interaction with stable private memory around 274–291 MiB; pooled scene churn had much larger footprints.

## What still prevents making it default?

### Isolated first-frame measurements

Development measurements used scene-revision tagging and temporary timing boundaries so an old-scene image could not be mistaken for the first image of a newly assigned scene. The benchmark measured construction plus Configure separately from assignment, worker application, render/staging and UI copy notification. These are copy-completion timings, not compositor display/scanout. Idle trials drain follow-up work for 100 ms first; two warmups are excluded. Busy background trials assign just after an old scene starts rendering. Native work is not interrupted. UI cannot assign concurrently with its own rendering, so busy UI trials finish old work first and are not equivalent residual-work measurements.

Before first-frame priority, idle Surface3DSinc assignment-to-copy median was 45.9 ms UI / 47.8 ms background for size 50, and 67.9 ms UI / 79.0 ms background for size 150. Background queue median was about 0.2 ms; application-to-render-start median was below 0.03 ms. Rasterization dominates; idle scheduling was not a major bottleneck. Early first-render samples were slower than later ones, so small-sample tails reflect runtime/cache warmup and development-machine load as well as rendering mode.

For busy background size 150, assignment-to-copy median was 205.7 ms: queue median 90.0 ms, render median 94.9 ms, and applied-to-render median 4.94 ms (maximum 13.81 ms). This justified a narrow improvement:

- The first frame of each scene revision bypasses normal 60 Hz pacing.
- Assignment wakes a pacing wait; obsolete work is discarded before rasterization where possible and before publication. UI also rejects pending frames of older scene revisions.
- Native rendering already in progress finishes normally. Driver commands retain order; synchronous picking continues independently of UI frame acknowledgement.

Afterward, a busy size-150 rerun measured assignment-to-copy median 141.8 ms, with applied-to-render median 0.008 ms / maximum 0.012 ms. Queue median was 60.6 ms and render median 68.5 ms. Reduced pacing delay is directly supported by the breakdown; the full 64 ms total difference is NOT attributable entirely to the change because rendering/queue times varied. No synchronous UI first-frame fallback was added.

Idle contour trials afterward (10 replacements) gave assignment-to-copy median 253.1 ms UI / 265.6 ms background, construction/configuration median 142.1 / 124.9 ms, and construction-plus-first-copy median 422.4 / 388.7 ms. Background applied-to-render median was 0.013 ms; render median was 255.4 ms. The approximately 12.5 ms first-copy gap does not warrant transferring a 250 ms rasterization to UI.

Regression checks hold an old-scene render while assigning a replacement: obsolete pixels are not published, the replacement revision is published, and picking/shutdown checks still pass. Both Release accelerator settings passed. Physical display latency remains a separate measurement.

### Previous direct rerun after global-scene API restoration (before first-frame priority)

The convenience method is now named `UpdateDriverSceneAsync`. Ordinary scene manipulation requires no async delegate. The comparison now assigns `panel.Scene`, configures it, and requests rendering identically in both modes. Tests verify that path, configured mutations, rapid replacement, synchronous picking and exact pixel parity.

A dependency initialization issue was found during this rerun: ILNumerics 7.4.16 HostDevice creates its static host pool with `MaxSize = 0` if the pool is first initialized on a thread other than its recorded main thread. Background construction now initializes that pool on the UI thread before starting the worker. This preserves the default policy rather than changing MaxSize. The final pair reports the same 17,082,009,600-byte pool maximum in both modes. Earlier runs that reported an empty background pool are not valid pool-policy-matched comparisons.

Windows/.NET 10, Release, project accelerator disabled, Headless/Skia, 800 x 600, sequential fresh processes:

| Workload | Mode | Copied frames/s | Heartbeat p95 | Simulated input p95 | CPU seconds | Final private MiB |
|---|---|---:|---:|---:|---:|---:|
| 50 x 60 surface interaction, 30 s | UI | 24.21 | 30.31 ms | 75.33 ms | 29.53 | 259.4 |
| Same | Background | 29.35 | 16.03 ms | 7.91 ms | 37.20 | 263.7 |
| 150 x 160 surface interaction, 30 s | UI | 10.08 | 120.71 ms | 261.03 ms | 35.28 | 272.3 |
| Same | Background | 12.63 | 16.08 ms | 3.50 ms | 36.67 | 274.5 |
| Scene switching, 60 s / 16 switches | UI | 9.64 | 174.73 ms | 255.03 ms | 76.16 | 878.5 |
| Same | Background | 2.92 | 16.19 ms | 3.25 ms | 95.39 | 906.2 |

All six runs completed without reported errors. These remain single diagnostic runs on a loaded development machine, not controlled statistical trials. Large transient early memory samples (up to 2.7 GiB) fell after warmup; final private memory and pool samples must not obscure those peaks. Fixed-scene host pool storage was approximately 6.1 MiB (small surface) and 20.7 MiB (large surface), consistent across modes. Scene churn used hundreds of MiB of reusable storage, with sample variation during live allocation.

Fixed-scene responsiveness and throughput improved in this rerun, while scene churn significantly reduced background throughput and increased CPU usage. Scene construction/configuration is application/UI-side in both modes, so expensive scene changes still block UI and appear in maximum input/heartbeat times. Worker-side picking also competes with color rendering. Background mode is not a universal performance win.

**Compatibility verdict:** the common global Scene/Configure/Render/PickAt workflow is compatible in both paths, but the entire IDriver surface is not yet interchangeable. Camera, LocalScene, synchronized roots, Rectangle, Size, ViewTransform, GetCurrentScene, Supports and Timeout still route through the restricted synchronous Driver accessor in background mode. Render callback affinity changes, FPS measures copied frames in background mode, and InvalidateVisual only redraws cached pixels there. Use Render/RequestRender for fresh content. Those differences must be resolved or documented before claiming complete API/behavior compatibility.

1. **Driver-state compatibility:** Scene is now the application-facing global scene in both modes. Its setter queues ordered driver assignment; its getter immediately returns the new global scene. Configure remains application-side and the worker no longer configures it. Ordinary configured global updates need no async driver delegate. Direct synchronized-root/Size access still cannot safely expose mutable worker state; UpdateDriverSceneAsync is optional for that state. SetSceneAsync is optional assignment completion, not required scene access. Synchronous `PickAt` is now preserved: non-owner calls queue and wait, owner calls execute directly. This adds preceding worker queue/render delay to the inherently synchronous picking render. Commands progress without waiting for UI frame acknowledgement; the separately published color buffer remains immutable. Active render-callback picking reentry is rejected and worker callbacks must not synchronously wait for UI dispatch.
2. **Event affinity:** render and scene mouse callbacks run on the owner thread. Existing UI-touching handlers must be adapted. Ordinary notifications are marshalled to UI without a render lock.
3. **Interaction latency:** input handlers return quickly, but picking/rasterization still serialize on the worker. Actual input-to-displayed-frame latency needs native compositor instrumentation. Extra copies and backpressure can increase latency even when UI controls remain responsive.
4. **Performance tradeoffs:** equal-churn throughput was lower and CPU consumption higher in background mode. Adaptive interaction resolution/detail is a potential separate optimization, not evidence that threading alone helps.
5. **Platforms/custom interactions:** browser intentionally falls back to UI mode. Native Windows is exercised; physical monitor changes, mobile, other desktops, concurrent panels and full custom pointer-capture semantics still need coverage. An earlier contour construction exception did not recur after explicit scopes, but needs continued observation before a blanket production-default claim.

The static opt-in is usable for applications adopting owner-thread APIs now. Pool retention itself is no longer being treated as a default-mode blocker without evidence of unreusable growth.
