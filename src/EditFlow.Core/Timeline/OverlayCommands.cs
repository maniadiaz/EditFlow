// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Undo;

namespace EditFlow.Core.Timeline;

/// <summary>Añade una capa de superposición encima de las demás.</summary>
public sealed class AddOverlayTrackCommand : IUndoableCommand
{
    private readonly EditSequence _sequence;
    private OverlayTrack? _track;

    /// <summary>Crea la operación.</summary>
    public AddOverlayTrackCommand(EditSequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        _sequence = sequence;
    }

    /// <inheritdoc/>
    public string Description => "Añadir capa";

    /// <summary>Capa creada.</summary>
    public OverlayTrack? Result => _track;

    /// <inheritdoc/>
    public void Execute()
    {
        // Al rehacer se reinserta la misma capa, no una nueva: los elementos añadidos
        // después apuntan a ella.
        if (_track is null)
        {
            _track = _sequence.AddOverlayTrack();
        }
        else
        {
            _sequence.InsertOverlayTrack(0, _track);
        }
    }

    /// <inheritdoc/>
    public void Undo()
    {
        if (_track is not null)
        {
            _sequence.RemoveOverlayTrack(_track);
        }
    }
}

/// <summary>Elimina una capa de superposición con todo lo que tiene.</summary>
public sealed class RemoveOverlayTrackCommand : IUndoableCommand
{
    private readonly EditSequence _sequence;
    private readonly OverlayTrack _track;
    private int _index = -1;

    /// <summary>Crea la operación.</summary>
    public RemoveOverlayTrackCommand(EditSequence sequence, OverlayTrack track)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(track);
        _sequence = sequence;
        _track = track;
    }

    /// <inheritdoc/>
    public string Description => "Eliminar capa";

    /// <inheritdoc/>
    public void Execute()
    {
        _index = _sequence.IndexOf(_track);
        if (_index >= 0)
        {
            _sequence.RemoveOverlayTrack(_track);
        }
    }

    /// <inheritdoc/>
    public void Undo()
    {
        if (_index >= 0)
        {
            _sequence.InsertOverlayTrack(Math.Min(_index, _sequence.OverlayTracks.Count), _track);
        }
    }
}

/// <summary>Añade un texto o una imagen a una capa.</summary>
public sealed class AddOverlayItemCommand : IUndoableCommand
{
    private readonly OverlayTrack _track;
    private readonly OverlayItem _item;
    private bool _added;

    /// <summary>Crea la operación.</summary>
    public AddOverlayItemCommand(OverlayTrack track, OverlayItem item)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(item);
        _track = track;
        _item = item;
    }

    /// <inheritdoc/>
    public string Description => _item.Kind == OverlayKind.Text ? "Añadir texto" : "Añadir imagen";

    /// <summary>Indica si se llegó a añadir.</summary>
    public bool Added => _added;

    /// <inheritdoc/>
    public void Execute() => _added = _track.TryAdd(_item);

    /// <inheritdoc/>
    public void Undo()
    {
        if (_added)
        {
            _track.Remove(_item);
        }
    }
}

/// <summary>Elimina un texto o una imagen de su capa.</summary>
public sealed class RemoveOverlayItemCommand : IUndoableCommand
{
    private readonly OverlayTrack _track;
    private readonly OverlayItem _item;
    private bool _removed;

    /// <summary>Crea la operación.</summary>
    public RemoveOverlayItemCommand(OverlayTrack track, OverlayItem item)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(item);
        _track = track;
        _item = item;
    }

    /// <inheritdoc/>
    public string Description => "Eliminar superposición";

    /// <inheritdoc/>
    public void Execute() => _removed = _track.Remove(_item);

    /// <inheritdoc/>
    public void Undo()
    {
        if (_removed)
        {
            _track.TryAdd(_item);
        }
    }
}

/// <summary>Cambia la posición y la duración de un elemento: mover, recortar o alargar.</summary>
public sealed class PlaceOverlayItemCommand : IUndoableCommand
{
    private readonly OverlayTrack _track;
    private readonly OverlayItem _item;
    private readonly TimeSpan _start;
    private readonly TimeSpan _duration;
    private TimeSpan _originalStart, _originalDuration;
    private bool _applied;

    /// <summary>Crea la operación.</summary>
    public PlaceOverlayItemCommand(OverlayTrack track, OverlayItem item, TimeSpan start, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(item);
        _track = track;
        _item = item;
        _start = start;
        _duration = duration;
    }

    /// <inheritdoc/>
    public string Description => "Colocar superposición";

    /// <summary>Indica si el cambio se llegó a aplicar.</summary>
    public bool Applied => _applied;

    /// <inheritdoc/>
    public void Execute()
    {
        _originalStart = _item.Start;
        _originalDuration = _item.Duration;
        _applied = _track.TryPlace(_item, _start, _duration);
    }

    /// <inheritdoc/>
    public void Undo()
    {
        // Solo se deshace lo que ocurrió: un cambio rechazado por chocar con otro elemento
        // no movió nada, y "deshacerlo" lo movería desde donde nunca se fue.
        if (_applied)
        {
            _track.Reinsert(_item, _originalStart, _originalDuration);
        }
    }
}

/// <summary>Cambia el aspecto de un elemento: su texto, posición, tamaño o transparencia.</summary>
public sealed class SetOverlayLookCommand : IUndoableCommand
{
    private readonly OverlayItem _item;
    private readonly OverlayTransform _transform;
    private readonly TextStyle? _text;
    private OverlayTransform _previousTransform = new();
    private TextStyle? _previousText;

    /// <summary>Crea la operación.</summary>
    /// <param name="item">Elemento a cambiar.</param>
    /// <param name="transform">Nueva posición, tamaño y transparencia; se ajustan a su rango.</param>
    /// <param name="text">Nuevo estilo de texto; se ignora en una imagen.</param>
    public SetOverlayLookCommand(OverlayItem item, OverlayTransform transform, TextStyle? text = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(transform);
        _item = item;
        _transform = transform.Clamped();
        _text = text is null ? null : text with { Size = Math.Clamp(text.Size, TextStyle.MinimumSize, TextStyle.MaximumSize) };
    }

    /// <inheritdoc/>
    public string Description => "Cambiar aspecto";

    /// <inheritdoc/>
    public void Execute()
    {
        _previousTransform = _item.Transform;
        _previousText = _item.Text;

        _item.Transform = _transform;
        if (_text is not null && _item.Kind == OverlayKind.Text)
        {
            _item.Text = _text;
        }
    }

    /// <inheritdoc/>
    public void Undo()
    {
        _item.Transform = _previousTransform;
        _item.Text = _previousText;
    }
}
