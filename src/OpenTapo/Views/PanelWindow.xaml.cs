using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OpenTapo.ViewModels;

namespace OpenTapo.Views;

/// <summary>Flyout anchored above the notification area; hides when it loses focus, like the Windows quick settings.</summary>
[ExcludeFromCodeCoverage(Justification = "WPF shell: needs a desktop session and a notification area.")]
public partial class PanelWindow : Window
{
    private const double ScreenMargin = 12;
    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(30);

    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _autoRefresh;

    public PanelWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        _autoRefresh = new DispatcherTimer { Interval = AutoRefreshInterval };
        _autoRefresh.Tick += (_, _) => RefreshIfSignedIn();
        IsVisibleChanged += OnIsVisibleChanged;
        _viewModel.PropertyChanged += (_, e) =>
        {
            // Never leave the typed password sitting in the box once the account state changes.
            if (e.PropertyName == nameof(MainViewModel.IsSignedIn))
            {
                PasswordInput.Clear();
            }
        };
        SizeChanged += (_, _) => AnchorToTray();
    }

    public DateTime LastHiddenAt { get; private set; }

    public void ShowNearTray()
    {
        Show();
        AnchorToTray();
        Activate();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowBackdrop.Apply(this);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        Hide();
        LastHiddenAt = DateTime.UtcNow;
    }

    private void AnchorToTray()
    {
        Rect workArea = SystemParameters.WorkArea;
        Left = workArea.Right - ActualWidth - ScreenMargin;
        Top = workArea.Bottom - ActualHeight - ScreenMargin;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            RefreshIfSignedIn();
            _autoRefresh.Start();
        }
        else
        {
            _autoRefresh.Stop();
        }
    }

    private void RefreshIfSignedIn()
    {
        if (_viewModel.IsSignedIn && _viewModel.RefreshCommand.CanExecute(null))
        {
            _viewModel.RefreshCommand.Execute(null);
        }
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        string password = ((PasswordBox)sender).Password;
        _viewModel.Password = password;
        PasswordPlaceholder.Visibility = password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
