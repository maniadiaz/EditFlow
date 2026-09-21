// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using EditFlow.Core.Timeline;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using EditFlow.Engine.Playback;

namespace EditFlow.App.Controls;

/// <summary>Una imagen que se dibuja sobre el video en el preview.</summary>
/// <param name="Bitmap">Imagen ya decodificada.</param>
/// <param name="Area">
/// Dónde va, en unidades de un lienzo de 854 × 480, sea cual sea la resolución a la que se
/// decodifique el video: la superficie lo escala a lo que mida en pantalla. Con un lienzo
/// abstracto, cada elemento se mantiene en su sitio al redimensionar la ventana o cambiar la
/// calidad del preview.
/// </param>
/// <param name="Opacity">De 0 a 1.</param>
/// <param name="Item">Elemento del montaje del que sale, para poder agarrarlo con el ratón.</param>
public sealed record PreviewOverlay(Bitmap Bitmap, Rect Area, double Opacity, OverlayItem? Item = null);

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
    private IReadOnlyList<PreviewOverlay> _overlays = [];

    // Arrastre de un elemento superpuesto sobre el propio preview.
    private OverlayItem? _selected;
    private PreviewOverlay? _dragging;
    private Point _grabOffset;
    private Rect _dragArea;
    private bool _snapX;
    private bool _snapY;
    private Rect _lastDestination;

    /// <summary>Se dispara al agarrar un texto o una imagen con el ratón.</summary>
    public event EventHandler<OverlayItem>? OverlayGrabbed;

    /// <summary>
    /// Se dispara al soltarlo, con su nuevo centro como fracción del video (0 a 1 en cada eje).
    /// </summary>
    public event EventHandler<OverlayDropEventArgs>? OverlayDropped;

    /// <summary>Elemento que se resalta con un contorno: el seleccionado en la timeline.</summary>
    public OverlayItem? SelectedOverlay
    {
        get => _selected;
        set
        {
            if (!ReferenceEquals(_selected, value))
            {
                _selected = value;
                InvalidateVisual();
            }
        }
    }

    /// <summary>Anchura del lienzo en el que se colocan las superposiciones.</summary>
    public const double CanvasWidth = 854;

    /// <summary>Altura del lienzo.</summary>
    public const double CanvasHeight = 480;

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

    /// <summary>Fija los textos e imágenes que se dibujan sobre el video, de abajo arriba.</summary>
    /// <remarks>Solo repinta si algo cambió: se llama cada pocos milisegundos durante la reproducción.</remarks>
    public void SetOverlays(IReadOnlyList<PreviewOverlay> overlays)
    {
        ArgumentNullException.ThrowIfNull(overlays);

        if (_overlays.SequenceEqual(overlays))
        {
            return;
        }

        _overlays = overlays;
        InvalidateVisual();
    }

    // ------------------------------------------------------- arrastrar sobre el preview

    private (double X, double Y)? ToCanvas(Point point)
    {
        if (_lastDestination.Width <= 0)
        {
            return null;
        }

        var unit = _lastDestination.Width / CanvasWidth;
        return ((point.X - _lastDestination.X) / unit, (point.Y - _lastDestination.Y) / unit);
    }

    private PreviewOverlay? HitOverlay(Point point)
    {
        if (ToCanvas(point) is not { } canvas)
        {
            return null;
        }

        // De delante hacia atrás: se agarra lo que se ve encima. Un margen de unos píxeles
        // evita fallar por poco en un texto de letra fina.
        var overlays = _overlays;
        for (var i = overlays.Count - 1; i >= 0; i--)
        {
            if (overlays[i].Item is not null && overlays[i].Area.Inflate(4).Contains(new Point(canvas.X, canvas.Y)))
            {
                return overlays[i];
            }
        }

        return null;
    }

    /// <inheritdoc/>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var point = e.GetPosition(this);
        if (HitOverlay(point) is not { Item: { } item } hit || ToCanvas(point) is not { } canvas)
        {
            return;
        }

        _dragging = hit;
        _dragArea = hit.Area;
        _grabOffset = new Point(canvas.X - hit.Area.Center.X, canvas.Y - hit.Area.Center.Y);
        e.Pointer.Capture(this);
        e.Handled = true;

        OverlayGrabbed?.Invoke(this, item);
        InvalidateVisual();
    }

    /// <inheritdoc/>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var point = e.GetPosition(this);

        if (_dragging is null)
        {
            Cursor = HitOverlay(point) is null ? Cursor.Default : new Cursor(StandardCursorType.SizeAll);
            return;
        }

        if (ToCanvas(point) is not { } canvas)
        {
            return;
        }

        var centerX = Math.Clamp(canvas.X - _grabOffset.X, 0, CanvasWidth);
        var centerY = Math.Clamp(canvas.Y - _grabOffset.Y, 0, CanvasHeight);

        // Imán al centro del video: es donde casi siempre se quiere un título, y acertar a ojo con
        // el ratón el píxel exacto no es razonable.
        const double snap = 6;
        _snapX = Math.Abs(centerX - (CanvasWidth / 2)) < snap;
        _snapY = Math.Abs(centerY - (CanvasHeight / 2)) < snap;

        if (_snapX)
        {
            centerX = CanvasWidth / 2;
        }

        if (_snapY)
        {
            centerY = CanvasHeight / 2;
        }

        _dragArea = new Rect(
            centerX - (_dragging.Area.Width / 2),
            centerY - (_dragging.Area.Height / 2),
            _dragging.Area.Width,
            _dragging.Area.Height);

        InvalidateVisual();
    }

    /// <inheritdoc/>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_dragging is not { Item: { } item })
        {
            return;
        }

        var center = _dragArea.Center;
        var moved = _dragArea.Position != _dragging.Area.Position;

        _dragging = null;
        _snapX = _snapY = false;
        e.Pointer.Capture(null);
        InvalidateVisual();

        if (moved)
        {
            OverlayDropped?.Invoke(this, new OverlayDropEventArgs(item, center.X / CanvasWidth, center.Y / CanvasHeight));
        }
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

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        // Sin fotograma —más allá del último clip— el lienzo sigue existiendo: un título puede
        // durar más que el video y debe verse sobre el negro.
        var frameWidth = bitmap?.PixelSize.Width ?? CanvasWidth;
        var frameHeight = bitmap?.PixelSize.Height ?? CanvasHeight;

        // El fotograma ya viene con bandas negras si hacía falta, así que aquí basta con
        // encajarlo sin deformarlo.
        var source = new Rect(0, 0, frameWidth, frameHeight);
        var scale = Math.Min(bounds.Width / source.Width, bounds.Height / source.Height);
        var width = source.Width * scale;
        var height = source.Height * scale;

        var destination = new Rect(
            (bounds.Width - width) / 2,
            (bounds.Height - height) / 2,
            width,
            height);

        _lastDestination = destination;

        if (bitmap is not null)
        {
            context.DrawImage(bitmap, source, destination);
        }

        DrawOverlays(context, destination);
    }

    private void DrawOverlays(DrawingContext context, Rect destination)
    {
        var overlays = _overlays;
        if (overlays.Count == 0)
        {
            return;
        }

        // Unidades del lienzo a píxeles de pantalla. No depende de la resolución del fotograma.
        var scale = destination.Width / CanvasWidth;

        // Nada se dibuja fuera del video, aunque el elemento se haya colocado en el borde.
        using var clip = context.PushClip(destination);

        foreach (var overlay in overlays)
        {
            var source = ReferenceEquals(overlay, _dragging) ? _dragArea : overlay.Area;
            var area = new Rect(
                destination.X + (source.X * scale),
                destination.Y + (source.Y * scale),
                source.Width * scale,
                source.Height * scale);

            using (context.PushOpacity(overlay.Opacity))
            {
                context.DrawImage(
                    overlay.Bitmap,
                    new Rect(0, 0, overlay.Bitmap.PixelSize.Width, overlay.Bitmap.PixelSize.Height),
                    area);
            }

            // Contorno del elemento seleccionado, para ver qué se está editando.
            if (overlay.Item is not null && ReferenceEquals(overlay.Item, _selected))
            {
                context.DrawRectangle(null, new Pen(SelectionBrush, 1.5, DashStyle.Dash), area);
            }
        }

        // Guías de centro mientras se arrastra y el elemento está imantado a ellas.
        if (_dragging is not null)
        {
            var guide = new Pen(GuideBrush, 1);
            if (_snapX)
            {
                var x = destination.X + (destination.Width / 2);
                context.DrawLine(guide, new Point(x, destination.Y), new Point(x, destination.Bottom));
            }

            if (_snapY)
            {
                var y = destination.Y + (destination.Height / 2);
                context.DrawLine(guide, new Point(destination.X, y), new Point(destination.Right, y));
            }
        }
    }

    private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.Parse("#2F8CFF"));
    private static readonly IBrush GuideBrush = new SolidColorBrush(Color.Parse("#ffb020"));

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

/// <summary>Datos de un elemento superpuesto soltado tras arrastrarlo sobre el preview.</summary>
/// <param name="Item">El elemento.</param>
/// <param name="CenterX">Centro horizontal, de 0 a 1.</param>
/// <param name="CenterY">Centro vertical, de 0 a 1.</param>
public sealed class OverlayDropEventArgs(OverlayItem item, double centerX, double centerY) : EventArgs
{
    /// <summary>El elemento.</summary>
    public OverlayItem Item { get; } = item;

    /// <summary>Centro horizontal, de 0 a 1.</summary>
    public double CenterX { get; } = centerX;

    /// <summary>Centro vertical, de 0 a 1.</summary>
    public double CenterY { get; } = centerY;
}
