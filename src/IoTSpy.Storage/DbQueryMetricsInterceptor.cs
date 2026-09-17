using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace IoTSpy.Storage;

/// <summary>
/// EF Core command interceptor that times every database command (reader, non-query and
/// scalar; sync and async) and records the elapsed time to <see cref="StorageMetrics"/>.
/// Registered once on <see cref="IoTSpyDbContext"/>'s <c>DbContextOptionsBuilder</c> —
/// this is the single implementation point for #52's "DB query latency" metric rather
/// than instrumenting every repository method individually.
/// </summary>
public sealed class DbQueryMetricsInterceptor : DbCommandInterceptor
{
    // CommandEventData.CommandId uniquely identifies one execution (correlates the
    // Executing/Executed pair), so it doubles as our timer key.
    private static readonly ConcurrentDictionary<Guid, long> Starts = new();

    private static void Start(CommandEventData eventData) =>
        Starts[eventData.CommandId] = Stopwatch.GetTimestamp();

    private static void Stop(CommandEventData eventData)
    {
        if (Starts.TryRemove(eventData.CommandId, out var startTimestamp))
        {
            var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
            StorageMetrics.RecordDbQueryDuration(elapsed.TotalSeconds);
        }
    }

    // ── Reader (SELECT) ──────────────────────────────────────────────────────────

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Start(eventData);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Start(eventData);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override DbDataReader ReaderExecuted(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        Stop(eventData);
        return base.ReaderExecuted(command, eventData, result);
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        Stop(eventData);
        return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }

    // ── NonQuery (INSERT/UPDATE/DELETE) ──────────────────────────────────────────

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Start(eventData);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Start(eventData);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override int NonQueryExecuted(
        DbCommand command, CommandExecutedEventData eventData, int result)
    {
        Stop(eventData);
        return base.NonQueryExecuted(command, eventData, result);
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        Stop(eventData);
        return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
    }

    // ── Scalar ────────────────────────────────────────────────────────────────────

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Start(eventData);
        return base.ScalarExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Start(eventData);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override object? ScalarExecuted(
        DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        Stop(eventData);
        return base.ScalarExecuted(command, eventData, result);
    }

    public override ValueTask<object?> ScalarExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, object? result,
        CancellationToken cancellationToken = default)
    {
        Stop(eventData);
        return base.ScalarExecutedAsync(command, eventData, result, cancellationToken);
    }

    // ── Command failures still need their timer entry cleaned up ───────────────────

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData) =>
        Stop(eventData);

    public override Task CommandFailedAsync(
        DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Stop(eventData);
        return Task.CompletedTask;
    }
}
