// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using EditFlow.Core.Timeline;

namespace EditFlow.App.Controls;

// Capas de superposición (textos e imágenes) de la timeline. Vive en su propio archivo porque
// el control principal ya es muy largo: aquí está todo lo específico de estas capas —geometría,
// dibujo, arrastre y menú— y el resto solo llama a estos métodos en los puntos de entrada.
public sealed partial class TimelineControl
{
    private const double OverlayLaneHeight = 40;

    private static readonly IBrush OverlayFill = new SolidColorBrush(Color.Parse("#6b4c9a"));
    private static readonly IBrush OverlayFillSelected = new SolidColorBrush(Color.Parse("#8a68bd"));
    private static readonly IBrush OverlayFillHidden = new SolidColorBrush(Color.Parse("#3a3a40"));
    private static readonly IBrush OverlayFillInvalid = new SolidColorBrush(Color.Parse("#8a3a3a"));
    private static readonly IBrush VideoOverlayFill = new SolidColorBrush(Color.Parse("#2f5f8f"));
    private static readonly IBrush VideoOverlayFillSelected = new SolidColorBrush(Color.Parse("#3f7cb8"));
    private static readonly IBrush SubFill = new SolidColorBrush(Color.Parse("#1f6b66"));
    private static readonly IBrush SubFillSelected = new SolidColorBrush(Color.Parse("#2b948d"));
    private static readonly IBrush OverlayStroke = new SolidColorBrush(Color.Parse("#a98bd6"));
    private static readonly IBrush ToggleHide = new SolidColorBrush(Color.Parse("#8a68bd"));

    private OverlayItem? _selectedOverlay;
    private OverlayTrack? _selectedOverlayTrack;

    private OverlayItem? _dragOverlay;
    private OverlayTrack? _dragOverlayTrack;
    private TimeSpan _overlayOrigin;
    private TimeSpan _overlayStart;
    private TimeSpan _overlayDuration;
    private bool _overlayValid = true;

    /// <summary>Texto o imagen superpuesto seleccionado, o <see langword="null"/>.</summary>
    public OverlayItem? SelectedOverlay => _selectedOverlay;

    /// <summary>Capa del elemento superpuesto seleccionado.</summary>
    public OverlayTrack? SelectedOverlayTrack => _selectedOverlayTrack;

    // --------------------------------------------------------------- geometría

    private int OverlayCount => _sequence?.OverlayTracks.Count ?? 0;

    /// <summary>Altura que ocupan todas las capas sobre el video; cero si no hay ninguna.</summary>
    private double OverlayBlockHeight => OverlayCount * (OverlayLaneHeight + LanePadding);

    private static double OverlayLaneTop(int index) =>
        RulerHeight + LanePadding + (index * (OverlayLaneHeight + LanePadding));

    private int OverlayLaneIndexAt(double y)
    {
        for (var i = 0; i < OverlayCount; i++)
        {
            var top = OverlayLaneTop(i);
            if (y >= top && y <= top + OverlayLaneHeight)
            {
                return i;
            }
        }

        return -1;
    }

    private OverlayItem? OverlayItemAt(OverlayTrack track, double x) =>
        track.Items.FirstOrDefault(item =>
        {
            var left = XOf(item.Start);
            return x >= left && x <= left + (item.Duration.TotalSeconds * _pixelsPerSecond);
        });

    private HitRegion OverlayEdgeAt(OverlayItem item, double x)
    {
        var left = XOf(item.Start);
        var width = item.Duration.TotalSeconds * _pixelsPerSecond;

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

    private static Rect OverlayToggleRect(double headerLeft, double laneTop, int index) =>
        new(headerLeft + HeaderWidth - 10 - ToggleSize - (index * (ToggleSize + 5)),
            laneTop + ((OverlayLaneHeight - ToggleSize) / 2),
            ToggleSize,
            ToggleSize);

    // ------------------------------------------------------------------ dibujo

    private void DrawOverlayLanes(DrawingContext context, double width)
    {
        if (_sequence is null)
        {
            return;
        }

        for (var i = 0; i < _sequence.OverlayTracks.Count; i++)
        {
            var track = _sequence.OverlayTracks[i];
            var top = OverlayLaneTop(i);

            context.FillRectangle(LaneBackground, new Rect(HeaderWidth, top, width - HeaderWidth, OverlayLaneHeight));

            foreach (var item in track.Items)
            {
                var moving = ReferenceEquals(item, _dragOverlay) && _drag is DragKind.OverlayMove or DragKind.OverlayTrimStart or DragKind.OverlayTrimEnd;
                var start = moving ? _overlayStart : item.Start;
                var length = moving ? _overlayDuration : item.Duration;

                var x = XOf(start);
                var itemWidth = Math.Max(length.TotalSeconds * _pixelsPerSecond, 2);
                if (x > width || x + itemWidth < 0)
                {
                    continue;
                }

                var selected = ReferenceEquals(item, _selectedOverlay);
                var rect = new Rect(x + 1, top + 2, Math.Max(itemWidth - 2, 1), OverlayLaneHeight - 4);

                var isVideo = item.Kind == OverlayKind.Video;
                var fill = moving && !_overlayValid ? OverlayFillInvalid
                    : track.IsHidden ? OverlayFillHidden
                    : track.IsSubtitles ? (selected ? SubFillSelected : SubFill)
                    : selected ? (isVideo ? VideoOverlayFillSelected : OverlayFillSelected)
                    : (isVideo ? VideoOverlayFill : OverlayFill);

                context.DrawRectangle(fill, new Pen(OverlayStroke, selected ? 2 : 1), rect, 4, 4);

                // Un video subido a una capa enseña sus fotogramas, como en la pista principal.
                if (isVideo && item.Media is { } media && !track.IsHidden)
                {
                    DrawFilmstrip(context, media.Path, media.AspectRatio, item.SourceIn, rect, shadeHeight: 18);
                }

                if (rect.Width >= 28)
                {
                    using var _ = context.PushClip(rect.Deflate(new Thickness(6, 2)));
                    var label = item.Kind switch
                    {
                        OverlayKind.Text => item.Text?.Content.ReplaceLineEndings(" ") ?? string.Empty,
                        OverlayKind.Video => System.IO.Path.GetFileName(item.Media?.Path) ?? string.Empty,
                        _ => System.IO.Path.GetFileName(item.ImagePath) ?? string.Empty,
                    };
                    var icon = item.Kind switch { OverlayKind.Text => "T  ", OverlayKind.Video => "▶  ", _ => "▣  " };
                    DrawText(context, icon + label, new Point(rect.X + 7, rect.Y + 6), 11, ClipText);
                }
            }
        }
    }

    private void DrawOverlayHeaders(DrawingContext context, double left)
    {
        if (_sequence is null)
        {
            return;
        }

        for (var i = 0; i < _sequence.OverlayTracks.Count; i++)
        {
            var track = _sequence.OverlayTracks[i];
            var top = OverlayLaneTop(i);

            if (track.IsLocked)
            {
                context.FillRectangle(HeaderLocked, new Rect(left, top, HeaderWidth, OverlayLaneHeight));
            }

            DrawText(context, track.Name, new Point(left + 10, top + 8), 12, track.IsHidden ? DimText : ClipText);
            DrawToggle(context, OverlayToggleRect(left, top, 1), "H", track.IsHidden, ToggleHide);
            DrawToggle(context, OverlayToggleRect(left, top, 0), "L", track.IsLocked, ToggleLock);
        }
    }

    // ------------------------------------------------------------- interacción

    private bool OverlayHeaderPressed(Point point)
    {
        var index = OverlayLaneIndexAt(point.Y);
        if (_sequence is null || index < 0)
        {
            return false;
        }

        var track = _sequence.OverlayTracks[index];
        var top = OverlayLaneTop(index);

        // Como en las pistas de audio, ocultar o bloquear no son ediciones del montaje y no
        // llenan el historial de deshacer.
        if (OverlayToggleRect(HeaderLeft, top, 1).Contains(point))
        {
            track.IsHidden = !track.IsHidden;
            NotifyEdited();
        }
        else if (OverlayToggleRect(HeaderLeft, top, 0).Contains(point))
        {
            track.IsLocked = !track.IsLocked;
            NotifyEdited();
        }

        return true;
    }

    private bool OverlayLanePressed(Point point, PointerPressedEventArgs e)
    {
        var index = OverlayLaneIndexAt(point.Y);
        if (_sequence is null || index < 0)
        {
            return false;
        }

        var track = _sequence.OverlayTracks[index];
        var item = OverlayItemAt(track, point.X);

        if (item is null)
        {
            ClearSelection();
            return true;
        }

        SelectOverlay(item, track);

        if (!track.IsLocked)
        {
            var region = OverlayEdgeAt(item, point.X);
            _drag = region switch
            {
                HitRegion.LeftEdge => DragKind.OverlayTrimStart,
                HitRegion.RightEdge => DragKind.OverlayTrimEnd,
                _ => DragKind.OverlayMove,
            };

            _dragOverlay = item;
            _dragOverlayTrack = track;
            _dragOriginX = point.X;
            _overlayOrigin = region switch
            {
                HitRegion.LeftEdge => item.Start,
                HitRegion.RightEdge => item.End,
                _ => item.Start,
            };
            _overlayStart = item.Start;
            _overlayDuration = item.Duration;
            _overlayValid = true;
            e.Pointer.Capture(this);
        }

        return true;
    }

    private bool IsOverlayDrag => _drag is DragKind.OverlayMove or DragKind.OverlayTrimStart or DragKind.OverlayTrimEnd;

    private bool OverlayDragMoved(Point point)
    {
        if (!IsOverlayDrag || _dragOverlay is null || _dragOverlayTrack is null || _sequence is null)
        {
            return false;
        }

        var requested = _overlayOrigin + TimeSpan.FromSeconds((point.X - _dragOriginX) / _pixelsPerSecond);
        var threshold = TimeSpan.FromSeconds(SnapDistance / _pixelsPerSecond);
        var points = Snapping.PointsForOverlay(_sequence, _playhead, _dragOverlay);

        if (_drag == DragKind.OverlayMove)
        {
            _overlayStart = Snapping.Snap(requested, _dragOverlay.Duration, points, threshold);
            _overlayDuration = _dragOverlay.Duration;
        }
        else
        {
            // Un borde suelto: se ajusta como un punto, sin duración, y de él salen inicio y duración.
            var edge = Snapping.Snap(requested, TimeSpan.Zero, points, threshold);

            if (_drag == DragKind.OverlayTrimStart)
            {
                _overlayStart = edge;
                _overlayDuration = _dragOverlay.End - edge;
            }
            else
            {
                _overlayStart = _dragOverlay.Start;
                _overlayDuration = edge - _dragOverlay.Start;
            }
        }

        _overlayValid = _dragOverlayTrack.CanPlace(_overlayStart, _overlayDuration, _dragOverlay);
        InvalidateVisual();
        return true;
    }

    private bool OverlayDragReleased()
    {
        if (!IsOverlayDrag)
        {
            return false;
        }

        if (_dragOverlay is not null && _dragOverlayTrack is not null && _overlayValid &&
            (_overlayStart != _dragOverlay.Start || _overlayDuration != _dragOverlay.Duration))
        {
            Apply(new PlaceOverlayItemCommand(_dragOverlayTrack, _dragOverlay, _overlayStart, _overlayDuration));
        }

        _drag = DragKind.None;
        _dragOverlay = null;
        _dragOverlayTrack = null;
        InvalidateVisual();
        return true;
    }

    private Cursor? OverlayCursorAt(Point point)
    {
        var index = OverlayLaneIndexAt(point.Y);
        if (_sequence is null || index < 0)
        {
            return null;
        }

        var track = _sequence.OverlayTracks[index];
        var item = OverlayItemAt(track, point.X);

        if (item is null || track.IsLocked)
        {
            return Cursor.Default;
        }

        return OverlayEdgeAt(item, point.X) is HitRegion.LeftEdge or HitRegion.RightEdge
            ? new Cursor(StandardCursorType.SizeWestEast)
            : new Cursor(StandardCursorType.SizeAll);
    }

    // ------------------------------------------------------------------ menú

    /// <summary>Rellena el menú de clic derecho si el punto cae en una capa de superposición.</summary>
    private bool BuildOverlayMenu(ContextMenu menu, Point point)
    {
        var index = OverlayLaneIndexAt(point.Y);
        if (_sequence is null || index < 0)
        {
            return false;
        }

        var track = _sequence.OverlayTracks[index];
        var item = point.X >= HeaderLeft + HeaderWidth ? OverlayItemAt(track, point.X) : null;

        if (item is not null)
        {
            SelectOverlay(item, track);
            AddItem(menu, "Eliminar   Supr", () => DeleteSelected(), !track.IsLocked);
            return true;
        }

        AddItem(menu, track.IsHidden ? "Mostrar capa" : "Ocultar capa",
            () => { track.IsHidden = !track.IsHidden; NotifyEdited(); });
        AddItem(menu, track.IsLocked ? "Desbloquear capa" : "Bloquear capa",
            () => { track.IsLocked = !track.IsLocked; NotifyEdited(); });
        menu.Items.Add(new Separator());
        AddItem(menu, "Eliminar capa", () => Apply(new RemoveOverlayTrackCommand(_sequence, track)), !track.IsLocked);
        return true;
    }

    // ---------------------------------------------------------------- selección

    private void SelectOverlay(OverlayItem item, OverlayTrack track)
    {
        if (ReferenceEquals(_selectedOverlay, item))
        {
            return;
        }

        _selectedClip = null;
        _selectedAudio = null;
        _selectedAudioTrack = null;
        _selectedOverlay = item;
        _selectedOverlayTrack = track;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private void DropStaleOverlaySelection()
    {
        if (_selectedOverlay is not null &&
            (_sequence is null ||
             _selectedOverlayTrack is null ||
             _sequence.IndexOf(_selectedOverlayTrack) < 0 ||
             !_selectedOverlayTrack.Items.Contains(_selectedOverlay)))
        {
            _selectedOverlay = null;
            _selectedOverlayTrack = null;
        }
    }

    // ------------------------------------------------------------ API pública

    /// <summary>Selecciona un elemento superpuesto, por ejemplo tras agarrarlo en el preview.</summary>
    /// <returns><see langword="false"/> si el elemento no está en ninguna capa.</returns>
    public bool SelectOverlayItem(OverlayItem item)
    {
        var track = _sequence?.OverlayTracks.FirstOrDefault(t => t.Items.Contains(item));
        if (track is null)
        {
            return false;
        }

        SelectOverlay(item, track);
        return true;
    }

    /// <summary>Añade un texto en el cabezal, en la primera capa con hueco o en una nueva.</summary>
    /// <param name="style">Estilo del texto.</param>
    /// <param name="duration">Cuánto tiempo se ve.</param>
    /// <returns>El elemento creado, ya seleccionado, o <see langword="null"/> si no se pudo.</returns>
    public OverlayItem? AddText(TextStyle style, TimeSpan duration, OverlayTransform? transform = null)
    {
        if (_sequence is null)
        {
            return null;
        }

        return AddOverlay(OverlayItem.CreateText(style, _playhead, duration, transform));
    }

    /// <summary>Añade una imagen en el cabezal, en la primera capa con hueco o en una nueva.</summary>
    public OverlayItem? AddImage(string path, double aspectRatio, TimeSpan duration)
    {
        if (_sequence is null)
        {
            return null;
        }

        return AddOverlay(OverlayItem.CreateImage(path, aspectRatio, _playhead, duration));
    }

    private OverlayItem? AddOverlay(OverlayItem item)
    {
        // Una capa nueva y su primer elemento son dos pasos del historial: deshacer quita
        // primero el elemento y deja la capa, que es lo que se espera al equivocarse de texto.
        // La capa «Sub» es solo para subtítulos: lo demás se organiza en las suyas.
        var track = _sequence!.OverlayTracks.FirstOrDefault(t => !t.IsLocked && !t.IsSubtitles && t.CanPlace(item.Start, item.Duration));

        if (track is null)
        {
            var create = new AddOverlayTrackCommand(_sequence);
            Apply(create);
            track = create.Result;
        }

        var add = new AddOverlayItemCommand(track!, item);
        Apply(add);

        if (!add.Added)
        {
            return null;
        }

        SelectOverlay(item, track!);
        return item;
    }

    /// <summary>
    /// Sube el clip seleccionado de la pista principal a una capa superior, dejando un hueco.
    /// </summary>
    /// <returns><see langword="false"/> si no hay un clip de video seleccionado.</returns>
    public bool LiftSelectedClip(OverlayTrack? preferred = null)
    {
        if (_sequence is null || _selectedClip is not { } clip || !LiftClipToLayerCommand.CanLift(clip))
        {
            return false;
        }

        var command = new LiftClipToLayerCommand(_sequence, clip, preferred);
        Apply(command);

        if (command.Item is { } item && command.Track is { } track)
        {
            SelectOverlay(item, track);
        }

        return true;
    }

    /// <summary>Añade un subtítulo en el cabezal, en la capa «Sub».</summary>
    /// <returns>El subtítulo creado y seleccionado, o <see langword="null"/> si ya hay otro en ese instante.</returns>
    public OverlayItem? AddSubtitleText(string text, TimeSpan duration)
    {
        if (_sequence is null)
        {
            return null;
        }

        var command = new AddSubtitlesCommand(_sequence, [new SubtitleCue(_playhead, _playhead + duration, text)]);
        Apply(command);

        if (command.First is { } item && command.Track is { } track)
        {
            SelectOverlay(item, track);
            return item;
        }

        return null;
    }

    /// <summary>Añade subtítulos como textos editables en la capa «Sub», en un solo paso del historial.</summary>
    /// <returns>Cuántos se colocaron, cuántos no cupieron y dónde empieza el primero.</returns>
    public SubtitleAddResult AddSubtitles(System.Collections.Generic.IEnumerable<SubtitleCue> cues)
    {
        if (_sequence is null)
        {
            return new SubtitleAddResult(0, 0, null);
        }

        var command = new AddSubtitlesCommand(_sequence, cues);
        Apply(command);
        return new SubtitleAddResult(command.Added, command.Skipped, command.First?.Start);
    }

    /// <summary>
    /// Cambia el color del clip de video (o del video superpuesto) seleccionado, como un paso del historial.
    /// </summary>
    /// <param name="color">Nuevo ajuste.</param>
    /// <param name="previous">Ajuste que había antes, si ya se mostraba de forma provisional.</param>
    public bool SetSelectedColor(EditFlow.Core.Timeline.ColorAdjust color, EditFlow.Core.Timeline.ColorAdjust? previous)
    {
        if (_selectedClip is { IsGap: false } clip && SelectionIsEditable)
        {
            Apply(new SetColorCommand(clip, color, previous));
            return true;
        }

        if (_selectedOverlay is { Kind: OverlayKind.Video } item && _selectedOverlayTrack is { IsLocked: false })
        {
            Apply(new SetColorCommand(item, color, previous));
            return true;
        }

        return false;
    }

    /// <summary>Pone un color provisional en el elemento seleccionado, sin historial, para verlo mientras se arrastra.</summary>
    public void SetSelectedColorProvisional(EditFlow.Core.Timeline.ColorAdjust color)
    {
        if (_selectedClip is { IsGap: false } clip)
        {
            clip.Color = color.Clamped();
        }
        else if (_selectedOverlay is { Kind: OverlayKind.Video } item)
        {
            item.Color = color.Clamped();
        }
    }

    /// <summary>
    /// Cambia la transición de entrada del clip de video seleccionado, como un paso del historial.
    /// </summary>
    /// <param name="transition">Nueva transición; <see cref="Transition.None"/> la quita.</param>
    /// <param name="previous">Transición que había antes, si ya se mostraba de forma provisional.</param>
    public bool SetSelectedTransition(Transition transition, Transition? previous = null)
    {
        if (_selectedClip is not { IsGap: false } clip || !SelectionIsEditable)
        {
            return false;
        }

        Apply(new SetTransitionCommand(clip, transition, previous));
        return true;
    }

    /// <summary>Pone una transición provisional en el clip seleccionado, sin historial, para verla mientras se arrastra.</summary>
    public void SetSelectedTransitionProvisional(Transition transition)
    {
        if (_selectedClip is { IsGap: false } clip)
        {
            clip.TransitionIn = transition;
            InvalidateVisual();
        }
    }

    /// <summary>
    /// Cambia la velocidad del clip de video seleccionado, como un paso del historial.
    /// </summary>
    /// <param name="speed">Nueva velocidad.</param>
    /// <param name="previous">Velocidad que había antes, si ya se mostraba de forma provisional.</param>
    public bool SetSelectedSpeed(double speed, double? previous = null)
    {
        if (_selectedClip is not { IsGap: false } clip || !SelectionIsEditable)
        {
            return false;
        }

        Apply(new SetSpeedCommand(clip, speed, previous));
        return true;
    }

    /// <summary>Pone una velocidad provisional en el clip seleccionado, sin historial, para verla mientras se arrastra.</summary>
    public void SetSelectedSpeedProvisional(double speed)
    {
        if (_selectedClip is { IsGap: false } clip)
        {
            clip.Speed = speed;
            Refresh();
        }
    }

    /// <summary>
    /// Cambia el encuadre (zoom, posición, rotación) del clip de video seleccionado, como un
    /// paso del historial.
    /// </summary>
    /// <param name="transform">Nuevo encuadre.</param>
    /// <param name="previous">Encuadre que había antes, si ya se mostraba de forma provisional.</param>
    public bool SetSelectedTransform(ClipTransform transform, ClipTransform? previous = null)
    {
        if (_selectedClip is not { IsGap: false } clip || !SelectionIsEditable)
        {
            return false;
        }

        Apply(new SetTransformCommand(clip, transform, previous));
        return true;
    }

    /// <summary>
    /// Cambia los fundidos de video (a negro) y de su propio audio (a silencio) del clip de
    /// video seleccionado, como un paso del historial.
    /// </summary>
    public bool SetSelectedClipFade(TimeSpan fadeIn, TimeSpan fadeOut)
    {
        if (_selectedClip is not { IsGap: false } clip || !SelectionIsEditable)
        {
            return false;
        }

        Apply(new SetClipFadeCommand(clip, fadeIn, fadeOut));
        return true;
    }

    /// <summary>Cambia el filtro visual del clip de video seleccionado, como un paso del historial.</summary>
    public bool SetSelectedFilter(VisualFilterKind filter)
    {
        if (_selectedClip is not { IsGap: false } clip || !SelectionIsEditable)
        {
            return false;
        }

        Apply(new SetFilterCommand(clip, filter));
        return true;
    }

    /// <summary>Cambia el efecto de estilo del clip de video seleccionado, como un paso del historial.</summary>
    public bool SetSelectedEffect(VisualEffectKind effect)
    {
        if (_selectedClip is not { IsGap: false } clip || !SelectionIsEditable)
        {
            return false;
        }

        Apply(new SetEffectCommand(clip, effect));
        return true;
    }

    /// <summary>Cambia el volumen o silencia el video superpuesto seleccionado.</summary>
    public bool SetSelectedOverlayAudio(bool playsAudio, double gainDb)
    {
        if (_selectedOverlay is not { Kind: OverlayKind.Video } item || _selectedOverlayTrack is not { IsLocked: false })
        {
            return false;
        }

        Apply(new SetOverlayAudioCommand(item, playsAudio, gainDb));
        return true;
    }

    /// <summary>Indica si el video superpuesto seleccionado cabe en la pista principal.</summary>
    public bool CanLowerSelectedOverlay() =>
        _sequence is not null
        && _selectedOverlay is { Kind: OverlayKind.Video } item
        && _selectedOverlayTrack is { IsLocked: false }
        && LowerOverlayToMainCommand.CanLower(_sequence, item);

    /// <summary>Baja el video superpuesto seleccionado a la pista principal.</summary>
    /// <returns><see langword="false"/> si no es un video, la capa está bloqueada o la pista principal no está libre bajo él.</returns>
    public bool LowerSelectedOverlay()
    {
        if (!CanLowerSelectedOverlay() || _selectedOverlay is not { } item || _selectedOverlayTrack is not { } track)
        {
            return false;
        }

        var command = new LowerOverlayToMainCommand(_sequence!, track, item);
        Apply(command);

        if (command.Result is { } clip)
        {
            Select(clip, null, null);
        }

        return true;
    }

    /// <summary>Cambia el aspecto del elemento superpuesto seleccionado.</summary>
    public bool SetSelectedOverlayLook(OverlayTransform transform, TextStyle? text = null)
    {
        if (_selectedOverlay is null || _selectedOverlayTrack is not { IsLocked: false })
        {
            return false;
        }

        Apply(new SetOverlayLookCommand(_selectedOverlay, transform, text));
        return true;
    }

    /// <summary>Cambia los fundidos de aparición y desaparición del elemento superpuesto seleccionado.</summary>
    public bool SetSelectedOverlayFade(TimeSpan fadeIn, TimeSpan fadeOut)
    {
        if (_selectedOverlay is null || _selectedOverlayTrack is not { IsLocked: false })
        {
            return false;
        }

        Apply(new SetOverlayFadeCommand(_selectedOverlay, fadeIn, fadeOut));
        return true;
    }

    /// <summary>Pone o mueve un punto de animación en un elemento de la secuencia.</summary>
    /// <param name="target">Clip, capa o audio que se anima.</param>
    /// <param name="property">Propiedad.</param>
    /// <param name="at">Instante dentro del elemento.</param>
    /// <param name="value">Valor en ese instante.</param>
    public void SetKeyframe(IAnimatable target, AnimatedProperty property, TimeSpan at, double value)
    {
        ArgumentNullException.ThrowIfNull(target);
        Apply(new SetKeyframeCommand(target, property, at, value));
    }

    /// <summary>Quita un punto de animación, o toda la animación de una propiedad.</summary>
    /// <param name="target">Clip, capa o audio.</param>
    /// <param name="property">Propiedad.</param>
    /// <param name="at">Instante del punto, o <see langword="null"/> para quitarla entera.</param>
    public void RemoveKeyframe(IAnimatable target, AnimatedProperty property, TimeSpan? at)
    {
        ArgumentNullException.ThrowIfNull(target);
        Apply(new RemoveKeyframeCommand(target, property, at));
    }

    /// <summary>Cambia la corrección de color avanzada del clip seleccionado.</summary>
    public bool SetSelectedGrade(ColorGrade grade, ColorGrade? previous = null)
    {
        if (_selectedClip is not { IsGap: false } clip)
        {
            return false;
        }

        Apply(new SetColorGradeCommand(clip, grade, previous));
        return true;
    }

    /// <summary>Cambia el recorte por color del video superpuesto seleccionado.</summary>
    public bool SetSelectedChromaKey(ChromaKey key)
    {
        if (_selectedOverlay is not { Kind: OverlayKind.Video } item || _selectedOverlayTrack is not { IsLocked: false })
        {
            return false;
        }

        Apply(new SetChromaKeyCommand(item, key));
        return true;
    }

    /// <summary>Cambia la posición y duración del elemento superpuesto seleccionado.</summary>
    public bool SetSelectedOverlayPlacement(TimeSpan start, TimeSpan duration)
    {
        if (_selectedOverlay is null || _selectedOverlayTrack is not { IsLocked: false })
        {
            return false;
        }

        var command = new PlaceOverlayItemCommand(_selectedOverlayTrack, _selectedOverlay, start, duration);
        Apply(command);
        return command.Applied;
    }
}
