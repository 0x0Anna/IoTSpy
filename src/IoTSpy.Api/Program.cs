using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.RateLimiting;
using IoTSpy.Analytics;
using IoTSpy.Api.Authentication;
using IoTSpy.Api.Hubs;
using IoTSpy.Api.Services;
using IoTSpy.Core.Enums;
using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using IoTSpy.Protocols.Anomaly;
using IoTSpy.Protocols.Mqtt;
using IoTSpy.Protocols.WebSocket;
using IoTSpy.Proxy;
using IoTSpy.Proxy.Interception;
using IoTSpy.Proxy.Resilience;
using IoTSpy.Proxy.Tls;
using IoTSpy.Manipulation;
using IoTSpy.Scanner;
using IoTSpy.Storage.Extensions;
using IoTSpy.Storage.Redis;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using FluentValidation;
using FluentValidation.AspNetCore;
using IoTSpy.Api.Validators;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Prometheus;
using Scalar.AspNetCore;
using Serilog;
using StackExchange.Redis;

// Raise the CLR thread-pool floor before the host starts accepting connections. Under
// concurrent proxy load (many simultaneous devices/hosts), the default pool's hill-climbing
// growth injects new worker threads at roughly one per ~0.5-1s once saturated — verified via
// a synthetic multi-device stress test to serialize TLS handshake completion into a strict
// one-at-a-time queue (11-30s for 36 concurrent connections) even though every handshake was
// otherwise independent. Setting the floor eliminates that queuing outright.
System.Threading.ThreadPool.SetMinThreads(200, 200);

var builder = WebApplication.CreateBuilder(args);

// ── Serilog (Phase 8.2) ───────────────────────────────────────────────────────
// Enrich.With<ActivityTraceEnricher> stamps TraceId/SpanId from the current Activity
// (backlog #51) so a log line and a trace span for the same request can be joined.
// It is a no-op when no Activity is current (e.g. Otel:Enabled is false).
builder.Host.UseSerilog((ctx, cfg) =>
    cfg.ReadFrom.Configuration(ctx.Configuration)
       .Enrich.FromLogContext()
       .Enrich.With<ActivityTraceEnricher>());

// ── Kestrel HTTPS (Phase 16.1) ────────────────────────────────────────────────
builder.Services.AddSingleton<HttpsCertificateHolder>();

var certPath = builder.Configuration["Kestrel:Certificate:Path"];
var certPassword = builder.Configuration["Kestrel:Certificate:Password"];
var leEnabled = builder.Configuration.GetValue<bool>("Kestrel:LetsEncrypt:Enabled");

if (leEnabled || !string.IsNullOrEmpty(certPath))
{
    builder.WebHost.UseKestrel((ctx, opts) =>
    {
        opts.ListenAnyIP(5000);
        opts.ListenAnyIP(5001, listen =>
        {
            if (!string.IsNullOrEmpty(certPath) && File.Exists(certPath))
            {
                listen.UseHttps(certPath, certPassword);
            }
            else
            {
                // Let's Encrypt: cert populated by CertesLetsEncryptService after startup
                var holder = opts.ApplicationServices.GetRequiredService<HttpsCertificateHolder>();
                listen.UseHttps(https =>
                    https.ServerCertificateSelector = (_, _) => holder.Certificate);
            }
        });
    });
}

if (leEnabled)
    builder.Services.AddHostedService<CertesLetsEncryptService>();

// ── Storage ─────────────────────────────────────────────────────────────────
var dbProvider = builder.Configuration["Database:Provider"] ?? "sqlite";
var connString = builder.Configuration["Database:ConnectionString"]
    ?? $"Data Source={Path.Combine(AppContext.BaseDirectory, "iotspy.db")}";
var dbMaxPool = int.TryParse(builder.Configuration["Database:MaxPoolSize"], out var mp) ? mp : 20;
var dbMinPool = int.TryParse(builder.Configuration["Database:MinPoolSize"], out var minp) ? minp : 1;
builder.Services.AddIoTSpyStorage(connString, dbProvider, dbMaxPool, dbMinPool);

// ── Authentication ───────────────────────────────────────────────────────────
var jwtSecret = builder.Configuration["Auth:JwtSecret"]
    ?? throw new InvalidOperationException("Auth:JwtSecret must be set in configuration.");
if (jwtSecret.Length < 32)
    throw new InvalidOperationException("Auth:JwtSecret must be at least 32 characters (256 bits) for HS256.");

// Use a policy scheme that forwards to ApiKey handler when X-Api-Key header is present,
// otherwise falls through to the standard JWT Bearer handler.
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = "SmartScheme";
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddPolicyScheme("SmartScheme", "JWT or API Key", opts =>
        opts.ForwardDefaultSelector = ctx =>
            ctx.Request.Headers.ContainsKey("X-Api-Key")
                ? ApiKeyAuthenticationHandler.SchemeName
                : JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opts =>
    {
        opts.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = "iotspy",
            ValidateAudience = true,
            ValidAudience = "iotspy",
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret))
        };

        // Allow JWT via SignalR query string
        opts.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var token = ctx.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                    ctx.Token = token;
                return Task.CompletedTask;
            }
        };
    })
    .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthorization();

// ── Redis (optional — enables Redis passive buffer + SignalR backplane) ───────
var redisOptions = builder.Configuration
    .GetSection(RedisOptions.SectionName)
    .Get<RedisOptions>() ?? new RedisOptions();

if (redisOptions.IsConfigured)
{
    // abortConnect=false: don't throw if Redis is unavailable at startup;
    // StackExchange.Redis will reconnect automatically in the background.
    var redisConfig = ConfigurationOptions.Parse(redisOptions.ConnectionString!);
    redisConfig.AbortOnConnectFail = false;
    var multiplexer = ConnectionMultiplexer.Connect(redisConfig);
    builder.Services.AddSingleton<IConnectionMultiplexer>(multiplexer);
    builder.Services.AddSingleton(redisOptions);
    builder.Services.AddSingleton<RedisPassiveProxyBuffer>();
    builder.Services.AddSingleton<IPassiveProxyBuffer>(sp => sp.GetRequiredService<RedisPassiveProxyBuffer>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<RedisPassiveProxyBuffer>());
}

// ── SignalR ──────────────────────────────────────────────────────────────────
var signalR = builder.Services.AddSignalR()
    .AddJsonProtocol(opts =>
        opts.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

if (redisOptions.IsConfigured && redisOptions.EnableSignalRBackplane)
    signalR.AddStackExchangeRedis(redisOptions.ConnectionString!);
builder.Services.AddSingleton<ICapturePublisher, SignalRCapturePublisher>();
builder.Services.AddSingleton<ICaptureBatchWriter, CaptureBatchWriter>();
builder.Services.AddHostedService(sp => (CaptureBatchWriter)sp.GetRequiredService<ICaptureBatchWriter>());
builder.Services.AddSingleton<IProtocolMessageWriter, ProtocolMessageBatchWriter>();
builder.Services.AddHostedService(sp => (ProtocolMessageBatchWriter)sp.GetRequiredService<IProtocolMessageWriter>());
builder.Services.AddSingleton<IPacketCapturePublisher, SignalRPacketPublisher>();
builder.Services.AddSingleton<IAnomalyAlertPublisher, SignalRAnomalyPublisher>();
builder.Services.AddSingleton<CollaborationPublisher>();

// ── Resilience ───────────────────────────────────────────────────────────────
var resilienceOptions = builder.Configuration
    .GetSection(ResilienceOptions.SectionName)
    .Get<ResilienceOptions>() ?? new ResilienceOptions();
var connectionPoolOptions = builder.Configuration
    .GetSection(UpstreamConnectionPoolOptions.SectionName)
    .Get<UpstreamConnectionPoolOptions>() ?? new UpstreamConnectionPoolOptions();
builder.Services.AddProxyResilience(resilienceOptions, connectionPoolOptions);

// ── Proxy ────────────────────────────────────────────────────────────────────
// All proxy servers and supporting engines are singletons (long-lived TCP listeners)
builder.Services.AddSingleton<SslStripService>();
// Fall back to the in-process ring buffer when Redis is not configured.
if (!redisOptions.IsConfigured)
    builder.Services.AddSingleton<IPassiveProxyBuffer, IoTSpy.Proxy.Passive.PassiveProxyBuffer>();
builder.Services.AddSingleton<ExplicitProxyServer>();
builder.Services.AddSingleton<TransparentProxyServer>();
builder.Services.AddSingleton<IptablesHelper>();
builder.Services.AddSingleton<IWindowsFirewallHelper, WindowsFirewallHelper>();
builder.Services.AddSingleton<IPlatformInfo, PlatformInfo>();
builder.Services.AddSingleton<ArpSpoofEngine>();
builder.Services.AddSingleton<ICertificateAuthority, CertificateAuthority>();
builder.Services.AddSingleton<IProxyService, ProxyService>();
builder.Services.AddHostedService(sp => (ProxyService)sp.GetRequiredService<IProxyService>());

// ── Phase 10: Protocol proxies (MQTT broker proxy, CoAP proxy) ─────────────
builder.Services.AddSingleton<IMqttBrokerProxy, MqttBrokerProxy>();
builder.Services.AddSingleton<ICoapProxy, CoapProxy>();

// ── Anomaly detection (Phase 8.5) ─────────────────────────────────────────
builder.Services.AddSingleton<IAnomalyDetector, AnomalyDetector>();
builder.Services.AddSingleton<MqttSessionAnalyzer>();
builder.Services.AddSingleton<WebSocketMessageReassembler>();
// Nothing else resolves this directly — a plain AddHostedService<T> is sufficient.
builder.Services.AddHostedService<HostBaselineCheckpointService>();

// ── Scanner ─────────────────────────────────────────────────────────────────
builder.Services.AddIoTSpyScanner(builder.Configuration);

// ── Manipulation ────────────────────────────────────────────────────────────
var aiConfig = builder.Configuration
    .GetSection(AiProviderConfig.SectionName)
    .Get<AiProviderConfig>();
builder.Services.AddIoTSpyManipulation(aiConfig);

// ── API ──────────────────────────────────────────────────────────────────────
builder.Services.AddControllers()
    .AddJsonOptions(opts =>
        opts.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddFluentValidationAutoValidation();
builder.Services.AddValidatorsFromAssemblyContaining<StartScanDtoValidator>();
builder.Services.AddOpenApi();
builder.Services.AddScoped<AuthService>();

// ── Health checks (Phase 8.1) ────────────────────────────────────────────────
var healthChecks = builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy("API is running"), tags: ["ready", "live"])
    .AddDbContextCheck<IoTSpy.Storage.IoTSpyDbContext>("database", tags: ["ready"]);

if (redisOptions.IsConfigured)
    healthChecks.AddCheck<RedisHealthCheck>("redis", tags: ["ready"]);

// ── Rate limiting (Phase 8.3) ─────────────────────────────────────────────────
var rateLimitEnabled = bool.TryParse(builder.Configuration["RateLimit:Enabled"], out var rle) && rle;
if (rateLimitEnabled)
{
    var permitLimit = int.TryParse(builder.Configuration["RateLimit:PermitLimit"], out var pl) ? pl : 100;
    var windowSeconds = int.TryParse(builder.Configuration["RateLimit:WindowSeconds"], out var ws) ? ws : 60;
    var queueLimit = int.TryParse(builder.Configuration["RateLimit:QueueLimit"], out var ql) ? ql : 10;

    builder.Services.AddRateLimiter(opts =>
    {
        opts.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        {
            var key = ctx.User?.Identity?.Name
                ?? ctx.Connection.RemoteIpAddress?.ToString()
                ?? "anonymous";
            return RateLimitPartition.GetSlidingWindowLimiter(key, _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = TimeSpan.FromSeconds(windowSeconds),
                SegmentsPerWindow = 4,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = queueLimit
            });
        });
        opts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    });
}

// ── ML Analytics (traffic insight scoring) ───────────────────────────────────
var analyticsOptions = builder.Configuration
    .GetSection(AnalyticsOptions.SectionName)
    .Get<AnalyticsOptions>() ?? new AnalyticsOptions();
builder.Services.AddIoTSpyAnalytics(analyticsOptions);

// ── Data retention (Phase 8.4) ────────────────────────────────────────────────
builder.Services.Configure<DataRetentionOptions>(
    builder.Configuration.GetSection(DataRetentionOptions.SectionName));
builder.Services.AddSingleton<DataRetentionSettingsService>();
builder.Services.AddHostedService(sp => new DataRetentionService(
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<DataRetentionSettingsService>(),
    sp.GetRequiredService<ILogger<DataRetentionService>>()));

// ── Alerting (Phase 9.4) ──────────────────────────────────────────────────────
builder.Services.Configure<AlertingOptions>(
    builder.Configuration.GetSection(AlertingOptions.SectionName));
builder.Services.AddHttpClient("alerting");
builder.Services.AddSingleton<IAlertingService, AlertingService>();

// ── Scheduled Scans (Phase 9.5) ───────────────────────────────────────────────
builder.Services.AddHostedService<ScheduledScanService>();

// ── Plugin system (Phase 16.4) ────────────────────────────────────────────────
builder.Services.AddSingleton<PluginSignatureVerifier>();
builder.Services.AddSingleton<PluginLoaderService>();
builder.Services.AddSingleton<IPluginRegistry>(sp =>
{
    var svc = sp.GetRequiredService<PluginLoaderService>();
    svc.Initialize();
    return svc;
});

// ── OpenTelemetry tracing (backlog #51) ───────────────────────────────────────
// Opt-in: registering the tracing pipeline only when Otel:Enabled is true (default
// false) so existing deployments aren't forced to stand up an OTLP collector. Unlike a
// no-op exporter, this skips AddOpenTelemetry()/WithTracing() entirely when disabled.
var otelOptions = builder.Configuration
    .GetSection(OtelOptions.SectionName)
    .Get<OtelOptions>() ?? new OtelOptions();

if (otelOptions.Enabled)
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService(otelOptions.ServiceName))
        .WithTracing(tracing => tracing
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddOtlpExporter(otlp => otlp.Endpoint = new Uri(otelOptions.OtlpEndpoint)));
}

// ── CORS (for Vite dev server) ─────────────────────────────────────────────
builder.Services.AddCors(opts => opts.AddDefaultPolicy(policy =>
    policy.WithOrigins(
            builder.Configuration["Frontend:Origin"] ?? "http://localhost:3000")
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()));

var app = builder.Build();

// ── Migrate DB on startup ────────────────────────────────────────────────────
await app.Services.MigrateAsync();

// ── Migrate legacy single-user account to Users table ───────────────────────
await using (var scope = app.Services.CreateAsyncScope())
{
    var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
    var settingsRepo = scope.ServiceProvider.GetRequiredService<IProxySettingsRepository>();
    var settings = await settingsRepo.GetAsync();

    if (await userRepo.CountAsync() == 0 && !string.IsNullOrEmpty(settings.PasswordHash))
    {
        await userRepo.CreateAsync(new User
        {
            Username = "admin",
            PasswordHash = settings.PasswordHash,
            DisplayName = "Administrator (legacy)",
            Role = UserRole.Admin,
            IsEnabled = true,
        });
        Log.Information("Migrated legacy password to admin user account");
    }
}

// ── Middleware ────────────────────────────────────────────────────────────────

// ACME HTTP-01 challenge endpoint (Let's Encrypt)
if (leEnabled)
{
    app.Map("/.well-known/acme-challenge/{token}", (string token, CertesLetsEncryptService le) =>
        le.TryGetChallenge(token, out var keyAuth)
            ? Results.Text(keyAuth, "text/plain")
            : Results.NotFound());
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

// Serve the Vite-built frontend from wwwroot (production / Docker)
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseSerilogRequestLogging(opts =>
{
    opts.EnrichDiagnosticContext = (diag, ctx) =>
    {
        diag.Set("RequestHost", ctx.Request.Host.Value ?? string.Empty);
        diag.Set("UserAgent", (object)(ctx.Request.Headers.UserAgent.FirstOrDefault() ?? string.Empty));
    };
});

app.UseCors();

// ── Security headers ──────────────────────────────────────────────────────────
app.Use(async (ctx, next) =>
{
    var headers = ctx.Response.Headers;
    headers["X-Frame-Options"] = "DENY";
    headers["X-Content-Type-Options"] = "nosniff";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    headers["X-Permitted-Cross-Domain-Policies"] = "none";
    headers["Content-Security-Policy"] =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: blob:; " +
        "font-src 'self'; " +
        "connect-src 'self' ws: wss:; " +
        "worker-src 'self' blob:; " +
        "frame-ancestors 'none'";
    if (!app.Environment.IsDevelopment())
        headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
    await next();
});

app.UseAuthentication();
app.UseAuthorization();

if (rateLimitEnabled)
    app.UseRateLimiter();

app.MapControllers();
app.MapHub<TrafficHub>("/hubs/traffic");
app.MapHub<PacketCaptureHub>("/hubs/packets");
app.MapHub<CollaborationHub>("/hubs/collaboration");

// ── Health check endpoints (Phase 8.1) ───────────────────────────────────────
app.MapHealthChecks("/health", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("live"),
    ResponseWriter = async (ctx, report) =>
    {
        ctx.Response.ContentType = "application/json";
        var result = System.Text.Json.JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new { name = e.Key, status = e.Value.Status.ToString() })
        });
        await ctx.Response.WriteAsync(result);
    }
});

app.MapHealthChecks("/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = async (ctx, report) =>
    {
        ctx.Response.ContentType = "application/json";
        var result = System.Text.Json.JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new { name = e.Key, status = e.Value.Status.ToString(), description = e.Value.Description })
        });
        await ctx.Response.WriteAsync(result);
    }
});

// ── Prometheus metrics endpoint (Phase 16.6) ─────────────────────────────────
app.MapMetrics("/metrics").RequireAuthorization();

// SPA fallback — any unmatched route serves index.html for client-side routing
app.MapFallbackToFile("index.html");

app.Run();

// Expose Program to WebApplicationFactory used in integration test projects
public partial class Program { }
