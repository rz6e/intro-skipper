// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using IntroSkipper.Manager;
using IntroSkipper.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IntroSkipper.Controllers;

/// <summary>
/// REST API for submitting episodes and retrieving skip timestamps.
/// </summary>
[ApiController]
[Route("api")]
public sealed class AnalysisController : ControllerBase
{
    private readonly AnalysisService _analysisService;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnalysisController"/> class.
    /// </summary>
    public AnalysisController(AnalysisService analysisService)
    {
        _analysisService = analysisService;
    }

    /// <summary>
    /// Submits one or more episodes for analysis. Returns immediately; analysis runs in the background.
    /// Poll <c>GET /api/skips/{aniListId}/{episodeNumber}</c> to check for results.
    /// </summary>
    /// <remarks>
    /// Example body:
    /// <code>
    /// [
    ///   { "aniListId": 21, "seasonNumber": 1, "episodeNumber": 1,
    ///     "seriesName": "One Piece", "episodeName": "I'm Luffy!",
    ///     "filePath": "/media/one-piece/S01E01.mkv", "durationSeconds": 1440,
    ///     "category": "AnimeEpisode" }
    /// ]
    /// </code>
    /// </remarks>
    [HttpPost("analyze")]
    public IActionResult Analyze([FromBody] IReadOnlyList<AnalyzeRequest> requests, CancellationToken cancellationToken)
    {
        if (requests.Count == 0)
        {
            return BadRequest("No episodes provided.");
        }

        _ = Task.Run(
            () => _analysisService.AnalyzeAsync(requests, cancellationToken: cancellationToken),
            cancellationToken);

        return Accepted(new { message = "Analysis started", count = requests.Count });
    }

    /// <summary>
    /// Returns stored skip timestamps for a single episode.
    /// Returns 404 if analysis has not yet produced results.
    /// </summary>
    /// <param name="aniListId">AniList series ID.</param>
    /// <param name="episodeNumber">Episode number within the season.</param>
    /// <param name="season">Season number (default 1).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("skips/{aniListId:int}/{episodeNumber:int}")]
    public async Task<IActionResult> GetSkips(
        int aniListId,
        int episodeNumber,
        [FromQuery] int season = 1,
        CancellationToken cancellationToken = default)
    {
        var timestamps = await _analysisService
            .GetTimestampsAsync(aniListId, season, episodeNumber, cancellationToken)
            .ConfigureAwait(false);

        if (timestamps.Count == 0)
        {
            return NotFound(new
            {
                aniListId,
                season,
                episodeNumber,
                message = "No segments found — analysis may not have run yet."
            });
        }

        var result = timestamps.ToDictionary(
            kvp => kvp.Key.ToString().ToLowerInvariant(),
            kvp => new { start = kvp.Value.Start, end = kvp.Value.End });

        return Ok(result);
    }

    /// <summary>
    /// Returns all stored skip timestamps for every analyzed episode of an AniList series/season.
    /// </summary>
    /// <param name="aniListId">AniList series ID.</param>
    /// <param name="season">Season number (default 1).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("skips/{aniListId:int}")]
    public async Task<IActionResult> GetAllSkips(
        int aniListId,
        [FromQuery] int season = 1,
        CancellationToken cancellationToken = default)
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin not initialized");

        var seasonGuid = Helper.AniListGuid.ForSeason(aniListId, season);

        using var db = Plugin.CreateDbContext();
        var seasonInfos = await db.DbSeasonInfo
            .AsNoTracking()
            .Where(s => s.SeasonId == seasonGuid)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (seasonInfos.Count == 0)
        {
            return NotFound(new { aniListId, season, message = "No data for this series/season." });
        }

        var episodeGuids = seasonInfos.SelectMany(s => s.EpisodeIds).Distinct().ToList();

        var result = new Dictionary<string, object>();
        foreach (var guid in episodeGuids)
        {
            var timestamps = await plugin.GetTimestampsAsync(guid, cancellationToken).ConfigureAwait(false);
            if (timestamps.Count > 0)
            {
                result[guid.ToString()] = timestamps.ToDictionary(
                    kvp => kvp.Key.ToString().ToLowerInvariant(),
                    kvp => new { start = kvp.Value.Start, end = kvp.Value.End });
            }
        }

        return Ok(result);
    }
}
