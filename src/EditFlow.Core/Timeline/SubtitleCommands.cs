// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Undo;

namespace EditFlow.Core.Timeline;

/// <summary>Un subtítulo: qué se dice y cuándo.</summary>
/// <param name="Start">Instante de la timeline en que aparece.</param>
/// <param name="End">Instante en que desaparece.</param>
/// <param name="Text">Texto.</param>
public sealed record SubtitleCue(TimeSpan Start, TimeSpan End, string Text);

/// <summary>
/// Añade una lista de subtítulos como textos editables en una capa nueva, en un solo paso del historial.
/// </summary>
/// <remarks>
/// Cada subtítulo es un texto normal de la capa: se puede corregir, moverlo, cambiarle el estilo o borrarlo
/// como cualquier otro. Deshacer quita la capa entera de una vez.
/// </remarks>
public sealed class AddSubtitlesCommand : IUndoableCommand
{
    /// <summary>Nombre de la capa que se crea.</summary>
    public const string LayerName = "Subtítulos";

    /// <summary>Aspecto por defecto: letra legible, abajo y centrada, con sombra para leerse sobre cualquier fondo.</summary>
    public static TextStyle DefaultStyle(string text) => new(text, 0.055, "#FFFFFF", Bold: true, Italic: false, Shadow: true);

    private readonly EditSequence _sequence;
    private readonly List<SubtitleCue> _cues;
    private OverlayTrack? _track;

    /// <summary>Crea la operación.</summary>
    public AddSubtitlesCommand(EditSequence sequence, IEnumerable<SubtitleCue> cues)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(cues);
        _sequence = sequence;
        _cues = cues.Where(c => !string.IsNullOrWhiteSpace(c.Text)).OrderBy(c => c.Start).ToList();
    }

    /// <inheritdoc/>
    public string Description => "Añadir subtítulos";

    /// <summary>Cuántos subtítulos se llegaron a colocar.</summary>
    public int Added { get; private set; }

    /// <summary>La capa creada.</summary>
    public OverlayTrack? Track => _track;

    /// <inheritdoc/>
    public void Execute()
    {
        // Al rehacer se reinserta la misma capa, con los mismos elementos.
        if (_track is not null)
        {
            _sequence.InsertOverlayTrack(0, _track);
            return;
        }

        var track = _sequence.AddOverlayTrack(LayerName);
        var transform = new OverlayTransform(0.5, 0.88);

        for (var i = 0; i < _cues.Count; i++)
        {
            var cue = _cues[i];

            // Sin solaparse con el siguiente: en una misma capa no caben dos a la vez.
            var end = cue.End;
            if (i + 1 < _cues.Count && _cues[i + 1].Start < end)
            {
                end = _cues[i + 1].Start;
            }

            var duration = end - cue.Start;
            if (cue.Start < TimeSpan.Zero || duration < OverlayItem.MinimumDuration)
            {
                continue;
            }

            if (track.TryAdd(OverlayItem.CreateText(DefaultStyle(cue.Text.Trim()), cue.Start, duration, transform)))
            {
                Added++;
            }
        }

        if (Added == 0)
        {
            _sequence.RemoveOverlayTrack(track);
            return;
        }

        _track = track;
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
