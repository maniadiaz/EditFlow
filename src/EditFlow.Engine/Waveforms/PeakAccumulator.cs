// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

namespace EditFlow.Engine.Waveforms;

/// <summary>
/// Convierte un flujo de muestras PCM de 16 bits en una lista de picos de amplitud.
/// </summary>
/// <remarks>
/// <para>
/// Cada pico resume un tramo fijo de audio con su amplitud máxima. Dibujar la forma de onda
/// necesita mucho menos que el audio completo: a 100 picos por segundo, una hora ocupa unos
/// 350 KB frente a los cientos de megabytes de las muestras.
/// </para>
/// <para>
/// El flujo llega en trozos de tamaño arbitrario, y un trozo puede cortar una muestra por la
/// mitad. Se conserva el byte suelto para completarla con el siguiente; sin ello, un solo byte
/// mal alineado desplazaría toda la onda que viene detrás y la convertiría en ruido.
/// </para>
/// </remarks>
public sealed class PeakAccumulator
{
    private readonly int _samplesPerPeak;
    private readonly List<byte> _peaks = [];

    private int _samplesInBucket;
    private int _bucketMax;
    private int _pendingByte = -1;

    /// <summary>Crea un acumulador que resume <paramref name="samplesPerPeak"/> muestras en cada pico.</summary>
    public PeakAccumulator(int samplesPerPeak)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(samplesPerPeak, 1);
        _samplesPerPeak = samplesPerPeak;
    }

    /// <summary>Picos completos acumulados hasta ahora.</summary>
    public int Count => _peaks.Count;

    /// <summary>Procesa un trozo de bytes PCM de 16 bits, little-endian, mono.</summary>
    public void Add(ReadOnlySpan<byte> data)
    {
        var index = 0;

        if (_pendingByte >= 0 && data.Length > 0)
        {
            AddSample((short)(_pendingByte | (data[0] << 8)));
            _pendingByte = -1;
            index = 1;
        }

        for (; index + 1 < data.Length; index += 2)
        {
            AddSample((short)(data[index] | (data[index + 1] << 8)));
        }

        if (index < data.Length)
        {
            _pendingByte = data[index];
        }
    }

    /// <summary>Cierra el último tramo, aunque esté incompleto, y devuelve todos los picos.</summary>
    public byte[] Finish()
    {
        if (_samplesInBucket > 0)
        {
            Flush();
        }

        return [.. _peaks];
    }

    private void AddSample(short sample)
    {
        // Math.Abs(short.MinValue) desbordaría un short; se hace en int.
        var magnitude = Math.Abs((int)sample);
        if (magnitude > _bucketMax)
        {
            _bucketMax = magnitude;
        }

        if (++_samplesInBucket == _samplesPerPeak)
        {
            Flush();
        }
    }

    private void Flush()
    {
        // 32768 es la amplitud máxima; se escala a un byte.
        _peaks.Add((byte)Math.Min(255, (_bucketMax * 255 + 16383) / 32768));
        _samplesInBucket = 0;
        _bucketMax = 0;
    }
}
