// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Undo;

namespace EditFlow.Core.Timeline;

/// <summary>Filtro de aspecto que se aplica a la imagen entera de un clip.</summary>
/// <remarks>
/// A diferencia de <see cref="ColorAdjust"/> —cuatro deslizadores que se combinan como se
/// quiera—, esto es un aspecto ya hecho, de un clic: sirve para cuando se busca un estilo
/// concreto y no un ajuste fino. Los dos se aplican a la vez y no estorban entre sí.
/// </remarks>
public enum VisualFilterKind
{
    /// <summary>Sin filtro: la imagen tal cual, con su propio ajuste de color si tiene.</summary>
    None,

    /// <summary>Blanco y negro.</summary>
    BlackAndWhite,

    /// <summary>Tono sepia, como una fotografía antigua.</summary>
    Sepia,

    /// <summary>Colores apagados y virado cálido, como una grabación de hace décadas.</summary>
    Vintage,

    /// <summary>Bordes oscurecidos alrededor del encuadre.</summary>
    Vignette,

    /// <summary>Vira la imagen hacia tonos cálidos (naranjas).</summary>
    Warm,

    /// <summary>Vira la imagen hacia tonos fríos (azules).</summary>
    Cool,
}

/// <summary>Cambia el filtro de aspecto de un clip de la pista principal.</summary>
public sealed class SetFilterCommand : IUndoableCommand
{
    private readonly Clip _clip;
    private readonly VisualFilterKind _filter;
    private readonly VisualFilterKind _previous;

    /// <summary>Crea la operación.</summary>
    /// <param name="clip">Clip cuyo filtro se cambia.</param>
    /// <param name="filter">Nuevo filtro; usa <see cref="VisualFilterKind.None"/> para quitarlo.</param>
    public SetFilterCommand(Clip clip, VisualFilterKind filter)
    {
        ArgumentNullException.ThrowIfNull(clip);
        _clip = clip;
        _filter = filter;
        _previous = clip.Filter;
    }

    /// <inheritdoc/>
    public string Description => _filter == VisualFilterKind.None ? "Quitar filtro" : "Aplicar filtro";

    /// <inheritdoc/>
    public void Execute() => _clip.Filter = _filter;

    /// <inheritdoc/>
    public void Undo() => _clip.Filter = _previous;
}
