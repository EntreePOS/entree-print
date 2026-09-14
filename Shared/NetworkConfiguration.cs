using System.Net;

namespace EntreePrint.Configuration;

internal static class NetworkConfiguration
{
    public static string NormalizeBindAddress(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Bind address is required. Use localhost for this computer or 0.0.0.0 for the local network.");
        value = value.Trim();
        if (value.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return "127.0.0.1";
        if (!IPAddress.TryParse(value, out var address))
            throw new ArgumentException("Bind address must be a local IP address or localhost. Use 0.0.0.0 to listen on all IPv4 interfaces.");
        return address.ToString();
    }

    public static int ValidatePort(int value, string name) => value is >= 1 and <= 65535
        ? value : throw new ArgumentException($"{name} must be between 1 and 65535.");

    public static bool IsLanAccessible(string bindAddress) => !IPAddress.IsLoopback(IPAddress.Parse(NormalizeBindAddress(bindAddress)));

    public static string ApplyLanPreference(string bindAddress, bool enabled) => IsLanAccessible(bindAddress) == enabled
        ? NormalizeBindAddress(bindAddress) : enabled ? "0.0.0.0" : "127.0.0.1";

    public static Uri ServiceUri(string bindAddress, int port, bool localClient = false)
    {
        var address = IPAddress.Parse(NormalizeBindAddress(bindAddress));
        if (localClient)
        {
            if (address.Equals(IPAddress.Any)) address = IPAddress.Loopback;
            else if (address.Equals(IPAddress.IPv6Any)) address = IPAddress.IPv6Loopback;
        }
        return new UriBuilder(Uri.UriSchemeHttp, address.ToString(), ValidatePort(port, "Plugin port")).Uri;
    }
}
