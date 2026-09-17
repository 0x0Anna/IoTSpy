using IoTSpy.Core.Interfaces;
using IoTSpy.Storage.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IoTSpy.Storage.Extensions;

public static class StorageExtensions
{
    public static IServiceCollection AddIoTSpyStorage(
        this IServiceCollection services,
        string connectionString,
        string provider = "sqlite",
        int maxPoolSize = 20,
        int minPoolSize = 1)
    {
        services.AddDbContext<IoTSpyDbContext>(opts =>
        {
            if (provider.Equals("postgres", StringComparison.OrdinalIgnoreCase))
            {
                // Apply connection pool tuning for Postgres (Phase 8.7)
                var npgsqlConnString = connectionString;
                if (!npgsqlConnString.Contains("Maximum Pool Size", StringComparison.OrdinalIgnoreCase))
                    npgsqlConnString += $";Maximum Pool Size={maxPoolSize};Minimum Pool Size={minPoolSize}";
                opts.UseNpgsql(npgsqlConnString);
            }
            else
            {
                opts.UseSqlite(connectionString);
            }
        });

        services.AddScoped<IDeviceRepository, DeviceRepository>();
        services.AddScoped<ICaptureRepository, CaptureRepository>();
        services.AddScoped<IProtocolMessageRepository, ProtocolMessageRepository>();
        services.AddScoped<ICertificateRepository, CertificateRepository>();
        services.AddScoped<IProxySettingsRepository, ProxySettingsRepository>();
        services.AddScoped<IScanJobRepository, ScanJobRepository>();
        services.AddScoped<IManipulationRuleRepository, ManipulationRuleRepository>();
        services.AddScoped<IBreakpointRepository, BreakpointRepository>();
        services.AddScoped<IReplaySessionRepository, ReplaySessionRepository>();
        services.AddScoped<IFuzzerJobRepository, FuzzerJobRepository>();
services.AddScoped<IOpenRtbEventRepository, OpenRtbEventRepository>();
        services.AddScoped<IPiiStrippingLogRepository, PiiStrippingLogRepository>();
        services.AddScoped<IOpenRtbPiiPolicyRepository, OpenRtbPiiPolicyRepository>();

        // Packet capture repositories
        services.AddScoped<ICaptureDeviceRepository, CaptureDeviceRepository>();
        services.AddScoped<IPacketRepository, CaptureDeviceRepository>();

        // Phase 9
        services.AddScoped<IScheduledScanRepository, ScheduledScanRepository>();

        // API Spec & Content Replacement
        services.AddScoped<IApiSpecRepository, ApiSpecRepository>();

        // Phase 11 — Multi-user & audit
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IAuditRepository, AuditRepository>();
        services.AddScoped<IDashboardLayoutRepository, DashboardLayoutRepository>();

        // Phase 14 — API key management
        services.AddScoped<IApiKeyRepository, ApiKeyRepository>();

        // Phase 15 — Collaboration & real-time sharing
        services.AddScoped<IInvestigationSessionRepository, InvestigationSessionRepository>();
        services.AddScoped<ICaptureAnnotationRepository, CaptureAnnotationRepository>();
        services.AddScoped<ISessionActivityRepository, SessionActivityRepository>();

        // Phase 21 — Passive proxy sessions
        services.AddScoped<IPassiveCaptureSessionRepository, PassiveCaptureSessionRepository>();

        // Gaps Batch 6 — gRPC proto schemas
        services.AddScoped<IProtoSchemaRepository, ProtoSchemaRepository>();

        // Security — scan scope CIDR allowlist
        services.AddScoped<IScanScopeRepository, ScanScopeRepository>();

        // ML analytics — traffic insights
        services.AddScoped<ITrafficInsightRepository, TrafficInsightRepository>();

        return services;
    }

    public static async Task MigrateAsync(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IoTSpyDbContext>();
        await db.Database.MigrateAsync();
    }
}
