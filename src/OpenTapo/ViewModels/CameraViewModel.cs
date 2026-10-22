using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTapo.Services;
using OpenTapo.Tapo;

namespace OpenTapo.ViewModels;

public sealed partial class CameraViewModel : DeviceViewModel
{
    private readonly List<(SettingToggle Toggle, CameraSwitch Switch)> _switches = [];
    private readonly CameraRequest[] _statusRequests;

    [ObservableProperty]
    private string _lastAlarm = "";

    /// <summary>Battery level for battery cameras (e.g. C410), shown on the row; empty for mains-powered ones.</summary>
    [ObservableProperty]
    private string _battery = "";

    [ObservableProperty]
    private bool _isBatteryLow;

    [ObservableProperty]
    private bool _hasStorage;

    [ObservableProperty]
    private double _storageUsedPercent;

    [ObservableProperty]
    private string _storage = "";

    [ObservableProperty]
    private bool _hasNightVision;

    [ObservableProperty]
    private NightVisionMode? _nightVision;

    /// <summary>The row switch: on means the camera is watching, off means privacy mode (lens covered).</summary>
    [ObservableProperty]
    private bool _isActive = true;

    /// <summary>False for devices with no lens, such as the H200 hub: the row switch is hidden.</summary>
    [ObservableProperty]
    private bool _supportsPrivacy = true;

    public CameraViewModel(TapoDevice device, DeviceSessions sessions)
        : base(device, sessions)
    {
        MotionDetection = Toggle("Motion detection", "", CameraSwitch.MotionDetection);
        Settings =
        [
            Toggle("Person detection", "", CameraSwitch.PersonDetection),
            Toggle("Auto tracking", "", CameraSwitch.AutoTracking),
            Toggle("Alarm on detection", "", CameraSwitch.AlarmOnDetection),
            Toggle("Status LED", "", CameraSwitch.StatusLed),
        ];
        Flip = new SettingToggle("Flip image", "", flipped => SendAsync(CameraApi.SetFlipped(flipped)));

        _statusRequests =
        [
            CameraApi.DeviceInfo,
            CameraApi.SdCard,
            CameraApi.LastAlarm,
            CameraApi.Connection,
            CameraApi.NightVision,
            CameraApi.Rotation,
            CameraSwitch.Privacy.Get,
            CameraApi.ChildDevices,
            .. _switches.Select(binding => binding.Switch.Get),
        ];
    }

    public SettingToggle MotionDetection { get; }

    public SettingToggle Flip { get; }

    public IReadOnlyList<SettingToggle> Settings { get; }

    public IReadOnlyList<NightVisionMode> NightVisionModes => NightVisionMode.All;

    /// <summary>Sensors and cameras paired to an H200 hub.</summary>
    public ObservableCollection<ChildReading> Children { get; } = [];

    private CameraClient Client => Sessions.Camera(Device.Ip);

    [RelayCommand]
    private Task StartSirenAsync() => SendAsync(CameraApi.StartSiren);

    [RelayCommand]
    private Task StopSirenAsync() => SendAsync(CameraApi.StopSiren);

    partial void OnIsActiveChanged(bool value)
    {
        if (!IsApplying)
        {
            _ = SendAsync(CameraSwitch.Privacy.Set(!value));
        }
    }

    partial void OnNightVisionChanged(NightVisionMode? value)
    {
        if (!IsApplying && value is not null)
        {
            _ = SendAsync(CameraApi.SetNightVision(value.Value));
        }
    }

    protected override async Task LoadAsync()
    {
        IReadOnlyDictionary<string, JsonNode?> results = await Client.ExecuteAsync(_statusRequests);
        JsonNode? info = results.GetValueOrDefault(CameraApi.DeviceInfo.Method)?["device_info"]?["basic_info"];
        JsonNode? disk = results.GetValueOrDefault(CameraApi.SdCard.Method)?["harddisk_manage"]?["hd_info"]?[0]?["hd_info_1"];
        JsonNode? alarm = results.GetValueOrDefault(CameraApi.LastAlarm.Method)?["system"]?["last_alarm_info"];
        string? nightVision = CameraApi.ReadNightVision(results);
        bool? privacy = CameraSwitch.Privacy.Read(results);
        // shortcut: only the first page of the hub's children is read; page with start_index for more.
        List<ChildReading> children = results.GetValueOrDefault(CameraApi.ChildDevices.Method)?["child_device_list"]?.AsArray()
            .OfType<JsonNode>().Select(ChildReading.From).ToList() ?? [];
        double total = Format.Gigabytes((string?)disk?["total_space"]);
        double free = Format.Gigabytes((string?)disk?["free_space"]);
        string alarmType = (string?)alarm?["last_alarm_type"] switch
        {
            "motion" => " · motion",
            "person" => " · person",
            null or "" => "",
            string other => " · " + other,
        };

        Apply(() =>
        {
            Name = (string?)info?["device_alias"] ?? "Camera";
            Subtitle = $"{info?["device_model"]} · {Device.Ip}";
            foreach ((SettingToggle toggle, CameraSwitch setting) in _switches)
            {
                toggle.Load(setting.Read(results));
            }

            IsActive = privacy != true;
            SupportsPrivacy = privacy is not null;
            Children.Clear();
            foreach (ChildReading child in children)
            {
                Children.Add(child);
            }

            Flip.Load(CameraApi.ReadFlipped(results));
            HasNightVision = nightVision is not null;
            NightVision = NightVisionModes.FirstOrDefault(mode => mode.Value == nightVision);
            LastAlarm = $"Last alert {Format.Ago((string?)alarm?["last_alarm_time"])}{alarmType}";
            Battery = (int?)info?["battery_percent"] is int percent
                ? $"{percent} %{((string?)info?["battery_charging"] == "YES" ? " ⚡" : "")}"
                : "";
            IsBatteryLow = (bool?)info?["low_battery"] == true;
            HasStorage = total > 0;
            StorageUsedPercent = total > 0 ? 100 * (total - free) / total : 0;
            Storage = total > 0 ? $"SD card · {free:0.#} GB free of {total:0.#} GB" : "";
            Signal = Format.Signal((int?)results.GetValueOrDefault(CameraApi.Connection.Method)?["rssiValue"]);
        });
    }

    private SettingToggle Toggle(string label, string glyph, CameraSwitch setting)
    {
        SettingToggle toggle = new(label, glyph, enabled => SendAsync(setting.Set(enabled)));
        _switches.Add((toggle, setting));
        return toggle;
    }

    private Task SendAsync(CameraRequest request) => RunAsync(() => Client.ExecuteAsync(request));
}
