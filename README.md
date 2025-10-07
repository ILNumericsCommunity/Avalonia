# ILNumerics.Community.Avalonia

[![Nuget](https://img.shields.io/nuget/v/ILNumerics.Community.Avalonia?style=flat-square&logo=nuget&color=blue)](https://www.nuget.org/packages/ILNumerics.Community.Avalonia)

Integration package for ILNumerics (http://ilnumerics.net/) scene graphs and plot cubes with Avalonia (https://avaloniaui.net/platforms) cross-platform UI framework. `ILNumerics.Community.Avalonia` provides an ILNumerics panel implementation for Avalonia and a set of helper / convenience functions to make embedding ILNumerics scenes into Avalonia apps straightforward.
This package makes it easy to host ILNumerics scene graphs and 2D/3D plot cubes inside Avalonia applications. The panel acts as a bridge between Avalonia's UI system and ILNumerics rendering, allowing you to build interactive visualizations that run cross-platform.

Note: This package currently uses a software renderer on all platforms. It generally provides a smooth rendering experience for moderate data sizes, but performance may vary per platform and scene complexity.

## Compatibility

- .NET: targets `.NET 9`.
- ILNumerics: `ILNumerics 7.3+`
- Avalonia: compatible with `Avalonia 11`.

> Note: Desktop platforms (Windows, Linux, macOS) are working well. There are currently some outstanding issues on mobile platforms; please refer to the issue tracker for details and status updates.

## Usage

Add the ILNumerics panel to your user interface (in XAML or in code). The example below shows a simple XAML usage; adjust XML namespaces as appropriate for your project:

```csharp
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
```

Notes:
- Call `Configure()` on the scene after setup.
- Assign a new `Scene` or modify an existing one to update what is rendered.

## Examples and demos

This repository includes demo projects under the `Demo/` folder showcasing usage across desktop, browser and mobile targets. Run the demos to see concrete usage and to experiment with different scenes and rendering configurations.

### License

ILNumerics.Community.Avalonia is licensed under the terms of the MIT license (<http://opensource.org/licenses/MIT>, see LICENSE.txt).
