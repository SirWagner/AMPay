using AMPay.Domain.Reporting;
using AMPay.Infrastructure.Credit;

namespace AMPay.Web.Services;

/// <summary>
/// Reruns the good-to-go check once a day, at 01:00 SAST, for every client whose last result is
/// older than their lender's recheck period.
/// <para>
/// Off unless <c>Screening:DailyRecheck</c> is true, for the same reason as the nightly portfolio
/// snapshot: on a pausing serverless database it costs a wake-up every night. With it off, the
/// client list does the same work a few clients at a time whenever it is opened.
/// </para>
/// </summary>
public class ScreeningRecheckService : BackgroundService
{
    public const string DailySetting = "Screening:DailyRecheck";

    private static readonly TimeSpan RunAt = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ScreeningRecheckService> _log;

    public ScreeningRecheckService(IServiceScopeFactory scopes, ILogger<ScreeningRecheckService> log)
    {
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            var next = Sast.StartUtc(Sast.DateOf(now)) + RunAt;
            if (next <= now) next = Sast.StartUtc(Sast.DateOf(now).AddDays(1)) + RunAt;

            try { await Task.Delay(next - now, stoppingToken); }
            catch (OperationCanceledException) { return; }

            try
            {
                using var scope = _scopes.CreateScope();
                var screening = scope.ServiceProvider.GetRequiredService<ScreeningService>();
                await screening.RecheckStaleAsync(null, DateTime.UtcNow, ct: stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A paused database or a blip: tomorrow's run, or the client list, picks it up.
                _log.LogWarning("Daily good-to-go recheck skipped: {Message}", ex.GetBaseException().Message);
            }
        }
    }
}
