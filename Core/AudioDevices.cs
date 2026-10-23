using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LiveInterp.Core;

public sealed class AudioDevice
{
    public required string Name { get; init; }
    public required string Id { get; init; }
    internal MMDevice? Device { get; init; }

    public override string ToString() => Name;
}

public sealed class OutputDevice
{
    public required string Name { get; init; }
    public int Number { get; init; }

    public override string ToString() => Name;
}

public static class AudioDevices
{
    private static MMDeviceEnumerator? _enumerator;

    public static List<AudioDevice> GetCaptureDevices()
    {
        var list = new List<AudioDevice>();
        _enumerator ??= new MMDeviceEnumerator();
        foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            string name;
            try { name = device.FriendlyName; }
            catch { name = device.ID; }
            list.Add(new AudioDevice { Name = name, Id = device.ID, Device = device });
        }
        return list;
    }

    public static List<OutputDevice> GetOutputDevices()
    {
        var list = new List<OutputDevice>();
        for (int i = 0; i < WaveOut.DeviceCount; i++)
        {
            WaveOutCapabilities caps;
            try { caps = WaveOut.GetCapabilities(i); }
            catch { continue; }
            list.Add(new OutputDevice { Number = i, Name = caps.ProductName });
        }
        return list;
    }

    public static string? GetDefaultRenderName()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.FriendlyName;
        }
        catch
        {
            return null;
        }
    }

    public static void Shutdown()
    {
        _enumerator?.Dispose();
        _enumerator = null;
    }
}
