// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Engine.Exporting;

namespace EditFlow.Engine.Tests.Exporting;

public class ProgressParserTests
{
    private static readonly TimeSpan Total = TimeSpan.FromSeconds(60);

    private static ExportProgress FeedBlock(ProgressParser parser, params string[] lines)
    {
        ExportProgress? last = null;
        foreach (var line in lines)
        {
            last = parser.Feed(line) ?? last;
        }

        Assert.NotNull(last);
        return last;
    }

    [Fact]
    public void A_progress_block_is_reported_only_once_it_closes()
    {
        // FFmpeg emite varias claves seguidas y cierra el bloque con 'progress='.
        // Notificar en cada clave daría avances incoherentes: el porcentaje de un
        // instante junto con la velocidad de otro.
        var parser = new ProgressParser(Total);

        Assert.Null(parser.Feed("frame=300"));
        Assert.Null(parser.Feed("fps=60.0"));
        Assert.Null(parser.Feed("out_time_us=15000000"));
        Assert.NotNull(parser.Feed("progress=continue"));
    }

    [Fact]
    public void Percentage_comes_from_the_processed_duration()
    {
        var parser = new ProgressParser(Total);

        var progress = FeedBlock(parser,
            "out_time_us=15000000",
            "progress=continue");

        Assert.Equal(TimeSpan.FromSeconds(15), progress.Processed);
        Assert.Equal(25, progress.Percentage, precision: 3);
    }

    [Fact]
    public void The_badly_named_out_time_ms_key_is_read_as_microseconds()
    {
        // 'out_time_ms' contiene microsegundos, no milisegundos: es un error histórico
        // de FFmpeg que se mantiene por compatibilidad. Leerlo como milisegundos daría
        // un porcentaje mil veces menor.
        var parser = new ProgressParser(Total);

        var progress = FeedBlock(parser,
            "out_time_ms=30000000",
            "progress=continue");

        Assert.Equal(TimeSpan.FromSeconds(30), progress.Processed);
    }

    [Fact]
    public void Speed_is_read_with_its_trailing_x()
    {
        var parser = new ProgressParser(Total);

        var progress = FeedBlock(parser,
            "speed=2.41x",
            "out_time_us=10000000",
            "progress=continue");

        Assert.Equal(2.41, progress.Speed, precision: 3);
    }

    [Fact]
    public void An_unavailable_speed_does_not_break_the_parse()
    {
        // En los primeros instantes FFmpeg emite "speed=N/A".
        var parser = new ProgressParser(Total);

        var progress = FeedBlock(parser,
            "speed=N/A",
            "out_time_us=1000000",
            "progress=continue");

        Assert.Equal(0, progress.Speed);
        Assert.Null(progress.Remaining);
    }

    [Fact]
    public void Remaining_time_uses_the_reported_speed()
    {
        // 15 de 60 segundos a 3x: quedan 45 segundos de material, 15 de espera.
        var parser = new ProgressParser(Total);

        var progress = FeedBlock(parser,
            "out_time_us=15000000",
            "speed=3.0x",
            "progress=continue");

        Assert.NotNull(progress.Remaining);
        Assert.Equal(15, progress.Remaining.Value.TotalSeconds, precision: 1);
    }

    [Fact]
    public void Completion_reports_one_hundred_percent()
    {
        // FFmpeg no siempre emite un out_time exactamente igual a la duración total,
        // así que sin forzarlo la barra se quedaría en el 99 % para siempre.
        var parser = new ProgressParser(Total);

        var progress = FeedBlock(parser,
            "out_time_us=59980000",
            "progress=end");

        Assert.True(parser.IsComplete);
        Assert.Equal(100, progress.Percentage, precision: 3);
        Assert.Equal(TimeSpan.Zero, progress.Remaining);
    }

    [Fact]
    public void Percentage_never_exceeds_one_hundred()
    {
        var parser = new ProgressParser(TimeSpan.FromSeconds(10));

        var progress = FeedBlock(parser,
            "out_time_us=99000000",
            "progress=continue");

        Assert.Equal(100, progress.Percentage);
    }

    [Fact]
    public void Frames_and_rate_are_carried_through()
    {
        var parser = new ProgressParser(Total);

        var progress = FeedBlock(parser,
            "frame=1800",
            "fps=120.5",
            "out_time_us=30000000",
            "progress=continue");

        Assert.Equal(1800, progress.Frames);
        Assert.Equal(120.5, progress.FramesPerSecond, precision: 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-separator-here")]
    [InlineData("=orphan")]
    public void Malformed_lines_are_ignored(string line)
    {
        var parser = new ProgressParser(Total);

        Assert.Null(parser.Feed(line));
        Assert.False(parser.IsComplete);
    }

    [Fact]
    public void A_timeline_of_zero_length_does_not_divide_by_zero()
    {
        var parser = new ProgressParser(TimeSpan.Zero);

        var progress = FeedBlock(parser, "out_time_us=0", "progress=continue");

        Assert.Equal(0, progress.Percentage);
        Assert.Null(progress.Remaining);
    }
}
