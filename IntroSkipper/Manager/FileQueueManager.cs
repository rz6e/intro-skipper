// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Helper;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Manager;

/// <summary>
/// Describes a single episode to be analyzed.
/// </summary>
public sealed class AnalyzeRequest
{
    /// <summary>Gets or sets the AniList series ID.</summary>
    public int AniListId { get; set; }

    /// <summary>Gets or sets the season number (1-based; use 1 for single-season shows).</summary>
    public int SeasonNumber { get; set; } = 1;

    /// <summary>Gets or sets the episode number within the season.</summary>
    public int EpisodeNumber { get; set; }

    /// <summary>Gets or sets the series title (used for logging).</summary>
    public string SeriesName { get; set; } = string.Empty;

    /// <summary>Gets or sets the episode title (used for logging).</summary>
    public string EpisodeName { get; set; } = string.Empty;

    /// <summary>Gets or sets the absolute path to the video file.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Gets or sets the episode duration in seconds. Pass 0 to auto-detect via ffprobe.</summary>
    public double DurationSeconds { get; set; }

    /// <summary>Gets or sets the content category.</summary>
    public QueuedMediaCategory Category { get; set; } = QueuedMediaCategory.AnimeEpisode;
}

/// <summary>
/// Builds the analysis queue from explicit file paths instead of scanning a Jellyfin library.
/// </summary>
public sealed class FileQueueManager
{
    private readonly ILogger<FileQueueManager> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileQueueManager"/> class.
    /// </summary>
    public FileQueueManager(ILogger<FileQueueManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Converts a flat list of episode requests into the grouped dictionary that
    /// <see cref="ScheduledTasks.BaseItemAnalyzerTask"/> expects (keyed by season Guid).
    /// When <see cref="AnalyzeRequest.DurationSeconds"/> is zero or negative, duration is
    /// auto-detected via ffprobe.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, List<QueuedEpisode>>> BuildQueueAsync(
        IEnumerable<AnalyzeRequest> requests,
        PluginConfiguration config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        var analysisPercent = Math.Clamp(config.AnalysisPercent, 1, 50) / 100.0;
        var queue = new Dictionary<Guid, List<QueuedEpisode>>();

        foreach (var req in requests)
        {
            if (string.IsNullOrEmpty(req.FilePath) || !File.Exists(req.FilePath))
            {
                _logger.LogWarning("Skipping episode {Name}: file not found at {Path}", req.EpisodeName, req.FilePath);
                continue;
            }

            var duration = req.DurationSeconds;
            if (duration <= 0)
            {
                duration = await GetDurationViaFfprobeAsync(req.FilePath, cancellationToken).ConfigureAwait(false);
                if (duration <= 0)
                {
                    _logger.LogWarning("Skipping episode {Name}: could not determine duration via ffprobe", req.EpisodeName);
                    continue;
                }

                _logger.LogDebug("Auto-detected duration for {Name}: {Duration:F1}s", req.EpisodeName, duration);
            }

            var seasonId = AniListGuid.ForSeason(req.AniListId, req.SeasonNumber);
            var episodeId = AniListGuid.ForEpisode(req.AniListId, req.SeasonNumber, req.EpisodeNumber);

            var fingerprintWindow = Math.Min(
                duration >= 5 * 60 ? duration * analysisPercent : duration,
                60.0 * config.AnalysisLengthLimit);

            var creditsWindow = Math.Min(
                duration >= 5 * 60 ? duration * analysisPercent : duration,
                60.0 * config.MaximumCreditsDuration);

            var episode = new QueuedEpisode
            {
                SeriesName = req.SeriesName,
                SeasonNumber = req.SeasonNumber,
                EpisodeNumber = req.EpisodeNumber,
                SeriesId = AniListGuid.ForSeason(req.AniListId, 0),
                SeasonId = seasonId,
                EpisodeId = episodeId,
                Name = req.EpisodeName,
                Path = req.FilePath,
                Duration = duration,
                Category = req.Category,
                IntroFingerprintEnd = fingerprintWindow,
                CreditsFingerprintStart = Math.Max(0, duration - creditsWindow),
            };

            if (!queue.TryGetValue(seasonId, out var list))
            {
                list = [];
                queue[seasonId] = list;
            }

            list.Add(episode);
        }

        // Sort each season's episode list by episode number so the analyzer sees
        // them in broadcast order (important for the adjacent-episode fingerprint search).
        foreach (var list in queue.Values)
        {
            list.Sort(static (a, b) => a.EpisodeNumber.CompareTo(b.EpisodeNumber));
        }

        return queue;
    }

    /// <summary>
    /// Verifies that queued episodes still exist on disk and marks already-analyzed
    /// episodes so the analyzer skips them.
    /// </summary>
    public async Task<IReadOnlyList<QueuedEpisode>> VerifyQueueAsync(
        IReadOnlyList<QueuedEpisode> candidates,
        IReadOnlyCollection<AnalysisMode> modes,
        CancellationToken cancellationToken = default)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin instance is null");
        var snapshot = await plugin
            .GetSeasonQueueSnapshotAsync(
                candidates[0].SeasonId,
                [.. candidates.Select(c => c.EpisodeId)],
                cancellationToken)
            .ConfigureAwait(false);

        var verified = new List<QueuedEpisode>(candidates.Count);

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(candidate.Path))
            {
                _logger.LogDebug("Skipping {Name}: file not found at {Path}", candidate.Name, candidate.Path);
                continue;
            }

            verified.Add(candidate);

            foreach (var mode in modes)
            {
                if (snapshot.SegmentsByEpisodeId.TryGetValue(candidate.EpisodeId, out var hasSegments) &&
                    hasSegments.TryGetValue(mode, out _))
                {
                    var isUserProvided = snapshot.UserProvidedByMode.TryGetValue(mode, out var userProvided) &&
                                         userProvided.Contains(candidate.EpisodeId);

                    if (isUserProvided || !plugin.AnalyzeAgain)
                    {
                        candidate.SetAnalyzed(mode, isUserProvided ? EpisodeState.UserProvided : EpisodeState.Analyzed);
                    }
                }
                else if (!plugin.AnalyzeAgain &&
                         snapshot.EpisodeIdsByMode.TryGetValue(mode, out var ids) &&
                         ids.Contains(candidate.EpisodeId))
                {
                    candidate.SetAnalyzed(mode, EpisodeState.NoSegments);
                }
            }
        }

        return verified;
    }

    private async Task<double> GetDurationViaFfprobeAsync(string filePath, CancellationToken cancellationToken)
    {
        var ffmpegPath = Plugin.Instance?.FFmpegPath ?? "ffmpeg";
        var dir = Path.GetDirectoryName(ffmpegPath) ?? string.Empty;
        var ext = Path.GetExtension(ffmpegPath);
        var ffprobePath = string.IsNullOrEmpty(dir)
            ? "ffprobe" + ext
            : Path.Combine(dir, "ffprobe" + ext);

        var info = new ProcessStartInfo(ffprobePath)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };
        info.ArgumentList.Add("-v");
        info.ArgumentList.Add("quiet");
        info.ArgumentList.Add("-print_format");
        info.ArgumentList.Add("json");
        info.ArgumentList.Add("-show_format");
        info.ArgumentList.Add(filePath);

        try
        {
            using var proc = new Process { StartInfo = info };
            proc.Start();
            var output = await proc.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.TryGetProperty("format", out var fmt) &&
                fmt.TryGetProperty("duration", out var dur) &&
                double.TryParse(dur.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var seconds))
            {
                return seconds;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ffprobe failed for {Path}", filePath);
        }

        return 0;
    }
}
