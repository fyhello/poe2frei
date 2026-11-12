using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using System.Security.Principal;

namespace FreiAtlas.Licensing;

public interface IMachineIdentityProvider
{
    string GetMachineHash();
}

public sealed class WindowsMachineIdentityProvider : IMachineIdentityProvider
{
    public string GetMachineHash()
    {
        var machineGuid = ReadMachineGuid();
        if (string.IsNullOrWhiteSpace(machineGuid))
        {
            machineGuid = Environment.MachineName;
            Trace.TraceWarning("MachineGuid unavailable; using machine name fallback.");
        }

        var userSid = ReadUserSid();
        if (string.IsNullOrWhiteSpace(userSid))
        {
            userSid = $"{Environment.UserDomainName}\\{Environment.UserName}";
            Trace.TraceWarning("User SID unavailable; using user name fallback.");
        }

        var volumeSerial = ReadSystemVolumeSerial();
        if (string.IsNullOrWhiteSpace(volumeSerial))
        {
            volumeSerial = "00000000";
            Trace.TraceWarning("System volume serial unavailable; using zero fallback.");
        }

        var material = $"v1|{machineGuid}|{userSid}|{volumeSerial}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    private static string? ReadMachineGuid()
    {
        var value = Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography",
            "MachineGuid",
            null) as string;
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }

        using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)
            .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        return (key?.GetValue("MachineGuid") as string)?.Trim();
    }

    private static string? ReadUserSid()
    {
        try
        {
            return WindowsIdentity.GetCurrent().User?.Value;
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string? ReadSystemVolumeSerial()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory);
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        try
        {
            if (!GetVolumeInformationW(
                    root,
                    null,
                    0,
                    out var serial,
                    out _,
                    out _,
                    null,
                    0))
            {
                return null;
            }

            return serial.ToString("X8");
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(
        string rootPathName,
        StringBuilder? volumeNameBuffer,
        int volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        StringBuilder? fileSystemNameBuffer,
        int fileSystemNameSize);
}

public sealed class StaticMachineIdentityProvider(string machineHash) : IMachineIdentityProvider
{
    public string GetMachineHash() => machineHash;
}
