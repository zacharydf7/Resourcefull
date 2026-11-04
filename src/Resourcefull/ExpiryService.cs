using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Resourcefull;

/// <summary>How often <see cref="ExpiryService"/> deletes expired versions.</summary>
internal sealed record ExpirySettings(TimeSpan Interval);

/// <summary>
/// Deletes expired versions, then purges what's due to leave the bin, when the application starts and every interval
/// after, each time in a scope of its own, as <see cref="ResourcefullOptionsBuilder.UseExpiry"/> registers it.
/// </summary>
internal sealed partial class ExpiryService(IServiceScopeFactory scopes, ExpirySettings settings, string? storeName,
    ILogger<ExpiryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // So the logs say which store's sweep they're about.
        using IDisposable? scope = storeName is null ? null : logger.BeginScope("Resourcefull store {StoreName}", storeName);
        using var timer = new PeriodicTimer(settings.Interval);
        try
        {
            do
            {
                try
                {
                    AsyncServiceScope services = scopes.CreateAsyncScope();
                    await using var servicesDisposal = services.ConfigureAwait(false);
                    IResourceServiceClient client = services.ServiceProvider.GetRequiredStoreService<IResourceServiceClient>(storeName);
                    try
                    {
                        await client.DeleteExpiredAsync(stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not NotSupportedException && !stoppingToken.IsCancellationRequested)
                    {
                        // The next interval tries again: the versions this one didn't reach are still expired. The purge
                        // still runs, so a failing expiry doesn't keep what's due to leave the bin.
                        LogFailed(logger, exception);
                    }
                    try
                    {
                        await client.PurgeDeletedAsync(stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not NotSupportedException && !stoppingToken.IsCancellationRequested)
                    {
                        LogPurgeFailed(logger, exception);
                    }
                }
                catch (NotSupportedException exception)
                {
                    LogNotSupported(logger, exception);
                    return;
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    LogFailed(logger, exception);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The application is stopping.
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Expiry is turned on, but the storage provider doesn't support it, so expired versions aren't deleted.")]
    private static partial void LogNotSupported(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Deleting expired versions failed; the next interval tries again.")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Purging versions due to leave the bin failed; the next interval tries again.")]
    private static partial void LogPurgeFailed(ILogger logger, Exception exception);
}
