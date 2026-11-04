using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace IoTSpy.Storage.Repositories;

public class ProxySettingsRepository(IoTSpyDbContext db, IConfiguration? configuration = null) : IProxySettingsRepository
{
    public async Task<ProxySettings> GetAsync(CancellationToken ct = default)
    {
        var settings = await db.ProxySettings.OrderBy(p => p.Id).FirstOrDefaultAsync(ct);
        if (settings is not null)
        {
            if (settings.NormalizeLegacyPassiveMode())
                await db.SaveChangesAsync(ct);
            return settings;
        }

        // First-run defaults. Proxy:DefaultPort lets deployments (e.g. the
        // Asustor NAS package) seed a non-standard port to avoid colliding
        // with a port the host platform already owns.
        settings = new ProxySettings();
        if (int.TryParse(configuration?["Proxy:DefaultPort"], out var configuredPort) && configuredPort > 0)
            settings.ProxyPort = configuredPort;
        db.ProxySettings.Add(settings);
        await db.SaveChangesAsync(ct);
        return settings;
    }

    public async Task<ProxySettings> SaveAsync(ProxySettings settings, CancellationToken ct = default)
    {
        settings.NormalizeLegacyPassiveMode();
        var existing = await db.ProxySettings.OrderBy(p => p.Id).FirstOrDefaultAsync(ct);
        if (existing is null)
        {
            db.ProxySettings.Add(settings);
        }
        else
        {
            db.Entry(existing).CurrentValues.SetValues(settings);
        }
        await db.SaveChangesAsync(ct);
        return settings;
    }
}
