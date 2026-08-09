using System.Net;
using System.Net.Http.Headers;
using System.Threading.RateLimiting;
using BoloPay.Web.Models;
using BoloPay.Web.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------
builder.Services.Configure<GroqOptions>(
    builder.Configuration.GetSection(GroqOptions.SectionName));
builder.Services.Configure<ConfidenceOptions>(
    builder.Configuration.GetSection(ConfidenceOptions.SectionName));

var groqOptions = builder.Configuration
    .GetSection(GroqOptions.SectionName).Get<GroqOptions>() ?? new GroqOptions();

// No key means no external calls. The app still runs end to end on the stub
// implementations, which is what lets the UI be built before the Bangla
// recordings exist — and stops a missing key in production from producing an
// error screen instead of a demo.
var hasGroqKey = !string.IsNullOrWhiteSpace(groqOptions.ApiKey);

// ---------------------------------------------------------------------------
// Upload limits
//
// Kestrel's defaults are generous (~28MB multipart). A 15-second voice clip is
// a few hundred KB, so cap this hard: the endpoint is unauthenticated, public,
// and forwards to a metered API.
// ---------------------------------------------------------------------------
const long MaxUploadBytes = 2 * 1024 * 1024;

builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = MaxUploadBytes;
    o.ValueLengthLimit = 1024 * 64;
    o.MemoryBufferThreshold = 1024 * 256;
});

builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = MaxUploadBytes);

// ---------------------------------------------------------------------------
// Rate limiting
//
// Built into ASP.NET Core, so no Redis and no third-party service. This exists
// to stop a bot silently exhausting the Groq free tier and leaving the demo
// broken for a real visitor — not to control spend, which is negligible.
// ---------------------------------------------------------------------------
// The limit is configurable so repeated local test runs don't lock the app out
// for the rest of the hour; Development defaults high, Production stays tight.
var voicePermitLimit = builder.Configuration.GetValue<int?>("RateLimit:VoicePermitsPerHour")
    ?? (builder.Environment.IsDevelopment() ? 500 : 20);

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("voice", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ResolveClientKey(context),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = voicePermitLimit,
                Window = TimeSpan.FromHours(1),
                QueueLimit = 0,
            }));

    options.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsJsonAsync(
            new { error = "rate_limited", message = "Too many requests. Please try again later." },
            token);
    };
});

// ---------------------------------------------------------------------------
// Pipeline services
// ---------------------------------------------------------------------------
builder.Services.AddSingleton<ConfidenceScorer>();
builder.Services.AddSingleton<ContactMatcher>();
builder.Services.AddScoped<VoicePipeline>();

if (hasGroqKey)
{
    builder.Services.AddHttpClient<ITranscriptionService, GroqTranscriptionService>(
        ConfigureGroqClient);
    builder.Services.AddHttpClient<IIntentExtractor, GroqIntentExtractor>(
        ConfigureGroqClient);
}
else
{
    // Scoped so both ASR passes within one request share scenario state.
    builder.Services.AddScoped<ITranscriptionService, StubTranscriptionService>();
    builder.Services.AddScoped<IIntentExtractor, StubIntentExtractor>();
}

builder.Services.AddRazorPages();

var app = builder.Build();

// ---------------------------------------------------------------------------
// Reverse proxy headers
//
// In production this sits behind a TLS-terminating proxy that forwards plain
// HTTP. Without this, two things break: UseHttpsRedirection sees an http://
// request and redirects forever, and the rate limiter partitions every visitor
// under the proxy's single IP — turning a per-visitor limit into a global one.
//
// KnownProxies/KnownNetworks are cleared because the proxy address is assigned
// dynamically by the host and is not knowable at build time.
// ---------------------------------------------------------------------------
if (!app.Environment.IsDevelopment())
{
    var forwardedHeaders = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        ForwardLimit = 1,
    };
    forwardedHeaders.KnownIPNetworks.Clear();
    forwardedHeaders.KnownProxies.Clear();

    app.UseForwardedHeaders(forwardedHeaders);

    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();

app.MapRazorPages();

// ---------------------------------------------------------------------------
// Health check
//
// Serves two purposes: the host's own readiness probe, and a cheap target for
// the keep-alive pinger that stops a free instance spinning down. Deliberately
// touches nothing external — pinging the voice endpoint every few minutes would
// burn Groq quota for no reason. Exempt from rate limiting.
// ---------------------------------------------------------------------------
app.MapGet("/healthz", () => Results.Ok(new
{
    status = "ok",
    // Useful when debugging a deploy: confirms whether the container picked up
    // a key, without ever revealing it.
    transcription = hasGroqKey ? "groq" : "stub",
}));

// ---------------------------------------------------------------------------
// The pipeline endpoint
// ---------------------------------------------------------------------------
app.MapPost("/api/process-voice-command", async (
        HttpRequest request,
        VoicePipeline pipeline,
        ILogger<Program> logger,
        CancellationToken cancellationToken) =>
    {
        if (!request.HasFormContentType)
            return Results.BadRequest(new { error = "invalid_request", message = "Expected multipart form data." });

        // Let the body be read (or aborted) before answering. Rejecting on
        // Content-Length up front resets the connection mid-upload, and the
        // client sees a transport error instead of this JSON.
        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is BadHttpRequestException or InvalidDataException)
        {
            // Both limits surface here, and InvalidDataException also covers a
            // plain malformed body — so size decides which one this is rather
            // than the exception type alone.
            var tooLarge =
                (ex as BadHttpRequestException)?.StatusCode == StatusCodes.Status413PayloadTooLarge
                || request.ContentLength > MaxUploadBytes;

            if (tooLarge)
            {
                logger.LogWarning(ex, "Upload exceeded the {Limit} byte limit.", MaxUploadBytes);
                return Results.Json(
                    new { error = "too_large", message = "That recording is too long." },
                    statusCode: StatusCodes.Status413PayloadTooLarge);
            }

            logger.LogWarning(ex, "Malformed upload rejected.");
            return Results.BadRequest(new { error = "invalid_request", message = "Could not read the upload." });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Malformed upload rejected.");
            return Results.BadRequest(new { error = "invalid_request", message = "Could not read the upload." });
        }

        var file = form.Files.GetFile("audio");

        if (file is null || file.Length == 0)
            return Results.BadRequest(new { error = "no_audio", message = "No audio was received." });

        if (file.Length > MaxUploadBytes)
        {
            return Results.Json(
                new { error = "too_large", message = "That recording is too long." },
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        if (!IsAllowedAudio(file.ContentType))
        {
            logger.LogWarning("Rejected upload with content type {ContentType}", file.ContentType);
            return Results.Json(
                new { error = "unsupported_format", message = "Unsupported audio format." },
                statusCode: StatusCodes.Status415UnsupportedMediaType);
        }

        try
        {
            await using var stream = file.OpenReadStream();

            var result = await pipeline.ProcessAsync(
                stream,
                SafeFileName(file.FileName),
                file.ContentType,
                cancellationToken);

            return Results.Ok(result);
        }
        catch (GroqException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            logger.LogWarning(ex, "Groq rate limit reached.");
            return Results.Json(
                new { error = "upstream_busy", message = "The speech service is busy. Please try again in a moment." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Client navigated away or aborted; nothing to report.
            return Results.Empty;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Voice pipeline failed.");
            return Results.Json(
                new { error = "pipeline_failed", message = "Something went wrong processing that. Please try again." },
                statusCode: StatusCodes.Status500InternalServerError);
        }
    })
    .RequireRateLimiting("voice")
    .DisableAntiforgery();

app.Run();

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

static void ConfigureGroqClient(IServiceProvider services, HttpClient client)
{
    var options = services.GetRequiredService<IOptions<GroqOptions>>().Value;

    client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
    client.DefaultRequestHeaders.Authorization =
        new AuthenticationHeaderValue("Bearer", options.ApiKey);
}

/// <summary>
/// Partitions the rate limiter by client IP.
///
/// This reads <c>RemoteIpAddress</c> only, never the X-Forwarded-For header
/// directly. UseForwardedHeaders has already resolved the real client IP from
/// the trusted proxy hop; reading the raw header here would take a value the
/// caller controls, letting anyone bypass the limit by sending a fake one.
/// </summary>
static string ResolveClientKey(HttpContext context) =>
    context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

/// <summary>
/// Browsers disagree on MediaRecorder output: Chrome emits webm/opus, Safari
/// mp4/aac. Both are accepted by the transcription API, so allow the realistic
/// set rather than assuming webm.
/// </summary>
static bool IsAllowedAudio(string? contentType)
{
    if (string.IsNullOrWhiteSpace(contentType)) return false;

    var type = contentType.Split(';')[0].Trim().ToLowerInvariant();

    return type is "audio/webm" or "audio/ogg" or "audio/mp4" or "audio/mpeg"
        or "audio/mp3" or "audio/wav" or "audio/x-wav" or "audio/wave"
        or "audio/flac" or "audio/x-m4a" or "audio/aac" or "video/mp4"
        or "video/webm";
}

/// <summary>
/// The filename is echoed to an upstream API and into logs, so strip anything
/// path-like and keep it short.
/// </summary>
static string SafeFileName(string? fileName)
{
    if (string.IsNullOrWhiteSpace(fileName)) return "command.webm";

    var name = Path.GetFileName(fileName);
    var cleaned = new string(name.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_').ToArray());

    return string.IsNullOrWhiteSpace(cleaned)
        ? "command.webm"
        : cleaned[..Math.Min(cleaned.Length, 60)];
}
