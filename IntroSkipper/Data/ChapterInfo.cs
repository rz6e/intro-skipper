// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// Lightweight chapter descriptor, equivalent to Jellyfin's ChapterInfo but without the Jellyfin dependency.
/// Times are stored as 100-nanosecond ticks (matching TimeSpan.Ticks) so ChapterAnalyzer math is unchanged.
/// </summary>
public class ChapterInfo
{
    /// <summary>Gets or sets the display name of the chapter.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the chapter start time in ticks (100 ns each).</summary>
    public long StartPositionTicks { get; set; }
}
