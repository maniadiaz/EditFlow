// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using EditFlow.Engine.Playback;

namespace EditFlow.App.Controls;

/// <summary>
/// Dibuja fotogramas de video decodificados.
/// </summary>
/// <remarks>
/// <para>
/// Sustituye al <c>VideoView</c> de LibVLCSharp, que es una ventana nativa y tapa
/// cualquier control dibujado sobre ella (comprobado; ver la sección 13 de
/// <c>docs/PLAN.md</c>). Al pintar en un control normal de Avalonia, superponer texto,
/// máscaras o tiradores de recorte deja de ser un problema.
/// </para>
/// <para>
/// Usa dos mapas de bits alternos. El decodificador escribe en el de atrás desde su
/// propio hilo y solo el intercambio ocurre en el hilo de interfaz. Con uno solo, el
/// renderizador podría estar leyendo mientras se escribe y la imagen saldría partida
/// entre dos fotogramas.
/// </para>
/// </remarks>
public sealed class VideoSurface : Control, IDisposable
{
    private readonly Lock _gate = new();

    private WriteableBitmap? _front;
    private WriteableBitmap? _back;
    private int _width;
    private int _height;
    private bool _disposed;

    /// <summary>Fotogramas presentados desde la última vez que se consultó.</summary>
    public long PresentedFrames { get; private set; }

    /// <summary>
    /// Entrega un fotograma para mostrarlo.
    /// </summary>
    /// <remarks>
    /// Puede llamarse desde cualquier hilo: la copia de píxeles ocurre aquí mismo y solo
    /// el intercambio y el repintado se envían al hilo de interfaz. Copiar en el hilo de
    /// interfaz perdería fotogramas, porque bloquearía el hilo que además debe dibujar.
    /// </remarks>
    public void Present(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (_disposed || !frame.IsValid)
        {
            return;
        }

        lock (_gate)
        {
            EnsureBuffers(frame.Width, frame.Height);

            var target = _back;
            if (target is null)
            {
                return;
            }

            using (var locked = target.Lock())
            {
                // El fotograma no tiene relleno entre filas y el mapa de bits puede
                // tenerlo, así que se copia fila a fila en lugar de de una vez.
                var sourceStride = frame.Stride;
                var destinationStride = locked.RowBytes;

                if (sourceStride == destinationStride)
                {
                    Marshal.Copy(frame.Pixels, 0, locked.Address, frame.Pixels.Length);
                }
                else
                {
                    for (var row = 0; row < frame.Height; row++)
                    {
                        Marshal.Copy(
                            frame.Pixels,
                            row * sourceStride,
                            locked.Address + (row * destinationStride),
                            sourceStride);
                    }
                }
            }

            (_front, _back) = (_back, _front);
            PresentedFrames++;
        }

        Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
    }

    /// <summary>Borra la imagen mostrada.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _front = null;
        }

        Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        context.FillRectangle(Brushes.Black, new Rect(bounds.Size));

        WriteableBitmap? bitmap;
        lock (_gate)
        {
            bitmap = _front;
        }

        if (bitmap is null || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        // El fotograma ya viene con bandas negras si hacía falta, así que aquí basta con
        // encajarlo sin deformarlo.
        var source = new Rect(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height);
        var scale = Math.Min(bounds.Width / source.Width, bounds.Height / source.Height);
        var width = source.Width * scale;
        var height = source.Height * scale;

        var destination = new Rect(
            (bounds.Width - width) / 2,
            (bounds.Height - height) / 2,
            width,
            height);

        context.DrawImage(bitmap, source, destination);
    }

    private void EnsureBuffers(int width, int height)
    {
        if (_front is not null && _width == width && _height == height)
        {
            return;
        }

        _front?.Dispose();
        _back?.Dispose();

        var size = new PixelSize(width, height);
        var dpi = new Vector(96, 96);

        // Bgra8888 es obligatorio: con Bgr24 hay que convertir cada fotograma y un video
        // de 30 fps cae por debajo de 10. Premultiplied porque es lo que espera el
        // compositor; el canal alfa siempre llega opaco desde FFmpeg.
        _front = new WriteableBitmap(size, dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
        _back = new WriteableBitmap(size, dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);

        _width = width;
        _height = height;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        lock (_gate)
        {
            _front?.Dispose();
            _back?.Dispose();
            _front = null;
            _back = null;
        }
    }
}
