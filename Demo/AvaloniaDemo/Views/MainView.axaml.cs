using System;
using System.Collections.Generic;
using System.Drawing;
using Avalonia.Controls;
using ILNumerics;
using ILNumerics.Drawing;
using ILNumerics.Drawing.Plotting;
using static ILNumerics.Globals;
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
    public MainView()
    {
        InitializeComponent();

        sceneComboBox.ItemsSource = Enum.GetNames(typeof(Scenes));
        sceneComboBox.SelectedIndex = (int) Scenes.Surface3DSinc;
    }

    private void SceneComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sceneComboBox.SelectedIndex < 0)
            return;

        switch (sceneComboBox.SelectedIndex)
        {
            case (int) Scenes.LinePlotXY:
            {
                // From: https://ilnumerics.net/examples.php?exid=ed2f92d6ec1927569ab5c3d4c7826845
                Array<float> n = linspace<float>(-6.2f, 6.2f, 100);
                Array<float> data = n.Concat(sin(n), 0).Concat(cos(n), 0);
                data[1, full] += tosingle(rand(1, data.S[1]));
                ilPanel.Scene = new Scene
                {
                    new PlotCube(twoDMode: true)
                    {
                        new LinePlot(data["0,1;:"], lineColor: Color.Red, markerStyle: MarkerStyle.Plus),
                        new LinePlot(data["0,2;:"], lineColor: Color.Green, markerStyle: MarkerStyle.Diamond, markerColor: Color.Green),
                        new Legend("sin(data)", "cos(data)")
                    }
                };
                break;
            }
            case (int) Scenes.ImageSCTerrain:
            {
                // From: https://ilnumerics.net/3d-contour-plots.html
                Array<float> terrainData = tosingle(SpecialData.terrain[":;0:250"]);
                ilPanel.Scene = new Scene { new PlotCube(twoDMode: true) { new ImageSCPlot(terrainData, Colormaps.Hot) } };
                break;
            }
            case (int) Scenes.Contour3DTerrain:
            {
                // From: https://ilnumerics.net/3d-contour-plots.html
                Array<float> terrainData = tosingle(SpecialData.terrain["100:end;0:300"]);
                ilPanel.Scene = new Scene
                {
                    new PlotCube(twoDMode: false)
                    {
                        new ContourPlot(terrainData, create3D: true,
                                        levels: new List<ContourLevel>
                                        {
                                            new() { Text = "Coast", Value = 5, LineWidth = 3 }, new() { Text = "Plateau", Value = 1000, LineWidth = 3 },
                                            new() { Text = "Basis 1", Value = 1500, LineWidth = 3, LineStyle = DashStyle.PointDash },
                                            new() { Text = "High", Value = 3000, LineWidth = 3 },
                                            new() { Text = "Rescue", Value = 4200, LineWidth = 3, LineStyle = DashStyle.Dotted },
                                            new() { Text = "Peak", Value = 5000, LineWidth = 3 }
                                        }),
                        new Surface(terrainData)
                        {
                            Wireframe = { Visible = false }, UseLighting = true,
                            Children = { new Legend { Location = new PointF(1f, .1f) }, new Colorbar { Location = new PointF(1, .4f), Anchor = new PointF(1, 0) } }
                        }
                    }
                };
                break;
            }
            case (int) Scenes.Surface3DSinc:
            {
                // From: https://ilnumerics.net/surface-plots.html
                Array<double> sincData = SpecialData.sinc(50, 60);
                ilPanel.Scene = new Scene { new PlotCube(twoDMode: false) { new Surface(tosingle(sincData), colormap: Colormaps.Jet) { new Colorbar() } } };
                break;
            }
            default:
                return;
        }

        ilPanel.Scene.Configure();
        ilPanel.InvalidateVisual();
    }
}
