// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Undo;

namespace EditFlow.Core.Timeline;

/// <summary>Cambia la velocidad de reproducción de un clip de la pista principal.</summary>
/// <remarks>
/// Ajustar la velocidad no toca <see cref="Clip.SourceIn"/> ni <see cref="Clip.SourceOut"/>:
/// el clip sigue usando el mismo material del archivo, solo cambia cuánto tiempo ocupa eso en
/// la timeline (<see cref="Clip.Duration"/>). Los clips que vienen detrás se desplazan solos,
/// porque su posición se deriva del orden y no se guarda en cada uno.
/// </remarks>
public sealed class SetSpeedCommand : IUndoableCommand
{
    private readonly Clip _clip;
    private readonly double _speed;
    private readonly double _previous;

    /// <summary>Crea la operación.</summary>
    /// <param name="clip">Clip cuya velocidad se cambia.</param>
    /// <param name="speed">
    /// Nueva velocidad; se acota a <see cref="Clip.MinimumSpeed"/>–<see cref="Clip.MaximumSpeed"/>.
    /// </param>
    /// <param name="previous">
    /// Velocidad que había antes, si ya se fue mostrando mientras se arrastraba un deslizador;
    /// sin ella se toma la actual, que es lo correcto cuando el cambio no se ha aplicado todavía.
    /// </param>
    public SetSpeedCommand(Clip clip, double speed, double? previous = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        _clip = clip;
        _speed = speed;
        _previous = previous ?? clip.Speed;
    }

    /// <inheritdoc/>
    public string Description => "Cambiar velocidad";

    /// <inheritdoc/>
    public void Execute() => _clip.Speed = _speed;

    /// <inheritdoc/>
    public void Undo() => _clip.Speed = _previous;
}
