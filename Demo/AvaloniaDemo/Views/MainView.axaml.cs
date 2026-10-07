using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using Avalonia.Controls;
using ILNumerics;
using ILNumerics.Drawing;
using ILNumerics.Drawing.Plotting;
using RenderingPanel = ILNumerics.Community.Avalonia.Panel;
using static ILNumerics.ILMath;

namespace AvaloniaDemo.Views;

public enum Scenes
{
    LinePlotXY,
    ImageSCTerrain,
    Contour3DTerrain,
    Surface3DSinc
}

public partial class MainView : UserControl
{
    private RenderingPanel _panel = null!;
    private long _generation;

    public MainView()
    {
        InitializeComponent();
        _panel = ilPanel;

        sceneComboBox.ItemsSource = Enum.GetNames(typeof(Scenes));
        sceneComboBox.SelectedIndex = (int) Scenes.Surface3DSinc;
    }

    private void SceneComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sceneComboBox.SelectedIndex < 0)
            return;

        SetSelectedScene();
    }

    private void SetSelectedScene()
    {
        var index = sceneComboBox.SelectedIndex;
        if (index < 0)
            return;
        var generation = _generation;
        try
        {
            _panel.Scene = CreateScene((Scenes) index);
            _panel.Scene.Configure();
            _panel.InvalidateVisual();
        }
        catch (Exception error)
        {
            if (generation == _generation)
                rendererStatus.Text = error.Message;
        }
    }

    private void RendererComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_panel == null || rendererHost == null)
            return;
        _generation++;
        _panel.Dispose();
        RenderingPanel.UseBackgroundRendering = rendererComboBox.SelectedIndex == 1;
        _panel = new RenderingPanel();
        var panel = _panel;
        var generation = _generation;
        var lastStatusAt = 0L;
        panel.RenderingFailed += (_, error) =>
        {
            if (generation == _generation)
                rendererStatus.Text = error.Exception?.Message ?? "Render timeout";
        };
        panel.FramePresented += (_, _) =>
        {
            // Status layout can request another frame. Throttle updates so feedback does not run continuously.
            if (generation != _generation || Stopwatch.GetElapsedTime(lastStatusAt).TotalMilliseconds < 250)
                return;
            lastStatusAt = Stopwatch.GetTimestamp();
            rendererStatus.Text = $"{(panel.IsBackgroundRendering ? "Worker" : "UI")}: {panel.LastRenderMilliseconds:F1} ms";
        };
        rendererHost.Content = panel;
        SetSelectedScene();
    }

    /// <summary>Releases rendering resources when the view is permanently closed.</summary>
    public void DisposeRenderer() => _panel.Dispose();

    /// <summary>Creates deterministic demo data on the selected renderer's owner thread.</summary>
    public static Scene CreateScene(Scenes selected, int surfaceSize = 50)
    {
        using var scope = Scope.Enter();
        switch (selected)
        {
            case Scenes.LinePlotXY:
            {
                // From: https://ilnumerics.net/examples.php?exid=ed2f92d6ec1927569ab5c3d4c7826845
                Array<float> n = linspace<float>(-6.2f, 6.2f, 100);
                Array<float> data = n.Concat(sin(n), 0).Concat(cos(n), 0);
                return new Scene
                {
                    new PlotCube(twoDMode: true)
                    {
                        new LinePlot(data["0,1;:"], lineColor: Color.Red, markerStyle: MarkerStyle.Plus),
                        new LinePlot(data["0,2;:"], lineColor: Color.Green, markerStyle: MarkerStyle.Diamond, markerColor: Color.Green),
                        new Legend("sin(data)", "cos(data)")
                    }
                };
            }
            case Scenes.ImageSCTerrain:
            {
                // From: https://ilnumerics.net/3d-contour-plots.html
                Array<float> terrainData = tosingle(SpecialData.terrain[":;0:250"]);
                return new Scene { new PlotCube(twoDMode: true) { new ImageSCPlot(terrainData, Colormaps.Hot) } };
            }
            case Scenes.Contour3DTerrain:
            {
                // From: https://ilnumerics.net/3d-contour-plots.html
                Array<float> terrainData = tosingle(SpecialData.terrain["100:end;0:300"]);
                return new Scene
                {
                    new PlotCube(twoDMode: false)
                    {
                        new ContourPlot(terrainData, create3D: true,
                                        levels: new List<ContourLevel>
                                        {
                                            new ContourLevel { Text = "Coast", Value = 5, LineWidth = 3 }, new ContourLevel { Text = "Plateau", Value = 1000, LineWidth = 3 },
                                            new ContourLevel { Text = "Basis 1", Value = 1500, LineWidth = 3, LineStyle = DashStyle.PointDash },
                                            new ContourLevel { Text = "High", Value = 3000, LineWidth = 3 },
                                            new ContourLevel { Text = "Rescue", Value = 4200, LineWidth = 3, LineStyle = DashStyle.Dotted },
                                            new ContourLevel { Text = "Peak", Value = 5000, LineWidth = 3 }
                                        }),
                        new Surface(terrainData)
                        {
                            Wireframe = { Visible = false }, UseLighting = true,
                            Children = { new Legend { Location = new PointF(1f, .1f) }, new Colorbar { Location = new PointF(1, .4f), Anchor = new PointF(1, 0) } }
                        }
                    }
                };
            }
            case Scenes.Surface3DSinc:
            {
                // From: https://ilnumerics.net/surface-plots.html
                Array<double> sincData = SpecialData.sinc(surfaceSize, surfaceSize + 10);
                return new Scene { new PlotCube(twoDMode: false) { new Surface(tosingle(sincData), colormap: Colormaps.Jet) { new Colorbar() } } };
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(selected));
        }
    }
}
