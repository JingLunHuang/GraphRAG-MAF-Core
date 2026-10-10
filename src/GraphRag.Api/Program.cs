using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GraphRag.Core;
using GraphRag.Infrastructure;
using GraphRag.Infrastructure.Hosting;
using GraphRag.Infrastructure.Mcp;
using OpenTelemetry.Trace;

namespace GraphRag.Api;

public static class ApiProgram
{
    internal static async Task Main(string[] args) => await BuildApi(args).RunAsync();

    public static WebApplication BuildApi(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);
        builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 2_100_000);
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, RagJsonContext.Default));
        builder.Services.AddGraphRag();
        builder.Services.AddOpenTelemetry().WithTracing(traces => traces.AddAspNetCoreInstrumentation());
        builder.Services.AddMcpServer().WithHttpTransport(options => options.Stateless = true).WithRagTools();
        WebApplication app = builder.Build();
        RuntimeSettings settings = app.Services.GetRequiredService<RuntimeSettings>();

        app.Use(async (HttpContext context, RequestDelegate next) =>
        {
            try
            {
                if (!context.Request.Path.StartsWithSegments("/health") && !string.IsNullOrWhiteSpace(settings.AppApiKey))
                {
                    byte[] supplied = SHA256.HashData(Encoding.UTF8.GetBytes(context.Request.Headers["X-API-Key"].ToString()));
                    byte[] expected = SHA256.HashData(Encoding.UTF8.GetBytes(settings.AppApiKey));
                    if (!CryptographicOperations.FixedTimeEquals(supplied, expected))
                    { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return; }
                }
                await next(context);
            }
            catch (Exception error) when (error is not OperationCanceledException && !context.Response.HasStarted)
            {
                int status = error is ArgumentException or JsonException ? 400 : error is InvalidOperationException ? 503 : 502;
                context.Response.StatusCode = status;
                string message = error is HttpRequestException ? "An upstream service request failed." : error.Message;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new ErrorResponse(message, Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier),
                    RagJsonContext.Default.ErrorResponse), context.RequestAborted);
            }
        });

        app.MapGet("/health/live", () => Results.Json(new HealthResponse("live", settings.Provider, settings.GraphStore,
            !RuntimeFeature.IsDynamicCodeSupported && AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is null, 0), RagJsonContext.Default.HealthResponse));
        app.MapGet("/health/ready", (GraphRagService service) => Results.Json(new HealthResponse(service.Ready ? "ready" : "starting", settings.Provider,
            settings.GraphStore, !RuntimeFeature.IsDynamicCodeSupported && AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is null, service.ChunkCount), RagJsonContext.Default.HealthResponse,
            statusCode: service.Ready ? 200 : 503));
        app.MapPost("/api/v1/documents", async (DocumentInput document, IngestionQueue queue, CancellationToken cancellationToken) =>
            Results.Json(await queue.EnqueueAsync(document, cancellationToken), RagJsonContext.Default.IngestResult));
        app.MapPost("/api/v1/communities", async (LeidenOptions options, GraphRagService service, CancellationToken cancellationToken) =>
            Results.Json(await service.BuildCommunitiesAsync(options, cancellationToken), RagJsonContext.Default.CommunityBuildResult));
        app.MapPost("/api/v1/query", async (QueryRequest query, GraphRagService service, CancellationToken cancellationToken) =>
            Results.Json(await service.QueryAsync(query, cancellationToken), RagJsonContext.Default.AnswerResult));
        app.MapPost("/api/v1/query/stream", async (QueryRequest query, GraphRagService service, HttpContext context) =>
        {
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            try
            {
                await foreach (StreamEvent item in service.StreamAsync(query, context.RequestAborted))
                {
                    await context.Response.WriteAsync("event: " + item.Kind + "\ndata: " +
                        JsonSerializer.Serialize(item, RagJsonContext.Default.StreamEvent).Replace("\n", "", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal) + "\n\n", context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                string message = error is HttpRequestException ? "An upstream service request failed." : error.Message;
                await context.Response.WriteAsync("event: error\ndata: " + JsonSerializer.Serialize(new ErrorResponse(message, Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier),
                    RagJsonContext.Default.ErrorResponse).Replace("\n", "", StringComparison.Ordinal) + "\n\n", context.RequestAborted);
            }
        });
        app.MapMcp("/mcp");
        app.MapGet("/openapi.json", () => Results.File(Path.Combine(AppContext.BaseDirectory, "openapi.json"), "application/json"));
        return app;
    }
}
