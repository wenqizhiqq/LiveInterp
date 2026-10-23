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
        if (_viewModel == null) return;

        _viewModel.ChatMessages.CollectionChanged += OnChatMessagesChanged;
        _viewModel.AutoStart();
        CommandBox.Focus();
    }

    private void OnChatMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        Dispatcher.BeginInvoke(new Action(() => ChatScroll.ScrollToEnd()), DispatcherPriority.Background);
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_viewModel != null)
            _viewModel.ChatMessages.CollectionChanged -= OnChatMessagesChanged;
        _viewModel?.Shutdown();
        _viewModel = null;
    }
}
