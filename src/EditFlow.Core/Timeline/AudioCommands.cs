// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Undo;

namespace EditFlow.Core.Timeline;

/// <summary>Separa el audio de un clip de video y lo coloca en una pista de audio.</summary>
public sealed class DetachAudioCommand : IUndoableCommand
{
    private readonly EditSequence _sequence;
    private readonly Clip _clip;
    private AudioTrack? _track;
    private AudioClip? _audio;
    private bool _createdTrack;

    /// <summary>Crea la operación.</summary>
    public DetachAudioCommand(EditSequence sequence, Clip clip)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(clip);

        _sequence = sequence;
        _clip = clip;
    }

    /// <inheritdoc/>
    public string Description => "Separar audio";

    /// <summary>Clip de audio resultante, o <see langword="null"/> si no se pudo separar.</summary>
    public AudioClip? Result => _audio;

    /// <inheritdoc/>
    public void Execute()
    {
        // Un clip mudo o con el audio ya separado no tiene nada que separar. Salir sin
        // tocar nada evita crear una pista vacía y dejar el clip marcado por error.
        if (!_clip.Source.HasAudio || _clip.IsAudioDetached || _sequence.Video.IndexOf(_clip) < 0)
        {
            return;
        }

        var start = _sequence.Video.StartOf(_clip);
        var audio = new AudioClip(_clip.Source, _clip.SourceIn, _clip.SourceOut, start);

        var before = _sequence.AudioTracks.Count;
        var track = _sequence.FindOrCreateTrackFor(start, audio.Duration);
        _createdTrack = _sequence.AudioTracks.Count > before;

        if (!track.TryAdd(audio))
        {
            if (_createdTrack)
            {
                _sequence.RemoveAudioTrack(track);
            }

            return;
        }

        _clip.IsAudioDetached = true;
        _track = track;
        _audio = audio;
    }

    /// <inheritdoc/>
    public void Undo()
    {
        if (_track is null || _audio is null)
        {
            return;
        }

        _track.Remove(_audio);
        if (_createdTrack)
        {
            // La pista solo se elimina si la creó esta operación: borrar una que ya
            // existía se llevaría clips ajenos.
            _sequence.RemoveAudioTrack(_track);
        }

        _clip.IsAudioDetached = false;
    }
}

/// <summary>Cambia el orden de una pista de audio dentro de la secuencia.</summary>
public sealed class MoveAudioTrackCommand : IUndoableCommand
{
    private readonly EditSequence _sequence;
    private readonly AudioTrack _track;
    private readonly int _targetIndex;
    private int _originalIndex = -1;

    /// <summary>Crea la operación.</summary>
    public MoveAudioTrackCommand(EditSequence sequence, AudioTrack track, int targetIndex)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(track);

        _sequence = sequence;
        _track = track;
        _targetIndex = targetIndex;
    }

    /// <inheritdoc/>
    public string Description => "Mover pista";

    /// <inheritdoc/>
    public void Execute()
    {
        // Se guarda al ejecutar, no al construir: entre ambos momentos pueden haber
        // cambiado las posiciones y deshacer devolvería la pista a un sitio equivocado.
        _originalIndex = _sequence.IndexOf(_track);
        _sequence.MoveAudioTrack(_track, _targetIndex);
    }

    /// <inheritdoc/>
    public void Undo()
    {
        if (_originalIndex >= 0)
        {
            _sequence.MoveAudioTrack(_track, _originalIndex);
        }
    }
}

/// <summary>Cambia el volumen de un clip de audio, con la opción de silenciarlo.</summary>
public sealed class SetAudioGainCommand : IUndoableCommand
{
    private readonly AudioClip _clip;
    private readonly double _gainDb;
    private double _previousGainDb;

    /// <summary>Crea la operación.</summary>
    public SetAudioGainCommand(AudioClip clip, double gainDb)
    {
        ArgumentNullException.ThrowIfNull(clip);

        _clip = clip;
        _gainDb = gainDb;
    }

    /// <inheritdoc/>
    public string Description => "Ajustar volumen";

    /// <inheritdoc/>
    public void Execute()
    {
        _previousGainDb = _clip.GainDb;
        _clip.GainDb = _gainDb;
    }

    /// <inheritdoc/>
    public void Undo() => _clip.GainDb = _previousGainDb;
}

/// <summary>Mueve un clip de audio a otra posición de su pista.</summary>
public sealed class MoveAudioClipCommand : IUndoableCommand
{
    private readonly AudioTrack _track;
    private readonly AudioClip _clip;
    private readonly TimeSpan _newStart;
    private TimeSpan _originalStart;
    private bool _moved;

    /// <summary>Crea la operación.</summary>
    public MoveAudioClipCommand(AudioTrack track, AudioClip clip, TimeSpan newStart)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(clip);

        _track = track;
        _clip = clip;
        _newStart = newStart;
    }

    /// <inheritdoc/>
    public string Description => "Mover clip de audio";

    /// <summary>Indica si el movimiento se llegó a realizar.</summary>
    public bool Moved => _moved;

    /// <inheritdoc/>
    public void Execute()
    {
        _originalStart = _clip.TimelineStart;
        _moved = _track.TryMove(_clip, _newStart);
    }

    /// <inheritdoc/>
    public void Undo()
    {
        // Solo se deshace lo que ocurrió: un movimiento rechazado por chocar con otro clip
        // no cambió nada, y "deshacerlo" movería el clip desde donde nunca se fue.
        if (_moved)
        {
            _track.TryMove(_clip, _originalStart);
        }
    }
}
