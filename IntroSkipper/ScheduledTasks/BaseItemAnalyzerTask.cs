// SPDX-FileCopyrightText: 2023 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Analyzers;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Manager;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.ScheduledTasks;

/// <summary>
/// Runs the full analysis pipeline over a pre-built episode queue.
/// </summary>
public partial class BaseItemAnalyzerTask
{
    private const double AnimePreviewStartTolerance = 0.5;

    private readonly ILogger _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly FileQueueManager _fileQueueManager;
    private readonly PluginConfiguration _config;
    private readonly bool _ffmpegValid;

    /// <summary>
    /// Initializes a new instance of the <see cref="BaseItemAnalyzerTask"/> class.
    /// </summary>
    public BaseItemAnalyzerTask(
        ILogger logger,
        ILoggerFactory loggerFactory,
        FileQueueManager fileQueueManager)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _fileQueueManager = fileQueueManager;
        _config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        _ffmpegValid = FFmpegWrapper.CheckFFmpegVersion();
    }

    /// <summary>
    /// Analyzes a pre-built queue of media items.
    /// </summary>
    /// <param name="queue">Episodes grouped by season Guid (built by <see cref="FileQueueManager"/>).</param>
    /// <param name="progress">Progress reporter (0-100).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="seasonsToAnalyze">Optional filter — only analyze these season Guids.</param>
    public async Task AnalyzeItemsAsync(
        IReadOnlyDictionary<Guid, List<QueuedEpisode>> queue,
        IProgress<double> progress,
        CancellationToken cancellationToken,
        IReadOnlyCollection<Guid>? seasonsToAnalyze = null)
    {
        HashSet<AnalysisMode> modes = [
            .. _config.ScanIntroduction ? [AnalysisMode.Introduction] : Array.Empty<AnalysisMode>(),
            .. _config.ScanCredits ? [AnalysisMode.Credits] : Array.Empty<AnalysisMode>(),
            .. _config.ScanRecap ? [AnalysisMode.Recap] : Array.Empty<AnalysisMode>(),
            .. _config.ScanPreview ? [AnalysisMode.Preview] : Array.Empty<AnalysisMode>(),
            .. _config.ScanCommercial ? [AnalysisMode.Commercial] : Array.Empty<AnalysisMode>()
        ];

        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin instance is null");

        var filteredQueue = seasonsToAnalyze?.Count > 0
            ? queue.Where(kvp => seasonsToAnalyze.Contains(kvp.Key)).ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
            : (IReadOnlyDictionary<Guid, List<QueuedEpisode>>)queue;

        if (!plugin.LegacyFingerprintMigrationDone)
        {
            FFmpegWrapper.MigrateLegacyDetectionCache(
                filteredQueue.Values.SelectMany(static eps => eps),
                cancellationToken);
            plugin.LegacyFingerprintMigrationDone = true;
        }

        int totalQueued = filteredQueue.Sum(kvp => kvp.Value.Count) * modes.Count;
        if (totalQueued == 0)
        {
            LogNoItemsQueued(_logger);
            return;
        }

        if (!_ffmpegValid)
        {
            LogSkippingChromaprint(_logger);
        }

        int totalProcessed = 0;
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, _config.MaxParallelism),
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(filteredQueue, options, async (season, ct) =>
        {
            var episodes = await _fileQueueManager.VerifyQueueAsync(season.Value, modes, ct).ConfigureAwait(false);
            if (episodes.Count == 0)
            {
                return;
            }

            var first = episodes[0];
            if (first.IsExcluded)
            {
                Interlocked.Add(ref totalProcessed, episodes.Count * modes.Count);
                progress.Report((double)totalProcessed / totalQueued * 100);
                LogSkippingExcludedSeason(_logger, first.SeasonNumber, first.SeriesName);
                return;
            }

            try
            {
                foreach (var mode in modes)
                {
                    ct.ThrowIfCancellationRequested();
                    int analyzed = await AnalyzeSeasonAsync(plugin, episodes, mode, ct).ConfigureAwait(false);
                    Interlocked.Add(ref totalProcessed, episodes.Count);
                    progress.Report((double)totalProcessed / totalQueued * 100);
                    _ = analyzed; // consumed for side effects
                }
            }
            catch (OperationCanceledException)
            {
                LogAnalysisCanceled(_logger);
            }
            catch (FingerprintException ex)
            {
                LogFingerprintException(_logger, ex);
            }
            catch (Exception ex)
            {
                LogUnexpectedError(_logger, ex);
                throw;
            }
        }).ConfigureAwait(false);

        plugin.AnalyzeAgain = false;
    }

    private async Task<int> AnalyzeSeasonAsync(
        Plugin plugin,
        IReadOnlyList<QueuedEpisode> items,
        AnalysisMode mode,
        CancellationToken cancellationToken)
    {
        if (!items.Any(e => e.GetAnalyzed(mode) == EpisodeState.NotAnalyzed))
        {
            return 0;
        }

        var first = items[0];
        var category = first.Category;
        var isMovie = category == QueuedMediaCategory.Movie;
        var isAnime = category == QueuedMediaCategory.AnimeEpisode;

        if (!isMovie && first.SeasonNumber == 0 && !_config.AnalyzeSeasonZero)
        {
            return 0;
        }

        var totalItems = items.Count(e => e.GetAnalyzed(mode) != EpisodeState.Analyzed);
        var action = await plugin.GetAnalyzerActionAsync(first.SeasonId, mode, cancellationToken).ConfigureAwait(false);

        if (action == AnalyzerAction.None)
        {
            LogSkippingNoneAction(_logger, mode, first.SeriesName, first.SeasonNumber);
            return 0;
        }

        LogAnalyzingFiles(_logger, mode, items.Count, first.SeriesName, first.SeasonNumber);

        var analyzers = new List<IMediaFileAnalyzer>
        {
            new ChapterAnalyzer(_loggerFactory.CreateLogger<ChapterAnalyzer>())
        };

        if (mode is AnalysisMode.Credits)
        {
            if (isAnime)
            {
                if (_ffmpegValid)
                {
                    analyzers.Add(new ChromaprintAnalyzer(_loggerFactory.CreateLogger<ChromaprintAnalyzer>()));
                }

                analyzers.Add(CreateBlackFrameAnalyzer());
            }
            else
            {
                analyzers.Add(CreateBlackFrameAnalyzer());

                if (!isMovie && _ffmpegValid)
                {
                    analyzers.Add(new ChromaprintAnalyzer(_loggerFactory.CreateLogger<ChromaprintAnalyzer>()));
                }
            }
        }
        else if (mode is AnalysisMode.Introduction)
        {
            if (!isMovie && _ffmpegValid)
            {
                analyzers.Add(new ChromaprintAnalyzer(_loggerFactory.CreateLogger<ChromaprintAnalyzer>()));
            }
        }

        switch (action)
        {
            case AnalyzerAction.Chapter:
                PromoteAnalyzer(analyzers, static a => a is ChapterAnalyzer);
                break;
            case AnalyzerAction.Chromaprint:
                PromoteAnalyzer(analyzers, static a => a is ChromaprintAnalyzer);
                break;
            case AnalyzerAction.BlackFrame:
                PromoteAnalyzer(analyzers, static a => a is BlackFrameAnalyzer or BlackFrameAltAnalyzer);
                break;
            default:
                if (_config.PreferChromaprint && _ffmpegValid)
                {
                    PromoteAnalyzer(analyzers, static a => a is ChromaprintAnalyzer);
                }

                break;
        }

        foreach (var analyzer in analyzers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            items = await analyzer.AnalyzeMediaFiles(items, mode, cancellationToken).ConfigureAwait(false);
        }

        if (mode == AnalysisMode.Credits && isAnime && _config.AnimePreviewFromCreditsEnd)
        {
            await CreateAnimePreviewFromCreditsAsync(plugin, items, cancellationToken).ConfigureAwait(false);
        }

        await plugin.SetEpisodeIdsAsync(first.SeasonId, mode, items.Select(i => i.EpisodeId), cancellationToken).ConfigureAwait(false);

        return totalItems - items.Count(e => e.GetAnalyzed(mode) != EpisodeState.Analyzed);
    }

    /// <summary>
    /// Computes the Preview segment derived from the end of credits (used for anime).
    /// Public for unit testing.
    /// </summary>
    public static Segment? ComputeAnimePreviewFromCredits(
        Guid episodeId,
        double episodeDuration,
        IReadOnlyDictionary<AnalysisMode, Segment> existingTimestamps)
    {
        ArgumentNullException.ThrowIfNull(existingTimestamps);

        if (!existingTimestamps.TryGetValue(AnalysisMode.Credits, out var credits) || !credits.Valid)
        {
            return null;
        }

        if (credits.End >= episodeDuration)
        {
            return null;
        }

        if (existingTimestamps.TryGetValue(AnalysisMode.Preview, out var existing)
            && existing.Valid
            && Math.Abs(existing.Start - credits.End) <= AnimePreviewStartTolerance
            && Math.Abs(existing.End - episodeDuration) <= AnimePreviewStartTolerance)
        {
            return null;
        }

        return new Segment(episodeId, new TimeRange(credits.End, episodeDuration));
    }

    private IMediaFileAnalyzer CreateBlackFrameAnalyzer() => _config.UseAlternativeBlackFrameAnalyzer
        ? new BlackFrameAltAnalyzer(_loggerFactory.CreateLogger<BlackFrameAltAnalyzer>())
        : new BlackFrameAnalyzer(_loggerFactory.CreateLogger<BlackFrameAnalyzer>());

    private static void PromoteAnalyzer(List<IMediaFileAnalyzer> analyzers, Func<IMediaFileAnalyzer, bool> predicate)
    {
        var index = analyzers.FindIndex(a => predicate(a));
        if (index > 0)
        {
            var analyzer = analyzers[index];
            analyzers.RemoveAt(index);
            analyzers.Insert(0, analyzer);
        }
    }

    private async Task CreateAnimePreviewFromCreditsAsync(
        Plugin plugin,
        IReadOnlyList<QueuedEpisode> items,
        CancellationToken cancellationToken)
    {
        foreach (var episode in items)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var dbSegments = await plugin.GetSegmentsAsync(episode.EpisodeId, cancellationToken).ConfigureAwait(false);

            if (dbSegments.Any(s => s.Type == AnalysisMode.Preview && s.IsUserProvided))
            {
                LogSkippedUserProvidedPreview(_logger, episode.Name);
                continue;
            }

            var timestamps = dbSegments
                .GroupBy(s => s.Type)
                .ToDictionary(g => g.Key, g => g.OrderBy(s => s.Start).First().ToSegment());

            var preview = ComputeAnimePreviewFromCredits(episode.EpisodeId, episode.Duration, timestamps);
            if (preview is null)
            {
                continue;
            }

            await plugin.UpdateTimestampAsync(preview, AnalysisMode.Preview, cancellationToken: cancellationToken).ConfigureAwait(false);
            episode.SetAnalyzed(AnalysisMode.Preview, EpisodeState.Analyzed);

            LogCreatedAnimePreview(_logger, episode.Name, preview.Start, preview.End);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Created anime preview for {Episode}: {Start:F2}s to {End:F2}s")]
    private static partial void LogCreatedAnimePreview(ILogger logger, string episode, double start, double end);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping anime preview for {Episode}: a user-provided Preview already exists.")]
    private static partial void LogSkippedUserProvidedPreview(ILogger logger, string episode);

    [LoggerMessage(Level = LogLevel.Information, Message = "No episodes in queue.")]
    private static partial void LogNoItemsQueued(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Skipping Chromaprint analysis: ffmpeg does not support chromaprint. Install jellyfin-ffmpeg7 or a build with libchromaprint.")]
    private static partial void LogSkippingChromaprint(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Skipping excluded season {Season} of {Series}")]
    private static partial void LogSkippingExcludedSeason(ILogger logger, int season, string series);

    [LoggerMessage(Level = LogLevel.Information, Message = "Analysis was canceled.")]
    private static partial void LogAnalysisCanceled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fingerprint exception during analysis.")]
    private static partial void LogFingerprintException(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "An unexpected error occurred during analysis.")]
    private static partial void LogUnexpectedError(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "[Mode: {Mode}] Analyzing {Count} files from {Name} season {Season}")]
    private static partial void LogAnalyzingFiles(ILogger logger, AnalysisMode mode, int count, string name, int season);

    [LoggerMessage(Level = LogLevel.Information, Message = "[Mode: {Mode}] Skipping {Name} season {Season}: analyzer action is set to None")]
    private static partial void LogSkippingNoneAction(ILogger logger, AnalysisMode mode, string name, int season);
}
