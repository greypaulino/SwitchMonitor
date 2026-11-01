using System.Runtime.InteropServices;

namespace SwitchMonitor.App.Services;

/// <summary>
/// The Windows master volume of the default playback device (speakers, headphones…), read and set through Core Audio.
/// Every call looks up the current default device, so switching outputs in Windows is followed. Null / false when
/// there is no playback device.
/// </summary>
public static class WindowsVolume
{
    private static Guid context = Guid.Empty;

    /// <summary>0–100, and whether it is muted; null without a playback device.</summary>
    public static (int Percent, bool Muted)? Read()
    {
        try
        {
            if (Endpoint() is not { } volume) return null;
            Marshal.ThrowExceptionForHR(volume.GetMasterVolumeLevelScalar(out float level));
            Marshal.ThrowExceptionForHR(volume.GetMute(out bool muted));
            return ((int)Math.Round(level * 100), muted);
        }
        catch (Exception error) when (error is COMException or InvalidCastException)
        {
            return null;
        }
    }

    public static bool SetPercent(int percent)
    {
        try
        {
            if (Endpoint() is not { } volume) return false;
            Marshal.ThrowExceptionForHR(volume.SetMasterVolumeLevelScalar(Math.Clamp(percent, 0, 100) / 100f, ref context));
            // Moving the volume up while muted turns the sound on, as the Windows flyout does.
            if (percent > 0) volume.SetMute(false, ref context);
            return true;
        }
        catch (Exception error) when (error is COMException or InvalidCastException)
        {
            return false;
        }
    }

    public static bool SetMuted(bool muted)
    {
        try
        {
            if (Endpoint() is not { } volume) return false;
            Marshal.ThrowExceptionForHR(volume.SetMute(muted, ref context));
            return true;
        }
        catch (Exception error) when (error is COMException or InvalidCastException)
        {
            return false;
        }
    }

    private static IAudioEndpointVolume? Endpoint()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        try
        {
            // eRender, eMultimedia
            if (enumerator.GetDefaultAudioEndpoint(0, 1, out var device) != 0 || device is null) return null;
            Guid iid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref iid, 0x17 /* CLSCTX_ALL */, IntPtr.Zero, out object volume) != 0) return null;
            return (IAudioEndpointVolume)volume;
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object result);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
