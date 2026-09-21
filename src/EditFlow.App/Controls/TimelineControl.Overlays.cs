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
    private const double OverlayLaneHeight = 32;

    private static readonly IBrush OverlayFill = new SolidColorBrush(Color.Parse("#6b4c9a"));
    private static readonly IBrush OverlayFillSelected = new SolidColorBrush(Color.Parse("#8a68bd"));
    private static readonly IBrush OverlayFillHidden = new SolidColorBrush(Color.Parse("#3a3a40"));
    private static readonly IBrush OverlayFillInvalid = new SolidColorBrush(Color.Parse("#8a3a3a"));
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

                var fill = moving && !_overlayValid ? OverlayFillInvalid
                    : track.IsHidden ? OverlayFillHidden
                    : selected ? OverlayFillSelected
                    : OverlayFill;

                context.DrawRectangle(fill, new Pen(OverlayStroke, selected ? 2 : 1), rect, 4, 4);

                if (rect.Width >= 28)
                {
                    using var _ = context.PushClip(rect.Deflate(new Thickness(6, 2)));
                    var label = item.Kind == OverlayKind.Text
                        ? item.Text?.Content.ReplaceLineEndings(" ") ?? string.Empty
                        : System.IO.Path.GetFileName(item.ImagePath) ?? string.Empty;
                    DrawText(context, (item.Kind == OverlayKind.Text ? "T  " : "▣  ") + label,
                        new Point(rect.X + 7, rect.Y + 6), 11, ClipText);
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
        var track = _sequence!.OverlayTracks.FirstOrDefault(t => !t.IsLocked && t.CanPlace(item.Start, item.Duration));

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
