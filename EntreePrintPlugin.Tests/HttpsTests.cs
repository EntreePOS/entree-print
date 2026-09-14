using System.IO.Pipelines;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using EntreePrintPlugin.Services;
using EntreePrintTray;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace EntreePrintPlugin.Tests;

public sealed class HttpsTests(ITestOutputHelper output)
{
    private const string Thumbprint = "0123456789ABCDEF0123456789ABCDEF01234567";

    [Fact]
    public void ServiceAndTrayPreserveTheSelectedHttpsEndpoint()
    {
        var values = new Dictionary<string,string?> { ["HttpsCertificateThumbprint"] = Thumbprint.ToLowerInvariant(),
            ["HttpsHost"] = " PRINT.EXAMPLE.COM ", ["Plugin:HttpsHost"] = "ignored.example.com" };
        var settings = PluginSettings.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        Assert.Equal(Thumbprint, settings.HttpsCertificateThumbprint);
        Assert.Equal("print.example.com",settings.HttpsHost);
        Assert.Equal("https://0.0.0.0:9779/",settings.ListenUri.AbsoluteUri);
        var tray = new PluginConfig { HttpsCertificateThumbprint = Thumbprint, HttpsHost = settings.HttpsHost };
        Assert.Equal("https://print.example.com:9779/",tray.ServiceUri.AbsoluteUri);
        Assert.Equal(Thumbprint,tray.ResetPreferences().HttpsCertificateThumbprint);
        Assert.Equal(settings.HttpsHost,tray.ResetPreferences().HttpsHost);
        values["HTTPS_HOST"] = "192.168.1.20";
        Assert.Equal("192.168.1.20",PluginSettings.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(values).Build()).HttpsHost);
    }

    [Theory]
    [InlineData("", "print.example.com")]
    [InlineData("invalid", "print.example.com")]
    [InlineData(Thumbprint, "")]
    [InlineData(Thumbprint, "https://print.example.com")]
    [InlineData(Thumbprint, "print.example.com:9779")]
    [InlineData(Thumbprint, "*.example.com")]
    public void InvalidOrIncompleteHttpsConfigurationCannotOverwriteSettings(string thumbprint,string host)
    {
        var values = new Dictionary<string,string?> { ["HttpsCertificateThumbprint"] = thumbprint,["HttpsHost"] = host };
        Assert.Throws<ArgumentException>(() => PluginSettings.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(values).Build()));
        var directory = Path.Combine(Path.GetTempPath(),"EntreeHttpsSettings",Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory,"settings.json");
        try
        {
            new PluginConfig().Save(path);
            var original = File.ReadAllBytes(path);
            Assert.Throws<ArgumentException>(() => new PluginConfig { HttpsCertificateThumbprint=thumbprint,HttpsHost=host }.Save(path));
            Assert.Equal(original,File.ReadAllBytes(path));
        }
        finally { Directory.Delete(directory,true); }
    }

    [Fact]
    public void CertificateChecksRequirePrivateKeyCurrentValiditySanAndServerUsage()
    {
        using var certificate = Certificate();
        var now = DateTimeOffset.UtcNow;
        HttpsCertificate.Validate(certificate,"print.example.com",now);
        HttpsCertificate.Validate(certificate,"127.0.0.1",now);
        Assert.Throws<InvalidOperationException>(() => HttpsCertificate.Validate(certificate,"another.example.com",now));
        Assert.Throws<InvalidOperationException>(() => HttpsCertificate.Validate(certificate,"print.example.com",now.AddDays(2)));
        Assert.Throws<InvalidOperationException>(() => HttpsCertificate.Validate(certificate,"print.example.com",now.AddDays(-2)));
        using var publicOnly = X509CertificateLoader.LoadCertificate(certificate.RawData);
        Assert.Throws<InvalidOperationException>(() => HttpsCertificate.Validate(publicOnly,"print.example.com",now));
        using var clientOnly = Certificate(serverUsage:false);
        Assert.Throws<InvalidOperationException>(() => HttpsCertificate.Validate(clientOnly,"print.example.com",now));
        using var commonNameOnly = Certificate(includeSan:false);
        Assert.Throws<InvalidOperationException>(() => HttpsCertificate.Validate(commonNameOnly,"print.example.com",now));
    }

    [Fact]
    public void DiscoveryAdvertisesTransportWithoutCertificateOrCredentialMaterial()
    {
        var settings = new PluginSettings { HttpsCertificateThumbprint=Thumbprint,HttpsHost="print.example.com",AccessToken="private-token" };
        var directory = Path.Combine(Path.GetTempPath(),"EntreeHttpsIdentity",Guid.NewGuid().ToString("N"));
        string payload;
        try { payload = UdpDiscoveryService.DiscoveryPayload(settings,new ServiceIdentity(directory),["192.168.1.20"]); }
        finally { if(Directory.Exists(directory)) Directory.Delete(directory,true); }
        using var json = JsonDocument.Parse(payload);
        Assert.Equal("https",json.RootElement.GetProperty("scheme").GetString());
        Assert.DoesNotContain(Thumbprint,payload); Assert.DoesNotContain("private-token",payload);
        Assert.Equal("http",new PluginSettings().Scheme);
        Assert.Null(HttpsCertificate.Load(new PluginSettings()));
    }

    // Exercise Kestrel's actual HTTPS middleware over memory pipes. No TCP/UDP
    // listener, print service, certificate-store write or Windows print job is started.
    [Theory]
    [InlineData(true,true)]
    [InlineData(false,true)]
    [InlineData(true,false)]
    public async Task KestrelEncryptsTrafficAndTheClientChecksCertificateNameAndTrust(bool matchingName,bool trustedIssuer)
    {
        using var generated = Certificate();
        // Windows Schannel requires a key container. DefaultKeySet creates a
        // temporary container removed on disposal, without adding a certificate to a store.
        using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx),null,X509KeyStorageFlags.DefaultKeySet);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reachedApplication = false;
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["Logging:LogLevel:Default"] = "Trace";
        builder.Configuration["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace";
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddProvider(new TestLogs(output));
        var transport = new MemoryTransport();
        builder.Services.RemoveAll<IConnectionListenerFactory>();
        builder.Services.AddSingleton<IConnectionListenerFactory>(transport);
        builder.Configuration["Kestrel:Endpoints:UnexpectedHttp:Url"] = "http://127.0.0.1:18888";
        builder.WebHost.UseSetting("urls","http://127.0.0.1:19999");
        HttpsCertificate.ConfigureServer(builder.WebHost,new PluginSettings
        {
            BindAddress="127.0.0.1",HttpsCertificateThumbprint=certificate.Thumbprint,HttpsHost="print.example.com"
        },certificate);
        await using var app = builder.Build();
        app.MapGet("/", (Microsoft.AspNetCore.Http.HttpContext context) =>
        {
            reachedApplication = true;
            Assert.True(context.Request.IsHttps);
            Assert.Equal(SslProtocols.Tls12,context.Features.Get<ITlsHandshakeFeature>()!.Protocol);
            context.Response.ContentLength = 2;
            return "OK";
        });
        await app.StartAsync(deadline.Token);
        Assert.Equal(new EndPoint[] { new IPEndPoint(IPAddress.Loopback,9779) },transport.Bindings);
        var incoming = new Pipe(); var outgoing = new Pipe();
        await using var server = new MemoryConnection { Transport = new Duplex(incoming.Reader,outgoing.Writer) };
        await using var wire = new PipeStream(outgoing.Reader.AsStream(),incoming.Writer.AsStream());
        await using var client = new SslStream(wire);
        await transport.Connections.Writer.WriteAsync(server,deadline.Token);
        var policy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust,RevocationMode = X509RevocationMode.NoCheck };
        if (trustedIssuer) policy.CustomTrustStore.Add(certificate);
        var authentication = client.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = matchingName ? "print.example.com" : "wrong.example.com",
            CertificateChainPolicy = policy, EnabledSslProtocols = SslProtocols.Tls12
        },deadline.Token);
        if (!matchingName || !trustedIssuer)
        {
            await Assert.ThrowsAsync<AuthenticationException>(() => authentication);
            await client.DisposeAsync();
            await app.StopAsync(deadline.Token);
            Assert.False(reachedApplication);
            return;
        }
        await authentication;
        Assert.True(client.IsEncrypted); Assert.True(client.IsAuthenticated);
        await client.WriteAsync("GET / HTTP/1.1\r\nHost: print.example.com\r\nConnection: close\r\n\r\n"u8.ToArray(),deadline.Token);
        using var reader = new StreamReader(client,leaveOpen:true);
        var reply = await reader.ReadToEndAsync(deadline.Token);
        Assert.Contains("200 OK",reply); Assert.EndsWith("OK",reply);
        await app.StopAsync(deadline.Token);
        Assert.True(reachedApplication);
    }

    private static X509Certificate2 Certificate(bool serverUsage=true,bool includeSan=true)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=print.example.com",key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature,true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection {new(serverUsage ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2")},true));
        if (includeSan)
        {
            var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("print.example.com"); san.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(san.Build());
        }
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(1));
    }
    private sealed record Duplex(PipeReader Input,PipeWriter Output) : IDuplexPipe;
    private sealed class MemoryConnection() : DefaultConnectionContext("tls-test")
    {
        public override async ValueTask DisposeAsync()
        {
            await Transport.Input.CompleteAsync(); await Transport.Output.CompleteAsync();
            await base.DisposeAsync();
        }
    }
    private sealed class TestLogs(ITestOutputHelper output) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Capture(output);
        public void Dispose() { }
        private sealed class Capture(ITestOutputHelper output) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState:notnull=>null;
            public bool IsEnabled(LogLevel level)=>true;
            public void Log<TState>(LogLevel level,EventId id,TState state,Exception? error,Func<TState,Exception?,string> formatter)
                => output.WriteLine(formatter(state,error)+" "+error);
        }
    }
    private sealed class MemoryTransport : IConnectionListenerFactory,IConnectionListener
    {
        public Channel<ConnectionContext> Connections {get;} = Channel.CreateUnbounded<ConnectionContext>();
        public List<EndPoint> Bindings {get;} = [];
        public EndPoint EndPoint {get;private set;} = null!;
        public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint,CancellationToken token=default)
        { Bindings.Add(endpoint);EndPoint=endpoint;return ValueTask.FromResult<IConnectionListener>(this); }
        public async ValueTask<ConnectionContext?> AcceptAsync(CancellationToken token=default)
        { try { return await Connections.Reader.ReadAsync(token); } catch(ChannelClosedException) {return null;} }
        public ValueTask UnbindAsync(CancellationToken token=default) {Connections.Writer.TryComplete();return ValueTask.CompletedTask;}
        public ValueTask DisposeAsync() => UnbindAsync();
    }
    private sealed class PipeStream(Stream input,Stream output) : Stream
    {
        public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position {get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override int Read(byte[] buffer,int offset,int count) => input.Read(buffer,offset,count);
        public override void Write(byte[] buffer,int offset,int count) => output.Write(buffer,offset,count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default) => input.ReadAsync(buffer,token);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken token=default) => output.WriteAsync(buffer,token);
        public override void Flush() => output.Flush(); public override Task FlushAsync(CancellationToken token)=>output.FlushAsync(token);
        public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
        public override void SetLength(long value)=>throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if(disposing){input.Dispose();output.Dispose();} base.Dispose(disposing); }
    }
}
