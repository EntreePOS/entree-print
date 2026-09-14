using System.Text.Json;
using EntreePrintPlugin.Models;

namespace EntreePrintPlugin.Tests;

public sealed class NetworkPrinterDestinationTests
{
    internal static PrinterStatusRecord Direct => new()
    {
        Name = "厨房", QueueType = 0, PortName = "IP_Printer", PortMonitor = "TCPMON.DLL",
        HostAddress = "192.0.2.10", PortNumber = 9100, PortProtocol = 1
    };

    [Fact]
    public void RenamedQueuesAndPortsMatchTheSameConfiguredEndpoint()
    {
        var original = NetworkPrinterDestination.From(Direct)!;
        var renamed = NetworkPrinterDestination.From(Direct with
        {
            Name = "Kitchen backup", PortName = "another Windows port", DriverName = "another driver",
            HostAddress = "::ffff:192.0.2.10", PortMonitor = "tcpmon.dll", LprQueueName = "ignored by RAW"
        })!;
        Assert.Equal(original, renamed);
        Assert.Matches("^tcpip:[0-9a-f]{64}$", original.Id);
        Assert.Equal("raw", original.Protocol); Assert.Null(original.Queue);
    }

    [Fact]
    public void ProtocolPortHostAndLprQueueArePartOfDestinationIdentity()
    {
        var configurations = new[]
        {
            Direct, Direct with { HostAddress = "192.0.2.11" }, Direct with { PortNumber = 9101 },
            Direct with { PortProtocol = 2, PortNumber = 515, LprQueueName = "kitchen" },
            Direct with { PortProtocol = 2, PortNumber = 515, LprQueueName = "Kitchen" },
            Direct with { PortProtocol = 2, PortNumber = 515, LprQueueName = "厨房" }
        };
        var destinations = configurations.Select(printer => NetworkPrinterDestination.From(printer)!).ToArray();
        Assert.Equal(configurations.Length, destinations.Select(destination => destination.Id).Distinct().Count());
        Assert.Equal("厨房", destinations[^1].Queue);
    }

    [Fact]
    public void GlobalIpv6AddressesUseCanonicalSpelling()
    {
        var a = NetworkPrinterDestination.From(Direct with { HostAddress = "2001:0DB8:0:0:0:0:0:1" });
        var b = NetworkPrinterDestination.From(Direct with { HostAddress = "2001:db8::1" });
        Assert.NotNull(a); Assert.Equal(a, b);
    }

    [Theory]
    [InlineData("queue-type-missing")]
    [InlineData("remote-type")]
    [InlineData("remote-server")]
    [InlineData("shared-queue")]
    [InlineData("usb")]
    [InlineData("pooled")]
    [InlineData("missing-port")]
    [InlineData("custom-monitor")]
    [InlineData("protocol-missing")]
    [InlineData("protocol-unknown")]
    [InlineData("port-missing")]
    [InlineData("port-zero")]
    [InlineData("port-too-large")]
    [InlineData("lpr-queue-missing")]
    [InlineData("lpr-queue-control")]
    public void AmbiguousOrIndirectConfigurationHasNoComparableDestination(string scenario)
    {
        var printer = scenario switch
        {
            "queue-type-missing" => Direct with { QueueType = null },
            "remote-type" => Direct with { QueueType = 1 },
            "remote-server" => Direct with { ConnectionServer = "primary" },
            "shared-queue" => Direct with { Name = @"\\primary\kitchen" },
            "usb" => Direct with { PortName = "USB001" },
            "pooled" => Direct with { PortName = "IP_1,IP_2" },
            "missing-port" => Direct with { PortName = "" },
            "custom-monitor" => Direct with { PortMonitor = "vendor.dll" },
            "protocol-missing" => Direct with { PortProtocol = null },
            "protocol-unknown" => Direct with { PortProtocol = 99 },
            "port-missing" => Direct with { PortNumber = null },
            "port-zero" => Direct with { PortNumber = 0 },
            "port-too-large" => Direct with { PortNumber = 65536 },
            "lpr-queue-missing" => Direct with { PortProtocol = 2 },
            "lpr-queue-control" => Direct with { PortProtocol = 2, LprQueueName = "queue\nother" },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        Assert.Null(NetworkPrinterDestination.From(printer));
    }

    [Theory]
    [InlineData("printer.example.com")]
    [InlineData("")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("255.255.255.255")]
    [InlineData("239.1.2.3")]
    [InlineData("fe80::1")]
    [InlineData("fe80::1%4")]
    [InlineData("ff02::1")]
    public void NonComparableAddressesAreNotResolvedOrGuessed(string address) =>
        Assert.Null(NetworkPrinterDestination.From(Direct with { HostAddress = address }));

    [Fact]
    public async Task ConnectionAndPrinterListExposeTheSameDestinationWithoutPrinting()
    {
        await using var host = await V2EndpointTests.Harness.Start();
        host.Inventory.Items = [Direct, Direct with { Name = "Renamed", PortName = "renamed-port" },
            new() { Name = "cashier", PortName = "USB001" }];
        using var connection = JsonDocument.Parse(await host.Client.GetStringAsync("/api/connection"));
        using var inventory = JsonDocument.Parse(await host.Client.GetStringAsync("/api/printers?refresh=true"));
        foreach (var list in new[] { connection.RootElement.GetProperty("printers"), inventory.RootElement })
        {
            var first = list[0].GetProperty("connection").GetProperty("destination");
            var other = list[1].GetProperty("connection").GetProperty("destination");
            Assert.Equal(NetworkPrinterDestination.From(Direct)!.Id, first.GetProperty("id").GetString());
            Assert.Equal(first.GetProperty("id").GetString(), other.GetProperty("id").GetString());
            Assert.Equal("raw", first.GetProperty("protocol").GetString());
            Assert.Equal(9100, first.GetProperty("port").GetInt32());
            Assert.Equal(JsonValueKind.Null, list[2].GetProperty("connection").GetProperty("destination").ValueKind);
        }
        Assert.Empty(host.Jobs.List()); Assert.Equal(0, host.Backend.Calls);
    }
}
