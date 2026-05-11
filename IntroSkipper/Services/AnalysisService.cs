// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using IntroSkipper.Manager;
using IntroSkipper.ScheduledTasks;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Services;

/// <summary>
/// Orchestrates fingerprint analysis for a list of episode requests.
/// Call <see cref="AnalyzeAsync"/> from the REST controller or CLI.
/// </summary>
public sealed class AnalysisService
{
    private readonly FileQueueManager _queueManager;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AnalysisService> _logger;
    private int _pendingBatches;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnalysisService"/> class.
    /// </summary>
    public AnalysisService(
        FileQueueManager queueManager,
        ILoggerFactory loggerFactory,
        ILogger<AnalysisService> logger)
    {
        _queueManager = queueManager;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <summary>Gets a value indicating whether analysis is currently in progress.</summary>
    public bool IsAnalyzing => Volatile.Read(ref _pendingBatches) > 0;

    /// <summary>
    /// Signals that a batch of episodes is about to be submitted for analysis.
    /// Must be called before firing the background task so that <see cref="IsAnalyzing"/>
    /// is true by the time the caller starts polling.
    /// </summary>
    public void IncrementPendingBatch() => Interlocked.Increment(ref _pendingBatches);

    /// <summary>
    /// Runs the full analysis pipeline over the supplied episode requests.
    /// </summary>
    /// <param name="requests">Episodes to analyze.</param>
    /// <param name="progress">Optional progress reporter (0–100).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task AnalyzeAsync(
        IEnumerable<AnalyzeRequest> requests,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin not initialized");

            var queue = await _queueManager.BuildQueueAsync(requests, plugin.Configuration, cancellationToken).ConfigureAwait(false);

            if (queue.Count == 0)
            {
                _logger.LogWarning("No valid episodes in request — nothing to analyze");
                return;
            }

            _logger.LogInformation(
                "Starting analysis: {Seasons} season(s), {Episodes} episode(s)",
                queue.Count,
                queue.Values.Sum(v => v.Count));

            var task = new BaseItemAnalyzerTask(
                _loggerFactory.CreateLogger<BaseItemAnalyzerTask>(),
                _loggerFactory,
                _queueManager);

            await task.AnalyzeItemsAsync(
                queue,
                progress ?? new Progress<double>(),
                cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _pendingBatches);
        }
    }

    /// <summary>
    /// Retrieves stored skip timestamps for a specific episode, keyed by AniList ID and episode number.
    /// </summary>
    public async Task<IReadOnlyDictionary<AnalysisMode, Segment>> GetTimestampsAsync(
        int aniListId,
        int seasonNumber,
        int episodeNumber,
        CancellationToken cancellationToken = default)
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin not initialized");
        var episodeGuid = Helper.AniListGuid.ForEpisode(aniListId, seasonNumber, episodeNumber);
        return await plugin.GetTimestampsAsync(episodeGuid, cancellationToken).ConfigureAwait(false);
    }
}
