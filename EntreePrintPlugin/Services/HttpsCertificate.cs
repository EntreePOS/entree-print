using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace EntreePrintPlugin.Services;

internal static class HttpsCertificate
{
    internal static void ConfigureServer(IWebHostBuilder builder, PluginSettings settings, X509Certificate2? certificate)
    {
        if ((settings.Scheme == "https") != (certificate is not null))
            throw new InvalidOperationException("The selected transport and certificate do not match. HTTP fallback is not permitted.");
        builder.PreferHostingUrls(false);
        builder.ConfigureKestrel(server =>
        {
            // Ignore ambient endpoints: one service setting must not leave an extra plaintext listener.
            server.Configure(new ConfigurationBuilder().Build());
            server.Listen(System.Net.IPAddress.Parse(settings.BindAddress), settings.HttpPort,
                endpoint => { if (certificate is not null) endpoint.UseHttps(certificate); });
        });
    }

    public static X509Certificate2? Load(PluginSettings settings)
    {
        var https = EntreePrint.Configuration.NetworkConfiguration.HttpsSettings(settings.HttpsCertificateThumbprint, settings.HttpsHost);
        if (https.Thumbprint.Length == 0) return null;
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, https.Thumbprint, validOnly: false);
        if (matches.Count != 1)
        {
            foreach (var match in matches) match.Dispose();
            throw new InvalidOperationException("The selected HTTPS certificate was not found uniquely in Local Computer / Personal. No HTTP fallback is enabled.");
        }
        var certificate = matches[0];
        try { Validate(certificate, https.Host, DateTimeOffset.UtcNow); return certificate; }
        catch { certificate.Dispose(); throw; }
    }

    internal static void Validate(X509Certificate2 certificate, string host, DateTimeOffset now)
    {
        if (!certificate.HasPrivateKey)
            throw new InvalidOperationException("The HTTPS certificate requires its private key in the computer certificate store.");
        if (now < certificate.NotBefore.ToUniversalTime() || now > certificate.NotAfter.ToUniversalTime())
            throw new InvalidOperationException("The HTTPS certificate is expired or not yet valid.");
        if (!certificate.MatchesHostname(host, allowWildcards: true, allowCommonName: false))
            throw new InvalidOperationException("The HTTPS host must match a certificate Subject Alternative Name.");
        var usage = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        if (usage is not null && !usage.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == "1.3.6.1.5.5.7.3.1"))
            throw new InvalidOperationException("The HTTPS certificate must permit Server Authentication.");
        var keyUsage = certificate.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
        if (keyUsage is not null && !keyUsage.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature))
            throw new InvalidOperationException("The HTTPS certificate must permit digital signatures.");
    }
}
