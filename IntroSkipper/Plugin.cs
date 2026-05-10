// SPDX-FileCopyrightText: 2019 dkanada
// SPDX-FileCopyrightText: 2022-2023 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IntroSkipper;

/// <summary>
/// Standalone intro/credits detection service — Jellyfin dependency removed.
/// Provides database access and configuration for the analysis pipeline.
/// </summary>
public partial class Plugin
{
    private const double SegmentComparisonEpsilon = 0.001;
    private const int SqliteParameterBatchSize = 500;
    private readonly ILogger<Plugin> _logger;
    private readonly string _dbPath;
    private readonly string _cacheDbPath;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="dataDirectory">Directory where databases are stored.</param>
    /// <param name="ffmpegPath">Full path to the ffmpeg executable.</param>
    /// <param name="configuration">Plugin configuration.</param>
    /// <param name="logger">Logger.</param>
    public Plugin(
        string dataDirectory,
        string ffmpegPath,
        PluginConfiguration configuration,
        ILogger<Plugin> logger)
    {
        Instance = this;
        _logger = logger;

        Configuration = configuration;
        FFmpegPath = ffmpegPath;

        Directory.CreateDirectory(dataDirectory);

        var pluginCachePath = Path.Join(dataDirectory, "chromaprints");
        FingerprintCachePath = pluginCachePath;

        _dbPath = Path.Join(dataDirectory, "introskipper.db");
        _cacheDbPath = Path.Join(dataDirectory, "introskipper-cache.db");

        try
        {
            using var db = CreateDbContext();
            db.ApplyMigrations();
        }
        catch (Exception ex)
        {
            LogDatabaseInitializationError(_logger, ex);
        }

        try
        {
            using var cacheDb = CreateCacheDbContext();
            cacheDb.EnsureSchema();
        }
        catch (Exception ex) when (ex is IOException or SqliteException)
        {
            LogCacheDbInitializationError(_logger, ex);
        }
    }

    /// <summary>Gets the plugin singleton.</summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>Gets or sets the active configuration.</summary>
    public PluginConfiguration Configuration { get; set; }

    /// <summary>Gets the full path to the ffmpeg executable.</summary>
    public string FFmpegPath { get; private set; }

    /// <summary>Gets the directory used to cache fingerprints.</summary>
    public string FingerprintCachePath { get; private set; }

    /// <summary>Gets the path to the segment database.</summary>
    public string DbPath => _dbPath;

    /// <summary>Gets the path to the detection cache database.</summary>
    public string CacheDbPath => _cacheDbPath;

    /// <summary>Gets or sets a value indicating whether to re-analyze already-analyzed episodes.</summary>
    public bool AnalyzeAgain { get; set; }

    internal bool LegacyFingerprintMigrationDone { get; set; }

    /// <summary>Gets the most recent media item queue.</summary>
    public ConcurrentDictionary<Guid, List<QueuedEpisode>> QueuedMediaItems { get; } = new();

    /// <summary>Gets or sets the total number of episodes in the queue.</summary>
    public int TotalQueued { get; set; }

    /// <summary>Gets or sets the number of seasons in the queue.</summary>
    public int TotalSeasons { get; set; }

    /// <summary>
    /// Creates a new <see cref="IntroSkipperDbContext"/> for the segment database.
    /// </summary>
    public static IntroSkipperDbContext CreateDbContext()
    {
        ArgumentNullException.ThrowIfNull(Instance);
        return new IntroSkipperDbContext(Instance.DbPath);
    }

    /// <summary>
    /// Creates a new <see cref="DetectionCacheDbContext"/> for the fingerprint cache.
    /// </summary>
    public static DetectionCacheDbContext CreateCacheDbContext()
    {
        ArgumentNullException.ThrowIfNull(Instance);
        return new DetectionCacheDbContext(Instance.CacheDbPath);
    }

    /// <summary>
    /// Returns an empty chapter list. Chapter-based detection requires embedded chapter metadata
    /// extracted from the video file — populate QueuedEpisode.Chapters before analysis to enable it.
    /// </summary>
    internal IReadOnlyList<ChapterInfo> GetChapters(Guid id) => [];

    // ── DB write / read methods ────────────────────────────────────────────────

    internal async Task UpdateTimestampAsync(Segment segment, AnalysisMode mode, bool isUserProvided = false, CancellationToken cancellationToken = default)
    {
        using var db = CreateDbContext();

        try
        {
            var dbSegment = new DbSegment(segment, mode, isUserProvided);

            if (mode == AnalysisMode.Commercial)
            {
                var exists = await db.DbSegment
                    .AnyAsync(
                        s => s.ItemId == segment.EpisodeId
                             && s.Type == mode
                             && Math.Abs(s.Start - dbSegment.Start) <= SegmentComparisonEpsilon
                             && Math.Abs(s.End - dbSegment.End) <= SegmentComparisonEpsilon,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!exists)
                {
                    db.DbSegment.Add(dbSegment);
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var existingSegments = await db.DbSegment
                        .Where(s => s.ItemId == segment.EpisodeId && s.Type == mode)
                        .ToListAsync(cancellationToken)
                        .ConfigureAwait(false);

                    if (!isUserProvided && existingSegments.Any(s => s.IsUserProvided))
                    {
                        return;
                    }

                    // Guard: prevent auto-detected credits from overlapping with the introduction.
                    if (mode == AnalysisMode.Credits && !isUserProvided)
                    {
                        var intro = await db.DbSegment
                            .AsNoTracking()
                            .Where(s => s.ItemId == segment.EpisodeId && s.Type == AnalysisMode.Introduction)
                            .FirstOrDefaultAsync(cancellationToken)
                            .ConfigureAwait(false);

                        if (intro is not null && segment.Start < intro.End && intro.Start < segment.End)
                        {
                            LogCreditsOverlapWithIntro(_logger, segment.EpisodeId);
                            return;
                        }
                    }

                    if (existingSegments.Count > 0)
                    {
                        db.DbSegment.RemoveRange(existingSegments);
                    }

                    db.DbSegment.Add(dbSegment);
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    await transaction.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            LogFailedToUpdateTimestamp(_logger, ex, segment.EpisodeId);
            throw;
        }
    }

    internal async Task<IReadOnlyDictionary<AnalysisMode, Segment>> GetTimestampsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var db = CreateDbContext();
        var segments = await db.DbSegment
            .AsNoTracking()
            .Where(s => s.ItemId == id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return segments
            .GroupBy(segment => segment.Type)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(segment => segment.Start).First().ToSegment());
    }

    internal async Task<IReadOnlyList<DbSegment>> GetSegmentsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var db = CreateDbContext();
        return await db.DbSegment
            .AsNoTracking()
            .Where(s => s.ItemId == id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task CleanTimestampsAsync(IEnumerable<Guid> episodeIds, CancellationToken cancellationToken = default)
    {
        var enabledEpisodeIds = episodeIds.ToHashSet();

        using var db = CreateDbContext();
        var segmentEpisodeIds = await db.DbSegment
            .AsNoTracking()
            .Select(s => s.ItemId)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var staleEpisodeIds = segmentEpisodeIds
            .Where(id => !enabledEpisodeIds.Contains(id))
            .ToArray();

        foreach (var staleEpisodeIdBatch in staleEpisodeIds.Chunk(SqliteParameterBatchSize))
        {
            await db.DbSegment
                .Where(s => staleEpisodeIdBatch.Contains(s.ItemId))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    internal async Task SetAnalyzerActionAsync(Guid id, IReadOnlyDictionary<AnalysisMode, AnalyzerAction> analyzerActions, CancellationToken cancellationToken = default)
    {
        using var db = CreateDbContext();
        var existingEntries = await db.DbSeasonInfo
            .Where(s => s.SeasonId == id)
            .ToDictionaryAsync(s => s.Type, cancellationToken)
            .ConfigureAwait(false);

        foreach (var (mode, action) in analyzerActions)
        {
            if (existingEntries.TryGetValue(mode, out var existing))
            {
                db.Entry(existing).Property(s => s.Action).CurrentValue = action;
            }
            else
            {
                db.DbSeasonInfo.Add(new DbSeasonInfo(id, mode, action));
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task SetEpisodeIdsAsync(Guid id, AnalysisMode mode, IEnumerable<Guid> episodeIds, CancellationToken cancellationToken = default)
    {
        using var db = CreateDbContext();
        var seasonInfo = await db.DbSeasonInfo
            .FirstOrDefaultAsync(s => s.SeasonId == id && s.Type == mode, cancellationToken)
            .ConfigureAwait(false);

        if (seasonInfo is null)
        {
            seasonInfo = new DbSeasonInfo(id, mode, AnalyzerAction.Default, episodeIds);
            db.DbSeasonInfo.Add(seasonInfo);
        }
        else
        {
            db.Entry(seasonInfo).Property(s => s.EpisodeIds).CurrentValue = episodeIds;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task RemoveEpisodeIdAsync(Guid seasonId, AnalysisMode mode, Guid episodeId, CancellationToken cancellationToken = default)
    {
        using var db = CreateDbContext();
        var seasonInfo = await db.DbSeasonInfo
            .FirstOrDefaultAsync(s => s.SeasonId == seasonId && s.Type == mode, cancellationToken)
            .ConfigureAwait(false);

        if (seasonInfo is null)
        {
            return;
        }

        var currentIds = seasonInfo.EpisodeIds.ToList();
        if (!currentIds.Remove(episodeId))
        {
            return;
        }

        db.Entry(seasonInfo).Property(s => s.EpisodeIds).CurrentValue = currentIds;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyDictionary<AnalysisMode, IEnumerable<Guid>>> GetEpisodeIdsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var db = CreateDbContext();
        return await db.DbSeasonInfo.Where(s => s.SeasonId == id)
            .ToDictionaryAsync(s => s.Type, s => s.EpisodeIds, cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<SeasonQueueSnapshot> GetSeasonQueueSnapshotAsync(Guid seasonId, IReadOnlyCollection<Guid> episodeIds, CancellationToken cancellationToken = default)
    {
        using var db = CreateDbContext();
        var episodeIdArray = (Guid[])[.. episodeIds.Distinct()];

        var seasonInfos = await db.DbSeasonInfo
            .AsNoTracking()
            .Where(s => s.SeasonId == seasonId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var allSegments = new List<DbSegment>();
        foreach (var batch in episodeIdArray.Chunk(SqliteParameterBatchSize))
        {
            allSegments.AddRange(await db.DbSegment
                .AsNoTracking()
                .Where(s => batch.Contains(s.ItemId))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false));
        }

        return new SeasonQueueSnapshot(
            seasonInfos.ToDictionary(s => s.Type, s => (IReadOnlySet<Guid>)s.EpisodeIds.ToHashSet()),
            allSegments
                .GroupBy(s => s.ItemId)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyDictionary<AnalysisMode, Segment>)group
                        .GroupBy(segment => segment.Type)
                        .ToDictionary(
                            segmentGroup => segmentGroup.Key,
                            segmentGroup => segmentGroup.OrderBy(segment => segment.Start).First().ToSegment())),
            allSegments
                .Where(s => s.IsUserProvided)
                .GroupBy(s => s.Type)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlySet<Guid>)group.Select(s => s.ItemId).ToHashSet()));
    }

    internal async Task<AnalyzerAction> GetAnalyzerActionAsync(Guid id, AnalysisMode mode, CancellationToken cancellationToken = default)
    {
        using var db = CreateDbContext();
        var info = await db.DbSeasonInfo
            .FirstOrDefaultAsync(s => s.SeasonId == id && s.Type == mode, cancellationToken)
            .ConfigureAwait(false);
        return info?.Action ?? AnalyzerAction.Default;
    }

    internal async Task<IReadOnlyDictionary<AnalysisMode, AnalyzerAction>> GetAllAnalyzerActionsAsync(Guid seasonId, CancellationToken cancellationToken = default)
    {
        using var db = CreateDbContext();
        var infos = await db.DbSeasonInfo
            .Where(s => s.SeasonId == seasonId)
            .ToDictionaryAsync(s => s.Type, s => s.Action, cancellationToken)
            .ConfigureAwait(false);

        var result = new Dictionary<AnalysisMode, AnalyzerAction>();
        foreach (var mode in Enum.GetValues<AnalysisMode>())
        {
            result[mode] = infos.TryGetValue(mode, out var action) ? action : AnalyzerAction.Default;
        }

        return result;
    }

    internal async Task CleanSeasonInfoAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken = default)
    {
        using var db = CreateDbContext();
        await db.DbSeasonInfo
            .Where(s => !ids.Contains(s.SeasonId))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task DeleteTimestampAsync(
        Guid itemId,
        AnalysisMode mode,
        Segment? segment = null,
        CancellationToken cancellationToken = default)
    {
        using var db = CreateDbContext();
        if (segment is null && mode == AnalysisMode.Commercial)
        {
            return;
        }

        var query = db.DbSegment.Where(s => s.ItemId == itemId && s.Type == mode);

        if (segment is not null)
        {
            query = query.Where(s =>
                Math.Abs(s.Start - segment.Start) <= SegmentComparisonEpsilon
                && Math.Abs(s.End - segment.End) <= SegmentComparisonEpsilon);
        }

        var entries = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
        if (entries.Count > 0)
        {
            db.DbSegment.RemoveRange(entries);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error initializing database")]
    private static partial void LogDatabaseInitializationError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error initializing detection cache database")]
    private static partial void LogCacheDbInitializationError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping credits for episode {EpisodeId}: detected segment overlaps with introduction")]
    private static partial void LogCreditsOverlapWithIntro(ILogger logger, Guid episodeId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to update timestamp for episode {EpisodeId}")]
    private static partial void LogFailedToUpdateTimestamp(ILogger logger, Exception ex, Guid episodeId);
}
