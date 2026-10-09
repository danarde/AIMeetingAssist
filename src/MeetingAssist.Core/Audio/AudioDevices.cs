using NAudio.CoreAudioApi;
using Serilog;

namespace MeetingAssist.Core.Audio;

/// <summary>
/// One selectable endpoint. <paramref name="Id"/> is the WASAPI endpoint id, which is stable
/// across reboots and is what gets persisted — friendly names are not unique and change when a
/// driver updates.
/// </summary>
public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault)
{
    public string Display => IsDefault ? $"{Name}  (Windows default)" : Name;
}

/// <summary>
/// Endpoint enumeration for the device pickers (spec FR-2.5).
///
/// Nothing here opens a stream or reads audio; it reads the endpoint list only. Every method
/// degrades to an empty list rather than throwing, because a machine with no microphone is a
/// perfectly ordinary machine and must not crash a settings window.
/// </summary>
public static class AudioDevices
{
    /// <summary>Microphones — the "You" channel.</summary>
    public static IReadOnlyList<AudioDeviceInfo> Capture() => Enumerate(DataFlow.Capture);

    /// <summary>
    /// Playback endpoints. Loopback captures what one of these is playing, which is how the
    /// far end of the meeting is recorded.
    /// </summary>
    public static IReadOnlyList<AudioDeviceInfo> Render() => Enumerate(DataFlow.Render);

    private static IReadOnlyList<AudioDeviceInfo> Enumerate(DataFlow flow)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();

            string? defaultId = null;
            try
            {
                using var def = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
                defaultId = def.ID;
            }
            catch (Exception ex)
            {
                // No endpoint of this kind at all — headless machines, or every device disabled.
                Log.Debug(ex, "No default {Flow} endpoint", flow);
            }

            var devices = new List<AudioDeviceInfo>();
            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (device)
                {
                    devices.Add(new AudioDeviceInfo(device.ID, device.FriendlyName, device.ID == defaultId));
                }
            }

            // Default first, then alphabetical: the list is read by someone looking for a name.
            return [.. devices.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name, StringComparer.CurrentCulture)];
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not enumerate {Flow} endpoints", flow);
            return [];
        }
    }

    /// <summary>
    /// Resolves a persisted endpoint id, or null when that device is no longer present — which
    /// is the normal outcome for a USB headset that is currently unplugged. Callers fall back to
    /// the Windows default rather than failing.
    /// </summary>
    public static MMDevice? TryResolve(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return null;

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(deviceId);

            if (device is null) return null;

            if (device.State != DeviceState.Active)
            {
                device.Dispose();
                Log.Warning("Pinned audio device {DeviceId} is present but not active", deviceId);
                return null;
            }

            return device;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Pinned audio device {DeviceId} is not available", deviceId);
            return null;
        }
    }
}
