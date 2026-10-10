using IoTSpy.Core.Interfaces;
using IoTSpy.Storage.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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

            // DB query latency metric (iotspy_db_query_duration_seconds)
            opts.AddInterceptors(new DbQueryMetricsInterceptor());
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

        // Anomaly detector baseline persistence
        services.AddScoped<IHostBaselineRepository, HostBaselineRepository>();

        return services;
    }

    public static async Task MigrateAsync(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IoTSpyDbContext>();

        if (db.Database.IsNpgsql())
        {
            // Migrations are SQLite-only (DesignTimeDbContextFactory targets SQLite), so Postgres
            // builds its schema from the model. EnsureCreated does nothing once tables exist, so
            // model changes after the first start need the database recreated.
            var created = await db.Database.EnsureCreatedAsync();
            if (!created)
            {
                var logger = scope.ServiceProvider.GetRequiredService<ILogger<IoTSpyDbContext>>();
                logger.LogWarning(
                    "PostgreSQL schema already exists and is not upgraded automatically. " +
                    "Drop and recreate the database after model changes.");
            }

            // The audit write-once trigger comes from a migration, which EnsureCreated skips.
            // The statements are idempotent, so they also apply to databases created earlier.
            foreach (var sql in PostgresAuditTriggerStatements)
                await db.Database.ExecuteSqlRawAsync(sql);
            return;
        }

        await db.Database.MigrateAsync();
    }

    // Mirrors the Postgres branch of Migrations/20260509021500_AuditWriteOnceTrigger.cs.
    private static readonly string[] PostgresAuditTriggerStatements =
    [
        """
        CREATE OR REPLACE FUNCTION prevent_audit_update_fn()
        RETURNS trigger AS $$
        BEGIN
            RAISE EXCEPTION 'AuditEntries are immutable and cannot be modified';
        END;
        $$ LANGUAGE plpgsql;
        """,
        """DROP TRIGGER IF EXISTS prevent_audit_update ON "AuditEntries";""",
        """
        CREATE TRIGGER prevent_audit_update
        BEFORE UPDATE ON "AuditEntries"
        FOR EACH ROW EXECUTE FUNCTION prevent_audit_update_fn();
        """,
    ];
}
