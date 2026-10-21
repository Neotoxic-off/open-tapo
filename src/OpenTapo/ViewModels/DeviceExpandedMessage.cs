using CommunityToolkit.Mvvm.Messaging.Messages;

namespace OpenTapo.ViewModels;

/// <summary>Sent when a device row unfolds, so every other row folds: one open row at a time.</summary>
public sealed class DeviceExpandedMessage(DeviceViewModel device) : ValueChangedMessage<DeviceViewModel>(device);
