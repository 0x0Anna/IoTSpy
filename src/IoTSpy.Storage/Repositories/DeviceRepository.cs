using IoTSpy.Core.Interfaces;
using IoTSpy.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace IoTSpy.Storage.Repositories;

public class DeviceRepository(IoTSpyDbContext db) : IDeviceRepository
{
    // UpsertByIpAsync runs on every single proxied connection (ExplicitProxyServer,
    // TransparentProxyServer, CoapProxy all call it once per connection, not once per
    // device). SQLite allows only one writer at a time for the whole database file, so
    // unconditionally writing LastSeen on every call serializes every connection from a
    // single client behind that write lock — a page that fans out to many hosts at once
    // (e.g. a video site loading dozens of CDN/ad/font hosts in parallel) collapses to
    // one connection completing at a time instead of running concurrently. LastSeen is
    // only ever shown as a coarse "when was this device last active" timestamp (never a
    // live/real-time indicator), so it doesn't need per-connection precision — only
    // write it when it's actually gone stale.
    private static readonly TimeSpan LastSeenWriteThreshold = TimeSpan.FromSeconds(30);

    public Task<List<Device>> GetAllAsync(CancellationToken ct = default) =>
        db.Devices.OrderByDescending(d => d.LastSeen).ToListAsync(ct);

    public Task<Device?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.Devices.FirstOrDefaultAsync(d => d.Id == id, ct);

    public Task<Device?> GetByIpAsync(string ip, CancellationToken ct = default) =>
        db.Devices.FirstOrDefaultAsync(d => d.IpAddress == ip, ct);

    public async Task<Device> UpsertByIpAsync(Device device, CancellationToken ct = default)
    {
        var existing = await db.Devices.FirstOrDefaultAsync(d => d.IpAddress == device.IpAddress, ct);
        if (existing is null)
        {
            db.Devices.Add(device);
            await db.SaveChangesAsync(ct);
            return device;
        }

        var now = DateTimeOffset.UtcNow;
        var hasMetadataChange =
            (!string.IsNullOrEmpty(device.Hostname) && existing.Hostname != device.Hostname) ||
            (!string.IsNullOrEmpty(device.Vendor) && existing.Vendor != device.Vendor) ||
            (!string.IsNullOrEmpty(device.MacAddress) && existing.MacAddress != device.MacAddress);
        var lastSeenIsStale = now - existing.LastSeen >= LastSeenWriteThreshold;

        // Nothing worth persisting for this connection — skip the write entirely rather
        // than round-tripping a no-op UPDATE through SQLite's single-writer lock.
        if (!hasMetadataChange && !lastSeenIsStale)
            return existing;

        existing.LastSeen = now;
        if (!string.IsNullOrEmpty(device.Hostname) && existing.Hostname != device.Hostname)
            existing.Hostname = device.Hostname;
        if (!string.IsNullOrEmpty(device.Vendor) && existing.Vendor != device.Vendor)
            existing.Vendor = device.Vendor;
        if (!string.IsNullOrEmpty(device.MacAddress) && existing.MacAddress != device.MacAddress)
            existing.MacAddress = device.MacAddress;

        await db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task<Device> UpdateAsync(Device device, CancellationToken ct = default)
    {
        db.Devices.Update(device);
        await db.SaveChangesAsync(ct);
        return device;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var device = await db.Devices.FindAsync([id], ct);
        if (device is not null)
        {
            db.Devices.Remove(device);
            await db.SaveChangesAsync(ct);
        }
    }
}
