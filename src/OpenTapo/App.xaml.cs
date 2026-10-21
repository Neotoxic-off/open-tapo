using System.Windows;
using OpenTapo.Diagnostics;
using OpenTapo.Services;
using OpenTapo.ViewModels;
using OpenTapo.Views;

namespace OpenTapo;

public partial class App : Application
{
    private const string SingleInstanceName = "OpenTapo.SingleInstance";
    private const string ShowPanelEventName = "OpenTapo.ShowPanel";

    private Mutex? _singleInstance;
    private EventWaitHandle? _showRequests;
    private TrayIcon? _tray;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            ErrorLog.Write(args.Exception);
            args.Handled = true;
        };

        if (e.Args.Contains("--probe"))
        {
            await DeviceProbe.RunAsync(Console.Out);
            Shutdown();
            return;
        }

        _singleInstance = new Mutex(initiallyOwned: true, SingleInstanceName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            // Launching the exe again just brings the running panel up (the event may not exist yet if the first instance is still starting).
            if (EventWaitHandle.TryOpenExisting(ShowPanelEventName, out EventWaitHandle? showRequest))
            {
                using (showRequest)
                {
                    showRequest.Set();
                }
            }

            Shutdown();
            return;
        }

        MainViewModel viewModel = new(new CredentialStore(), new ManualDeviceStore());
        PanelWindow panel = new(viewModel);
        _tray = new TrayIcon(panel);
        ListenForShowRequests(panel);
        panel.ShowNearTray();
        await viewModel.InitializeAsync();
    }

    private void ListenForShowRequests(PanelWindow panel)
    {
        _showRequests = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, ShowPanelEventName);
        ThreadPool.RegisterWaitForSingleObject(
            _showRequests,
            (_, _) => Dispatcher.Invoke(panel.ShowNearTray),
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _showRequests?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
