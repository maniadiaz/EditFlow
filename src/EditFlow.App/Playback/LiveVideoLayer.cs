// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using EditFlow.Core.Timeline;
using EditFlow.Engine;
using EditFlow.Engine.Exporting;
using EditFlow.Engine.Playback;

namespace EditFlow.App.Playback;

/// <summary>
/// Reproduce en vivo un video superpuesto: su propio decodificador, atado al mismo reloj de audio que el video principal.
/// </summary>
/// <remarks>
/// <para>
/// Sin esto, un video en una capa solo se veía como fotogramas sueltos mientras se reproducía. Cada capa visible
/// abre su propio proceso de FFmpeg, que decodifica al tamaño con el que se ve (nunca más de 540 de alto, y como
/// mucho 30 fotogramas por segundo) y con el ajuste de color de la capa, y deja los fotogramas en un mapa de bits
/// doble para que el dibujo no lea uno a medio escribir.
/// </para>
/// <para>
/// El sonido no pasa por aquí: sale de la mezcla de audio, como el de cualquier otro clip.
/// </para>
/// </remarks>
public sealed class LiveVideoLayer : IDisposable
{
    private readonly VideoPlayer _player;
    private readonly OverlayItem _item;
    private readonly string _path;
    private readonly Action _repaint;
    private readonly object _gate = new();

    private WriteableBitmap? _front;
    private WriteableBitmap? _back;
    private bool _disposed;

    /// <summary>Crea la capa; no decodifica nada hasta <see cref="Start"/>.</summary>
    /// <param name="tools">Ejecutables de FFmpeg.</param>
    /// <param name="item">Video superpuesto.</param>
    /// <param name="path">Archivo que se decodifica (el original o su copia ligera).</param>
    /// <param name="clock">Posición del reloj de audio en la timeline, o <see langword="null"/> si no está lista.</param>
    /// <param name="repaint">Se llama, desde cualquier hilo, cuando hay un fotograma nuevo que dibujar.</param>
    public LiveVideoLayer(FFmpegTools tools, OverlayItem item, string path, Func<TimeSpan>? clock, Action repaint)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(repaint);

        _item = item;
        _path = path;
        _repaint = repaint;

        var media = item.Media ?? throw new ArgumentException("El elemento no es un video.", nameof(item));
        var (width, height) = DecodeSize(media.DisplayWidth, media.DisplayHeight);
        var rate = media.FrameRate > 1 ? Math.Clamp(media.FrameRate, 24, 30) : 30;

        _player = new VideoPlayer(tools, width, height, rate);
        _player.Configure(width, height, rate, hardwareDecoding: false);
        _player.ColorFilter = ColorFilter.Build(item.Color);
        _player.KeyFilter = ChromaKeyFilter.Build(item.ChromaKey);
        _player.FrameReady = Present;

        if (clock is not null)
        {
            var sourceIn = item.SourceIn;
            var start = item.Start;
            _player.MasterClock = () => sourceIn + (clock() - start);
        }

        Source = () =>
        {
            lock (_gate)
            {
                return _front;
            }
        };
    }

    /// <summary>Devuelve el fotograma más reciente. Es siempre la misma función: comparar dos capas es barato.</summary>
    public Func<Bitmap?> Source { get; }

    /// <summary>Indica si ya llegó algún fotograma.</summary>
    public bool HasFrame
    {
        get
        {
            lock (_gate)
            {
                return _front is not null;
            }
        }
    }

    /// <summary>Tamaño de decodificación: la proporción del video, con un alto razonable para verse en el preview.</summary>
    internal static (int Width, int Height) DecodeSize(int displayWidth, int displayHeight)
    {
        var aspect = displayHeight > 0 ? (double)displayWidth / displayHeight : 16.0 / 9;
        var height = Math.Clamp(displayHeight > 0 ? displayHeight : 360, 90, 540) / 2 * 2;
        var width = Math.Max((int)Math.Round(height * aspect / 2) * 2, 16);
        return (width, height);
    }

    /// <summary>Empieza a reproducir desde un instante de la timeline.</summary>
    public void Start(TimeSpan timelinePosition)
    {
        var offset = _item.SourceIn + (timelinePosition - _item.Start);
        _player.Scrub(_path, offset < TimeSpan.Zero ? TimeSpan.Zero : offset);
        _player.Play();
    }

    private void Present(VideoFrame frame)
    {
        if (_disposed || !frame.IsValid)
        {
            return;
        }

        lock (_gate)
        {
            if (_back is null || _back.PixelSize.Width != frame.Width || _back.PixelSize.Height != frame.Height)
            {
                var size = new PixelSize(frame.Width, frame.Height);
                var dpi = new Vector(96, 96);

                // Bgra8888 sin conversión, igual que el video principal.
                _front = _front is not null && _front.PixelSize == size ? _front : new WriteableBitmap(size, dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
                _back = new WriteableBitmap(size, dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
            }

            using (var locked = _back.Lock())
            {
                if (frame.Stride == locked.RowBytes)
                {
                    Marshal.Copy(frame.Pixels, 0, locked.Address, frame.Pixels.Length);
                }
                else
                {
                    for (var row = 0; row < frame.Height; row++)
                    {
                        Marshal.Copy(frame.Pixels, row * frame.Stride, locked.Address + (row * locked.RowBytes), frame.Stride);
                    }
                }
            }

            (_front, _back) = (_back, _front);
            _back ??= new WriteableBitmap(_front!.PixelSize, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        }

        _repaint();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _player.FrameReady = null;
        _player.Dispose();

        // Los mapas de bits no se liberan aquí: la composición puede tener aún el último fotograma en cola.
    }
}
