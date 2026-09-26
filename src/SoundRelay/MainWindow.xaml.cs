using System.Windows;
using SoundRelay.ViewModels;

namespace SoundRelay;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        Closing += (_, _) => _viewModel.OnClosing();
    }
}
