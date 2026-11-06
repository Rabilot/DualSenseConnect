using System.Runtime.InteropServices;

namespace DualSenseConnect.Microphone;

/// <summary>Mutes the default capture devices through the Windows Core Audio API.</summary>
static class MicMute
{
    const int ECapture = 1;
    const int EConsole = 0;
    const int ECommunications = 2;
    const int ClsctxAll = 0x17;

    /// <summary>Returns null when there is no default microphone.</summary>
    public static bool? IsMuted()
    {
        var volume = GetEndpointVolume(EConsole);
        if (volume == null)
            return null;
        try
        {
            Marshal.ThrowExceptionForHR(volume.GetMute(out bool muted));
            return muted;
        }
        finally
        {
            Marshal.ReleaseComObject(volume);
        }
    }

    /// <summary>Applies to both the default and the default communications microphone.</summary>
    public static void SetMuted(bool muted)
    {
        foreach (int role in new[] { EConsole, ECommunications })
        {
            var volume = GetEndpointVolume(role);
            if (volume == null)
                continue;
            try
            {
                var context = Guid.Empty;
                Marshal.ThrowExceptionForHR(volume.SetMute(muted, ref context));
            }
            finally
            {
                Marshal.ReleaseComObject(volume);
            }
        }
    }

    static IAudioEndpointVolume? GetEndpointVolume(int role)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(ECapture, role, out var device) != 0 || device == null)
                return null;
            try
            {
                var iid = typeof(IAudioEndpointVolume).GUID;
                Marshal.ThrowExceptionForHR(device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out object volume));
                return (IAudioEndpointVolume)volume;
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumerator
    {
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
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
