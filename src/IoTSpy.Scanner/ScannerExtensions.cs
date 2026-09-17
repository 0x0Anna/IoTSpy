using IoTSpy.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IoTSpy.Scanner;

public static class ScannerExtensions
{
    public static IServiceCollection AddIoTSpyScanner(this IServiceCollection services, IConfiguration? configuration = null)
    {
        var ringBufferCapacity = configuration?.GetValue<int>("PacketCapture:RingBufferCapacity") is > 0 and var v
            ? v
            : LockFreePacketRingBuffer.DefaultCapacity;

        var maxConcurrentScans = configuration?.GetValue<int>("Scanner:MaxConcurrentScans") is > 0 and var mcs
            ? mcs
            : ScannerService.DefaultMaxConcurrentScans;

        services.AddSingleton<PortScanner>();
        services.AddSingleton<ServiceFingerprinter>();
        services.AddSingleton<CredentialTester>();
        services.AddSingleton<ConfigAuditor>();
        services.AddSingleton<IScannerService>(sp => new ScannerService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<PortScanner>(),
            sp.GetRequiredService<ServiceFingerprinter>(),
            sp.GetRequiredService<CredentialTester>(),
            sp.GetRequiredService<CveLookupService>(),
            sp.GetRequiredService<ConfigAuditor>(),
            sp.GetRequiredService<ILogger<ScannerService>>(),
            maxConcurrentScans));
        services.AddSingleton<IPacketBuffer>(new LockFreePacketRingBuffer(ringBufferCapacity));
        services.AddSingleton<IPacketCaptureService, PacketCaptureService>();
        services.AddSingleton<PacketCaptureCheckpointService>();
        services.AddHostedService(sp => sp.GetRequiredService<PacketCaptureCheckpointService>());
        services.AddSingleton<IReportService, ReportService>();

        services.AddHttpClient<CveLookupService>(client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("IoTSpy/1.0");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        return services;
    }
}
