using Avalonia.Controls;
using ILNumerics;
using ILNumerics.Drawing;
using ILNumerics.Drawing.Plotting;
using static ILNumerics.ILMath;

namespace AvaloniaDemo
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            Array<double> B = SpecialData.sinc(50, 60);

            ilPanel.Scene = new Scene
            {
                new PlotCube(twoDMode: false)
                {
                    new Surface(tosingle(B),
                                colormap: Colormaps.Hot) { new Colorbar() }
                }
            };
            ilPanel.Scene.Configure();
        }
    }
}