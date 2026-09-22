// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Undo;

namespace EditFlow.Core.Timeline;

/// <summary>Efecto de estilo, de un clic, sobre la imagen entera de un clip.</summary>
/// <remarks>
/// A diferencia de <see cref="VisualFilterKind"/> —un ajuste de color, en el sentido de
/// fotografía—, esto son texturas y distorsiones: grano, ruido de cinta, desenfoque. Los dos
/// se aplican a la vez y no estorban entre sí, igual que en cualquier editor que separe
/// «Filtros» de «Efectos».
/// </remarks>
public enum VisualEffectKind
{
    /// <summary>Sin efecto: la imagen tal cual.</summary>
    None,

    /// <summary>Aspecto de cinta VHS: ruido, algo de desenfoque de color y contraste realzado.</summary>
    Vhs,

    /// <summary>Aberración cromática: separa ligeramente los canales de color, como una lente barata.</summary>
    ChromaticAberration,

    /// <summary>Grano de película.</summary>
    FilmGrain,

    /// <summary>Desenfoque uniforme.</summary>
    Blur,

    /// <summary>Vira los colores hacia el rosa y el cian, como una estética vaporwave.</summary>
    Vaporwave,
}

/// <summary>Cambia el efecto de estilo de un clip de la pista principal.</summary>
public sealed class SetEffectCommand : IUndoableCommand
{
    private readonly Clip _clip;
    private readonly VisualEffectKind _effect;
    private readonly VisualEffectKind _previous;

    /// <summary>Crea la operación.</summary>
    /// <param name="clip">Clip cuyo efecto se cambia.</param>
    /// <param name="effect">Nuevo efecto; usa <see cref="VisualEffectKind.None"/> para quitarlo.</param>
    public SetEffectCommand(Clip clip, VisualEffectKind effect)
    {
        ArgumentNullException.ThrowIfNull(clip);
        _clip = clip;
        _effect = effect;
        _previous = clip.Effect;
    }

    /// <inheritdoc/>
    public string Description => _effect == VisualEffectKind.None ? "Quitar efecto" : "Aplicar efecto";

    /// <inheritdoc/>
    public void Execute() => _clip.Effect = _effect;

    /// <inheritdoc/>
    public void Undo() => _clip.Effect = _previous;
}
