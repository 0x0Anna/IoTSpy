using IoTSpy.Core.Enums;
using IoTSpy.Core.Models;
using IoTSpy.Storage.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace IoTSpy.Storage.Tests.Repositories;

/// <summary>Minimal IConfiguration fake exposing a single key, avoiding a new
/// Microsoft.Extensions.Configuration package reference for one test.</summary>
file sealed class SingleKeyConfiguration(string key, string value) : IConfiguration
{
    public string? this[string k] { get => k == key ? value : null; set => throw new NotSupportedException(); }
    public IEnumerable<IConfigurationSection> GetChildren() => [];
    public IChangeToken GetReloadToken() => throw new NotSupportedException();
    public IConfigurationSection GetSection(string k) => throw new NotSupportedException();
}

public class ProxySettingsRepositoryTests : IDisposable
{
    private readonly IoTSpyDbContext _db = TestDbContextFactory.Create();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task GetAsync_WhenNoSettings_ReturnsDefaults()
    {
        var repo = new ProxySettingsRepository(_db);
        var settings = await repo.GetAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(settings);
        // Default port is 8888
        Assert.Equal(8888, settings.ProxyPort);
    }

    [Fact]
    public async Task GetAsync_WhenNoSettings_UsesConfiguredDefaultPort()
    {
        var config = new SingleKeyConfiguration("Proxy:DefaultPort", "8899");
        var repo = new ProxySettingsRepository(_db, config);

        var settings = await repo.GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(8899, settings.ProxyPort);
    }

    [Fact]
    public async Task GetAsync_CalledTwice_ReturnsSameRow()
    {
        var repo = new ProxySettingsRepository(_db);
        var s1 = await repo.GetAsync(TestContext.Current.CancellationToken);
        var s2 = await repo.GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(s1.Id, s2.Id);
    }

    [Fact]
    public async Task SaveAsync_PersistsChanges()
    {
        var repo = new ProxySettingsRepository(_db);
        var settings = await repo.GetAsync(TestContext.Current.CancellationToken);
        settings.ProxyPort = 9999;
        settings.Mode = ProxyMode.GatewayRedirect;

        await repo.SaveAsync(settings, TestContext.Current.CancellationToken);

        var reloaded = await repo.GetAsync(TestContext.Current.CancellationToken);
        Assert.Equal(9999, reloaded.ProxyPort);
        Assert.Equal(ProxyMode.GatewayRedirect, reloaded.Mode);
    }

    [Fact]
    public async Task SaveAsync_WhenNoExisting_CreatesRow()
    {
        var repo = new ProxySettingsRepository(_db);
        var newSettings = new ProxySettings { ProxyPort = 7777 };

        await repo.SaveAsync(newSettings, TestContext.Current.CancellationToken);

        var reloaded = await repo.GetAsync(TestContext.Current.CancellationToken);
        Assert.Equal(7777, reloaded.ProxyPort);
    }

    [Fact]
    public async Task GetAsync_LegacyPassiveMode_IsNormalizedAndPersisted()
    {
        var ct = TestContext.Current.CancellationToken;
#pragma warning disable CS0618 // a database written before IsPassive existed
        _db.ProxySettings.Add(new ProxySettings { Mode = ProxyMode.Passive });
#pragma warning restore CS0618
        await _db.SaveChangesAsync(ct);
        var repo = new ProxySettingsRepository(_db);

        var settings = await repo.GetAsync(ct);

        Assert.Equal(ProxyMode.ExplicitProxy, settings.Mode);
        Assert.True(settings.IsPassive);
        _db.ChangeTracker.Clear();
        var stored = Assert.Single(_db.ProxySettings);
        Assert.Equal(ProxyMode.ExplicitProxy, stored.Mode);
        Assert.True(stored.IsPassive);
    }

    [Fact]
    public async Task SaveAsync_PassiveWithGatewayRedirect_RoundTrips()
    {
        var ct = TestContext.Current.CancellationToken;
        var repo = new ProxySettingsRepository(_db);
        var settings = await repo.GetAsync(ct);
        settings.Mode = ProxyMode.GatewayRedirect;
        settings.IsPassive = true;
        await repo.SaveAsync(settings, ct);

        _db.ChangeTracker.Clear();
        var reloaded = await repo.GetAsync(ct);

        Assert.Equal(ProxyMode.GatewayRedirect, reloaded.Mode);
        Assert.True(reloaded.IsPassive);
    }
}
