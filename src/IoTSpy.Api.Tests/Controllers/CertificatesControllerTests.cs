using IoTSpy.Api.Controllers;
using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Xunit;

namespace IoTSpy.Api.Tests.Controllers;

public class CertificatesControllerTests
{
    private static CertificatesController CreateController(ICertificateAuthority? ca = null, ICertificateRepository? certs = null, IAuditRepository? audit = null)
    {
        return new CertificatesController(
            ca ?? Substitute.For<ICertificateAuthority>(),
            certs ?? Substitute.For<ICertificateRepository>(),
            audit ?? Substitute.For<IAuditRepository>());
    }

    private static CertificateEntry MakeEntry(string commonName) => new()
    {
        CommonName = commonName,
        CertificatePem = "-----BEGIN CERTIFICATE-----\nMOCK\n-----END CERTIFICATE-----",
        IsRootCa = true
    };

    [Theory]
    [InlineData("IoTSpy CA", "IoTSpy-CA")]
    [InlineData("Acme Corp CA", "Acme-Corp-CA")]
    [InlineData("My/CA:Name", "MyCAName")]
    [InlineData("  ", "iotspy-ca")]
    [InlineData("", "iotspy-ca")]
    public void SanitizeFilename_ProducesFilesystemSafeName(string input, string expected)
    {
        Assert.Equal(expected, CertificatesController.SanitizeFilename(input));
    }

    [Fact]
    public async Task DownloadRootCaDer_UsesSanitizedCommonNameAsFilename()
    {
        var ca = Substitute.For<ICertificateAuthority>();
        var entry = MakeEntry("Acme Corp CA");
        ca.GetOrCreateRootCaAsync(Arg.Any<CancellationToken>()).Returns(entry);
        ca.ExportRootCaDerAsync(Arg.Any<CancellationToken>()).Returns([1, 2, 3]);
        var controller = CreateController(ca);

        var result = await controller.DownloadRootCaDer() as FileContentResult;

        Assert.NotNull(result);
        Assert.Equal("Acme-Corp-CA.crt", result.FileDownloadName);
        Assert.Equal("application/x-x509-ca-cert", result.ContentType);
    }

    [Fact]
    public async Task DownloadRootCaDer_DefaultName_ProducesDefaultFilename()
    {
        var ca = Substitute.For<ICertificateAuthority>();
        var entry = MakeEntry("IoTSpy CA");
        ca.GetOrCreateRootCaAsync(Arg.Any<CancellationToken>()).Returns(entry);
        ca.ExportRootCaDerAsync(Arg.Any<CancellationToken>()).Returns([1, 2, 3]);
        var controller = CreateController(ca);

        var result = await controller.DownloadRootCaDer() as FileContentResult;

        Assert.NotNull(result);
        Assert.Equal("IoTSpy-CA.crt", result.FileDownloadName);
    }

    [Fact]
    public async Task DownloadRootCaPem_UsesSanitizedCommonNameAsFilename()
    {
        var ca = Substitute.For<ICertificateAuthority>();
        var entry = MakeEntry("My/CA:Name");
        ca.GetOrCreateRootCaAsync(Arg.Any<CancellationToken>()).Returns(entry);
        var controller = CreateController(ca);

        var result = await controller.DownloadRootCaPem() as FileContentResult;

        Assert.NotNull(result);
        Assert.Equal("MyCAName.pem", result.FileDownloadName);
        Assert.Equal("application/x-pem-file", result.ContentType);
    }

    [Fact]
    public async Task DownloadRootCaPem_EmptyCommonName_FallsBackToDefaultFilename()
    {
        var ca = Substitute.For<ICertificateAuthority>();
        var entry = MakeEntry("   ");
        ca.GetOrCreateRootCaAsync(Arg.Any<CancellationToken>()).Returns(entry);
        var controller = CreateController(ca);

        var result = await controller.DownloadRootCaPem() as FileContentResult;

        Assert.NotNull(result);
        Assert.Equal("iotspy-ca.pem", result.FileDownloadName);
    }
}
