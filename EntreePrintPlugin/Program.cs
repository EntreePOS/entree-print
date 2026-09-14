using EntreePrintPlugin;
using EntreePrintPlugin.Services;
using Microsoft.Extensions.Hosting.WindowsServices;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "ENTREE Print Plugin";
});

var sharedConfigPath = Environment.GetEnvironmentVariable("ENTREE_PRINT_CONFIG")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EntreePrintPlugin", "entree-print-settings.json");
builder.Configuration.AddJsonFile(sharedConfigPath, optional: true, reloadOnChange: false);

var settings = PluginSettings.FromConfiguration(builder.Configuration);
if (WindowsServiceHelpers.IsWindowsService())
{
    // Validate before claiming the ledger, launching a browser or opening listeners.
    EntreePrint.Security.ProtectedStorage.AssertTrustedPath(sharedConfigPath);
    EntreePrint.Security.ProtectedStorage.EnsureDirectory(settings.SpoolPath, privateData: true);
    EntreePrint.Security.ProtectedStorage.ValidatePrivateTree(settings.SpoolPath);
}
builder.WebHost.UseUrls(settings.ListenUri.AbsoluteUri);

builder.Services.AddPrintCors(settings);

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<EventBroadcaster>();
builder.Services.AddSingleton(provider => new JobStore(provider.GetRequiredService<EventBroadcaster>(), Path.Combine(settings.SpoolPath, "jobs"), receiptRetentionDays: settings.ReceiptRetentionDays));
builder.Services.AddHostedService<JobRetentionService>();
builder.Services.AddSingleton(provider =>
{
    _ = provider.GetRequiredService<JobStore>(); // Claim the ledger before exposing this installation identity.
    return new ServiceIdentity(settings.SpoolPath);
});
builder.Services.AddSingleton<RawPrinterWriter>();
builder.Services.AddSingleton<WindowsSpoolerMonitor>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<WindowsSpoolerMonitor>());
builder.Services.AddSingleton<IPrinterBackend, WindowsPrinterBackend>();
builder.Services.AddSingleton<PrinterExecutionQueue>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<PrinterExecutionQueue>());
builder.Services.AddSingleton<CommandProcessor>();
builder.Services.AddSingleton(provider => new PreparedReceiptStore(Path.Combine(settings.SpoolPath, "renders"), provider.GetRequiredService<ServiceIdentity>()));
builder.Services.AddSingleton<V2ApiService>();
builder.Services.AddSingleton<IPrinterLayoutSettings, WindowsPrinterLayout>();
builder.Services.AddSingleton<PrinterStatusService>();
builder.Services.AddSingleton<IPrinterInventory>(provider => provider.GetRequiredService<PrinterStatusService>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<PrinterStatusService>());
builder.Services.AddHostedService<UdpDiscoveryService>();

var app = builder.Build();
_ = app.Services.GetRequiredService<ServiceIdentity>();
app.Services.GetRequiredService<CommandProcessor>().RecoverPendingJobs();
app.UseCors();
app.MapPrintApi(settings);

app.Run();
