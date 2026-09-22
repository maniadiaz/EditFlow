// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Undo;

namespace EditFlow.Core.Timeline;

/// <summary>Tipo de transición entre dos clips.</summary>
public enum TransitionKind
{
    /// <summary>Sin transición: corte seco.</summary>
    None,

    /// <summary>Disolvencia: un clip se funde en el otro.</summary>
    Dissolve,

    /// <summary>El clip saliente se funde a negro y el entrante aparece desde negro.</summary>
    FadeToBlack,

    /// <summary>El clip saliente se funde a blanco y el entrante aparece desde blanco.</summary>
    FadeToWhite,

    /// <summary>El entrante empuja al saliente hacia la izquierda.</summary>
    WipeLeft,

    /// <summary>El entrante empuja al saliente hacia la derecha.</summary>
    WipeRight,

    /// <summary>El entrante se desliza desde la derecha, tapando al saliente.</summary>
    SlideLeft,

    /// <summary>El entrante se desliza desde la izquierda, tapando al saliente.</summary>
    SlideRight,

    /// <summary>El entrante aparece dentro de un círculo que crece hasta cubrir la imagen.</summary>
    CircleOpen,
}

/// <summary>
/// Transición hacia un clip desde el que lo precede en la pista principal.
/// </summary>
/// <param name="Kind">Tipo. <see cref="TransitionKind.None"/> significa que no hay transición.</param>
/// <param name="Duration">
/// Cuánto se solapan los dos clips. Se acota siempre al material disponible: nunca puede ser
/// mayor que la duración del clip más corto de los dos.
/// </param>
/// <remarks>
/// Vive en el clip <b>entrante</b>: describe la transición desde quien lo precede hacia él. No es
/// un recorte de ninguno de los dos clips —ambos conservan su <see cref="Clip.SourceIn"/> y
/// <see cref="Clip.SourceOut"/> intactos—, sino que acorta la timeline compuesta en
/// <see cref="Duration"/>: durante ese tramo se ven fundidos el final de uno y el principio del
/// otro, en vez de uno después del otro.
/// </remarks>
public sealed record Transition(TransitionKind Kind, TimeSpan Duration)
{
    /// <summary>Sin transición.</summary>
    public static Transition None { get; } = new(TransitionKind.None, TimeSpan.Zero);

    /// <summary>Por debajo de esto no se percibe como una transición, solo como un corte impreciso.</summary>
    public static TimeSpan MinimumDuration { get; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Duración por defecto al añadir una transición nueva.</summary>
    public static TimeSpan DefaultDuration { get; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Tope superior: una transición más larga deja de leerse como tal.</summary>
    public static TimeSpan MaximumDuration { get; } = TimeSpan.FromSeconds(3);

    /// <summary>Indica que no hay transición real.</summary>
    public bool IsNone => Kind == TransitionKind.None || Duration <= TimeSpan.Zero;
}

/// <summary>
/// Cuánto se solapan de verdad dos clips consecutivos, una vez acotada la transición pedida al
/// material disponible.
/// </summary>
/// <remarks>
/// Un único sitio para esta cuenta: la usan tanto el modelo de la timeline (duración, posición de
/// cada clip) como el grafo de exportación (dónde empieza cada <c>xfade</c>), y deben coincidir
/// exactamente o la imagen y el sonido se desincronizarían a partir de la primera transición.
/// </remarks>
public static class TransitionMath
{
    /// <summary>
    /// Solape efectivo, en segundos, entre <paramref name="previous"/> y <paramref name="current"/>.
    /// </summary>
    /// <param name="previous">Clip que precede a <paramref name="current"/>, o <see langword="null"/> si es el primero.</param>
    /// <param name="current">Clip cuya <see cref="Clip.TransitionIn"/> se evalúa.</param>
    /// <returns>
    /// Cero si no hay transición, si alguno de los dos es un hueco, o si no hay clip anterior.
    /// En otro caso, la duración pedida acotada a la del más corto de los dos clips: una
    /// transición nunca puede consumir más de lo que un clip tiene.
    /// </returns>
    public static TimeSpan Overlap(Clip? previous, Clip current)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (previous is null || previous.IsGap || current.IsGap || current.TransitionIn.IsNone)
        {
            return TimeSpan.Zero;
        }

        var cap = previous.Duration < current.Duration ? previous.Duration : current.Duration;
        var wanted = current.TransitionIn.Duration;
        return wanted > cap ? cap : wanted;
    }
}

/// <summary>Añade, cambia o quita la transición de entrada de un clip.</summary>
public sealed class SetTransitionCommand : IUndoableCommand
{
    private readonly Clip _clip;
    private readonly Transition _transition;
    private readonly Transition _previous;

    /// <summary>Crea la operación.</summary>
    /// <param name="clip">Clip entrante, cuya <see cref="Clip.TransitionIn"/> se cambia.</param>
    /// <param name="transition">Nueva transición; usa <see cref="Transition.None"/> para quitarla.</param>
    /// <param name="previous">
    /// Transición que había antes, si ya se fue mostrando mientras se arrastraba un
    /// deslizador; sin ella se toma la actual, que es lo correcto cuando el cambio no se ha
    /// aplicado todavía.
    /// </param>
    public SetTransitionCommand(Clip clip, Transition transition, Transition? previous = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(transition);
        _clip = clip;
        _transition = transition;
        _previous = previous ?? clip.TransitionIn;
    }

    /// <inheritdoc/>
    public string Description => _transition.IsNone ? "Quitar transición" : "Añadir transición";

    /// <inheritdoc/>
    public void Execute() => _clip.TransitionIn = _transition;

    /// <inheritdoc/>
    public void Undo() => _clip.TransitionIn = _previous;
}
