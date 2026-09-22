// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Undo;

namespace EditFlow.Core.Timeline;

/// <summary>Efecto de sonido, de un clic, sobre el propio audio de un clip.</summary>
/// <remarks>
/// Mismo espíritu que <see cref="VisualEffectKind"/> pero para el sonido: un tratamiento ya
/// hecho, sin ecualizador paramétrico ni mezclador multibanda que ajustar a mano. Cubre lo
/// cotidiano; un mezclador completo con automatización por keyframes es un paso posterior.
/// </remarks>
public enum AudioEffectKind
{
    /// <summary>Sin efecto: el audio tal cual.</summary>
    None,

    /// <summary>Realza la voz y recorta los graves que solo ensucian, para diálogo y locución.</summary>
    Voice,

    /// <summary>Reduce el ruido de fondo constante (ventilador, habitación, siseo).</summary>
    Denoise,

    /// <summary>Nivela el volumen: sube lo bajo y contiene los picos.</summary>
    Compressor,

    /// <summary>Evita que los picos pasen de un techo, sin nivelar el resto.</summary>
    Limiter,

    /// <summary>Simula el eco de una sala.</summary>
    Reverb,

    /// <summary>Duplica la señal con un ligero retardo variable, para un sonido más lleno.</summary>
    Chorus,

    /// <summary>Normaliza la sonoridad a un nivel de referencia (EBU R128).</summary>
    Normalize,
}

/// <summary>Cambia el efecto de sonido del propio audio de un clip de la pista principal.</summary>
public sealed class SetClipAudioEffectCommand : IUndoableCommand
{
    private readonly Clip _clip;
    private readonly AudioEffectKind _effect;
    private readonly AudioEffectKind _previous;

    /// <summary>Crea la operación.</summary>
    public SetClipAudioEffectCommand(Clip clip, AudioEffectKind effect)
    {
        ArgumentNullException.ThrowIfNull(clip);
        _clip = clip;
        _effect = effect;
        _previous = clip.AudioEffect;
    }

    /// <inheritdoc/>
    public string Description => _effect == AudioEffectKind.None ? "Quitar efecto de audio" : "Aplicar efecto de audio";

    /// <inheritdoc/>
    public void Execute() => _clip.AudioEffect = _effect;

    /// <inheritdoc/>
    public void Undo() => _clip.AudioEffect = _previous;
}

/// <summary>Cambia el efecto de sonido de un clip de una pista de audio.</summary>
public sealed class SetAudioEffectCommand : IUndoableCommand
{
    private readonly AudioClip _clip;
    private readonly AudioEffectKind _effect;
    private readonly AudioEffectKind _previous;

    /// <summary>Crea la operación.</summary>
    public SetAudioEffectCommand(AudioClip clip, AudioEffectKind effect)
    {
        ArgumentNullException.ThrowIfNull(clip);
        _clip = clip;
        _effect = effect;
        _previous = clip.Effect;
    }

    /// <inheritdoc/>
    public string Description => _effect == AudioEffectKind.None ? "Quitar efecto de audio" : "Aplicar efecto de audio";

    /// <inheritdoc/>
    public void Execute() => _clip.Effect = _effect;

    /// <inheritdoc/>
    public void Undo() => _clip.Effect = _previous;
}

/// <summary>Cambia el balance estéreo del propio audio de un clip de la pista principal.</summary>
public sealed class SetClipPanCommand : IUndoableCommand
{
    private readonly Clip _clip;
    private readonly double _pan;
    private double _previous;

    /// <summary>Crea la operación.</summary>
    public SetClipPanCommand(Clip clip, double pan)
    {
        ArgumentNullException.ThrowIfNull(clip);
        _clip = clip;
        _pan = pan;
    }

    /// <inheritdoc/>
    public string Description => "Cambiar balance";

    /// <inheritdoc/>
    public void Execute()
    {
        _previous = _clip.Pan;
        _clip.Pan = _pan;
    }

    /// <inheritdoc/>
    public void Undo() => _clip.Pan = _previous;
}

/// <summary>Cambia el balance estéreo de un clip de una pista de audio.</summary>
public sealed class SetAudioPanCommand : IUndoableCommand
{
    private readonly AudioClip _clip;
    private readonly double _pan;
    private double _previous;

    /// <summary>Crea la operación.</summary>
    public SetAudioPanCommand(AudioClip clip, double pan)
    {
        ArgumentNullException.ThrowIfNull(clip);
        _clip = clip;
        _pan = pan;
    }

    /// <inheritdoc/>
    public string Description => "Cambiar balance";

    /// <inheritdoc/>
    public void Execute()
    {
        _previous = _clip.Pan;
        _clip.Pan = _pan;
    }

    /// <inheritdoc/>
    public void Undo() => _clip.Pan = _previous;
}
