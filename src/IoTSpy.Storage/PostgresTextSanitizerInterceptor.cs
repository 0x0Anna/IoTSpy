using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;

namespace IoTSpy.Storage;

/// <summary>
/// PostgreSQL <c>text</c> columns cannot hold a NUL (0x00) character; the insert fails with
/// <c>22021: invalid byte sequence for encoding "UTF8": 0x00</c> and the whole row is lost.
/// Captured bodies are binary payloads (images, protobuf, TLS records) decoded to strings, so
/// they routinely contain NULs. This interceptor replaces them with U+FFFD, the Unicode
/// replacement character, in every string property of added or modified entities just before
/// they are saved. Only registered for the Postgres provider; SQLite stores NULs fine.
/// </summary>
public sealed class PostgresTextSanitizerInterceptor : SaveChangesInterceptor
{
    private const char Nul = '\0';
    private const char Replacement = '�';

    // Entity type -> its string properties, so each save does not re-walk the model metadata.
    private static readonly ConcurrentDictionary<IEntityType, IProperty[]> StringProperties = new();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Sanitize(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Sanitize(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    /// <summary>Replaces NUL characters in a string, returning the same instance when there are none.</summary>
    public static string Sanitize(string value) =>
        value.Contains(Nul) ? value.Replace(Nul, Replacement) : value;

    private static void Sanitize(DbContext? context)
    {
        if (context is null) return;

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified)) continue;

            var properties = StringProperties.GetOrAdd(entry.Metadata,
                static type => type.GetProperties().Where(p => p.ClrType == typeof(string)).ToArray());

            foreach (var property in properties)
            {
                var propertyEntry = entry.Property(property.Name);
                // An unmodified property of a Modified entity is not written, so leave it alone.
                if (entry.State == EntityState.Modified && !propertyEntry.IsModified) continue;

                if (propertyEntry.CurrentValue is string text && text.Contains(Nul))
                    propertyEntry.CurrentValue = Sanitize(text);
            }
        }
    }
}
