// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Engine.Playback;

namespace EditFlow.Engine.Tests.Playback;

public class FrameReaderTests
{
    [Fact]
    public void Seeking_happens_before_the_input_so_it_is_indexed()
    {
        // '-ss' después de '-i' obliga a decodificar desde el principio y descartar.
        // En un archivo de media hora eso convierte un salto instantáneo en una espera
        // de varios segundos.
        var arguments = FrameReader.BuildArguments("v.mp4", TimeSpan.FromSeconds(12), 854, 480, 30);

        var seek = arguments.ToList().IndexOf("-ss");
        var input = arguments.ToList().IndexOf("-i");

        Assert.True(seek >= 0 && seek < input, "'-ss' debe ir antes de '-i'");
    }

    [Fact]
    public void Output_is_raw_bgra()
    {
        // BGRA es lo que el mapa de bits de Avalonia consume sin conversión. Con BGR24
        // hay que convertir cada fotograma, y esa conversión hunde 30 fps por debajo de 10.
        var arguments = string.Join(' ', FrameReader.BuildArguments("v.mp4", TimeSpan.Zero, 854, 480, 30));

        Assert.Contains("-f rawvideo", arguments, StringComparison.Ordinal);
        Assert.Contains("-pix_fmt bgra", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void The_frame_rate_is_normalised_during_decoding()
    {
        // El reloj de reproducción cuenta fotogramas para saber en qué instante está.
        // Con una cadencia variable en el origen, esa cuenta se desviaría del tiempo real.
        var arguments = string.Join(' ', FrameReader.BuildArguments("v.mp4", TimeSpan.Zero, 854, 480, 25));

        Assert.Contains("fps=25", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void Aspect_ratio_is_preserved_with_padding()
    {
        var arguments = string.Join(' ', FrameReader.BuildArguments("v.mp4", TimeSpan.Zero, 854, 480, 30));

        Assert.Contains("force_original_aspect_ratio=decrease", arguments, StringComparison.Ordinal);
        Assert.Contains("pad=854:480", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void Times_are_formatted_independently_of_the_system_locale()
    {
        // En una máquina en español, un formateo descuidado escribiría "1,5" y FFmpeg
        // leería 1 segundo, descartando los decimales sin avisar.
        var arguments = string.Join(' ', FrameReader.BuildArguments("v.mp4", TimeSpan.FromSeconds(1.5), 854, 480, 30));

        Assert.Contains("-ss 1.5", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("1,5", arguments, StringComparison.Ordinal);
    }
}

public class FramePoolTests
{
    [Fact]
    public async Task Frames_are_reused_rather_than_allocated()
    {
        // Un fotograma de 854×480 ocupa 1,6 MB. A 30 por segundo, crearlos y descartarlos
        // produce 48 MB/s de basura para el recolector.
        using var pool = new FramePool(capacity: 2, width: 64, height: 64);

        var first = await pool.RentAsync(CancellationToken.None);
        pool.Return(first);
        var again = await pool.RentAsync(CancellationToken.None);

        Assert.Same(first, again);
    }

    [Fact]
    public async Task Renting_beyond_the_capacity_waits_instead_of_growing()
    {
        // Esperar frena al decodificador cuando la interfaz no consume lo bastante rápido.
        // Crecer sin límite dejaría que decodificara por delante hasta agotar la memoria.
        using var pool = new FramePool(capacity: 1, width: 16, height: 16);

        var held = await pool.RentAsync(CancellationToken.None);

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pool.RentAsync(timeout.Token));

        pool.Return(held);
    }

    [Fact]
    public async Task A_returned_frame_is_marked_invalid()
    {
        // Sin esto, un fotograma reciclado seguiría pareciendo válido con el contenido
        // del anterior, y el reproductor mostraría una imagen que ya no toca.
        using var pool = new FramePool(capacity: 1, width: 16, height: 16);

        var frame = await pool.RentAsync(CancellationToken.None);
        frame.IsValid = true;
        pool.Return(frame);

        Assert.False(frame.IsValid);
    }

    [Theory]
    [InlineData(854, 480, 30, 46)]
    [InlineData(1920, 1080, 30, 237)]
    public void Memory_use_is_predictable(int width, int height, int capacity, int expectedMegabytes)
    {
        // La reserva es la mayor consumidora de memoria del reproductor, y el objetivo es
        // funcionar en máquinas de 8 GB donde Windows ya usa entre 4 y 6.
        using var pool = new FramePool(capacity, width, height);

        Assert.Equal(expectedMegabytes, (int)(pool.MemoryBytes / (1024 * 1024)));
    }

    [Fact]
    public void A_frame_reports_its_row_size()
    {
        using var pool = new FramePool(1, 100, 50);
        var frame = pool.RentAsync(CancellationToken.None).Result;

        Assert.Equal(400, frame.Stride);
        Assert.Equal(100 * 50 * 4, frame.Pixels.Length);
    }
}
