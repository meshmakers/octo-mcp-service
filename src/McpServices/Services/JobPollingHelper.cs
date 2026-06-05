using Meshmakers.Octo.Sdk.ServiceClient.BotServices;

namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Polls the Bot service for a long-running job until it reaches a terminal state. The Bot service hosts
///     job tracking for asset imports + exports + dumps; the asset SDK returns a job id and the bot SDK
///     reports its progress.
/// </summary>
internal static class JobPollingHelper
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    ///     Polls until job is Succeeded (returns), Failed (throws with reason), or timeout (throws TimeoutException).
    /// </summary>
    public static async Task WaitForJobAsync(
        IBotServicesClient bot, string jobId,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow.Add(timeout ?? DefaultTimeout);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var job = await bot.GetImportJobStatus(jobId);
            if (job.Status == "Succeeded")
            {
                return;
            }

            if (job.Status == "Failed")
            {
                throw new InvalidOperationException(
                    $"Job '{jobId}' failed: {job.ErrorMessage ?? job.Reason ?? "(no reason given)"}");
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"Job '{jobId}' did not complete within {(timeout ?? DefaultTimeout).TotalMinutes:N0} minutes " +
                    $"(last status: {job.Status}).");
            }

            try { await Task.Delay(PollInterval, cancellationToken); }
            catch (TaskCanceledException) { throw; }
        }
    }
}
