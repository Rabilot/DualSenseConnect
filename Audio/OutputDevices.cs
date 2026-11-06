using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace DualSenseConnect.Audio;

/// <summary>
/// Finds the VB-CABLE playback device that acts as the "DualSense" output and switches
/// the Windows default output to it and back.
/// </summary>
static class OutputDevices
{
    const string CableAdapterName = "VB-Audio Virtual Cable";

    public const string CableDownloadUrl = "https://vb-audio.com/Cable/";

    /// <summary>The cable's playback endpoint ("CABLE Input", or whatever the user renamed it to); null if not installed.</summary>
    public static (string Id, string Name)? FindCable()
    {
        using var enumerator = new MMDeviceEnumerator();
        // Matching the adapter name keeps working after the endpoint is renamed to "DualSense".
        // VB-CABLE 3.3+ also has a 16-channel "CABLE In 16ch" endpoint; the stereo one is the one to use.
        var cable = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Where(d => d.DeviceFriendlyName == CableAdapterName)
            .OrderBy(d => d.FriendlyName.Contains("16ch", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        return cable == null ? null : (cable.ID, cable.FriendlyName);
    }

    public static string? GetDefault(Role role)
    {
        using var enumerator = new MMDeviceEnumerator();
        try
        {
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, role).ID;
        }
        catch (COMException)
        {
            return null;   // no playback device at all
        }
    }

    /// <summary>Same call as picking a device in the Windows sound settings.</summary>
    public static void SetDefault(string deviceId, params Role[] roles)
    {
        var policy = (IPolicyConfig)new PolicyConfigClient();
        try
        {
            foreach (var role in roles)
                Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, role));
        }
        finally
        {
            Marshal.ReleaseComObject(policy);
        }
    }

    public static bool Exists(string deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        try
        {
            return enumerator.GetDevice(deviceId).State == DeviceState.Active;
        }
        catch (COMException)
        {
            return false;
        }
    }

    // Undocumented but stable since Windows 7; used by the Windows sound settings and many tools.
    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    class PolicyConfigClient
    {
    }

    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(string deviceId, IntPtr format);
        [PreserveSig] int GetDeviceFormat(string deviceId, bool defaultFormat, IntPtr format);
        [PreserveSig] int ResetDeviceFormat(string deviceId);
        [PreserveSig] int SetDeviceFormat(string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod(string deviceId, bool defaultPeriod, IntPtr defaultPeriodOut, IntPtr minPeriod);
        [PreserveSig] int SetProcessingPeriod(string deviceId, IntPtr period);
        [PreserveSig] int GetShareMode(string deviceId, IntPtr mode);
        [PreserveSig] int SetShareMode(string deviceId, IntPtr mode);
        [PreserveSig] int GetPropertyValue(string deviceId, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue(string deviceId, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, Role role);
        [PreserveSig] int SetEndpointVisibility(string deviceId, bool visible);
    }
}
