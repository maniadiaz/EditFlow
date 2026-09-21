// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Text;

namespace EditFlow.Engine.Tests.Text;

public class RelativeTimeTests
{
    private static readonly DateTime Now = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, "hace un momento")]
    [InlineData(45, "hace un momento")]
    [InlineData(60, "hace 1 min")]
    [InlineData(59 * 60, "hace 59 min")]
    [InlineData(60 * 60, "hace 1 h")]
    [InlineData(23 * 3600, "hace 23 h")]
    [InlineData(24 * 3600, "ayer")]
    [InlineData(47 * 3600, "ayer")]
    [InlineData(2 * 86400, "hace 2 días")]
    [InlineData(6 * 86400, "hace 6 días")]
    public void Describes_recent_times_in_plain_spanish(int secondsAgo, string expected) =>
        Assert.Equal(expected, RelativeTime.Describe(Now.AddSeconds(-secondsAgo), Now));

    [Fact]
    public void A_time_in_the_future_never_produces_a_negative_amount() =>
        Assert.Equal("hace un momento", RelativeTime.Describe(Now.AddMinutes(5), Now));

    [Fact]
    public void Old_times_show_the_date()
    {
        var text = RelativeTime.Describe(new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc), Now);

        Assert.Contains("2026", text, StringComparison.Ordinal);
        Assert.DoesNotContain("hace", text, StringComparison.Ordinal);
    }
}
