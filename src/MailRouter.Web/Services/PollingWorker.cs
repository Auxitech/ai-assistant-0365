using MailRouter.Web.Data;

namespace MailRouter.Web.Services;

public class PollingWorker(IServiceScopeFactory scopes, ILogger<PollingWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            int interval = 60;
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var auth = scope.ServiceProvider.GetRequiredService<GraphAuthService>();
                var settings = await db.GetSettingsAsync(stoppingToken);
                interval = Math.Max(15, settings.PollIntervalSeconds);

                if (settings.Enabled && await auth.GetAccountAsync() != null)
                {
                    var result = await scope.ServiceProvider.GetRequiredService<ProcessingService>().RunOnceAsync(stoppingToken);
                    if (result.Processed > 0) log.LogInformation("Processed {Count} new message(s)", result.Processed);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "Polling loop error");
            }
            await Task.Delay(TimeSpan.FromSeconds(interval), stoppingToken);
        }
    }
}
