using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using WinCleaner.App.ViewModels;

namespace WinCleaner.App;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        DataContext = new MainViewModel();
        InitializeComponent();
        SystemThemeWatcher.Watch(this);   // follow Windows light/dark
    }
}
