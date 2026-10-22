using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTapo.Services;
using OpenTapo.Tapo;

namespace OpenTapo.ViewModels;

/// <summary>A Tapo robot vacuum: status and battery on the row, start / pause / dock in the details.</summary>
public sealed partial class VacuumViewModel : DeviceViewModel
{
    private const string DeviceInfo = "get_device_info";
    private const string VacuumStatus = "getVacStatus";
    private const string BatteryInfo = "getBatteryInfo";
    private const int Paused = 7;

    [ObservableProperty]
    private string _state = "";

    [ObservableProperty]
    private string _battery = "";

    private int _status = -1;

    public VacuumViewModel(TapoDevice device, DeviceSessions sessions)
        : base(device, sessions)
    {
    }

    private SmartClient Client => Sessions.Smart(Device);

    /// <summary>Starts cleaning, or resumes it when paused (a fresh start would drop the current run).</summary>
    [RelayCommand]
    private Task StartAsync() => _status == Paused
        ? SendAsync("setRobotPause", new JsonObject { ["pause"] = false })
        : SendAsync("setSwitchClean", new JsonObject { ["clean_mode"] = 0, ["clean_on"] = true, ["clean_order"] = true, ["force_clean"] = false });

    [RelayCommand]
    private Task PauseAsync() => SendAsync("setRobotPause", new JsonObject { ["pause"] = true });

    [RelayCommand]
    private Task DockAsync() => SendAsync("setSwitchCharge", new JsonObject { ["switch_charge"] = true });

    protected override async Task LoadAsync()
    {
        IReadOnlyDictionary<string, JsonNode?> results = await Client.ExecuteAsync(DeviceInfo, VacuumStatus, BatteryInfo);
        JsonNode info = results.GetValueOrDefault(DeviceInfo) ?? throw new TapoException("The vacuum did not report its state.");
        int status = (int?)results.GetValueOrDefault(VacuumStatus)?["status"] ?? -1;
        int? battery = (int?)results.GetValueOrDefault(BatteryInfo)?["battery_percentage"];

        Apply(() =>
        {
            Name = Format.Nickname((string?)info["nickname"]) ?? (string?)info["model"] ?? Device.Model;
            Subtitle = $"{info["model"]} · {Device.Ip}";
            _status = status;
            State = StatusText(status);
            Battery = battery is int percent ? $"{percent} %" : "";
            Signal = Format.Signal((int?)info["rssi"]);
        });
    }

    // python-kasa's Status enum for getVacStatus.
    internal static string StatusText(int status) => status switch
    {
        0 => "Idle",
        1 => "Cleaning",
        2 => "Mapping",
        4 => "Returning to dock",
        5 => "Charging",
        6 => "Charged",
        Paused => "Paused",
        8 => "Off the dock",
        100 => "Needs attention",
        _ => "",
    };

    private Task SendAsync(string method, JsonObject parameters) => RunAsync(() => Client.CallAsync(method, parameters));
}
