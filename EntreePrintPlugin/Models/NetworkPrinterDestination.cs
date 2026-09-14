using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EntreePrintPlugin.Models;

// Identifies the configured Windows endpoint, not a printer serial number or a
// compatible page layout. No DNS lookup, printer probe or direct printing occurs.
public sealed record NetworkPrinterDestination(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("protocol")] string Protocol,
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("queue")] string? Queue)
{
    internal static NetworkPrinterDestination? From(PrinterStatusRecord printer)
    {
        if (printer.QueueType != 0 || printer.ConnectionServer.Length != 0 || printer.Name.StartsWith(@"\\") ||
            printer.PortName.Length == 0 || printer.PortName.Contains(',') || printer.PortName.StartsWith("USB", StringComparison.OrdinalIgnoreCase) ||
            !printer.PortMonitor.Equals("TCPMON.DLL", StringComparison.OrdinalIgnoreCase) ||
            printer.PortProtocol is not (1 or 2) || printer.PortNumber is not (>= 1 and <= 65535) ||
            !IPAddress.TryParse(printer.HostAddress, out var address)) return null;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return null;
        if (address.AddressFamily == AddressFamily.InterNetworkV6 &&
            (address.ScopeId != 0 || address.IsIPv6LinkLocal || address.IsIPv6Multicast)) return null;
        if (address.AddressFamily == AddressFamily.InterNetwork &&
            (address.GetAddressBytes()[0] is 0 or >= 224 || address.Equals(IPAddress.Broadcast))) return null;
        var protocol = printer.PortProtocol == 1 ? "raw" : "lpr";
        var queue = protocol == "lpr" ? printer.LprQueueName : null;
        if (queue is not null && (string.IsNullOrWhiteSpace(queue) || queue.Length > 255 || queue.Any(char.IsControl))) return null;
        var host = address.ToString();
        var port = printer.PortNumber.Value;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new object?[] { "windows-tcpip-v1", protocol, host, port, queue });
        var id = "tcpip:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new(id, protocol, host, port, queue);
    }
}
