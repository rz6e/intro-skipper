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
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin not initialized");

        var queue = _queueManager.BuildQueue(requests, plugin.Configuration);

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
