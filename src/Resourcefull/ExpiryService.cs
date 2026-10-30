using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Resourcefull;

/// <summary>How often <see cref="ExpiryService"/> deletes expired versions.</summary>
internal sealed record ExpirySettings(TimeSpan Interval);

/// <summary>
/// Deletes expired versions when the application starts and every interval after, each time in a scope of its own,
/// as <see cref="ResourcefullOptionsBuilder.UseExpiry"/> registers it.
/// </summary>
internal sealed partial class ExpiryService(IServiceScopeFactory scopes, ExpirySettings settings, ILogger<ExpiryService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(settings.Interval);
        try
        {
            do
            {
                try
                {
                    await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<IResourceServiceClient>().DeleteExpiredAsync(stoppingToken);
                }
                catch (NotSupportedException exception)
                {
                    LogNotSupported(logger, exception);
                    return;
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    // The next interval tries again: the versions this one didn't reach are still expired.
                    LogFailed(logger, exception);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
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
}
