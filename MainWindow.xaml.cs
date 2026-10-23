using System.Collections.Specialized;
using System.Windows;
using System.Windows.Threading;
using LiveInterp.ViewModels;

namespace LiveInterp;

public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel = DataContext as MainViewModel;
        if (_viewModel != null)
            _viewModel.Segments.CollectionChanged += OnSegmentsChanged;
    }

    private void OnSegmentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (SubtitleList.Items.Count > 0)
                SubtitleList.ScrollIntoView(SubtitleList.Items[^1]);
        }), DispatcherPriority.Background);
    }

    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.ApiKey = ApiKeyBox.Password;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_viewModel != null)
            _viewModel.Segments.CollectionChanged -= OnSegmentsChanged;
        _viewModel?.Shutdown();
        _viewModel = null;
    }
}
