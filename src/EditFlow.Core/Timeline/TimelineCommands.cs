// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Undo;

namespace EditFlow.Core.Timeline;

/// <summary>Añade un clip al final de la secuencia.</summary>
public sealed class AppendClipCommand : IUndoableCommand
{
    private readonly VideoTimeline _timeline;
    private readonly Clip _clip;

    /// <summary>Crea la operación.</summary>
    public AppendClipCommand(VideoTimeline timeline, Clip clip)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(clip);

        _timeline = timeline;
        _clip = clip;
    }

    /// <inheritdoc/>
    public string Description => "Añadir clip";

    /// <inheritdoc/>
    public void Execute() => _timeline.Append(_clip);

    /// <inheritdoc/>
    public void Undo() => _timeline.Remove(_clip);
}

/// <summary>Elimina un clip; los siguientes se desplazan para cerrar el hueco.</summary>
public sealed class RemoveClipCommand : IUndoableCommand
{
    private readonly VideoTimeline _timeline;
    private readonly Clip _clip;
    private int _index = -1;
    private Clip? _nextClip;
    private Transition _nextTransition = Transition.None;

    /// <summary>Crea la operación.</summary>
    public RemoveClipCommand(VideoTimeline timeline, Clip clip)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(clip);

        _timeline = timeline;
        _clip = clip;
    }

    /// <inheritdoc/>
    public string Description => "Eliminar clip";

    /// <inheritdoc/>
    public void Execute()
    {
        // La posición se guarda al ejecutar, no al construir: entre la creación de la
        // operación y su ejecución pueden haber ocurrido otras, y el índice habría
        // cambiado. Deshacer devolvería el clip a un sitio equivocado.
        _index = _timeline.IndexOf(_clip);

        // El clip que quedaba justo después pasa a tener un vecino anterior distinto: su
        // transición de entrada, pensada para el clip que se va, ya no tiene sentido.
        _nextClip = _index >= 0 && _index + 1 < _timeline.Clips.Count ? _timeline.Clips[_index + 1] : null;
        if (_nextClip is not null)
        {
            _nextTransition = _nextClip.TransitionIn;
            _nextClip.TransitionIn = Transition.None;
        }

        _timeline.Remove(_clip);
    }

    /// <inheritdoc/>
    public void Undo()
    {
        if (_index >= 0)
        {
            _timeline.Insert(Math.Min(_index, _timeline.Clips.Count), _clip);
        }

        if (_nextClip is not null)
        {
            _nextClip.TransitionIn = _nextTransition;
        }
    }
}

/// <summary>Cambia un clip de posición dentro de la secuencia.</summary>
public sealed class MoveClipCommand : IUndoableCommand
{
    private readonly VideoTimeline _timeline;
    private readonly Clip _clip;
    private readonly int _targetIndex;
    private int _originalIndex = -1;
    private readonly List<(Clip Clip, Transition Was)> _clearedTransitions = [];

    /// <summary>Crea la operación.</summary>
    public MoveClipCommand(VideoTimeline timeline, Clip clip, int targetIndex)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(clip);

        _timeline = timeline;
        _clip = clip;
        _targetIndex = targetIndex;
    }

    /// <inheritdoc/>
    public string Description => "Mover clip";

    /// <inheritdoc/>
    public void Execute()
    {
        _originalIndex = _timeline.IndexOf(_clip);
        if (_originalIndex < 0)
        {
            return;
        }

        var oldNext = _originalIndex + 1 < _timeline.Clips.Count ? _timeline.Clips[_originalIndex + 1] : null;

        _timeline.Move(_clip, _targetIndex);

        var newIndex = _timeline.IndexOf(_clip);
        var newNext = newIndex >= 0 && newIndex + 1 < _timeline.Clips.Count ? _timeline.Clips[newIndex + 1] : null;

        // El clip movido tiene un vecino anterior distinto, y quien lo seguía antes o lo sigue
        // ahora también cambió de vecino: sus transiciones de entrada ya no describen a quien
        // tienen delante y se quitan, en vez de reaplicarse a un clip distinto sin que se note.
        _clearedTransitions.Clear();
        Clear(_clip);

        if (oldNext is not null && !ReferenceEquals(oldNext, newNext))
        {
            Clear(oldNext);
        }

        if (newNext is not null && !ReferenceEquals(newNext, oldNext))
        {
            Clear(newNext);
        }
    }

    private void Clear(Clip clip)
    {
        if (clip.TransitionIn.IsNone)
        {
            return;
        }

        _clearedTransitions.Add((clip, clip.TransitionIn));
        clip.TransitionIn = Transition.None;
    }

    /// <inheritdoc/>
    public void Undo()
    {
        if (_originalIndex >= 0)
        {
            _timeline.Move(_clip, _originalIndex);
        }

        foreach (var (clip, was) in _clearedTransitions)
        {
            clip.TransitionIn = was;
        }
    }
}

/// <summary>Divide en dos el clip que se reproduce en un instante dado.</summary>
public sealed class SplitClipCommand : IUndoableCommand
{
    private readonly VideoTimeline _timeline;
    private readonly TimeSpan _position;
    private Clip? _firstHalf;
    private Clip? _secondHalf;
    private TimeSpan _originalSourceOut;

    /// <summary>Crea la operación.</summary>
    public SplitClipCommand(VideoTimeline timeline, TimeSpan position)
    {
        ArgumentNullException.ThrowIfNull(timeline);

        _timeline = timeline;
        _position = position;
    }

    /// <inheritdoc/>
    public string Description => "Dividir clip";

    /// <summary>Segunda mitad resultante, o <see langword="null"/> si el corte se rechazó.</summary>
    public Clip? SecondHalf => _secondHalf;

    /// <inheritdoc/>
    public void Execute()
    {
        var located = _timeline.ClipAt(_position);
        if (located is null)
        {
            return;
        }

        _firstHalf = located.Value.Clip;
        _originalSourceOut = _firstHalf.SourceOut;
        _secondHalf = _timeline.SplitAt(_position);

        if (_secondHalf is null)
        {
            // El corte se rechazó por caer demasiado cerca de un borde. No queda nada
            // que deshacer, y guardar la referencia al primer clip haría que Undo
            // recortara un clip que nunca se tocó.
            _firstHalf = null;
        }
    }

    /// <inheritdoc/>
    public void Undo()
    {
        if (_firstHalf is null || _secondHalf is null)
        {
            return;
        }

        _timeline.Remove(_secondHalf);
        _firstHalf.RestoreRange(_firstHalf.SourceIn, _originalSourceOut);
    }
}

/// <summary>Borde de un clip sobre el que se aplica un recorte.</summary>
public enum ClipEdge
{
    /// <summary>Borde de entrada.</summary>
    Start,

    /// <summary>Borde de salida.</summary>
    End,
}

/// <summary>Ajusta uno de los bordes de un clip.</summary>
public sealed class TrimClipCommand : IUndoableCommand
{
    private readonly Clip _clip;
    private readonly ClipEdge _edge;
    private readonly TimeSpan _delta;
    private TimeSpan _originalIn;
    private TimeSpan _originalOut;

    /// <summary>Crea la operación.</summary>
    public TrimClipCommand(Clip clip, ClipEdge edge, TimeSpan delta)
    {
        ArgumentNullException.ThrowIfNull(clip);

        _clip = clip;
        _edge = edge;
        _delta = delta;
    }

    /// <inheritdoc/>
    public string Description => _edge == ClipEdge.Start ? "Recortar inicio" : "Recortar final";

    /// <inheritdoc/>
    public void Execute()
    {
        // Se guarda el intervalo completo, no el desplazamiento pedido: el recorte se
        // acota al material disponible, así que lo aplicado puede ser menor que lo
        // solicitado. Deshacer con el desplazamiento original desplazaría de más.
        _originalIn = _clip.SourceIn;
        _originalOut = _clip.SourceOut;

        if (_edge == ClipEdge.Start)
        {
            _clip.TrimStart(_delta);
        }
        else
        {
            _clip.TrimEnd(_delta);
        }
    }

    /// <inheritdoc/>
    public void Undo() => _clip.RestoreRange(_originalIn, _originalOut);
}
