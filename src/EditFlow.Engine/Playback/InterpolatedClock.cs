// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

namespace EditFlow.Engine.Playback;

/// <summary>
/// Reloj continuo construido sobre una fuente que solo informa de la posición a intervalos.
/// </summary>
/// <remarks>
/// <para>
/// Medido en este proyecto: LibVLC actualiza la posición del audio cada 256 ms. El video, que
/// sigue a ese reloj, solo veía instantes separados 256 ms y mostraba sus fotogramas en
/// ráfagas: cuatro actualizaciones visibles por segundo aunque se decodificaran cientos.
/// </para>
/// <para>
/// Este reloj recuerda el último punto de referencia y avanza con un cronómetro entre una
/// lectura y la siguiente. Cuando llega una lectura nueva la usa para corregir la estimación:
/// suavemente si difiere poco, para que el video no dé tirones con cada corrección, y de golpe
/// si difiere mucho, que es lo que ocurre tras un salto.
/// </para>
/// </remarks>
public sealed class InterpolatedClock
{
    /// <summary>Diferencia a partir de la cual una lectura sustituye a la estimación en lugar de suavizarla.</summary>
    public static readonly TimeSpan ResyncThreshold = TimeSpan.FromMilliseconds(120);

    private const double Smoothing = 0.25;

    private readonly TimeProvider _time;
    private readonly Lock _gate = new();

    private TimeSpan _anchor;
    private long _anchorTimestamp;
    private long _lastReported = -1;
    private bool _running;

    /// <summary>Crea el reloj, parado en cero.</summary>
    /// <param name="time">Fuente de tiempo; por defecto el reloj del sistema.</param>
    public InterpolatedClock(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _anchorTimestamp = _time.GetTimestamp();
    }

    /// <summary>Indica si el reloj está avanzando.</summary>
    public bool IsRunning
    {
        get { lock (_gate) { return _running; } }
    }

    /// <summary>Coloca el reloj en una posición y lo pone en marcha o lo detiene.</summary>
    /// <param name="position">Posición actual.</param>
    /// <param name="running">Si debe avanzar.</param>
    /// <param name="reported">
    /// Última lectura de la fuente en este momento. Se toma como ya vista: tras un salto la fuente
    /// aún puede informar de la posición anterior, y hacerle caso tiraría hacia atrás la
    /// estimación recién colocada.
    /// </param>
    public void Reset(TimeSpan position, bool running, long reported = -1)
    {
        lock (_gate)
        {
            _anchor = position;
            _anchorTimestamp = _time.GetTimestamp();
            _running = running;
            _lastReported = reported;
        }
    }

    /// <summary>Posición estimada ahora.</summary>
    /// <param name="reportedMilliseconds">
    /// Lo que informa la fuente, en milisegundos, o un valor negativo si no hay dato. Puede
    /// repetirse entre llamadas: solo cuenta como lectura nueva cuando cambia.
    /// </param>
    public TimeSpan Read(long reportedMilliseconds)
    {
        lock (_gate)
        {
            if (!_running)
            {
                return _anchor;
            }

            var now = _time.GetTimestamp();
            var estimate = _anchor + _time.GetElapsedTime(_anchorTimestamp, now);

            if (reportedMilliseconds >= 0 && reportedMilliseconds != _lastReported)
            {
                _lastReported = reportedMilliseconds;
                var error = TimeSpan.FromMilliseconds(reportedMilliseconds) - estimate;

                estimate = error.Duration() > ResyncThreshold
                    ? TimeSpan.FromMilliseconds(reportedMilliseconds)
                    : estimate + (error * Smoothing);

                _anchor = estimate;
                _anchorTimestamp = now;
            }

            return estimate;
        }
    }
}
