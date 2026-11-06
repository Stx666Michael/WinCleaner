using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using WinCleaner.App.ViewModels;

namespace WinCleaner.App;

public partial class MainWindow : FluentWindow
{
    private MainViewModel Vm => (MainViewModel)DataContext;

    public MainWindow()
    {
        DataContext = new MainViewModel();
        InitializeComponent();
        SystemThemeWatcher.Watch(this);   // follow Windows light/dark
        PreviewMouseDown += OnPreviewMouseDown;
    }

    /// <summary>Mouse back/forward buttons navigate the explorer, like in a file manager or browser.</summary>
    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Vm.State != AppState.Explore) return;
        if (e.ChangedButton == MouseButton.XButton1) { Vm.Explore.GoBackCommand.Execute(null); e.Handled = true; }
        else if (e.ChangedButton == MouseButton.XButton2) { Vm.Explore.GoForwardCommand.Execute(null); e.Handled = true; }
    }

    // Hovering a list row lights up the matching treemap block (and the other way round, through HoverItem).
    private void Row_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ExploreItem item }) Vm.Explore.HoverItem = item;
    }

    private void Row_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ExploreItem item } && ReferenceEquals(Vm.Explore.HoverItem, item))
            Vm.Explore.HoverItem = null;
    }
}
