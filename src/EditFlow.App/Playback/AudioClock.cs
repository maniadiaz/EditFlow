// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using LibVLCSharp.Shared;

namespace EditFlow.App.Playback;

/// <summary>
/// Reproduce el audio de un archivo y hace de reloj maestro de la reproducción.
/// </summary>
/// <remarks>
/// <para>
/// El audio manda y el video lo sigue, que es como lo resuelven todos los reproductores
/// serios: un fotograma repetido o saltado pasa desapercibido, mientras que un corte o un
/// chasquido en el audio se oye siempre. Si el video llevara el reloj, habría que estirar
/// o encoger el audio para seguirlo, y eso se nota.
/// </para>
/// <para>
/// Usa LibVLC con el video desactivado. Se descartó sustituirlo por una biblioteca de
/// audio dedicada tras comprobar las opciones reales: los paquetes de NAudio con
/// dispositivo exigen un destino específico de Windows, lo que obligaría al motor a dejar
/// de ser multiplataforma; y OwnAudioSharp, que sí tiene destino net10.0 y binarios para
/// las tres plataformas, arrastra una dependencia de Avalonia —un framework de interfaz
/// dentro de una biblioteca de audio— incompatible además con la versión que el proyecto
/// tiene fijada.
/// </para>
/// <para>
/// LibVLC ya estaba en el proyecto, es LGPL y por tanto compatible con GPL-3.0, y el
/// problema que obligó a sustituirlo era el de su <c>VideoView</c>, no el del audio.
/// </para>
/// </remarks>
public sealed class AudioClock : IDisposable
{
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _player;
    private bool _disposed;

    /// <summary>Crea el reloj, inicializando LibVLC sin salida de video.</summary>
    public AudioClock()
    {
        // Calificado por completo: 'Core' a secas resuelve al namespace EditFlow.Core.
        LibVLCSharp.Shared.Core.Initialize();

        // '--no-video' evita que LibVLC cree ninguna ventana nativa. Esa ventana era
        // justamente el problema: tapaba cualquier control dibujado encima.
        _libVlc = new LibVLC("--no-video", "--quiet");
        _player = new MediaPlayer(_libVlc);
    }

    /// <summary>Indica si hay audio sonando.</summary>
    public bool IsPlaying => !_disposed && _player.IsPlaying;

    /// <summary>Instante actual dentro del archivo cargado.</summary>
    /// <remarks>
    /// Devuelve <see cref="TimeSpan.Zero"/> mientras no hay nada cargado: LibVLC informa
    /// valores negativos en ese caso, y propagarlos haría que el video se colocara en una
    /// posición imposible.
    /// </remarks>
    public TimeSpan Position
    {
        get
        {
            if (_disposed)
            {
                return TimeSpan.Zero;
            }

            var milliseconds = _player.Time;
            return milliseconds < 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(milliseconds);
        }
    }

    /// <summary>Duración del archivo cargado.</summary>
    public TimeSpan Duration
    {
        get
        {
            var milliseconds = _disposed ? 0 : _player.Length;
            return milliseconds <= 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(milliseconds);
        }
    }

    /// <summary>Volumen, de 0 a 100.</summary>
    public int Volume
    {
        get => _disposed ? 0 : _player.Volume;
        set { if (!_disposed) { _player.Volume = Math.Clamp(value, 0, 100); } }
    }

    /// <summary>Indica si el archivo cargado tiene pista de audio.</summary>
    /// <remarks>
    /// Un clip mudo no puede llevar el reloj. Quien lo use debe recurrir a su propio
    /// cronómetro en ese caso, o el video se quedaría congelado esperando un audio que
    /// nunca avanza.
    /// </remarks>
    public bool HasAudio { get; private set; }

    /// <summary>Carga un archivo y se sitúa en una posición, en pausa.</summary>
    public void Open(string path, TimeSpan position, bool hasAudio)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ObjectDisposedException.ThrowIf(_disposed, this);

        HasAudio = hasAudio;

        using var media = new Media(_libVlc, new Uri(path));
        _player.Play(media);
        _player.Time = (long)position.TotalMilliseconds;
        _player.SetPause(true);
    }

    /// <summary>Salta dentro del archivo ya cargado.</summary>
    public void SeekTo(TimeSpan position)
    {
        if (!_disposed)
        {
            _player.Time = (long)Math.Max(position.TotalMilliseconds, 0);
        }
    }

    /// <summary>Reanuda.</summary>
    public void Play()
    {
        if (!_disposed)
        {
            _player.SetPause(false);
        }
    }

    /// <summary>Pausa sin perder la posición.</summary>
    public void Pause()
    {
        if (!_disposed)
        {
            _player.SetPause(true);
        }
    }

    /// <summary>Detiene y descarga.</summary>
    public void Stop()
    {
        if (!_disposed)
        {
            _player.Stop();
            HasAudio = false;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _player.Stop();
        _player.Dispose();
        _libVlc.Dispose();
    }
}
