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

/// <summary>Añade una pista de audio vacía.</summary>
public sealed class AddAudioTrackCommand : IUndoableCommand
{
    private readonly EditSequence _sequence;
    private AudioTrack? _track;

    /// <summary>Crea la operación.</summary>
    public AddAudioTrackCommand(EditSequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        _sequence = sequence;
    }

    /// <inheritdoc/>
    public string Description => "Añadir pista de audio";

    /// <summary>Pista creada.</summary>
    public AudioTrack? Result => _track;

    /// <inheritdoc/>
    public void Execute()
    {
        // Al rehacer se reinserta la misma pista, no una nueva: los clips que se hayan
        // añadido después apuntan a ella.
        if (_track is null)
        {
            _track = _sequence.AddAudioTrack();
        }
        else
        {
            _sequence.InsertAudioTrack(_sequence.AudioTracks.Count, _track);
        }
    }

    /// <inheritdoc/>
    public void Undo()
    {
        if (_track is not null)
        {
            _sequence.RemoveAudioTrack(_track);
        }
    }
}

/// <summary>Elimina una pista de audio con todos sus clips.</summary>
public sealed class RemoveAudioTrackCommand : IUndoableCommand
{
    private readonly EditSequence _sequence;
    private readonly AudioTrack _track;
    private int _index = -1;

    /// <summary>Crea la operación.</summary>
    public RemoveAudioTrackCommand(EditSequence sequence, AudioTrack track)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(track);

        _sequence = sequence;
        _track = track;
    }

    /// <inheritdoc/>
    public string Description => "Eliminar pista de audio";

    /// <inheritdoc/>
    public void Execute()
    {
        _index = _sequence.IndexOf(_track);
        _sequence.RemoveAudioTrack(_track);
    }

    /// <inheritdoc/>
    public void Undo()
    {
        // La pista conserva sus clips mientras está fuera de la secuencia, así que
        // reinsertarla los devuelve todos sin tener que reconstruirlos.
        if (_index >= 0)
        {
            _sequence.InsertAudioTrack(Math.Min(_index, _sequence.AudioTracks.Count), _track);
        }
    }
}

/// <summary>Añade un clip de audio a una pista, por ejemplo al importar música.</summary>
public sealed class AddAudioClipCommand : IUndoableCommand
{
    private readonly AudioTrack _track;
    private readonly AudioClip _clip;
    private bool _added;

    /// <summary>Crea la operación.</summary>
    public AddAudioClipCommand(AudioTrack track, AudioClip clip)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(clip);

        _track = track;
        _clip = clip;
    }

    /// <inheritdoc/>
    public string Description => "Añadir audio";

    /// <summary>Indica si el clip llegó a añadirse.</summary>
    public bool Added => _added;

    /// <inheritdoc/>
    public void Execute() => _added = _track.TryAdd(_clip);

    /// <inheritdoc/>
    public void Undo()
    {
        if (_added)
        {
            _track.Remove(_clip);
        }
    }
}

/// <summary>Elimina un clip de audio de su pista.</summary>
public sealed class RemoveAudioClipCommand : IUndoableCommand
{
    private readonly AudioTrack _track;
    private readonly AudioClip _clip;
    private bool _removed;

    /// <summary>Crea la operación.</summary>
    public RemoveAudioClipCommand(AudioTrack track, AudioClip clip)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(clip);

        _track = track;
        _clip = clip;
    }

    /// <inheritdoc/>
    public string Description => "Eliminar audio";

    /// <inheritdoc/>
    public void Execute() => _removed = _track.Remove(_clip);

    /// <inheritdoc/>
    public void Undo()
    {
        if (_removed)
        {
            _track.TryAdd(_clip);
        }
    }
}

/// <summary>Silencia o reactiva un clip de audio.</summary>
public sealed class SetAudioMutedCommand : IUndoableCommand
{
    private readonly AudioClip _clip;
    private readonly bool _muted;
    private bool _previous;

    /// <summary>Crea la operación.</summary>
    public SetAudioMutedCommand(AudioClip clip, bool muted)
    {
        ArgumentNullException.ThrowIfNull(clip);

        _clip = clip;
        _muted = muted;
    }

    /// <inheritdoc/>
    public string Description => _muted ? "Silenciar audio" : "Activar audio";

    /// <inheritdoc/>
    public void Execute()
    {
        _previous = _clip.IsMuted;
        _clip.IsMuted = _muted;
    }

    /// <inheritdoc/>
    public void Undo() => _clip.IsMuted = _previous;
}

/// <summary>Cambia los fundidos de entrada y salida de un clip de audio.</summary>
public sealed class SetAudioFadeCommand : IUndoableCommand
{
    private readonly AudioClip _clip;
    private readonly TimeSpan _fadeIn;
    private readonly TimeSpan _fadeOut;
    private TimeSpan _previousIn;
    private TimeSpan _previousOut;

    /// <summary>Crea la operación.</summary>
    public SetAudioFadeCommand(AudioClip clip, TimeSpan fadeIn, TimeSpan fadeOut)
    {
        ArgumentNullException.ThrowIfNull(clip);

        _clip = clip;
        _fadeIn = fadeIn;
        _fadeOut = fadeOut;
    }

    /// <inheritdoc/>
    public string Description => "Cambiar fundidos";

    /// <inheritdoc/>
    public void Execute()
    {
        _previousIn = _clip.FadeIn;
        _previousOut = _clip.FadeOut;

        // Se anulan ambos antes de fijar los nuevos: cada fundido se acota contra el
        // otro, así que asignarlos de uno en uno con los antiguos aún puestos los
        // recortaría por un valor que ya no debería contar.
        _clip.FadeIn = TimeSpan.Zero;
        _clip.FadeOut = TimeSpan.Zero;
        _clip.FadeIn = _fadeIn;
        _clip.FadeOut = _fadeOut;
    }

    /// <inheritdoc/>
    public void Undo()
    {
        _clip.FadeIn = TimeSpan.Zero;
        _clip.FadeOut = TimeSpan.Zero;
        _clip.FadeIn = _previousIn;
        _clip.FadeOut = _previousOut;
    }
}

/// <summary>Cambia el volumen del propio audio de un clip de video.</summary>
public sealed class SetClipAudioGainCommand : IUndoableCommand
{
    private readonly Clip _clip;
    private readonly double _gainDb;
    private double _previous;

    /// <summary>Crea la operación.</summary>
    public SetClipAudioGainCommand(Clip clip, double gainDb)
    {
        ArgumentNullException.ThrowIfNull(clip);

        _clip = clip;
        _gainDb = gainDb;
    }

    /// <inheritdoc/>
    public string Description => "Ajustar volumen del clip";

    /// <inheritdoc/>
    public void Execute()
    {
        _previous = _clip.AudioGainDb;
        _clip.AudioGainDb = _gainDb;
    }

    /// <inheritdoc/>
    public void Undo() => _clip.AudioGainDb = _previous;
}

/// <summary>Silencia o reactiva el propio audio de un clip de video.</summary>
public sealed class SetClipAudioMutedCommand : IUndoableCommand
{
    private readonly Clip _clip;
    private readonly bool _muted;
    private bool _previous;

    /// <summary>Crea la operación.</summary>
    public SetClipAudioMutedCommand(Clip clip, bool muted)
    {
        ArgumentNullException.ThrowIfNull(clip);

        _clip = clip;
        _muted = muted;
    }

    /// <inheritdoc/>
    public string Description => _muted ? "Silenciar clip" : "Activar sonido del clip";

    /// <inheritdoc/>
    public void Execute()
    {
        _previous = _clip.IsAudioMuted;
        _clip.IsAudioMuted = _muted;
    }

    /// <inheritdoc/>
    public void Undo() => _clip.IsAudioMuted = _previous;
}
