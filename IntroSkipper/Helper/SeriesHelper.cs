// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Helper;

/// <summary>
/// Anime detection helper. In the standalone build, category is set explicitly on each
/// <see cref="Data.QueuedEpisode"/> via the API request; this stub is retained for compatibility.
/// </summary>
internal static class SeriesHelper
{
    /// <summary>
    /// Always returns false in the standalone build.
    /// Set <see cref="Manager.AnalyzeRequest.Category"/> to <c>AnimeEpisode</c> instead.
    /// </summary>
    internal static bool IsAnime(object? series) => false;
}
