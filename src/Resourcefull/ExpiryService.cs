using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Resourcefull.Providers;

namespace Resourcefull;

/// <summary>How often <see cref="ExpiryService"/> deletes expired versions.</summary>
internal sealed record ExpirySettings(TimeSpan Interval);

/// <summary>
/// Deletes expired versions, then purges what's due to leave the bin, when the application starts and every interval
/// after, each time in a scope of its own, as <see cref="ResourcefullOptionsBuilder.UseExpiry"/> registers it. It stops
/// the application from starting on a provider that doesn't support expiry, which could never delete or purge anything.
/// </summary>
internal sealed partial class ExpiryService(IServiceScopeFactory scopes, ExpirySettings settings, string? storeName,
    ILogger<ExpiryService> logger) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        AsyncServiceScope services = scopes.CreateAsyncScope();
        await using (services.ConfigureAwait(false))
        {
            ResourceRepository repository = services.ServiceProvider.GetRequiredStoreService<ResourceRepository>(storeName);
            if (!repository.SupportsExpiry)
            {
                throw new InvalidOperationException($"{nameof(ResourcefullOptionsBuilder.UseExpiry)} is turned on, but " +
                    $"{repository.GetType().Name} doesn't support expiry ({nameof(ResourceRepository.SupportsExpiry)} is " +
                    "false), so nothing would delete expired versions or purge deleted ones. Remove " +
                    $"{nameof(ResourcefullOptionsBuilder.UseExpiry)} or use a provider that supports expiry.");
            }
        }
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

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
                    catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                    {
                        // The next interval tries again: the versions this one didn't reach are still expired. The purge
                        // still runs, so a failing expiry doesn't keep what's due to leave the bin.
                        LogFailed(logger, exception);
                    }
                    try
                    {
                        await client.PurgeDeletedAsync(stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                    {
                        LogPurgeFailed(logger, exception);
                    }
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Deleting expired versions failed; the next interval tries again.")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Purging versions due to leave the bin failed; the next interval tries again.")]
    private static partial void LogPurgeFailed(ILogger logger, Exception exception);
}
