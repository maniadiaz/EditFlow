// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Engine.Playback;

namespace EditFlow.Engine.Tests.Playback;

public class InterpolatedClockTests
{
    /// <summary>Tiempo controlado por la prueba.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => 1_000_000;

        public override long GetTimestamp() => _ticks;

        public void Advance(double milliseconds) => _ticks += (long)(milliseconds * 1000);
    }

    private static TimeSpan Ms(double value) => TimeSpan.FromMilliseconds(value);

    [Fact]
    public void A_stopped_clock_does_not_move()
    {
        var time = new ManualTime();
        var clock = new InterpolatedClock(time);
        clock.Reset(Ms(5000), running: false);

        time.Advance(3000);

        Assert.Equal(Ms(5000), clock.Read(9999));
    }

    [Fact]
    public void A_running_clock_advances_between_the_sparse_readings_of_its_source()
    {
        // La fuente solo cambia cada 256 ms. Sin interpolar, lo que se leyera entre medias sería
        // siempre el mismo valor y el video se mostraría a saltos.
        var time = new ManualTime();
        var clock = new InterpolatedClock(time);
        clock.Reset(Ms(1000), running: true, reported: 1000);

        var samples = new List<TimeSpan>();
        for (var i = 0; i < 20; i++)
        {
            time.Advance(10);
            samples.Add(clock.Read(1000));   // la fuente sigue diciendo 1000
        }

        Assert.Equal(20, samples.Distinct().Count());
        Assert.All(samples.Zip(samples.Skip(1)), pair => Assert.True(pair.Second > pair.First));
        Assert.Equal(Ms(1200), samples[^1]);
    }

    [Fact]
    public void Playing_for_a_second_advances_a_second_even_though_the_source_reports_in_256_ms_steps()
    {
        var time = new ManualTime();
        var clock = new InterpolatedClock(time);
        clock.Reset(TimeSpan.Zero, running: true, reported: 0);

        var distinct = new HashSet<TimeSpan>();
        for (var elapsed = 5; elapsed <= 1000; elapsed += 5)
        {
            time.Advance(5);
            var reported = elapsed / 256 * 256;   // lo que vería LibVLC: 0, 256, 512, 768
            distinct.Add(clock.Read(reported));
        }

        Assert.InRange(clock.Read(768).TotalMilliseconds, 990, 1010);
        Assert.True(distinct.Count > 150, $"el reloj debe cambiar en casi cada lectura, no cada 256 ms: {distinct.Count}");
    }

    [Fact]
    public void A_small_disagreement_is_absorbed_gradually_instead_of_jumping()
    {
        var time = new ManualTime();
        var clock = new InterpolatedClock(time);
        clock.Reset(Ms(0), running: true, reported: 0);

        time.Advance(256);
        var before = clock.Read(0);        // estimación: 256
        var after = clock.Read(216);       // la fuente dice 216: 40 ms atrás

        Assert.Equal(Ms(256), before);
        Assert.InRange(after.TotalMilliseconds, 240, 250);   // corrige un cuarto, no todo
    }

    [Fact]
    public void A_big_disagreement_replaces_the_estimate_at_once()
    {
        var time = new ManualTime();
        var clock = new InterpolatedClock(time);
        clock.Reset(Ms(0), running: true, reported: 0);

        time.Advance(100);

        Assert.Equal(Ms(60_000), clock.Read(60_000));
    }

    [Fact]
    public void A_stale_reading_after_a_reset_is_ignored()
    {
        // Tras saltar a los 30 s, la fuente aún puede decir 5 s durante un rato. Si se le hiciera
        // caso, el reloj volvería a 5 s y el video se iría con él.
        var time = new ManualTime();
        var clock = new InterpolatedClock(time);
        clock.Reset(Ms(30_000), running: true, reported: 5_000);

        time.Advance(50);

        Assert.Equal(Ms(30_050), clock.Read(5_000));
    }

    [Fact]
    public void A_missing_reading_keeps_the_clock_running_on_its_own()
    {
        var time = new ManualTime();
        var clock = new InterpolatedClock(time);
        clock.Reset(Ms(100), running: true);

        time.Advance(400);

        Assert.Equal(Ms(500), clock.Read(-1));
    }

    [Fact]
    public void Stopping_freezes_the_position_and_restarting_continues_from_it()
    {
        var time = new ManualTime();
        var clock = new InterpolatedClock(time);
        clock.Reset(Ms(0), running: true);

        time.Advance(300);
        clock.Reset(clock.Read(-1), running: false);
        time.Advance(5000);
        Assert.Equal(Ms(300), clock.Read(-1));

        clock.Reset(clock.Read(-1), running: true);
        time.Advance(200);
        Assert.Equal(Ms(500), clock.Read(-1));
    }
}
