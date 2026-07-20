using System.ComponentModel;
using System.Windows;

namespace RouterMonitor.Wpf.Views;

public partial class MainWindow : Window
{
    /// <summary>Set right before Application.Current.Shutdown() so the close handler lets it through instead of hiding to tray.</summary>
    public bool AllowClose { get; set; }

    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (AllowClose)
            return;

        e.Cancel = true;
        Hide();
    }
}
