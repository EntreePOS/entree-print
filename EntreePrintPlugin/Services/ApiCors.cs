namespace EntreePrintPlugin.Services;

public static class ApiCors
{
    public static void AddPrintCors(this IServiceCollection services, PluginSettings settings)
    {
        services.AddCors(options => options.AddDefaultPolicy(policy =>
        {
            var origins = EntreePrint.Configuration.SecurityConfiguration.NormalizeOrigins(settings.CorsAllowedOrigins);
            if (origins == "*") policy.AllowAnyOrigin();
            else policy.WithOrigins(origins.Split(',', StringSplitOptions.RemoveEmptyEntries));
            policy.AllowAnyHeader().AllowAnyMethod()
                .WithExposedHeaders(V2Request.DigestHeader);
        }));
    }
}
