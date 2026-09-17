using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using IoTSpy.Storage;
using Xunit;

namespace IoTSpy.Api.IntegrationTests;

/// <summary>
/// Verifies the OpenTelemetry tracing pipeline (backlog #51) is truly opt-in: the DI
/// container must build and the app must start cleanly both with Otel:Enabled absent
/// (the default, tracing pipeline not registered at all) and with Otel:Enabled=true
/// (tracing pipeline registered against a config-supplied OTLP endpoint that is never
/// actually dialed during these tests — the exporter batches asynchronously and never
/// blocks startup or request handling).
/// </summary>
public class OtelStartupTests
{
    [Fact]
    public async Task Startup_WithOtelDisabled_Default_BuildsAndServesHealthCheck()
    {
        var factory = new IoTSpyWebApplicationFactory();
        await factory.InitializeDbAsync();
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Startup_WithOtelExplicitlyDisabled_BuildsAndServesHealthCheck()
    {
        var baseFactory = new IoTSpyWebApplicationFactory();
        var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.UseSetting("Otel:Enabled", "false"));
        await InitializeDbAsync(factory);
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Startup_WithOtelEnabled_BuildsAndServesHealthCheck()
    {
        var baseFactory = new IoTSpyWebApplicationFactory();
        var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.UseSetting("Otel:Enabled", "true")
                   .UseSetting("Otel:ServiceName", "IoTSpy.Api.Tests")
                   .UseSetting("Otel:OtlpEndpoint", "http://localhost:4317"));
        await InitializeDbAsync(factory);
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    /// <summary>
    /// Mirrors IoTSpyWebApplicationFactory.InitializeDbAsync, needed here because
    /// WithWebHostBuilder returns the base WebApplicationFactory&lt;Program&gt; type
    /// (losing access to that helper method) while still sharing the same in-memory
    /// SQLite DbContext registration.
    /// </summary>
    private static async Task InitializeDbAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IoTSpyDbContext>();
        await db.Database.EnsureCreatedAsync();
    }
}
