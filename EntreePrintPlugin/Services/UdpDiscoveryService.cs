using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace EntreePrintPlugin.Services;

public sealed class UdpDiscoveryService(
    PluginSettings settings,
    ILogger<UdpDiscoveryService> logger,
    ServiceIdentity identity) : BackgroundService
{
    private const string DiscoveryMessage = "ENTREE_PRINT_DISCOVER";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Parse(settings.BindAddress), settings.DiscoveryPort));
        if (udp.Client.AddressFamily == AddressFamily.InterNetwork) udp.EnableBroadcast = true;

        logger.LogInformation("UDP discovery listening on port {Port}.", settings.DiscoveryPort);

        while (!stoppingToken.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await udp.ReceiveAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var message = Encoding.UTF8.GetString(received.Buffer).Trim();
            if (!string.Equals(message, DiscoveryMessage, StringComparison.Ordinal))
            {
                continue;
            }

            var payload = DiscoveryPayload(settings, identity, GetLanAddresses());
            var bytes = Encoding.UTF8.GetBytes(payload);
            await udp.SendAsync(bytes, received.RemoteEndPoint, stoppingToken);
        }
    }

    internal static string DiscoveryPayload(PluginSettings settings, ServiceIdentity identity, string[] addresses) => JsonSerializer.Serialize(new
            {
                ok = true,
                service = "entree-print-plugin",
                name = "ENTREE Print Plugin",
                serviceId = identity.ServiceId,
                bootId = identity.BootId,
                version = ServiceIdentity.Version,
                protocol = "entree-print",
                scheme = settings.Scheme,
                apiVersion = "0.0.1",
                port = settings.HttpPort,
                httpPort = settings.HttpPort,
                addresses
            });

    private static string[] GetLanAddresses()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up)
            .SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address.Address))
            .Select(address => address.Address.ToString())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}
