using IoTSpy.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace IoTSpy.Storage;

public class IoTSpyDbContext(DbContextOptions<IoTSpyDbContext> options) : DbContext(options)
{
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<CapturedRequest> Captures => Set<CapturedRequest>();
    public DbSet<CertificateEntry> Certificates => Set<CertificateEntry>();
    public DbSet<ProxySettings> ProxySettings => Set<ProxySettings>();
    public DbSet<ScanJob> ScanJobs => Set<ScanJob>();
    public DbSet<ScanFinding> ScanFindings => Set<ScanFinding>();
    public DbSet<ManipulationRule> ManipulationRules => Set<ManipulationRule>();
    public DbSet<Breakpoint> Breakpoints => Set<Breakpoint>();
    public DbSet<ReplaySession> ReplaySessions => Set<ReplaySession>();
    public DbSet<FuzzerJob> FuzzerJobs => Set<FuzzerJob>();
    public DbSet<FuzzerResult> FuzzerResults => Set<FuzzerResult>();
public DbSet<OpenRtbEvent> OpenRtbEvents => Set<OpenRtbEvent>();
    public DbSet<PiiStrippingLog> PiiStrippingLogs => Set<PiiStrippingLog>();
    public DbSet<OpenRtbPiiPolicy> OpenRtbPiiPolicies => Set<OpenRtbPiiPolicy>();

    // Packet capture DbSet
    public DbSet<CaptureDevice> CaptureDevices => Set<CaptureDevice>();
    public DbSet<CapturedPacket> Packets => Set<CapturedPacket>();

    public DbSet<PersistedProtocolMessage> ProtocolMessages => Set<PersistedProtocolMessage>();

    // Phase 9
    public DbSet<ScheduledScan> ScheduledScans => Set<ScheduledScan>();

    // API Spec & Content Replacement
    public DbSet<ApiSpecDocument> ApiSpecDocuments => Set<ApiSpecDocument>();
    public DbSet<ContentReplacementRule> ContentReplacementRules => Set<ContentReplacementRule>();

    // Phase 11 — Multi-user & audit
    public DbSet<User> Users => Set<User>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<AuditArchiveEntry> AuditArchive => Set<AuditArchiveEntry>();
    public DbSet<DashboardLayout> DashboardLayouts => Set<DashboardLayout>();

    // Phase 14 — API key management
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    // Phase 15 — Collaboration & real-time sharing
    public DbSet<InvestigationSession> InvestigationSessions => Set<InvestigationSession>();
    public DbSet<SessionCapture> SessionCaptures => Set<SessionCapture>();
    public DbSet<CaptureAnnotation> CaptureAnnotations => Set<CaptureAnnotation>();
    public DbSet<SessionActivity> SessionActivities => Set<SessionActivity>();

    // Phase 21 — Passive proxy sessions
    public DbSet<PassiveCaptureSession> PassiveCaptureSessions => Set<PassiveCaptureSession>();

    // Gaps Batch 6 — gRPC proto schemas
    public DbSet<ProtoSchema> ProtoSchemas => Set<ProtoSchema>();

    // Security — scan scope CIDR allowlist
    public DbSet<ScanScope> ScanScopes => Set<ScanScope>();

    // ML analytics — traffic insights
    public DbSet<TrafficInsight> TrafficInsights => Set<TrafficInsight>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Device>(e =>
        {
            e.HasKey(d => d.Id);
            e.HasIndex(d => d.IpAddress).IsUnique();
            e.HasIndex(d => d.MacAddress);
        });

        modelBuilder.Entity<CapturedRequest>(e =>
        {
            e.HasKey(c => c.Id);
            e.HasIndex(c => c.Timestamp);
            e.HasIndex(c => c.DeviceId);
            e.HasIndex(c => c.Host);
            e.HasOne(c => c.Device)
             .WithMany(d => d.Captures)
             .HasForeignKey(c => c.DeviceId)
             .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<CertificateEntry>(e =>
        {
            e.HasKey(c => c.Id);
            e.HasIndex(c => c.CommonName);
            e.HasIndex(c => c.IsRootCa);
        });

        modelBuilder.Entity<ScanJob>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.DeviceId);
            e.HasIndex(s => s.Status);
            e.HasIndex(s => s.CreatedAt);
            e.HasOne(s => s.Device)
             .WithMany()
             .HasForeignKey(s => s.DeviceId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ScanFinding>(e =>
        {
            e.HasKey(f => f.Id);
            e.HasIndex(f => f.ScanJobId);
            e.HasIndex(f => f.Type);
            e.HasIndex(f => f.Severity);
            e.HasOne(f => f.ScanJob)
             .WithMany(s => s.Findings)
             .HasForeignKey(f => f.ScanJobId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProxySettings>(e =>
        {
            e.HasKey(p => p.Id);
            // No HasDefaultValue here — C# property initializers (= true) supply the values.
            // HasDefaultValue(true) causes EF Core 8+ to treat true as the sentinel and omit
            // the column from INSERT, which fails because the migrated schema has no DB DEFAULT.
        });

        modelBuilder.Entity<ManipulationRule>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasIndex(r => r.Enabled);
            e.HasIndex(r => r.Priority);
        });

        modelBuilder.Entity<Breakpoint>(e =>
        {
            e.HasKey(b => b.Id);
            e.HasIndex(b => b.Enabled);
        });

        modelBuilder.Entity<ReplaySession>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasIndex(r => r.OriginalCaptureId);
            e.HasIndex(r => r.CreatedAt);
            e.HasOne(r => r.OriginalCapture)
             .WithMany()
             .HasForeignKey(r => r.OriginalCaptureId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FuzzerJob>(e =>
        {
            e.HasKey(j => j.Id);
            e.HasIndex(j => j.BaseCaptureId);
            e.HasIndex(j => j.Status);
            e.HasOne(j => j.BaseCapture)
             .WithMany()
             .HasForeignKey(j => j.BaseCaptureId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FuzzerResult>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasIndex(r => r.FuzzerJobId);
            e.HasIndex(r => r.IsAnomaly);
            e.HasOne(r => r.FuzzerJob)
             .WithMany(j => j.Results)
             .HasForeignKey(r => r.FuzzerJobId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OpenRtbEvent>(e =>
        {
            e.HasKey(o => o.Id);
            e.HasIndex(o => o.CapturedRequestId);
            e.HasIndex(o => o.DetectedAt);
            e.HasIndex(o => o.Exchange);
            e.HasIndex(o => o.MessageType);
        });

        modelBuilder.Entity<PiiStrippingLog>(e =>
        {
            e.HasKey(l => l.Id);
            e.HasIndex(l => l.CapturedRequestId);
            e.HasIndex(l => l.StrippedAt);
            e.HasIndex(l => l.Host);
            e.HasIndex(l => l.FieldPath);
        });

modelBuilder.Entity<OpenRtbPiiPolicy>(e =>
        {
            e.HasKey(p => p.Id);
            e.HasIndex(p => p.Enabled);
            e.HasIndex(p => p.Priority);
        });

        modelBuilder.Entity<ScheduledScan>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.DeviceId);
            e.HasIndex(s => s.IsEnabled);
            e.HasOne(s => s.Device)
             .WithMany()
             .HasForeignKey(s => s.DeviceId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // Phase 11 — Multi-user & audit
        modelBuilder.Entity<User>(e =>
        {
            e.HasKey(u => u.Id);
            e.HasIndex(u => u.Username).IsUnique();
            e.Property(u => u.Username).IsRequired().HasMaxLength(100);
            e.Property(u => u.PasswordHash).IsRequired();
            e.Property(u => u.DisplayName).HasMaxLength(200);
        });

        modelBuilder.Entity<AuditEntry>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasIndex(a => a.Timestamp);
            e.HasIndex(a => a.UserId);
            e.HasIndex(a => a.Action);
            e.Property(a => a.Action).IsRequired().HasMaxLength(100);
            e.Property(a => a.EntityType).HasMaxLength(100);
            e.Property(a => a.EntityId).HasMaxLength(100);
            e.Property(a => a.IpAddress).HasMaxLength(45);
        });

        modelBuilder.Entity<AuditArchiveEntry>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasIndex(a => a.Timestamp);
            e.HasIndex(a => a.ArchivedAt);
            e.HasIndex(a => a.UserId);
            e.Property(a => a.Action).IsRequired().HasMaxLength(100);
            e.Property(a => a.EntityType).HasMaxLength(100);
            e.Property(a => a.EntityId).HasMaxLength(100);
            e.Property(a => a.IpAddress).HasMaxLength(45);
        });

        modelBuilder.Entity<DashboardLayout>(e =>
        {
            e.HasKey(d => d.Id);
            e.HasIndex(d => d.UserId);
            e.Property(d => d.Name).IsRequired().HasMaxLength(100);
        });

        // Phase 14 — API key management
        modelBuilder.Entity<ApiKey>(e =>
        {
            e.HasKey(k => k.Id);
            e.HasIndex(k => k.KeyHash).IsUnique();
            e.HasIndex(k => k.OwnerId);
            e.Property(k => k.Name).IsRequired().HasMaxLength(200);
            e.Property(k => k.KeyHash).IsRequired().HasMaxLength(64);
        });

        modelBuilder.Entity<CaptureDevice>(e =>
        {
            e.HasKey(c => c.Id);
            e.Property(c => c.Name).IsRequired().HasMaxLength(100);
            e.Property(c => c.DisplayName).IsRequired().HasMaxLength(200);
            e.HasIndex(c => c.MacAddress);
            e.HasIndex(c => c.IpAddress);
        });

        modelBuilder.Entity<CapturedPacket>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Protocol).IsRequired().HasMaxLength(50);
            e.Property(p => p.Layer2Protocol).IsRequired().HasMaxLength(50);
            e.Property(p => p.Layer3Protocol).IsRequired().HasMaxLength(50);
            e.Property(p => p.Layer4Protocol).IsRequired().HasMaxLength(50);
            e.Property(p => p.SourceIp).IsRequired().HasMaxLength(45);
            e.Property(p => p.DestinationIp).IsRequired().HasMaxLength(45);
            e.Property(p => p.SourceMac).IsRequired().HasMaxLength(17);
            e.Property(p => p.DestinationMac).IsRequired().HasMaxLength(17);
            e.Property(p => p.PayloadPreview).HasMaxLength(2048);
            e.HasIndex(p => p.Timestamp);
            e.HasIndex(p => p.DeviceId);
            e.HasIndex(p => p.Protocol);
            e.HasIndex(p => p.SourceIp);
            e.HasIndex(p => p.DestinationIp);
        });

        modelBuilder.Entity<PersistedProtocolMessage>(e =>
        {
            e.HasKey(m => m.Id);
            e.Property(m => m.Direction).IsRequired().HasMaxLength(32);
            e.Property(m => m.Subject).HasMaxLength(512);
            e.Property(m => m.Summary).IsRequired().HasMaxLength(256);
            e.Property(m => m.PayloadPreview).HasMaxLength(2048);
            e.HasIndex(m => m.Timestamp);
            e.HasIndex(m => m.DeviceId);
            e.HasIndex(m => m.Protocol);
        });

        // API Spec & Content Replacement
        modelBuilder.Entity<ApiSpecDocument>(e =>
        {
            e.HasKey(d => d.Id);
            e.HasIndex(d => d.Host);
            e.HasIndex(d => d.Status);
        });

        modelBuilder.Entity<ContentReplacementRule>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasIndex(r => r.ApiSpecDocumentId);
            e.HasIndex(r => r.Priority);
            e.HasIndex(r => r.Host);
            e.HasOne(r => r.ApiSpecDocument)
             .WithMany(d => d.ReplacementRules)
             .HasForeignKey(r => r.ApiSpecDocumentId)
             .IsRequired(false)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // Phase 21 — Passive proxy sessions
        modelBuilder.Entity<PassiveCaptureSession>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.CreatedAt);
            e.Property(s => s.Name).IsRequired().HasMaxLength(200);
            e.Property(s => s.DeviceFilter).HasMaxLength(500);
        });

        modelBuilder.Entity<CapturedRequest>(e =>
        {
            e.HasOne(c => c.PassiveCaptureSession)
             .WithMany()
             .HasForeignKey(c => c.PassiveCaptureSessionId)
             .OnDelete(DeleteBehavior.SetNull)
             .IsRequired(false);
        });

        // Phase 15 — Collaboration
        modelBuilder.Entity<InvestigationSession>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.IsActive);
            e.HasIndex(s => s.CreatedByUserId);
            e.HasIndex(s => s.ShareToken).IsUnique().HasFilter("ShareToken IS NOT NULL");
            e.Property(s => s.Name).IsRequired().HasMaxLength(200);
            e.Property(s => s.ShareToken).HasMaxLength(64);
        });

        modelBuilder.Entity<SessionCapture>(e =>
        {
            e.HasKey(sc => sc.Id);
            e.HasIndex(sc => sc.SessionId);
            e.HasIndex(sc => sc.CaptureId);
            e.HasIndex(sc => new { sc.SessionId, sc.CaptureId }).IsUnique();
            e.HasOne(sc => sc.Session)
             .WithMany(s => s.SessionCaptures)
             .HasForeignKey(sc => sc.SessionId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(sc => sc.Capture)
             .WithMany()
             .HasForeignKey(sc => sc.CaptureId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CaptureAnnotation>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasIndex(a => a.SessionId);
            e.HasIndex(a => a.CaptureId);
            e.HasIndex(a => a.UserId);
            e.Property(a => a.Note).IsRequired().HasMaxLength(2000);
            e.Property(a => a.Username).IsRequired().HasMaxLength(100);
            e.Property(a => a.Tags).HasMaxLength(500);
            e.HasOne(a => a.Session)
             .WithMany(s => s.Annotations)
             .HasForeignKey(a => a.SessionId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SessionActivity>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasIndex(a => a.SessionId);
            e.HasIndex(a => a.Timestamp);
            e.Property(a => a.Action).IsRequired().HasMaxLength(200);
            e.Property(a => a.Username).IsRequired().HasMaxLength(100);
            e.HasOne(a => a.Session)
             .WithMany(s => s.Activities)
             .HasForeignKey(a => a.SessionId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // Gaps Batch 6 — gRPC proto schemas
        modelBuilder.Entity<ProtoSchema>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.CreatedAt);
            e.Property(s => s.Name).IsRequired().HasMaxLength(200);
        });

        // Security — scan scope CIDR allowlist
        modelBuilder.Entity<ScanScope>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.IsActive);
            e.HasIndex(s => s.CreatedAt);
            e.Property(s => s.Name).IsRequired().HasMaxLength(200);
            e.Property(s => s.Cidr).IsRequired().HasMaxLength(50);
            e.Property(s => s.CreatedByUsername).IsRequired().HasMaxLength(100);
        });

        // ML analytics — traffic insights
        modelBuilder.Entity<TrafficInsight>(e =>
        {
            e.HasKey(t => t.Id);
            e.HasIndex(t => t.CaptureId).IsUnique();
            e.HasIndex(t => t.RiskScore);
            e.HasIndex(t => t.IsReviewed);
            e.HasIndex(t => t.CreatedAt);
            e.Property(t => t.ModelVersion).IsRequired().HasMaxLength(50);
            e.Property(t => t.Source).IsRequired().HasMaxLength(20);
            e.Property(t => t.ReviewNote).HasMaxLength(2000);
            e.HasOne(t => t.Capture)
             .WithMany()
             .HasForeignKey(t => t.CaptureId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // SQLite cannot ORDER BY DateTimeOffset columns; store all as Unix ms (long) so
        // LINQ ordering translates correctly.
        var dtoConverter = new ValueConverter<DateTimeOffset, long>(
            v => v.ToUnixTimeMilliseconds(),
            v => DateTimeOffset.FromUnixTimeMilliseconds(v));

        var nullableDtoConverter = new ValueConverter<DateTimeOffset?, long?>(
            v => v.HasValue ? v.Value.ToUnixTimeMilliseconds() : null,
            v => v.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(v.Value) : null);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        foreach (var property in entityType.GetProperties())
        {
            if (property.ClrType == typeof(DateTimeOffset))
                property.SetValueConverter(dtoConverter);
            else if (property.ClrType == typeof(DateTimeOffset?))
                property.SetValueConverter(nullableDtoConverter);
        }
    }
}
