namespace IoTSpy.Core.Interfaces;

public interface IReportService
{
    Task<byte[]> GenerateDeviceHtmlReportAsync(Guid deviceId, CancellationToken ct = default);
    Task<byte[]> GenerateDevicePdfReportAsync(Guid deviceId, CancellationToken ct = default);
    Task<byte[]> GenerateSessionHtmlReportAsync(Guid sessionId, CancellationToken ct = default);
    Task<byte[]> GenerateSessionPdfReportAsync(Guid sessionId, CancellationToken ct = default);
}
