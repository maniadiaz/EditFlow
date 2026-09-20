using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using EditFlow.Core.Timeline;
using EditFlow.Core.Undo;

namespace EditFlow.App.Controls;

/// <summary>
/// Pista de video dibujada a medida: regla de tiempo, clips y cabezal.
/// </summary>
/// <remarks>
/// Se dibuja directamente en lugar de componerse con <c>ItemsControl</c> y plantillas.
/// Una timeline puede tener cientos de clips, y cada uno como árbol de controles
/// supondría miles de objetos visuales que medir y disponer en cada cambio de zoom.
/// Dibujar rectángulos es una operación por clip y por fotograma.
/// </remarks>
public sealed class TimelineControl : Control
{
    private const double RulerHeight = 26;
    private const double TrackTop = RulerHeight + 8;
    private const double TrackHeight = 84;

    /// <summary>Anchura de la zona sensible al recorte en cada borde de un clip.</summary>
    /// <remarks>
    /// Seis píxeles es lo bastante ancho para acertar con el ratón sin mirar, y lo
    /// bastante estrecho para que arrastrar el cuerpo del clip siga siendo lo natural.
    /// </remarks>
    private const double EdgeGrip = 6;

    private static readonly IBrush RulerBackground = new SolidColorBrush(Color.Parse("#1a1a1e"));
    private static readonly IBrush TrackBackground = new SolidColorBrush(Color.Parse("#0f0f12"));
    private static readonly IBrush ClipFill = new SolidColorBrush(Color.Parse("#2d4f7c"));
    private static readonly IBrush ClipFillSelected = new SolidColorBrush(Color.Parse("#3d6fac"));
    private static readonly IBrush ClipStroke = new SolidColorBrush(Color.Parse("#5a8fd0"));
    private static readonly IBrush ClipText = new SolidColorBrush(Color.Parse("#dce6f5"));
    private static readonly IBrush RulerText = new SolidColorBrush(Color.Parse("#7a7a86"));
    private static readonly IBrush TickBrush = new SolidColorBrush(Color.Parse("#3a3a44"));
    private static readonly IBrush PlayheadBrush = new SolidColorBrush(Color.Parse("#ff5555"));
    private static readonly IBrush DropIndicator = new SolidColorBrush(Color.Parse("#ffd166"));

    private VideoTimeline? _timeline;
    private double _pixelsPerSecond = 60;
    private TimeSpan _playhead;
    private Clip? _selectedClip;

    private DragKind _drag = DragKind.None;
    private Clip? _dragClip;
    private double _dragOriginX;
    private int _dropIndex = -1;

    /// <summary>Secuencia que se dibuja.</summary>
    public VideoTimeline? Timeline
    {
        get => _timeline;
        set
        {
            _timeline = value;
            _selectedClip = null;
            InvalidateVisual();
        }
    }

    /// <summary>Historial al que se envían las ediciones.</summary>
    public UndoHistory? UndoHistory { get; set; }

    /// <summary>Escala de zoom, en píxeles por segundo.</summary>
    public double PixelsPerSecond
    {
        get => _pixelsPerSecond;
        set
        {
            var clamped = Math.Clamp(value, 4, 400);
            if (Math.Abs(clamped - _pixelsPerSecond) < 0.01)
            {
                return;
            }

            _pixelsPerSecond = clamped;
            InvalidateVisual();
        }
    }

    /// <summary>Posición del cabezal de reproducción.</summary>
    public TimeSpan Playhead
    {
        get => _playhead;
        set
        {
            var clamped = value < TimeSpan.Zero ? TimeSpan.Zero : value;
            if (clamped == _playhead)
            {
                return;
            }

            _playhead = clamped;
            InvalidateVisual();
        }
    }

    /// <summary>Clip seleccionado, o <see langword="null"/> si no hay ninguno.</summary>
    public Clip? SelectedClip
    {
        get => _selectedClip;
        private set
        {
            if (ReferenceEquals(_selectedClip, value))
            {
                return;
            }

            _selectedClip = value;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
        }
    }

    /// <summary>Se dispara cuando el usuario mueve el cabezal.</summary>
    public event EventHandler<TimeSpan>? PlayheadMoved;

    /// <summary>Se dispara cuando cambia el clip seleccionado.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Se dispara cuando una edición modifica la secuencia.</summary>
    public event EventHandler? TimelineEdited;

    // ------------------------------------------------------------------ dibujo

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;

        context.FillRectangle(TrackBackground, new Rect(0, 0, width, height));
        DrawRuler(context, width);
        DrawClips(context, width);
        DrawPlayhead(context, height);
    }

    private void DrawRuler(DrawingContext context, double width)
    {
        context.FillRectangle(RulerBackground, new Rect(0, 0, width, RulerHeight));

        var step = ChooseTickInterval();
        var pen = new Pen(TickBrush, 1);

        for (var seconds = 0.0; seconds * _pixelsPerSecond <= width; seconds += step)
        {
            var x = Math.Round(seconds * _pixelsPerSecond) + 0.5;
            context.DrawLine(pen, new Point(x, RulerHeight - 7), new Point(x, RulerHeight));

            var label = FormatRulerLabel(TimeSpan.FromSeconds(seconds));
            var text = new FormattedText(
                label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                Typeface.Default, 10, RulerText);

            context.DrawText(text, new Point(x + 4, 4));
        }
    }

    /// <summary>
    /// Elige cada cuántos segundos marcar la regla según el zoom.
    /// </summary>
    /// <remarks>
    /// Un intervalo fijo produce una regla inútil en los extremos: alejada, las marcas
    /// se amontonan hasta formar una mancha; acercada, desaparecen y no queda referencia
    /// temporal. Se busca el primer intervalo de la escala que deje al menos 70 píxeles
    /// entre marcas.
    /// </remarks>
    private double ChooseTickInterval()
    {
        double[] scale = [0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600];

        foreach (var candidate in scale)
        {
            if (candidate * _pixelsPerSecond >= 70)
            {
                return candidate;
            }
        }

        return scale[^1];
    }

    private static string FormatRulerLabel(TimeSpan value) =>
        value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : value.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    private void DrawClips(DrawingContext context, double width)
    {
        if (_timeline is null || _timeline.IsEmpty)
        {
            var hint = new FormattedText(
                "Sin clips. Importa un video para empezar.",
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                Typeface.Default, 12, RulerText);

            context.DrawText(hint, new Point(16, TrackTop + (TrackHeight / 2) - 8));
            return;
        }

        var start = TimeSpan.Zero;

        foreach (var clip in _timeline.Clips)
        {
            var x = start.TotalSeconds * _pixelsPerSecond;
            var clipWidth = clip.Duration.TotalSeconds * _pixelsPerSecond;
            start += clip.Duration;

            // Un clip fuera de la parte visible no se dibuja: con cientos de clips y
            // mucho zoom, dibujarlos todos multiplica el trabajo sin cambiar el
            // resultado en pantalla.
            if (x > width || x + clipWidth < 0)
            {
                continue;
            }

            var selected = ReferenceEquals(clip, _selectedClip);
            var rect = new Rect(x + 1, TrackTop, Math.Max(clipWidth - 2, 1), TrackHeight);

            context.DrawRectangle(
                selected ? ClipFillSelected : ClipFill,
                new Pen(ClipStroke, selected ? 2 : 1),
                rect,
                4, 4);

            DrawClipLabel(context, clip, rect);
        }

        if (_dropIndex >= 0)
        {
            var indicatorX = XOfIndex(_dropIndex);
            context.DrawLine(
                new Pen(DropIndicator, 3),
                new Point(indicatorX, TrackTop - 4),
                new Point(indicatorX, TrackTop + TrackHeight + 4));
        }
    }

    private static void DrawClipLabel(DrawingContext context, Clip clip, Rect rect)
    {
        // Sin recorte, el nombre se desborda sobre el clip siguiente y ambos quedan
        // ilegibles justo cuando hay muchos clips cortos, que es cuando más falta hace.
        if (rect.Width < 28)
        {
            return;
        }

        using var _ = context.PushClip(rect.Deflate(new Thickness(6, 4)));

        var name = System.IO.Path.GetFileName(clip.Source.Path);
        var title = new FormattedText(
            name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface.Default, 11, ClipText);

        context.DrawText(title, new Point(rect.X + 7, rect.Y + 6));

        if (rect.Height > 40)
        {
            var duration = new FormattedText(
                clip.Duration.ToString(@"mm\:ss\.ff", CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                Typeface.Default, 10, RulerText);

            context.DrawText(duration, new Point(rect.X + 7, rect.Y + 24));
        }
    }

    private void DrawPlayhead(DrawingContext context, double height)
    {
        var x = Math.Round(_playhead.TotalSeconds * _pixelsPerSecond) + 0.5;
        context.DrawLine(new Pen(PlayheadBrush, 2), new Point(x, 0), new Point(x, height));

        // Un triángulo en la cabeza da una zona de agarre visible; una línea de dos
        // píxeles es demasiado fina para apuntar con el ratón.
        var head = new StreamGeometry();
        using (var geometry = head.Open())
        {
            geometry.BeginFigure(new Point(x - 6, 0), isFilled: true);
            geometry.LineTo(new Point(x + 6, 0));
            geometry.LineTo(new Point(x, 10));
            geometry.EndFigure(true);
        }

        context.DrawGeometry(PlayheadBrush, null, head);
    }

    // ------------------------------------------------------------- interacción

    /// <inheritdoc/>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();

        var point = e.GetPosition(this);

        if (point.Y < RulerHeight)
        {
            _drag = DragKind.Playhead;
            MovePlayheadTo(point.X);
            e.Pointer.Capture(this);
            return;
        }

        var hit = HitTest(point);
        if (hit.Clip is null)
        {
            SelectedClip = null;
            return;
        }

        SelectedClip = hit.Clip;
        _dragClip = hit.Clip;
        _dragOriginX = point.X;
        _drag = hit.Region switch
        {
            HitRegion.LeftEdge => DragKind.TrimStart,
            HitRegion.RightEdge => DragKind.TrimEnd,
            _ => DragKind.Reorder,
        };

        e.Pointer.Capture(this);
    }

    /// <inheritdoc/>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);

        if (_drag == DragKind.None)
        {
            UpdateCursor(point);
            return;
        }

        switch (_drag)
        {
            case DragKind.Playhead:
                MovePlayheadTo(point.X);
                break;

            case DragKind.Reorder when _dragClip is not null && _timeline is not null:
                var index = IndexAtX(point.X);
                if (index != _dropIndex)
                {
                    _dropIndex = index;
                    InvalidateVisual();
                }

                break;
        }
    }

    /// <inheritdoc/>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        var point = e.GetPosition(this);
        var delta = TimeSpan.FromSeconds((point.X - _dragOriginX) / _pixelsPerSecond);

        switch (_drag)
        {
            case DragKind.TrimStart when _dragClip is not null:
                Apply(new TrimClipCommand(_dragClip, ClipEdge.Start, delta));
                break;

            case DragKind.TrimEnd when _dragClip is not null:
                Apply(new TrimClipCommand(_dragClip, ClipEdge.End, delta));
                break;

            case DragKind.Reorder when _dragClip is not null && _timeline is not null && _dropIndex >= 0:
                var currentIndex = _timeline.Clips.ToList().IndexOf(_dragClip);
                var target = _dropIndex > currentIndex ? _dropIndex - 1 : _dropIndex;
                if (target != currentIndex)
                {
                    Apply(new MoveClipCommand(_timeline, _dragClip, target));
                }

                break;
        }

        _drag = DragKind.None;
        _dragClip = null;
        _dropIndex = -1;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    /// <inheritdoc/>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return;
        }

        // El zoom se ancla al puntero: el instante que hay bajo el ratón debe seguir
        // ahí después de ampliar, o la vista salta y se pierde la referencia.
        PixelsPerSecond *= e.Delta.Y > 0 ? 1.25 : 0.8;
        e.Handled = true;
    }

    private void UpdateCursor(Point point)
    {
        if (point.Y < RulerHeight)
        {
            Cursor = new Cursor(StandardCursorType.Hand);
            return;
        }

        var hit = HitTest(point);
        Cursor = hit.Region switch
        {
            HitRegion.LeftEdge or HitRegion.RightEdge => new Cursor(StandardCursorType.SizeWestEast),
            HitRegion.Body => new Cursor(StandardCursorType.SizeAll),
            _ => Cursor.Default,
        };
    }

    private void MovePlayheadTo(double x)
    {
        var seconds = Math.Max(0, x / _pixelsPerSecond);
        var position = TimeSpan.FromSeconds(seconds);

        if (_timeline is not null && position > _timeline.Duration)
        {
            position = _timeline.Duration;
        }

        Playhead = position;
        PlayheadMoved?.Invoke(this, Playhead);
    }

    // ------------------------------------------------------------------ edición

    /// <summary>
    /// Vuelve a dibujar tras un cambio hecho fuera del control.
    /// </summary>
    /// <remarks>
    /// El control no observa la secuencia: no hay notificación de cambios en
    /// <see cref="VideoTimeline"/>, y añadir una obligaría a que el modelo conociera a
    /// quien lo dibuja. Quien edite por su cuenta —importar archivos, por ejemplo— avisa
    /// llamando aquí.
    /// </remarks>
    public void Refresh()
    {
        if (_selectedClip is not null && _timeline is not null && _timeline.IndexOf(_selectedClip) < 0)
        {
            _selectedClip = null;
        }

        InvalidateVisual();
    }

    /// <summary>Divide el clip que se encuentra bajo el cabezal.</summary>
    /// <returns><see langword="true"/> si el corte se realizó.</returns>
    public bool SplitAtPlayhead()
    {
        if (_timeline is null)
        {
            return false;
        }

        var command = new SplitClipCommand(_timeline, _playhead);
        Apply(command);

        return command.SecondHalf is not null;
    }

    /// <summary>Elimina el clip seleccionado.</summary>
    /// <returns><see langword="true"/> si había algo seleccionado.</returns>
    public bool DeleteSelected()
    {
        if (_timeline is null || _selectedClip is null)
        {
            return false;
        }

        Apply(new RemoveClipCommand(_timeline, _selectedClip));
        SelectedClip = null;
        return true;
    }

    /// <summary>Deshace la última edición.</summary>
    public bool Undo() => Finish(UndoHistory?.Undo() ?? false);

    /// <summary>Rehace la última edición deshecha.</summary>
    public bool Redo() => Finish(UndoHistory?.Redo() ?? false);

    private void Apply(IUndoableCommand command)
    {
        if (UndoHistory is not null)
        {
            UndoHistory.Do(command);
        }
        else
        {
            command.Execute();
        }

        Finish(true);
    }

    private bool Finish(bool changed)
    {
        if (changed)
        {
            // Un clip eliminado por deshacer o rehacer puede seguir seleccionado, y
            // dibujar su borde resaltado sobre una posición que ya no existe.
            if (_selectedClip is not null && _timeline is not null &&
                _timeline.IndexOf(_selectedClip) < 0)
            {
                _selectedClip = null;
            }

            TimelineEdited?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
        }

        return changed;
    }

    // -------------------------------------------------------------- hit testing

    private ClipHit HitTest(Point point)
    {
        if (_timeline is null || point.Y < TrackTop || point.Y > TrackTop + TrackHeight)
        {
            return new ClipHit(null, HitRegion.None);
        }

        var start = TimeSpan.Zero;

        foreach (var clip in _timeline.Clips)
        {
            var x = start.TotalSeconds * _pixelsPerSecond;
            var clipWidth = clip.Duration.TotalSeconds * _pixelsPerSecond;
            start += clip.Duration;

            if (point.X < x || point.X > x + clipWidth)
            {
                continue;
            }

            // En un clip muy estrecho, dos zonas de recorte de seis píxeles no dejarían
            // sitio para agarrar el cuerpo. Por debajo de ese ancho, todo el clip
            // responde como cuerpo.
            if (clipWidth > EdgeGrip * 3)
            {
                if (point.X - x <= EdgeGrip)
                {
                    return new ClipHit(clip, HitRegion.LeftEdge);
                }

                if (x + clipWidth - point.X <= EdgeGrip)
                {
                    return new ClipHit(clip, HitRegion.RightEdge);
                }
            }

            return new ClipHit(clip, HitRegion.Body);
        }

        return new ClipHit(null, HitRegion.None);
    }

    /// <summary>Índice de inserción correspondiente a una coordenada horizontal.</summary>
    private int IndexAtX(double x)
    {
        if (_timeline is null)
        {
            return -1;
        }

        var start = TimeSpan.Zero;

        for (var i = 0; i < _timeline.Clips.Count; i++)
        {
            var clip = _timeline.Clips[i];
            var left = start.TotalSeconds * _pixelsPerSecond;
            var clipWidth = clip.Duration.TotalSeconds * _pixelsPerSecond;

            // El punto medio decide el lado: soltar en la mitad izquierda inserta antes,
            // en la derecha después. Usar el borde haría que el indicador saltara de
            // sitio con un movimiento de un píxel.
            if (x < left + (clipWidth / 2))
            {
                return i;
            }

            start += clip.Duration;
        }

        return _timeline.Clips.Count;
    }

    private double XOfIndex(int index)
    {
        if (_timeline is null)
        {
            return 0;
        }

        var start = TimeSpan.Zero;
        for (var i = 0; i < index && i < _timeline.Clips.Count; i++)
        {
            start += _timeline.Clips[i].Duration;
        }

        return start.TotalSeconds * _pixelsPerSecond;
    }

    private enum DragKind { None, Playhead, Reorder, TrimStart, TrimEnd }

    private enum HitRegion { None, Body, LeftEdge, RightEdge }

    private readonly record struct ClipHit(Clip? Clip, HitRegion Region);
}
