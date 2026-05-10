// SPDX-License-Identifier: GPL-3.0-only

using System.Security.Cryptography;
using System.Text;

namespace IntroSkipper.Helper;

/// <summary>
/// Generates deterministic Guids from AniList IDs so episodes can be keyed
/// the same way as in the original Jellyfin plugin, without requiring Jellyfin.
/// </summary>
public static class AniListGuid
{
    /// <summary>
    /// Returns a stable Guid for a specific episode within an AniList entry.
    /// </summary>
    public static Guid ForEpisode(int aniListId, int seasonNumber, int episodeNumber)
        => Derive($"anilist:{aniListId}:s{seasonNumber}:e{episodeNumber}");

    /// <summary>
    /// Returns a stable Guid representing the season (used as the group key during analysis).
    /// </summary>
    public static Guid ForSeason(int aniListId, int seasonNumber)
        => Derive($"anilist:{aniListId}:s{seasonNumber}");

    private static Guid Derive(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        // Stamp as UUID v5 variant so the bytes are recognisable in logs.
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash[..16]);
    }
}
