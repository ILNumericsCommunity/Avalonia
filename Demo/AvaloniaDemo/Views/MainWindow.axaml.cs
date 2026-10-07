using System;
using Avalonia.Controls;

namespace AvaloniaDemo.Views;

public partial class MainWindow : Window
{
    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        if (Content is MainView view)
            view.DisposeRenderer();
        base.OnClosed(e);
    }

    public MainWindow()
    {
        InitializeComponent();
    }
}
