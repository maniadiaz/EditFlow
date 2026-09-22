// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using EditFlow.Core.Timeline;
using EditFlow.Core.Undo;
using EditFlow.App.Services;
using EditFlow.Engine.Filmstrips;
using EditFlow.Engine.PreviewCache;
using EditFlow.Engine.Waveforms;

namespace EditFlow.App.Controls;

/// <summary>
/// Timeline multipista dibujada a medida: regla, pista de video, pistas de audio y cabezal.
/// </summary>
/// <remarks>
/// <para>
/// Se dibuja directamente en lugar de componerse con <c>ItemsControl</c> y plantillas.
/// Una timeline puede tener cientos de clips, y cada uno como árbol de controles
/// supondría miles de objetos visuales que medir y disponer en cada cambio de zoom.
/// </para>
/// <para>
/// Las cabeceras de pista quedan <b>pegadas al borde izquierdo</b> de la vista aunque se
/// desplace en horizontal: sin ello, con la timeline avanzada no se sabría de qué pista es
/// cada fila. Para lograrlo el control consulta el desplazamiento de su
/// <see cref="ScrollViewer"/> y dibuja las cabeceras en esa posición.
/// </para>
/// </remarks>
public sealed partial class TimelineControl : Control
{
    private const double HeaderWidth = 124;
    private const double RulerHeight = 26;
    private const double LanePadding = 4;
    private const double VideoLaneHeight = 72;
    private const double AudioLaneHeight = 48;
    private const double MinimumHeight = 150;
    private const double ToggleSize = 16;

    /// <summary>Anchura de la zona sensible al recorte en cada borde de un clip.</summary>
    /// <remarks>
    /// Seis píxeles es lo bastante ancho para acertar con el ratón sin mirar, y lo
    /// bastante estrecho para que arrastrar el cuerpo del clip siga siendo lo natural.
    /// </remarks>
    private const double EdgeGrip = 6;

    /// <summary>Distancia en píxeles a la que un clip se imanta a un borde cercano.</summary>
    private const double SnapDistance = 8;

    private static readonly IBrush Background = new SolidColorBrush(Color.Parse("#0f0f12"));
    private static readonly IBrush RulerBackground = new SolidColorBrush(Color.Parse("#1a1a1e"));
    private static readonly IBrush LaneBackground = new SolidColorBrush(Color.Parse("#141418"));
    private static readonly IBrush HeaderBackground = new SolidColorBrush(Color.Parse("#1c1c21"));
    private static readonly IBrush HeaderLocked = new SolidColorBrush(Color.Parse("#17171a"));
    private static readonly IBrush VideoFill = new SolidColorBrush(Color.Parse("#2d4f7c"));
    private static readonly IBrush VideoFillSelected = new SolidColorBrush(Color.Parse("#3d6fac"));
    private static readonly IBrush VideoStroke = new SolidColorBrush(Color.Parse("#5a8fd0"));
    private static readonly IBrush AudioFill = new SolidColorBrush(Color.Parse("#2b7a55"));
    private static readonly IBrush AudioFillSelected = new SolidColorBrush(Color.Parse("#3a9c6f"));
    private static readonly IBrush AudioFillMuted = new SolidColorBrush(Color.Parse("#3a3a40"));
    private static readonly IBrush AudioFillInvalid = new SolidColorBrush(Color.Parse("#8a3a3a"));
    private static readonly IBrush AudioStroke = new SolidColorBrush(Color.Parse("#56c28a"));
    private static readonly IBrush ClipText = new SolidColorBrush(Color.Parse("#e4eef8"));
    private static readonly IBrush DimText = new SolidColorBrush(Color.Parse("#8a8a96"));
    private static readonly IBrush TickBrush = new SolidColorBrush(Color.Parse("#3a3a44"));
    private static readonly IBrush PlayheadBrush = new SolidColorBrush(Color.Parse("#ff5555"));
    private static readonly IBrush DropIndicator = new SolidColorBrush(Color.Parse("#ffd166"));
    private static readonly IBrush FadeBrush = new SolidColorBrush(Color.Parse("#66ffffff"));
    private static readonly IBrush ToolAccent = new SolidColorBrush(Color.Parse("#ffb020"));
    private static readonly IBrush ToolPill = new SolidColorBrush(Color.Parse("#e6141416"));
    private static readonly IBrush FilmstripShade = new SolidColorBrush(Color.Parse("#a6101216"));
    private static readonly IBrush WaveBrush = new SolidColorBrush(Color.Parse("#7fffffff"));
    private static readonly IBrush ToggleOff = new SolidColorBrush(Color.Parse("#2a2a31"));
    private static readonly IBrush ToggleMute = new SolidColorBrush(Color.Parse("#c0504d"));
    private static readonly IBrush ToggleSolo = new SolidColorBrush(Color.Parse("#d9a441"));
    private static readonly IBrush ToggleLock = new SolidColorBrush(Color.Parse("#4a86c8"));

    private EditSequence? _sequence;
    private ScrollViewer? _scroll;
    private double _pixelsPerSecond = 60;
    private TimeSpan _playhead;
    private Clip? _selectedClip;
    private AudioClip? _selectedAudio;
    private AudioTrack? _selectedAudioTrack;

    private DragKind _drag = DragKind.None;
    private Clip? _dragClip;
    private AudioClip? _dragAudio;
    private AudioTrack? _dragTrack;
    private double _dragOriginX;
    private TimeSpan _dragAudioOrigin;
    private TimeSpan _audioPreviewStart;
    private bool _audioPreviewValid = true;
    private TimeSpan _audioTrimPosition;
    private TimeSpan _toolDelta;
    private int _dropIndex = -1;
    private bool _liftActive;
    private int _liftLane = -1;
    private int _trackDropIndex = -1;

    /// <summary>Secuencia que se dibuja.</summary>
    public EditSequence? Sequence
    {
        get => _sequence;
        set
        {
            _sequence = value;
            _selectedClip = null;
            _selectedAudio = null;
            _selectedAudioTrack = null;
            Refresh();
        }
    }

    /// <summary>Miniaturas de los videos, si se dispone de ellas.</summary>
    public FilmstripCache? Filmstrips { get; set; }

    /// <summary>Imágenes decodificadas para dibujar las miniaturas.</summary>
    public FrameBitmaps? FrameBitmaps { get; set; }

    /// <summary>Formas de onda de los archivos de audio, si se dispone de ellas.</summary>
    public WaveformCache? Waveforms { get; set; }

    /// <summary>Historial al que se envían las ediciones.</summary>
    public UndoHistory? UndoHistory { get; set; }

    private IReadOnlyList<CacheSection>? _cacheSections;

    /// <summary>
    /// Estado de la copia de preview por tramos: se dibuja como una franja de color bajo la regla.
    /// </summary>
    public IReadOnlyList<CacheSection>? CacheSections
    {
        get => _cacheSections;
        set
        {
            _cacheSections = value;
            InvalidateVisual();
        }
    }

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
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    /// <summary>
    /// Desplaza la vista para que el cabezal sea visible, si se ha salido de ella.
    /// </summary>
    /// <remarks>
    /// Pasa página en lugar de seguir al cabezal píxel a píxel: mover todo el contenido en cada
    /// fotograma marea, y así el cabezal reaparece cerca del borde izquierdo y tiene todo un
    /// tramo por delante antes de volver a salirse. Es lo que hacen Premiere y Resolve.
    /// </remarks>
    public void EnsurePlayheadVisible()
    {
        if (_scroll is null || _scroll.Viewport.Width <= HeaderWidth)
        {
            return;
        }

        var x = XOf(_playhead);

        // Lo que queda bajo las cabeceras pegadas al borde izquierdo no cuenta como visible.
        var left = _scroll.Offset.X + HeaderWidth;
        var right = _scroll.Offset.X + _scroll.Viewport.Width - 24;

        if (x >= left && x <= right)
        {
            return;
        }

        var target = x - HeaderWidth - (_scroll.Viewport.Width * 0.12);
        _scroll.Offset = new Vector(Math.Max(0, target), _scroll.Offset.Y);
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
            Layer?.InvalidateVisual();
        }
    }

    /// <summary>Clip de video seleccionado, o <see langword="null"/>.</summary>
    public Clip? SelectedClip => _selectedClip;

    /// <summary>Clip de audio seleccionado, o <see langword="null"/>.</summary>
    public AudioClip? SelectedAudio => _selectedAudio;

    /// <summary>Se dispara cuando el usuario mueve el cabezal.</summary>
    public event EventHandler<TimeSpan>? PlayheadMoved;

    /// <summary>Se dispara cuando cambia la selección.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Se dispara al hacer clic en la marca de transición de un límite entre clips.</summary>
    public event EventHandler? TransitionBadgeClicked;

    /// <summary>Se dispara cuando una edición modifica la secuencia.</summary>
    public event EventHandler? TimelineEdited;

    // --------------------------------------------------------------- geometría

    private double HeaderLeft => _scroll?.Offset.X ?? 0;

    // Las capas de superposición ocupan el espacio sobre el video; sin ellas no queda hueco.
    private double VideoLaneTop => RulerHeight + LanePadding + OverlayBlockHeight;

    private double AudioLaneTop(int index) =>
        VideoLaneTop + VideoLaneHeight + LanePadding + (index * (AudioLaneHeight + LanePadding));

    private double ContentHeight
    {
        get
        {
            var tracks = _sequence?.AudioTracks.Count ?? 0;

            // Un hueco vacío bajo la última pista: es donde se suelta una pista arrastrada
            // al final y donde se hace clic derecho para añadir una nueva.
            return Math.Max(MinimumHeight, AudioLaneTop(tracks) + 28);
        }
    }

    private double XOf(TimeSpan time) => HeaderWidth + (time.TotalSeconds * _pixelsPerSecond);

    private TimeSpan TimeOf(double x) =>
        TimeSpan.FromSeconds(Math.Max(0, (x - HeaderWidth) / _pixelsPerSecond));

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        // Ancho suficiente para toda la secuencia más un margen para poder trabajar más
        // allá del final; con menos de treinta segundos se reserva ese mínimo para que
        // una secuencia vacía no parezca una franja diminuta.
        var seconds = Math.Max(_sequence?.Duration.TotalSeconds ?? 0, 30);
        return new Size(HeaderWidth + (seconds * _pixelsPerSecond) + 240, ContentHeight);
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _scroll = this.FindAncestorOfType<ScrollViewer>();
        if (_scroll is not null)
        {
            _scroll.ScrollChanged += OnScrollChanged;
        }
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_scroll is not null)
        {
            _scroll.ScrollChanged -= OnScrollChanged;
            _scroll = null;
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        InvalidateVisual();
        Layer?.InvalidateVisual();
    }

    // ------------------------------------------------------------------ dibujo

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;

        context.FillRectangle(Background, new Rect(0, 0, width, height));

        DrawLanes(context, width);
        DrawRuler(context, width);
        DrawVideoClips(context, width);
        DrawAudioClips(context, width);
        DrawOverlayLanes(context, width);
        DrawToolFeedback(context);
        DrawDropIndicators(context, width);
        DrawHeaders(context, height);
    }

    private void DrawLanes(DrawingContext context, double width)
    {
        context.FillRectangle(LaneBackground, new Rect(HeaderWidth, VideoLaneTop, width - HeaderWidth, VideoLaneHeight));

        var tracks = _sequence?.AudioTracks.Count ?? 0;
        for (var i = 0; i < tracks; i++)
        {
            context.FillRectangle(
                LaneBackground,
                new Rect(HeaderWidth, AudioLaneTop(i), width - HeaderWidth, AudioLaneHeight));
        }
    }

    private void DrawRuler(DrawingContext context, double width)
    {
        context.FillRectangle(RulerBackground, new Rect(0, 0, width, RulerHeight));

        var step = ChooseTickInterval();
        var pen = new Pen(TickBrush, 1);

        for (var seconds = 0.0; HeaderWidth + (seconds * _pixelsPerSecond) <= width; seconds += step)
        {
            var x = Math.Round(HeaderWidth + (seconds * _pixelsPerSecond)) + 0.5;
            context.DrawLine(pen, new Point(x, RulerHeight - 7), new Point(x, RulerHeight));

            var label = FormatRulerLabel(TimeSpan.FromSeconds(seconds));
            DrawText(context, label, new Point(x + 4, 4), 10, DimText);
        }

        DrawCacheStrip(context, width);
    }

    private static readonly IBrush CacheReady = new SolidColorBrush(Color.Parse("#3ECF6B"));
    private static readonly IBrush CacheRendering = new SolidColorBrush(Color.Parse("#F5C542"));
    private static readonly IBrush CacheQueued = new SolidColorBrush(Color.Parse("#8F7A2E"));
    private static readonly IBrush CacheMissing = new SolidColorBrush(Color.Parse("#E5484D"));

    /// <summary>Franja de la copia de preview: verde lista, amarilla renderizándose, roja sin renderizar.</summary>
    private void DrawCacheStrip(DrawingContext context, double width)
    {
        if (_cacheSections is not { Count: > 0 } sections)
        {
            return;
        }

        const double strip = 4;
        var top = RulerHeight - strip;

        foreach (var section in sections)
        {
            var left = HeaderWidth + (section.Start.TotalSeconds * _pixelsPerSecond);
            var right = HeaderWidth + (section.End.TotalSeconds * _pixelsPerSecond);
            if (right < HeaderWidth || left > width)
            {
                continue;
            }

            var brush = section.State switch
            {
                SectionState.Ready => CacheReady,
                SectionState.Rendering => CacheRendering,
                SectionState.Queued => CacheQueued,
                _ => CacheMissing,
            };

            // Una línea de separación de un píxel deja ver cada trozo por separado.
            context.FillRectangle(brush, new Rect(left, top, Math.Max(right - left - 1, 1), strip));
        }
    }

    /// <summary>
    /// Elige cada cuántos segundos marcar la regla según el zoom.
    /// </summary>
    /// <remarks>
    /// Un intervalo fijo produce una regla inútil en los extremos: alejada, las marcas
    /// se amontonan hasta formar una mancha; acercada, desaparecen. Se busca el primer
    /// intervalo de la escala que deje al menos 70 píxeles entre marcas.
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

    private void DrawVideoClips(DrawingContext context, double width)
    {
        if (_sequence is null || _sequence.Video.IsEmpty)
        {
            DrawText(context, "Sin clips. Importa un video para empezar.",
                new Point(HeaderWidth + 16, VideoLaneTop + (VideoLaneHeight / 2) - 8), 12, DimText);
            return;
        }

        // Se usa la posición ya compuesta (Layout), no la suma plana de duraciones: con
        // transiciones, un clip empieza antes de que el anterior termine, y dibujarlos con el
        // tiempo plano los separaría en vez de solaparlos como de verdad se ven.
        var layout = _sequence.Video.Layout();

        foreach (var entry in layout)
        {
            var clip = entry.Clip;
            var x = XOf(entry.Start);
            var clipWidth = (entry.End - entry.Start).TotalSeconds * _pixelsPerSecond;

            // Un clip fuera de la parte visible no se dibuja: con cientos de clips y mucho
            // zoom, dibujarlos todos multiplica el trabajo sin cambiar lo que se ve.
            if (x > width || x + clipWidth < 0)
            {
                continue;
            }

            var selected = ReferenceEquals(clip, _selectedClip);
            var rect = new Rect(x + 1, VideoLaneTop + 2, Math.Max(clipWidth - 2, 1), VideoLaneHeight - 4);

            if (clip.IsGap)
            {
                DrawGap(context, clip, rect, selected);
                continue;
            }

            context.DrawRectangle(
                selected ? VideoFillSelected : VideoFill,
                new Pen(VideoStroke, selected ? 2 : 1),
                rect,
                4, 4);

            DrawFilmstrip(context, clip, rect);
            DrawVideoClipLabel(context, clip, rect);
        }

        DrawTransitionBadges(context, width, layout);
    }

    private static readonly IBrush TransitionBadgeStroke = new SolidColorBrush(Color.Parse("#66ffffff"));
    private const double TransitionBadgeSize = 18;

    /// <summary>
    /// Pequeña marca clicable en cada límite interior entre clips: vacía cuando el clip
    /// entrante no pide transición, o con su duración cuando sí.
    /// </summary>
    private void DrawTransitionBadges(DrawingContext context, double width, IReadOnlyList<ClipLayout> layout)
    {
        for (var i = 1; i < layout.Count; i++)
        {
            if (layout[i].Clip.IsGap || layout[i - 1].Clip.IsGap)
            {
                continue;
            }

            var rect = TransitionBadgeRect(layout, i);
            if (rect.Right < HeaderLeft + HeaderWidth || rect.Left > HeaderLeft + width)
            {
                continue;
            }

            var has = !layout[i].Clip.TransitionIn.IsNone;
            var label = has ? FormatTransitionDuration(layout[i].Clip.TransitionIn.Duration) : "+";
            var textBrush = has ? Brushes.White : DimText;

            context.DrawRectangle(has ? ToolAccent : ToolPill, has ? null : new Pen(TransitionBadgeStroke, 1), rect, 9, 9);
            DrawText(context, label, new Point(rect.Center.X - (label.Length * 3.2), rect.Y + 3), 10, textBrush);
        }
    }

    /// <summary>Geometría de la marca de transición de un límite, compartida entre dibujo y hit-testing.</summary>
    private Rect TransitionBadgeRect(IReadOnlyList<ClipLayout> layout, int i)
    {
        var overlap = TransitionMath.Overlap(layout[i - 1].Clip, layout[i].Clip);
        var seam = layout[i].Start + TimeSpan.FromTicks(overlap.Ticks / 2);
        var x = XOf(seam);

        var has = !layout[i].Clip.TransitionIn.IsNone;
        var label = has ? FormatTransitionDuration(layout[i].Clip.TransitionIn.Duration) : "+";
        var pillWidth = has ? (label.Length * 6.5) + 10 : TransitionBadgeSize;
        var y = VideoLaneTop + (VideoLaneHeight / 2) - (TransitionBadgeSize / 2);

        return new Rect(x - (pillWidth / 2), y, Math.Max(pillWidth, TransitionBadgeSize), TransitionBadgeSize);
    }

    private static string FormatTransitionDuration(TimeSpan value) =>
        value.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + "s";

    /// <summary>Clip entrante cuyo límite con el anterior cae bajo el punto dado, o <see langword="null"/>.</summary>
    private Clip? TransitionBadgeAt(Point point)
    {
        if (_sequence is null || _sequence.Video.Clips.Count < 2)
        {
            return null;
        }

        var layout = _sequence.Video.Layout();
        for (var i = 1; i < layout.Count; i++)
        {
            if (layout[i].Clip.IsGap || layout[i - 1].Clip.IsGap)
            {
                continue;
            }

            if (TransitionBadgeRect(layout, i).Contains(point))
            {
                return layout[i].Clip;
            }
        }

        return null;
    }

    private static readonly IPen GapPen = new Pen(new SolidColorBrush(Color.Parse("#4a4a55")), 1, new DashStyle([4, 3], 0));
    private static readonly IPen GapPenSelected = new Pen(new SolidColorBrush(Color.Parse("#8a8a98")), 2, new DashStyle([4, 3], 0));

    /// <summary>Un hueco: tiempo en negro de la pista principal, por ejemplo tras subir un trozo a una capa.</summary>
    private static void DrawGap(DrawingContext context, Clip clip, Rect rect, bool selected)
    {
        context.DrawRectangle(null, selected ? GapPenSelected : GapPen, rect, 4, 4);

        if (rect.Width >= 60)
        {
            using var _ = context.PushClip(rect.Deflate(new Thickness(6, 4)));
            DrawText(context, "Hueco", new Point(rect.X + 7, rect.Y + 5), 11, DimText);
            DrawText(context, clip.Duration.ToString(@"mm\:ss\.ff", CultureInfo.InvariantCulture),
                new Point(rect.X + 7, rect.Y + 22), 10, DimText);
        }
    }

    /// <summary>Dibuja la tira de fotogramas dentro de un clip de video.</summary>
    /// <remarks>
    /// Cada casilla toma el fotograma más cercano a su centro. Solo se recorre lo visible:
    /// con mucho zoom un clip mide decenas de miles de píxeles y el resto no se ve.
    /// </remarks>
    private void DrawFilmstrip(DrawingContext context, Clip clip, Rect rect) =>
        DrawFilmstrip(context, clip.Source.Path, clip.Source.AspectRatio, clip.SourceIn, rect, shadeHeight: 36);

    private void DrawFilmstrip(
        DrawingContext context, string path, double sourceAspect, TimeSpan sourceIn, Rect rect, double shadeHeight)
    {
        if (Filmstrips is null || FrameBitmaps is null || rect.Width < 8)
        {
            return;
        }

        var aspect = sourceAspect > 0 ? sourceAspect : 16.0 / 9;
        var tileHeight = rect.Height - 2;
        var tileWidth = Math.Max(tileHeight * aspect, 8);

        var visibleLeft = Math.Max(rect.Left, HeaderLeft + HeaderWidth);
        var visibleRight = Math.Min(rect.Right, HeaderLeft + (_scroll?.Viewport.Width ?? Bounds.Width));
        if (visibleRight <= visibleLeft)
        {
            return;
        }

        // Las casillas parten del borde izquierdo del clip, no de la parte visible: así no
        // se desplazan al hacer scroll.
        var firstTile = (int)Math.Floor((visibleLeft - rect.Left) / tileWidth);

        using var clipScope = context.PushClip(rect);

        for (var tile = Math.Max(firstTile, 0); rect.Left + (tile * tileWidth) < visibleRight; tile++)
        {
            var x = rect.Left + (tile * tileWidth);
            var centre = sourceIn + TimeSpan.FromSeconds((x + (tileWidth / 2) - rect.Left) / _pixelsPerSecond);

            var frame = Filmstrips.FrameAt(path, centre);
            var bitmap = frame is null ? null : FrameBitmaps.TryGet(frame);
            if (bitmap is null)
            {
                continue;
            }

            context.DrawImage(
                bitmap,
                new Rect(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height),
                new Rect(x, rect.Y + 1, tileWidth, tileHeight));
        }

        // Una banda oscura arriba mantiene legible el nombre sobre cualquier imagen.
        context.FillRectangle(FilmstripShade, new Rect(rect.X, rect.Y, rect.Width, Math.Min(shadeHeight, rect.Height)));
    }

    private static void DrawVideoClipLabel(DrawingContext context, Clip clip, Rect rect)
    {
        // Sin recorte, el nombre se desborda sobre el clip siguiente y ambos quedan
        // ilegibles justo cuando hay muchos clips cortos, que es cuando más falta hace.
        if (rect.Width < 28)
        {
            return;
        }

        using var _ = context.PushClip(rect.Deflate(new Thickness(6, 4)));

        DrawText(context, Path.GetFileName(clip.Source.Path), new Point(rect.X + 7, rect.Y + 5), 11, ClipText);
        DrawText(context, clip.Duration.ToString(@"mm\:ss\.ff", CultureInfo.InvariantCulture),
            new Point(rect.X + 7, rect.Y + 22), 10, DimText);

        var note = clip.IsAudioDetached ? "audio separado"
            : clip.IsAudioMuted ? "silenciado"
            : Math.Abs(clip.AudioGainDb) > 0.05
                ? $"{clip.AudioGainDb.ToString("+0.#;-0.#", CultureInfo.InvariantCulture)} dB"
                : null;

        if (note is not null)
        {
            DrawText(context, note, new Point(rect.X + 7, rect.Y + 38), 10, DimText);
        }
    }

    private void DrawAudioClips(DrawingContext context, double width)
    {
        if (_sequence is null)
        {
            return;
        }

        for (var i = 0; i < _sequence.AudioTracks.Count; i++)
        {
            var track = _sequence.AudioTracks[i];
            var top = AudioLaneTop(i);

            foreach (var clip in track.Clips)
            {
                var trimming = _drag is DragKind.AudioTrimStart or DragKind.AudioTrimEnd && ReferenceEquals(clip, _dragAudio);
                var dragging = (_drag == DragKind.AudioMove && ReferenceEquals(clip, _dragAudio)) || trimming;
                var start = clip.TimelineStart;
                var length = clip.Duration;

                if (dragging && !trimming)
                {
                    start = _audioPreviewStart;
                }
                else if (trimming && _drag == DragKind.AudioTrimStart)
                {
                    start = _audioTrimPosition;
                    length = clip.TimelineEnd - _audioTrimPosition;
                }
                else if (trimming)
                {
                    length = _audioTrimPosition - clip.TimelineStart;
                }

                var x = XOf(start);
                var clipWidth = Math.Max(length.TotalSeconds * _pixelsPerSecond, 2);
                if (x > width || x + clipWidth < 0)
                {
                    continue;
                }

                var selected = ReferenceEquals(clip, _selectedAudio);
                var rect = new Rect(x + 1, top + 2, Math.Max(clipWidth - 2, 1), AudioLaneHeight - 4);

                var fill = dragging && !_audioPreviewValid ? AudioFillInvalid
                    : clip.IsMuted || track.IsMuted ? AudioFillMuted
                    : selected ? AudioFillSelected
                    : AudioFill;

                context.DrawRectangle(fill, new Pen(AudioStroke, selected ? 2 : 1), rect, 4, 4);

                // Al recortar por el inicio, el borde izquierdo se mueve pero el audio no: lo que
                // hay bajo él es el que corresponde a esa posición, no el del inicio del clip.
                var sourceAtLeft = trimming && _drag == DragKind.AudioTrimStart
                    ? clip.SourceIn + (_audioTrimPosition - clip.TimelineStart)
                    : clip.SourceIn;
                DrawWaveform(context, clip, rect, sourceAtLeft);
                DrawFades(context, clip, rect);
                DrawAudioClipLabel(context, clip, rect);
            }
        }
    }

    /// <summary>Dibuja la forma de onda de un clip de audio dentro de su rectángulo.</summary>
    /// <remarks>
    /// Solo se recorre la parte visible: un clip de una hora a buen zoom mide decenas de miles
    /// de píxeles y dibujar los que están fuera de pantalla sería trabajo tirado en cada
    /// repintado. Cada columna toma el máximo de los picos que cubre.
    /// </remarks>
    private void DrawWaveform(DrawingContext context, AudioClip clip, Rect rect, TimeSpan sourceAtLeft)
    {
        var peaks = Waveforms?.PeaksIfLoaded(clip.Source.Path);
        if (peaks is null || rect.Width < 2)
        {
            return;
        }

        var visibleLeft = Math.Max(rect.Left, HeaderLeft + HeaderWidth);
        var visibleRight = Math.Min(rect.Right, HeaderLeft + (_scroll?.Viewport.Width ?? Bounds.Width));
        if (visibleRight <= visibleLeft)
        {
            return;
        }

        var middle = rect.Y + (rect.Height / 2);
        var half = (rect.Height / 2) - 3;

        // La ganancia del clip se refleja en la altura: subir el volumen se ve.
        var gain = Math.Pow(10, clip.GainDb / 20) / 255.0;

        var top = new List<Point>();
        var bottom = new List<Point>();

        for (var x = Math.Floor(visibleLeft); x < visibleRight; x += 1)
        {
            var from = sourceAtLeft + TimeSpan.FromSeconds((x - rect.Left) / _pixelsPerSecond);
            var to = from + TimeSpan.FromSeconds(1 / _pixelsPerSecond);
            if (to < TimeSpan.Zero)
            {
                continue;
            }

            var amplitude = Math.Pow(Math.Min(1, WaveformPeaks.MaxIn(peaks, from < TimeSpan.Zero ? TimeSpan.Zero : from, to) * gain), 0.6);

            // El exponente 0,6 realza lo suave: la amplitud de música y voz es mucho menor que la
            // máxima, y dibujada en lineal casi toda la onda quedaría como una raya.
            // Una raya mínima para que el silencio se distinga de la falta de datos.
            var h = Math.Max(0.6, amplitude * half);
            top.Add(new Point(x, middle - h));
            bottom.Add(new Point(x, middle + h));
        }

        if (top.Count < 2)
        {
            return;
        }

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(top[0], isFilled: true);
            for (var i = 1; i < top.Count; i++)
            {
                g.LineTo(top[i]);
            }

            for (var i = bottom.Count - 1; i >= 0; i--)
            {
                g.LineTo(bottom[i]);
            }

            g.EndFigure(isClosed: true);
        }

        using var clipScope = context.PushClip(rect);
        context.DrawGeometry(WaveBrush, null, geometry);
    }

    private void DrawFades(DrawingContext context, AudioClip clip, Rect rect)
    {
        var pen = new Pen(FadeBrush, 1.5);

        if (clip.FadeIn > TimeSpan.Zero)
        {
            var span = Math.Min(clip.FadeIn.TotalSeconds * _pixelsPerSecond, rect.Width);
            context.DrawLine(pen, new Point(rect.X, rect.Bottom), new Point(rect.X + span, rect.Y));
        }

        if (clip.FadeOut > TimeSpan.Zero)
        {
            var span = Math.Min(clip.FadeOut.TotalSeconds * _pixelsPerSecond, rect.Width);
            context.DrawLine(pen, new Point(rect.Right - span, rect.Y), new Point(rect.Right, rect.Bottom));
        }
    }

    private static void DrawAudioClipLabel(DrawingContext context, AudioClip clip, Rect rect)
    {
        if (rect.Width < 28)
        {
            return;
        }

        using var _ = context.PushClip(rect.Deflate(new Thickness(6, 3)));

        DrawText(context, Path.GetFileName(clip.Source.Path), new Point(rect.X + 7, rect.Y + 4), 11, ClipText);

        var detail = clip.IsMuted
            ? "silenciado"
            : Math.Abs(clip.GainDb) > 0.05
                ? $"{clip.GainDb.ToString("+0.#;-0.#", CultureInfo.InvariantCulture)} dB"
                : clip.Duration.ToString(@"mm\:ss", CultureInfo.InvariantCulture);

        DrawText(context, detail, new Point(rect.X + 7, rect.Y + 22), 10, DimText);
    }

    private static readonly IBrush LiftGhostFill = new SolidColorBrush(Color.Parse("#552F8CFF"));

    private void DrawDropIndicators(DrawingContext context, double width)
    {
        var pen = new Pen(DropIndicator, 3);

        if (_dropIndex >= 0 && _sequence is not null)
        {
            var x = XOf(StartOfIndex(_dropIndex));
            context.DrawLine(pen, new Point(x, VideoLaneTop - 2), new Point(x, VideoLaneTop + VideoLaneHeight + 2));
        }

        if (_trackDropIndex >= 0 && _sequence is not null)
        {
            var y = AudioLaneTop(_trackDropIndex) - (LanePadding / 2);
            context.DrawLine(pen, new Point(HeaderLeft, y), new Point(width, y));
        }

        if (_liftActive && _dragClip is not null && _sequence is not null)
        {
            // Vista previa de dónde quedará: en la capa bajo el ratón, o en una nueva arriba del todo.
            var start = _sequence.Video.StartOf(_dragClip);
            var left = XOf(start);
            var length = Math.Max(_dragClip.Duration.TotalSeconds * _pixelsPerSecond, 8);
            // Solo cabe en la capa bajo el ratón si está libre en ese tramo, no es de subtítulos y no está bloqueada.
            var fits = _liftLane >= 0
                && _liftLane < _sequence.OverlayTracks.Count
                && _sequence.OverlayTracks[_liftLane] is { IsLocked: false, IsSubtitles: false } target
                && target.CanPlace(start, _dragClip.Duration);
            var top = fits ? OverlayLaneTop(_liftLane) + 2 : 3;
            var height = fits ? OverlayLaneHeight - 4 : RulerHeight - 6;
            var ghost = new Rect(left + 1, top, length - 2, height);

            context.DrawRectangle(LiftGhostFill, new Pen(DropIndicator, 2), ghost, 4, 4);
            using var clip = context.PushClip(ghost.Deflate(new Thickness(4, 0)));
            DrawText(
                context,
                fits ? "Subir a esta capa" : "Subir a una capa nueva",
                new Point(ghost.X + 8, ghost.Y + Math.Max((ghost.Height - 14) / 2, 1)),
                11,
                ClipText);
        }
    }

    private void DrawPlayhead(DrawingContext context, double height)
    {
        // Sin redondear a píxeles enteros: con el cabezal moviéndose a cada fotograma de pantalla,
        // saltar de píxel en píxel se nota como un avance a tirones, sobre todo con zoom lejano.
        var x = XOf(_playhead);
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

    private void DrawHeaders(DrawingContext context, double height)
    {
        var left = HeaderLeft;

        // Fondo opaco de toda la columna: los clips que pasan por debajo al desplazarse
        // no deben verse a través de las cabeceras.
        context.FillRectangle(HeaderBackground, new Rect(left, 0, HeaderWidth, height));
        context.FillRectangle(RulerBackground, new Rect(left, 0, HeaderWidth, RulerHeight));

        DrawText(context, "V1", new Point(left + 10, VideoLaneTop + 8), 12, ClipText);
        DrawText(context, "Video", new Point(left + 10, VideoLaneTop + 26), 10, DimText);

        DrawOverlayHeaders(context, left);

        if (_sequence is null)
        {
            return;
        }

        var anySolo = _sequence.AnySolo;

        for (var i = 0; i < _sequence.AudioTracks.Count; i++)
        {
            var track = _sequence.AudioTracks[i];
            var top = AudioLaneTop(i);

            if (track.IsLocked)
            {
                context.FillRectangle(HeaderLocked, new Rect(left, top, HeaderWidth, AudioLaneHeight));
            }

            var nameBrush = track.IsAudible(anySolo) ? ClipText : DimText;
            DrawText(context, track.Name, new Point(left + 10, top + 5), 12, nameBrush);

            DrawToggle(context, ToggleRect(left, top, 0), "M", track.IsMuted, ToggleMute);
            DrawToggle(context, ToggleRect(left, top, 1), "S", track.IsSolo, ToggleSolo);
            DrawToggle(context, ToggleRect(left, top, 2), "L", track.IsLocked, ToggleLock);
        }

        context.DrawLine(new Pen(TickBrush, 1), new Point(left + HeaderWidth - 0.5, 0), new Point(left + HeaderWidth - 0.5, height));
    }

    private static Rect ToggleRect(double headerLeft, double laneTop, int index) =>
        new(headerLeft + 10 + (index * (ToggleSize + 5)), laneTop + AudioLaneHeight - ToggleSize - 6, ToggleSize, ToggleSize);

    private static void DrawToggle(DrawingContext context, Rect rect, string letter, bool active, IBrush activeBrush)
    {
        context.DrawRectangle(active ? activeBrush : ToggleOff, null, rect, 3, 3);
        DrawText(context, letter, new Point(rect.X + 4.5, rect.Y + 1.5), 10, active ? Brushes.White : DimText);
    }

    // ------------------------------------------------------------ cursor de referencia

    private static readonly IBrush HoverLine = new SolidColorBrush(Color.Parse("#99FFFFFF"));
    private static readonly IBrush HoverPill = new SolidColorBrush(Color.Parse("#101014"));
    private double? _hoverX;

    private void SetHover(double? x)
    {
        // Solo se repinta si el cursor se movió de verdad: con cada evento del ratón la timeline
        // entera se redibujaría sin cambiar nada visible.
        if (_hoverX is null && x is null)
        {
            return;
        }

        if (_hoverX is { } current && x is { } next && Math.Abs(current - next) < 1)
        {
            return;
        }

        _hoverX = x;
        Layer?.InvalidateVisual();
    }

    /// <summary>Capa donde se dibujan el cabezal y el cursor de referencia.</summary>
    internal PlayheadLayer? Layer { get; set; }

    /// <summary>Dibuja el cursor de referencia y el cabezal, sin invadir la columna de cabeceras.</summary>
    internal void DrawPlayheadLayer(DrawingContext context, double height)
    {
        var left = HeaderLeft + HeaderWidth;
        using var _ = context.PushClip(new Rect(left, 0, Math.Max(Bounds.Width - left, 0), height));

        DrawHoverCursor(context, height);
        DrawPlayhead(context, height);
    }

    /// <inheritdoc/>
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetHover(null);
    }

    /// <summary>Una línea fina bajo el ratón con el instante que marca, para medir sin mover el cabezal.</summary>
    private void DrawHoverCursor(DrawingContext context, double height)
    {
        if (_hoverX is not { } x)
        {
            return;
        }

        var time = TimeOf(x);
        var line = Math.Round(x) + 0.5;
        context.DrawLine(new Pen(HoverLine, 1), new Point(line, RulerHeight), new Point(line, height));

        var label = FormatClock(time);
        const double pillWidth = 60;
        var pill = new Rect(line - (pillWidth / 2), 3, pillWidth, 19);
        context.DrawRectangle(HoverPill, null, pill, 5, 5);
        DrawText(context, label, new Point(pill.X + 8, pill.Y + 3), 11, ClipText);
    }

    /// <summary>Formato <c>m:ss.cc</c> (centésimas), como el reloj del preview.</summary>
    internal static string FormatClock(TimeSpan value)
    {
        var centis = (long)Math.Floor(value.TotalSeconds * 100);
        var minutes = centis / 6000;
        var seconds = centis / 100 % 60;
        return string.Create(CultureInfo.InvariantCulture, $"{minutes}:{seconds:00}.{centis % 100:00}");
    }

    private static void DrawText(DrawingContext context, string text, Point origin, double size, IBrush brush)
    {
        var formatted = new FormattedText(
            text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush);
        context.DrawText(formatted, origin);
    }

    // ------------------------------------------------------------- interacción

    /// <inheritdoc/>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();

        var point = e.GetPosition(this);
        var properties = e.GetCurrentPoint(this).Properties;

        if (properties.IsRightButtonPressed)
        {
            ShowContextMenu(point);
            e.Handled = true;
            return;
        }

        if (!properties.IsLeftButtonPressed)
        {
            return;
        }

        if (point.Y < RulerHeight)
        {
            if (point.X >= HeaderLeft + HeaderWidth)
            {
                _drag = DragKind.Playhead;
                MovePlayheadTo(point.X);
                e.Pointer.Capture(this);
            }

            return;
        }

        if (point.X < HeaderLeft + HeaderWidth)
        {
            HeaderPressed(point, e);
            return;
        }

        LanePressed(point, e);
    }

    private void HeaderPressed(Point point, PointerPressedEventArgs e)
    {
        if (OverlayHeaderPressed(point))
        {
            return;
        }

        var index = AudioLaneIndexAt(point.Y);
        if (_sequence is null || index < 0)
        {
            return;
        }

        var track = _sequence.AudioTracks[index];
        var top = AudioLaneTop(index);

        // Los interruptores no son ediciones del montaje y no pasan por el historial:
        // silenciar una pista para escucharla no debería llenar la lista de deshacer.
        if (ToggleRect(HeaderLeft, top, 0).Contains(point))
        {
            track.IsMuted = !track.IsMuted;
            NotifyEdited();
            return;
        }

        if (ToggleRect(HeaderLeft, top, 1).Contains(point))
        {
            track.IsSolo = !track.IsSolo;
            NotifyEdited();
            return;
        }

        if (ToggleRect(HeaderLeft, top, 2).Contains(point))
        {
            track.IsLocked = !track.IsLocked;
            NotifyEdited();
            return;
        }

        _drag = DragKind.TrackReorder;
        _dragTrack = track;
        _trackDropIndex = index;
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    private void LanePressed(Point point, PointerPressedEventArgs e)
    {
        if (_sequence is null)
        {
            return;
        }

        if (OverlayLanePressed(point, e))
        {
            return;
        }

        // Pista de video.
        if (point.Y >= VideoLaneTop && point.Y <= VideoLaneTop + VideoLaneHeight)
        {
            // La marca de transición tiene prioridad: cae justo en la zona donde dos clips se
            // solapan, y un clic ahí quiere decir "edita esta transición", no "arrastra el clip".
            if (TransitionBadgeAt(point) is { } incoming)
            {
                Select(incoming, null, null);
                TransitionBadgeClicked?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;
            }

            var hit = HitTestVideo(point);
            if (hit.Clip is null)
            {
                ClearSelection();
                return;
            }

            Select(hit.Clip, null, null);
            _dragClip = hit.Clip;
            _dragOriginX = point.X;
            _toolDelta = TimeSpan.Zero;
            _drag = ToolFor(hit, e.KeyModifiers) ?? hit.Region switch
            {
                HitRegion.LeftEdge => DragKind.VideoTrimStart,
                HitRegion.RightEdge => DragKind.VideoTrimEnd,
                _ => DragKind.VideoReorder,
            };

            // Con la herramienta de corte, el clip que se arrastra es el anterior al corte.
            if (_drag == DragKind.VideoRoll && hit.Region == HitRegion.LeftEdge)
            {
                var index = _sequence.Video.IndexOf(hit.Clip);
                _dragClip = _sequence.Video.Clips[index - 1];
            }

            e.Pointer.Capture(this);
            return;
        }

        // Pistas de audio.
        var laneIndex = AudioLaneIndexAt(point.Y);
        if (laneIndex < 0)
        {
            ClearSelection();
            return;
        }

        var track = _sequence.AudioTracks[laneIndex];
        var audio = AudioClipAt(track, point.X);

        if (audio is null)
        {
            ClearSelection();
            return;
        }

        Select(null, audio, track);

        if (!track.IsLocked)
        {
            var region = AudioEdgeAt(audio, point.X);

            _drag = region switch
            {
                HitRegion.LeftEdge => DragKind.AudioTrimStart,
                HitRegion.RightEdge => DragKind.AudioTrimEnd,
                _ => DragKind.AudioMove,
            };
            _dragAudio = audio;
            _dragTrack = track;
            _dragOriginX = point.X;

            // En un recorte, lo que se arrastra es el borde: se parte de su posición.
            _dragAudioOrigin = region switch
            {
                HitRegion.LeftEdge => audio.TimelineStart,
                HitRegion.RightEdge => audio.TimelineEnd,
                _ => audio.TimelineStart,
            };
            _audioPreviewStart = audio.TimelineStart;
            _audioTrimPosition = _dragAudioOrigin;
            _audioPreviewValid = true;
            e.Pointer.Capture(this);
        }
    }

    /// <inheritdoc/>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);

        if (_drag == DragKind.None)
        {
            UpdateCursor(point);
            SetHover(point.X > HeaderLeft + HeaderWidth ? point.X : null);
            return;
        }

        SetHover(null);

        if (OverlayDragMoved(point))
        {
            return;
        }

        switch (_drag)
        {
            case DragKind.Playhead:
                MovePlayheadTo(point.X);
                break;

            case DragKind.VideoReorder when _dragClip is not null && _sequence is not null:
                // Sacar el clip por arriba de su pista lo sube a una capa; el hueco que deja lo llena un hueco.
                if (point.Y < VideoLaneTop - 6 && LiftClipToLayerCommand.CanLift(_dragClip))
                {
                    var lane = OverlayLaneIndexAt(point.Y);
                    if (!_liftActive || lane != _liftLane || _dropIndex != -1)
                    {
                        _liftActive = true;
                        _liftLane = lane;
                        _dropIndex = -1;
                        InvalidateVisual();
                    }

                    break;
                }

                if (_liftActive)
                {
                    _liftActive = false;
                    _liftLane = -1;
                    InvalidateVisual();
                }

                var index = IndexAtX(point.X);
                if (index != _dropIndex)
                {
                    _dropIndex = index;
                    InvalidateVisual();
                }

                break;

            case DragKind.VideoSlip or DragKind.VideoRoll or DragKind.VideoSlide when _dragClip is not null && _sequence is not null:
                var moved = TimeSpan.FromSeconds((point.X - _dragOriginX) / _pixelsPerSecond);
                _toolDelta = ClampTool(_drag, _dragClip, moved);
                InvalidateVisual();
                break;

            case DragKind.AudioMove when _dragAudio is not null && _dragTrack is not null:
                var requested = _dragAudioOrigin + TimeSpan.FromSeconds((point.X - _dragOriginX) / _pixelsPerSecond);
                _audioPreviewStart = Snap(requested, _dragAudio);
                _audioPreviewValid = _dragTrack.CanPlace(_audioPreviewStart, _dragAudio.Duration, _dragAudio);
                InvalidateVisual();
                break;

            case DragKind.AudioTrimStart or DragKind.AudioTrimEnd when _dragAudio is not null && _dragTrack is not null:
                var edgeRequested = _dragAudioOrigin + TimeSpan.FromSeconds((point.X - _dragOriginX) / _pixelsPerSecond);
                _audioTrimPosition = SnapEdge(edgeRequested, _dragAudio);
                _audioPreviewValid = _dragTrack.CanTrim(
                    _dragAudio,
                    _drag == DragKind.AudioTrimStart ? ClipEdge.Start : ClipEdge.End,
                    _audioTrimPosition);
                InvalidateVisual();
                break;

            case DragKind.TrackReorder when _sequence is not null:
                var target = Math.Clamp(AudioLaneIndexAtClamped(point.Y), 0, _sequence.AudioTracks.Count - 1);
                if (target != _trackDropIndex)
                {
                    _trackDropIndex = target;
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
        if (OverlayDragReleased())
        {
            e.Pointer.Capture(null);
            return;
        }

        var delta = TimeSpan.FromSeconds((point.X - _dragOriginX) / _pixelsPerSecond);

        switch (_drag)
        {
            case DragKind.VideoTrimStart when _dragClip is not null:
                Apply(new TrimClipCommand(_dragClip, ClipEdge.Start, delta));
                break;

            case DragKind.VideoTrimEnd when _dragClip is not null:
                Apply(new TrimClipCommand(_dragClip, ClipEdge.End, delta));
                break;

            case DragKind.VideoSlip when _dragClip is not null && _toolDelta != TimeSpan.Zero:
                Apply(new SlipClipCommand(_dragClip, _toolDelta));
                break;

            case DragKind.VideoRoll when _dragClip is not null && _sequence is not null && _toolDelta != TimeSpan.Zero:
                Apply(new RollEditCommand(_sequence.Video, _dragClip, _toolDelta));
                break;

            case DragKind.VideoSlide when _dragClip is not null && _sequence is not null && _toolDelta != TimeSpan.Zero:
                Apply(new SlideClipCommand(_sequence.Video, _dragClip, _toolDelta));
                break;

            case DragKind.VideoReorder when _liftActive && _dragClip is not null && _sequence is not null:
                LiftSelectedClip(_liftLane >= 0 && _liftLane < _sequence.OverlayTracks.Count ? _sequence.OverlayTracks[_liftLane] : null);
                break;

            case DragKind.VideoReorder when _dragClip is not null && _sequence is not null && _dropIndex >= 0:
                var currentIndex = _sequence.Video.IndexOf(_dragClip);
                var target = _dropIndex > currentIndex ? _dropIndex - 1 : _dropIndex;
                if (target != currentIndex)
                {
                    Apply(new MoveClipCommand(_sequence.Video, _dragClip, target));
                }

                break;

            case DragKind.AudioMove when _dragAudio is not null && _dragTrack is not null:
                // Solo se aplica si cabe. Soltar sobre otro clip no debe sustituirlo ni
                // dejar dos sonando encima: el clip vuelve donde estaba.
                if (_audioPreviewValid && _audioPreviewStart != _dragAudioOrigin)
                {
                    Apply(new MoveAudioClipCommand(_dragTrack, _dragAudio, _audioPreviewStart));
                }

                break;

            case DragKind.AudioTrimStart or DragKind.AudioTrimEnd when _dragAudio is not null && _dragTrack is not null:
                // Un recorte que no cabe se descarta: el clip vuelve a su tamaño.
                if (_audioPreviewValid && _audioTrimPosition != _dragAudioOrigin)
                {
                    Apply(new TrimAudioClipCommand(
                        _dragTrack,
                        _dragAudio,
                        _drag == DragKind.AudioTrimStart ? ClipEdge.Start : ClipEdge.End,
                        _audioTrimPosition));
                }

                break;

            case DragKind.TrackReorder when _dragTrack is not null && _sequence is not null && _trackDropIndex >= 0:
                if (_trackDropIndex != _sequence.IndexOf(_dragTrack))
                {
                    Apply(new MoveAudioTrackCommand(_sequence, _dragTrack, _trackDropIndex));
                }

                break;
        }

        _drag = DragKind.None;
        _dragClip = null;
        _dragAudio = null;
        _dragTrack = null;
        _dropIndex = -1;
        _liftActive = false;
        _liftLane = -1;
        _trackDropIndex = -1;
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

        if (point.X < HeaderLeft + HeaderWidth)
        {
            Cursor = _sequence is not null && AudioLaneIndexAt(point.Y) >= 0
                ? new Cursor(StandardCursorType.Hand)
                : Cursor.Default;
            return;
        }

        if (OverlayCursorAt(point) is { } overlayCursor)
        {
            Cursor = overlayCursor;
            return;
        }

        if (_sequence is not null && point.Y >= VideoLaneTop && point.Y <= VideoLaneTop + VideoLaneHeight)
        {
            var hit = HitTestVideo(point);
            Cursor = hit.Region switch
            {
                HitRegion.LeftEdge or HitRegion.RightEdge => new Cursor(StandardCursorType.SizeWestEast),
                HitRegion.Body => new Cursor(StandardCursorType.SizeAll),
                _ => Cursor.Default,
            };
            return;
        }

        var lane = AudioLaneIndexAt(point.Y);
        var hovered = _sequence is not null && lane >= 0 ? AudioClipAt(_sequence.AudioTracks[lane], point.X) : null;

        Cursor = hovered is null
            ? Cursor.Default
            : _sequence!.AudioTracks[lane].IsLocked
                ? new Cursor(StandardCursorType.Arrow)
                : AudioEdgeAt(hovered, point.X) is HitRegion.LeftEdge or HitRegion.RightEdge
                    ? new Cursor(StandardCursorType.SizeWestEast)
                    : new Cursor(StandardCursorType.SizeAll);
    }

    private void MovePlayheadTo(double x)
    {
        var position = TimeOf(x);

        if (_sequence is not null && position > _sequence.Duration)
        {
            position = _sequence.Duration;
        }

        Playhead = position;
        PlayheadMoved?.Invoke(this, Playhead);
    }

    // ---------------------------------------------------------------- imán

    private TimeSpan Snap(TimeSpan requested, AudioClip moving)
    {
        if (_sequence is null)
        {
            return requested < TimeSpan.Zero ? TimeSpan.Zero : requested;
        }

        // La distancia se define en píxeles, no en tiempo: con mucho zoom 8 píxeles son
        // milisegundos, y con poco zoom son segundos. El imán debe sentirse igual en ambos.
        var threshold = TimeSpan.FromSeconds(SnapDistance / _pixelsPerSecond);

        return Snapping.Snap(
            requested,
            moving.Duration,
            Snapping.PointsFor(_sequence, _playhead, moving),
            threshold);
    }

    /// <summary>
    /// Herramienta de edición fina que corresponde a un agarre con Alt pulsado, o
    /// <see langword="null"/> si no hay ninguna y vale el comportamiento normal.
    /// </summary>
    /// <remarks>
    /// Alt + borde mueve el corte; Alt + cuerpo desliza el contenido del clip; Alt + Mayús +
    /// cuerpo desliza el clip entre sus vecinos. Sin Alt, arrastrar sigue siendo recortar o
    /// reordenar, de modo que quien no conozca las herramientas no las activa sin querer.
    /// </remarks>
    private DragKind? ToolFor(ClipHit hit, KeyModifiers modifiers)
    {
        if (!modifiers.HasFlag(KeyModifiers.Alt) || _sequence is null || hit.Clip is null)
        {
            return null;
        }

        var clips = _sequence.Video.Clips;
        var index = _sequence.Video.IndexOf(hit.Clip);

        switch (hit.Region)
        {
            case HitRegion.LeftEdge when index > 0:
                return DragKind.VideoRoll;

            case HitRegion.RightEdge when index + 1 < clips.Count:
                return DragKind.VideoRoll;

            case HitRegion.Body when modifiers.HasFlag(KeyModifiers.Shift) && index > 0 && index + 1 < clips.Count:
                return DragKind.VideoSlide;

            case HitRegion.Body:
                return DragKind.VideoSlip;

            default:
                return null;
        }
    }

    /// <summary>Desplazamiento que cabe para una herramienta, dado lo que se ha arrastrado.</summary>
    private TimeSpan ClampTool(DragKind kind, Clip clip, TimeSpan moved) => kind switch
    {
        // El contenido sigue al ratón: arrastrar hacia la derecha muestra un momento anterior.
        DragKind.VideoSlip => TimelineTools.ClampSlip(clip, -moved),
        DragKind.VideoRoll => TimelineTools.ClampRoll(_sequence!.Video, clip, moved),
        DragKind.VideoSlide => TimelineTools.ClampSlide(_sequence!.Video, clip, moved),
        _ => TimeSpan.Zero,
    };

    /// <summary>Dibuja la indicación de la herramienta de edición fina en curso.</summary>
    private void DrawToolFeedback(DrawingContext context)
    {
        if (_sequence is null || _dragClip is null ||
            _drag is not (DragKind.VideoSlip or DragKind.VideoRoll or DragKind.VideoSlide))
        {
            return;
        }

        var clipStart = _sequence.Video.StartOf(_dragClip);
        var top = VideoLaneTop;
        var sign = _toolDelta < TimeSpan.Zero ? "−" : "+";
        var amount = Math.Abs(_toolDelta.TotalSeconds).ToString("0.00", CultureInfo.InvariantCulture);

        string label;
        double labelX;

        switch (_drag)
        {
            case DragKind.VideoRoll:
                // El corte nuevo: una línea sobre la posición donde quedará.
                var cut = XOf(clipStart + _dragClip.Duration + _toolDelta);
                context.DrawLine(new Pen(ToolAccent, 2), new Point(cut, top), new Point(cut, top + VideoLaneHeight));
                label = $"Mover corte  {sign}{amount} s";
                labelX = cut + 8;
                break;

            case DragKind.VideoSlide:
                // El clip en su nueva posición, dibujado como contorno.
                var ghost = new Rect(
                    XOf(clipStart + _toolDelta), top + 2, _dragClip.Duration.TotalSeconds * _pixelsPerSecond, VideoLaneHeight - 4);
                context.DrawRectangle(null, new Pen(ToolAccent, 2, DashStyle.Dash), ghost, 4, 4);
                label = $"Deslizar clip  {sign}{amount} s";
                labelX = ghost.X + 8;
                break;

            default:
                label = $"Deslizar contenido  {sign}{amount} s";
                labelX = XOf(clipStart) + 8;
                break;
        }

        var pill = new Rect(Math.Max(labelX - 4, HeaderLeft + HeaderWidth), top + VideoLaneHeight - 24, 178, 18);
        context.DrawRectangle(ToolPill, null, pill, 4, 4);
        DrawText(context, label, new Point(pill.X + 6, pill.Y + 2), 11, ClipText);
    }

    /// <summary>Imán para un borde suelto: el del clip que se recorta, no un bloque entero.</summary>
    private TimeSpan SnapEdge(TimeSpan requested, AudioClip moving)
    {
        if (_sequence is null)
        {
            return requested < TimeSpan.Zero ? TimeSpan.Zero : requested;
        }

        var threshold = TimeSpan.FromSeconds(SnapDistance / _pixelsPerSecond);

        // Duración cero: se ajusta un único punto, sin que un extremo imante el otro.
        return Snapping.Snap(
            requested,
            TimeSpan.Zero,
            Snapping.PointsFor(_sequence, _playhead, moving),
            threshold);
    }

    /// <summary>Zona de un clip de audio bajo una coordenada: borde izquierdo, derecho o cuerpo.</summary>
    private HitRegion AudioEdgeAt(AudioClip clip, double x)
    {
        var left = XOf(clip.TimelineStart);
        var width = clip.Duration.TotalSeconds * _pixelsPerSecond;

        // En un clip muy estrecho no cabrían las dos zonas de recorte y el cuerpo a la vez.
        if (width <= EdgeGrip * 3)
        {
            return HitRegion.Body;
        }

        if (x - left <= EdgeGrip)
        {
            return HitRegion.LeftEdge;
        }

        return left + width - x <= EdgeGrip ? HitRegion.RightEdge : HitRegion.Body;
    }

    // ---------------------------------------------------------------- menú

    private void ShowContextMenu(Point point)
    {
        if (_sequence is null)
        {
            return;
        }

        var menu = new ContextMenu();

        if (BuildOverlayMenu(menu, point))
        {
            menu.Open(this);
            return;
        }

        if (point.X < HeaderLeft + HeaderWidth)
        {
            BuildTrackMenu(menu, AudioLaneIndexAt(point.Y));
        }
        else if (point.Y >= VideoLaneTop && point.Y <= VideoLaneTop + VideoLaneHeight)
        {
            var hit = HitTestVideo(point);
            if (hit.Clip is not null)
            {
                Select(hit.Clip, null, null);
                BuildVideoClipMenu(menu, hit.Clip);
            }
        }
        else
        {
            var lane = AudioLaneIndexAt(point.Y);
            var audio = lane >= 0 ? AudioClipAt(_sequence.AudioTracks[lane], point.X) : null;

            if (audio is not null)
            {
                var track = _sequence.AudioTracks[lane];
                Select(null, audio, track);
                BuildAudioClipMenu(menu, track, audio);
            }
            else
            {
                BuildTrackMenu(menu, lane);
            }
        }

        if (menu.Items.Count > 0)
        {
            menu.Open(this);
        }
    }

    private void BuildVideoClipMenu(ContextMenu menu, Clip clip)
    {
        var atPlayhead = _sequence!.Video.ClipAt(_playhead);
        var canSplit = atPlayhead is not null && ReferenceEquals(atPlayhead.Value.Clip, clip);

        AddItem(menu, "Dividir en el cabezal   S", () => SplitAtPlayhead(), canSplit);
        AddItem(menu, "Separar audio", () => Apply(new DetachAudioCommand(_sequence!, clip)),
            clip.Source.HasAudio && !clip.IsAudioDetached);
        menu.Items.Add(new Separator());

        // El volumen y el silencio del propio clip solo tienen sentido si el clip todavía
        // suena por su cuenta: con el audio separado, el volumen se ajusta en su pista.
        var ownSound = clip.Source.HasAudio && !clip.IsAudioDetached;
        AddItem(menu, "Subir volumen  +3 dB",
            () => Apply(new SetClipAudioGainCommand(clip, clip.AudioGainDb + 3)),
            ownSound && clip.AudioGainDb < AudioClip.MaximumGainDb);
        AddItem(menu, "Bajar volumen  −3 dB",
            () => Apply(new SetClipAudioGainCommand(clip, clip.AudioGainDb - 3)),
            ownSound && clip.AudioGainDb > AudioClip.MinimumGainDb);
        AddItem(menu, "Restablecer volumen",
            () => Apply(new SetClipAudioGainCommand(clip, 0)),
            ownSound && Math.Abs(clip.AudioGainDb) > 0.05);
        AddItem(menu, clip.IsAudioMuted ? "Activar sonido del clip" : "Silenciar clip",
            () => Apply(new SetClipAudioMutedCommand(clip, !clip.IsAudioMuted)), ownSound);
        menu.Items.Add(new Separator());
        AddItem(menu, "Eliminar   Supr", () => DeleteSelected());
    }

    private void BuildAudioClipMenu(ContextMenu menu, AudioTrack track, AudioClip clip)
    {
        var editable = !track.IsLocked;

        AddItem(menu, "Subir volumen  +3 dB",
            () => Apply(new SetAudioGainCommand(clip, clip.GainDb + 3)), editable && clip.GainDb < AudioClip.MaximumGainDb);
        AddItem(menu, "Bajar volumen  −3 dB",
            () => Apply(new SetAudioGainCommand(clip, clip.GainDb - 3)), editable && clip.GainDb > AudioClip.MinimumGainDb);
        AddItem(menu, "Restablecer volumen",
            () => Apply(new SetAudioGainCommand(clip, 0)), editable && Math.Abs(clip.GainDb) > 0.05);
        menu.Items.Add(new Separator());
        AddItem(menu, clip.IsMuted ? "Activar sonido" : "Silenciar",
            () => Apply(new SetAudioMutedCommand(clip, !clip.IsMuted)), editable);
        menu.Items.Add(new Separator());

        AddItem(menu, "Dividir en el cabezal   S", () => SplitAtPlayhead(),
            editable && _playhead > clip.TimelineStart && _playhead < clip.TimelineEnd);
        menu.Items.Add(new Separator());

        var oneSecond = TimeSpan.FromSeconds(1);
        AddItem(menu, clip.FadeIn > TimeSpan.Zero ? "Quitar fundido de entrada" : "Fundido de entrada  1 s",
            () => Apply(new SetAudioFadeCommand(clip, clip.FadeIn > TimeSpan.Zero ? TimeSpan.Zero : oneSecond, clip.FadeOut)),
            editable);
        AddItem(menu, clip.FadeOut > TimeSpan.Zero ? "Quitar fundido de salida" : "Fundido de salida  1 s",
            () => Apply(new SetAudioFadeCommand(clip, clip.FadeIn, clip.FadeOut > TimeSpan.Zero ? TimeSpan.Zero : oneSecond)),
            editable);
        menu.Items.Add(new Separator());
        AddItem(menu, "Eliminar   Supr", () => DeleteSelected(), editable);
    }

    private void BuildTrackMenu(ContextMenu menu, int index)
    {
        AddItem(menu, "Añadir pista de audio", () => Apply(new AddAudioTrackCommand(_sequence!)));

        if (index >= 0 && index < _sequence!.AudioTracks.Count)
        {
            var track = _sequence.AudioTracks[index];
            menu.Items.Add(new Separator());
            AddItem(menu, track.IsMuted ? "Activar pista" : "Silenciar pista",
                () => { track.IsMuted = !track.IsMuted; NotifyEdited(); });
            AddItem(menu, track.IsSolo ? "Quitar solo" : "Solo",
                () => { track.IsSolo = !track.IsSolo; NotifyEdited(); });
            AddItem(menu, track.IsLocked ? "Desbloquear pista" : "Bloquear pista",
                () => { track.IsLocked = !track.IsLocked; NotifyEdited(); });
            menu.Items.Add(new Separator());
            AddItem(menu, "Eliminar pista",
                () => Apply(new RemoveAudioTrackCommand(_sequence, track)), !track.IsLocked);
        }
    }

    private static void AddItem(ContextMenu menu, string header, Action action, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    // ------------------------------------------------------------------ edición

    /// <summary>
    /// Vuelve a dibujar y a medir tras un cambio hecho fuera del control.
    /// </summary>
    /// <remarks>
    /// El control no observa la secuencia: no hay notificación de cambios en el modelo, y
    /// añadirla obligaría a que el modelo conociera a quien lo dibuja. Quien edite por su
    /// cuenta —importar archivos, por ejemplo— avisa llamando aquí.
    /// </remarks>
    public void Refresh()
    {
        DropStaleSelection();
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>Divide el clip de video que se encuentra bajo el cabezal.</summary>
    /// <returns><see langword="true"/> si el corte se realizó.</returns>
    public bool SplitAtPlayhead()
    {
        if (_sequence is null)
        {
            return false;
        }

        // Con un clip de audio seleccionado el corte es suyo. Sin selección, o con uno de
        // video, se corta la pista de video como siempre: cortar música cada vez que se corta
        // la imagen sería lo contrario de lo que casi todo el mundo espera.
        if (_selectedAudio is not null && _selectedAudioTrack is not null)
        {
            var audioSplit = new SplitAudioClipCommand(_selectedAudioTrack, _playhead);
            Apply(audioSplit);
            return audioSplit.SecondHalf is not null;
        }

        var command = new SplitClipCommand(_sequence.Video, _playhead);
        Apply(command);
        return command.SecondHalf is not null;
    }

    /// <summary>Separa el audio del clip de video seleccionado.</summary>
    /// <returns><see langword="true"/> si había algo que separar.</returns>
    public bool DetachSelectedAudio()
    {
        if (_sequence is null || _selectedClip is null ||
            !_selectedClip.Source.HasAudio || _selectedClip.IsAudioDetached)
        {
            return false;
        }

        Apply(new DetachAudioCommand(_sequence, _selectedClip));
        return true;
    }

    /// <summary>Elimina el clip seleccionado, sea de video o de audio.</summary>
    /// <returns><see langword="true"/> si había algo seleccionado.</returns>
    public bool DeleteSelected()
    {
        if (_sequence is null)
        {
            return false;
        }

        if (_selectedOverlay is not null && _selectedOverlayTrack is { IsLocked: false })
        {
            Apply(new RemoveOverlayItemCommand(_selectedOverlayTrack, _selectedOverlay));
            ClearSelection();
            return true;
        }

        if (_selectedClip is not null)
        {
            Apply(new RemoveClipCommand(_sequence.Video, _selectedClip));
            ClearSelection();
            return true;
        }

        if (_selectedAudio is not null && _selectedAudioTrack is not null && !_selectedAudioTrack.IsLocked)
        {
            Apply(new RemoveAudioClipCommand(_selectedAudioTrack, _selectedAudio));
            ClearSelection();
            return true;
        }

        return false;
    }

    /// <summary>Indica si lo seleccionado admite ediciones de audio: no está en una pista bloqueada.</summary>
    public bool SelectionIsEditable =>
        _selectedClip is not null || (_selectedAudio is not null && _selectedAudioTrack is { IsLocked: false });

    /// <summary>Fija la ganancia, en dB, del clip seleccionado, sea de video o de audio.</summary>
    /// <returns><see langword="true"/> si había algo editable seleccionado.</returns>
    public bool SetSelectedGain(double gainDb)
    {
        if (!SelectionIsEditable)
        {
            return false;
        }

        if (_selectedClip is { } clip)
        {
            Apply(new SetClipAudioGainCommand(clip, gainDb));
            return true;
        }

        Apply(new SetAudioGainCommand(_selectedAudio!, gainDb));
        return true;
    }

    /// <summary>Silencia o restaura el sonido del clip seleccionado.</summary>
    public bool SetSelectedMuted(bool muted)
    {
        if (!SelectionIsEditable)
        {
            return false;
        }

        if (_selectedClip is { } clip)
        {
            Apply(new SetClipAudioMutedCommand(clip, muted));
            return true;
        }

        Apply(new SetAudioMutedCommand(_selectedAudio!, muted));
        return true;
    }

    /// <summary>Fija los fundidos del clip de audio seleccionado.</summary>
    /// <returns><see langword="false"/> si lo seleccionado no es un clip de audio editable.</returns>
    public bool SetSelectedFades(TimeSpan fadeIn, TimeSpan fadeOut)
    {
        if (_selectedAudio is null || _selectedAudioTrack is not { IsLocked: false })
        {
            return false;
        }

        Apply(new SetAudioFadeCommand(_selectedAudio, fadeIn, fadeOut));
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
            DropStaleSelection();
            NotifyEdited();
        }

        return changed;
    }

    private void NotifyEdited()
    {
        InvalidateMeasure();
        InvalidateVisual();
        TimelineEdited?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Suelta la selección si lo seleccionado ya no está en la secuencia.
    /// </summary>
    /// <remarks>
    /// Deshacer o rehacer puede eliminar un clip que sigue seleccionado, y dibujar su
    /// borde resaltado sobre una posición que ya no existe.
    /// </remarks>
    private void DropStaleSelection()
    {
        if (_sequence is null)
        {
            return;
        }

        if (_selectedClip is not null && _sequence.Video.IndexOf(_selectedClip) < 0)
        {
            _selectedClip = null;
        }

        DropStaleOverlaySelection();

        if (_selectedAudio is not null &&
            (_selectedAudioTrack is null ||
             _sequence.IndexOf(_selectedAudioTrack) < 0 ||
             !_selectedAudioTrack.Clips.Contains(_selectedAudio)))
        {
            _selectedAudio = null;
            _selectedAudioTrack = null;
        }
    }

    private void Select(Clip? clip, AudioClip? audio, AudioTrack? track)
    {
        if (ReferenceEquals(_selectedClip, clip) && ReferenceEquals(_selectedAudio, audio) && _selectedOverlay is null)
        {
            return;
        }

        _selectedOverlay = null;
        _selectedOverlayTrack = null;
        _selectedClip = clip;
        _selectedAudio = audio;
        _selectedAudioTrack = track;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private void ClearSelection() => Select(null, null, null);

    // -------------------------------------------------------------- hit testing

    private ClipHit HitTestVideo(Point point)
    {
        if (_sequence is null)
        {
            return new ClipHit(null, HitRegion.None);
        }

        // De atrás adelante: en el tramo que dos clips comparten por una transición, el que
        // se dibujó encima (el entrante, más adelante en la lista) es el que se ve, y debe ser
        // el que responde al clic. Es al revés de ClipAt(), que durante la reproducción
        // resuelve el saliente por simplicidad; aquí importa lo que el ojo ve.
        var layout = _sequence.Video.Layout();
        for (var i = layout.Count - 1; i >= 0; i--)
        {
            var clip = layout[i].Clip;
            var x = XOf(layout[i].Start);
            var clipWidth = (layout[i].End - layout[i].Start).TotalSeconds * _pixelsPerSecond;

            if (point.X < x || point.X > x + clipWidth)
            {
                continue;
            }

            // En un clip muy estrecho, dos zonas de recorte de seis píxeles no dejarían
            // sitio para agarrar el cuerpo. Por debajo de ese ancho, todo responde como cuerpo.
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

    private AudioClip? AudioClipAt(AudioTrack track, double x)
    {
        foreach (var clip in track.Clips)
        {
            var left = XOf(clip.TimelineStart);
            if (x >= left && x <= left + (clip.Duration.TotalSeconds * _pixelsPerSecond))
            {
                return clip;
            }
        }

        return null;
    }

    /// <summary>Índice de la pista de audio bajo una coordenada vertical, o -1.</summary>
    private int AudioLaneIndexAt(double y)
    {
        if (_sequence is null)
        {
            return -1;
        }

        for (var i = 0; i < _sequence.AudioTracks.Count; i++)
        {
            var top = AudioLaneTop(i);
            if (y >= top && y <= top + AudioLaneHeight)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Índice de la pista más cercana a una coordenada vertical, aunque caiga fuera.
    /// </summary>
    /// <remarks>
    /// Al arrastrar una pista el puntero pasa por los huecos entre filas y por encima y
    /// debajo de todas; devolver -1 ahí haría que el indicador de destino parpadeara.
    /// </remarks>
    private int AudioLaneIndexAtClamped(double y)
    {
        if (_sequence is null || _sequence.AudioTracks.Count == 0)
        {
            return 0;
        }

        var relative = y - AudioLaneTop(0);
        return (int)Math.Floor(relative / (AudioLaneHeight + LanePadding));
    }

    /// <summary>Índice de inserción de un clip de video para una coordenada horizontal.</summary>
    private int IndexAtX(double x)
    {
        if (_sequence is null)
        {
            return -1;
        }

        var start = TimeSpan.Zero;

        for (var i = 0; i < _sequence.Video.Clips.Count; i++)
        {
            var clip = _sequence.Video.Clips[i];
            var left = XOf(start);
            var clipWidth = clip.Duration.TotalSeconds * _pixelsPerSecond;

            // El punto medio decide el lado: soltar en la mitad izquierda inserta antes, en
            // la derecha después. Usar el borde haría que el indicador saltara con un píxel.
            if (x < left + (clipWidth / 2))
            {
                return i;
            }

            start += clip.Duration;
        }

        return _sequence.Video.Clips.Count;
    }

    private TimeSpan StartOfIndex(int index)
    {
        var start = TimeSpan.Zero;
        for (var i = 0; i < index && _sequence is not null && i < _sequence.Video.Clips.Count; i++)
        {
            start += _sequence.Video.Clips[i].Duration;
        }

        return start;
    }

    private enum DragKind { None, Playhead, VideoReorder, VideoTrimStart, VideoTrimEnd, VideoSlip, VideoRoll, VideoSlide, AudioMove, AudioTrimStart, AudioTrimEnd, OverlayMove, OverlayTrimStart, OverlayTrimEnd, TrackReorder }

    private enum HitRegion { None, Body, LeftEdge, RightEdge }

    private readonly record struct ClipHit(Clip? Clip, HitRegion Region);
}
