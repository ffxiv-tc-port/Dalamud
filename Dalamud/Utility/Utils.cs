using System.Security.Cryptography;

namespace Dalamud.Utility;

public class DeviceUtils
{
    private static readonly Lazy<string> deviceID = new(GenerateRandomMachineCode);

    public static string GetDeviceId() => deviceID.Value;

    public static string GenerateRandomMachineCode()
    {
        var parts = new string[3];
        for (var i = 0; i < 3; i++)
            parts[i] = RandomHex(32);
        
        return string.Join(':', parts);
    }
    
    private static string RandomHex(int len)
    {
        const string hex   = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        var          chars = new char[len];
        for (var i = 0; i < len; i++)
        {
            chars[i] = hex[new Random().Next(36)];
        }
        return new string(chars);
    }

    public static string GetMD5(byte[] payload)
    {
        var md5Bytes = MD5.HashData(payload);
        return BitConverter.ToString(md5Bytes).Replace("-", string.Empty).ToUpperInvariant();
    }

    private static string GetCPUId() => deviceID.Value.Split(':')[1];

    public static string GetMacAddress() => deviceID.Value.Split(':')[0];

    public static string GetMac() => RandomHex(32);

    private static string GetDiskSerialNumber() => deviceID.Value.Split(':')[2];
}
