using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EntreePrintPlugin.Services;

public static class V2Endpoints
{
    public static void MapPrintApi(this WebApplication app, PluginSettings settings)
    {
        var group = app.MapGroup("/api");
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            http.Response.Headers.CacheControl = "no-store";
            try
            {
                if (http.Request.Path != "/api/health")
                {
                    if (settings.AccessToken.Length < 32) throw new CommandException("AUTH_NOT_CONFIGURED", "Configure an API access token of at least 32 characters.");
                    var authorization = http.Request.Headers.Authorization.ToString();
                    var supplied = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization[7..] : "";
                    if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(settings.AccessToken)))
                        throw new CommandException("UNAUTHORIZED", "A valid API bearer token is required.");
                    var expected = http.Request.Headers["X-Entree-Service-ID"].ToString();
                    if (HttpMethods.IsPost(http.Request.Method) && expected.Length == 0)
                        throw new CommandException("SERVICE_ID_REQUIRED", "Include the service identity returned by connect.");
                    if (expected.Length != 0 && expected != http.RequestServices.GetRequiredService<ServiceIdentity>().ServiceId)
                        throw new CommandException("SERVICE_MISMATCH", "This address now belongs to a different print service.");
                }
                return await next(context);
            }
            catch (CommandException error) { return Error(error.Code, error.Message); }
            catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                http.RequestServices.GetRequiredService<ILogger<V2ApiService>>().LogWarning(error, "API operation failed.");
                return Results.Json(new { error = new { code = "SERVICE_ERROR", message = "The operation could not be completed; reconcile any submitted intent before retrying.", retryable = true, delivery = "unknown" } }, statusCode: 503);
            }
        });

        group.MapGet("/health", (V2ApiService api, HttpRequest request) => api.Health(RequestId(request, false)));
        group.MapGet("/heartbeat", (V2ApiService api, HttpRequest request) => api.Health(RequestId(request, true)));
        group.MapGet("/connection", (V2ApiService api, HttpRequest request, CancellationToken token) => api.ConnectionAsync(token, request.Host.Host));
        group.MapGet("/events/checkpoint", (HttpRequest request, EventBroadcaster events, JobStore jobs, V2ApiService api, ServiceIdentity identity) =>
            EventStream.Checkpoint(request, events, jobs, api, identity));
        group.MapGet("/events", (HttpRequest request, EventBroadcaster events, JobStore jobs, V2ApiService api, ServiceIdentity identity) =>
            EventStream.Open(request, events, jobs, api, identity));
        group.MapGet("/printers", (V2ApiService api, HttpRequest request, CancellationToken token) => api.InventoryAsync(request.Query["refresh"] == "true", token));
        group.MapGet("/printers/status", (V2ApiService api, HttpRequest request, CancellationToken token) =>
        {
            if (!request.Query.TryGetValue("printer", out var name) || name.Count != 1 || string.IsNullOrWhiteSpace(name[0]))
                throw new CommandException("PRINTER_REQUIRED", "Select an installed Windows printer queue.");
            return api.StatusAsync(name[0]!, request.Query["refresh"] == "true", token);
        });
        group.MapPost("/renders", async (V2ApiService api, HttpContext context) =>
        {
            var request = await V2Request.ReadAsync(context.Request, context.RequestAborted);
            var rendered = await api.RenderAsync(request, context.RequestAborted);
            context.Response.Headers[V2Request.DigestHeader] = request.Digest;
            return Results.Json(rendered);
        });
        group.MapPost("/jobs", async (V2ApiService api, HttpContext context) =>
        {
            var request = await V2Request.ReadAsync(context.Request, context.RequestAborted);
            var result = await api.SubmitAsync(request, context.RequestAborted);
            context.Response.Headers[V2Request.DigestHeader] = request.Digest;
            return Results.Json(result.Job, statusCode: result.Created ? 202 : 200);
        });
        group.MapPost("/jobs/{id}/reprints", async (string id, V2ApiService api, HttpContext context) =>
        {
            var request = await V2Request.ReadAsync(context.Request, context.RequestAborted);
            var result = await api.ReprintAsync(id, request, context.RequestAborted);
            context.Response.Headers[V2Request.DigestHeader] = request.Digest;
            return Results.Json(result.Job, statusCode: result.Created ? 202 : 200);
        });
        group.MapGet("/jobs", (V2ApiService api, HttpRequest request) => api.ListJobs(JobHistoryQuery.Parse(request)));
        group.MapGet("/jobs/lookup", (V2ApiService api, HttpRequest request) =>
        {
            if (request.Query.Keys.Any(key => key != "idempotencyKey"))
                throw new CommandException("FIELD_UNSUPPORTED", "Job lookup accepts only idempotencyKey.");
            if (!request.Query.TryGetValue("idempotencyKey", out var key) || key.Count != 1)
                throw new CommandException("REQUEST_INVALID", "Provide exactly one idempotencyKey.");
            return api.GetJobByKey(key[0]!);
        });
        group.MapGet("/jobs/{id}", (string id, V2ApiService api) => api.GetJob(id));
        group.MapGet("/jobs/{id}/render", (string id, V2ApiService api) => api.GetRender(id));
    }

    private static string RequestId(HttpRequest request, bool required)
    {
        var id = request.Query["requestId"].ToString();
        if (id.Length > 128 || (required && id.Length == 0)) throw new CommandException("REQUEST_ID_INVALID", "Provide a request ID of 1–128 characters.");
        return id;
    }

    private static IResult Error(string code, string message)
    {
        var status = code switch
        {
            "UNAUTHORIZED" => 401,
            "AUTH_NOT_CONFIGURED" or "PRINTER_QUERY_FAILED" or "PRINTER_SETTINGS_UNAVAILABLE" or "RENDER_BUSY" or "RENDER_STORE_FULL" or "QUEUE_FULL" or "STORAGE_UNAVAILABLE" or "EVENTS_UNAVAILABLE" => 503,
            "JOB_NOT_FOUND" or "PRINTER_NOT_FOUND" or "RENDER_NOT_FOUND" => 404,
            "RENDER_EXPIRED" or "ARTIFACT_EXPIRED" or "EVENT_CURSOR_EXPIRED" => 410,
            "IDEMPOTENCY_CONFLICT" or "SERVICE_MISMATCH" or "RENDER_PRINTER_MISMATCH" or "RENDER_DESTINATION_MISMATCH" or "RENDER_CHANGED" or "PRINTER_PROFILE_CHANGED" or "PRINTER_SETTINGS_CHANGED" or "JOB_NOT_REPRINTABLE" or "REPRINT_CONTENT_CHANGED" => 409,
            "REQUEST_TOO_LARGE" or "RENDER_TOO_LARGE" => 413,
            _ => 422
        };
        return Results.Json(new { error = new { code, message, retryable = status == 503 && code != "AUTH_NOT_CONFIGURED", delivery = "not_sent" } }, statusCode: status);
    }
}
