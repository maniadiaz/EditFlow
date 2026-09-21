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
/// Añade subtítulos como textos editables en la capa «Sub», en un solo paso del historial.
/// </summary>
/// <remarks>
/// <para>
/// La capa «Sub» es única y siempre va delante de todas las demás: así el resto del montaje (videos
/// superpuestos, imágenes, títulos…) se organiza en sus propias capas sin afectar a los subtítulos.
/// Si ya existe, los subtítulos nuevos se añaden a ella en los huecos libres, sin pisar los que ya había.
/// </para>
/// <para>
/// Cada subtítulo es un texto normal de la capa: se puede corregir, mover, cambiarle el estilo o borrar.
/// Deshacer quita solo lo que este paso añadió, y la capa si la creó él.
/// </para>
/// </remarks>
public sealed class AddSubtitlesCommand : IUndoableCommand
{
    /// <summary>Nombre de la capa de subtítulos.</summary>
    public const string LayerName = OverlayTrack.SubtitleLayerName;

    /// <summary>Aspecto por defecto: letra legible, abajo y centrada, con sombra para leerse sobre cualquier fondo.</summary>
    public static TextStyle DefaultStyle(string text) => new(text, 0.055, "#FFFFFF", Bold: true, Italic: false, Shadow: true);

    private readonly EditSequence _sequence;
    private readonly List<SubtitleCue> _cues;
    private readonly List<OverlayItem> _items = [];
    private OverlayTrack? _track;
    private bool _createdLayer;
    private bool _built;

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
    public int Added => _items.Count;

    /// <summary>Cuántos no cupieron porque ya había otro en ese instante.</summary>
    public int Skipped { get; private set; }

    /// <summary>El primer subtítulo colocado.</summary>
    public OverlayItem? First => _items.Count > 0 ? _items[0] : null;

    /// <summary>La capa de subtítulos.</summary>
    public OverlayTrack? Track => _track;

    /// <inheritdoc/>
    public void Execute()
    {
        var track = _sequence.GetOrCreateSubtitleLayer(out var created);

        if (!_built)
        {
            _built = true;
            _createdLayer = created;
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

                var item = OverlayItem.CreateText(DefaultStyle(cue.Text.Trim()), cue.Start, duration, transform);
                if (track.TryAdd(item))
                {
                    _items.Add(item);
                }
                else
                {
                    Skipped++;
                }
            }

            _track = track;

            // Sin nada que añadir, no se deja una capa vacía creada por este paso.
            if (_items.Count == 0 && created)
            {
                _sequence.RemoveOverlayTrack(track);
            }

            return;
        }

        // Al rehacer: la misma capa (si la había creado este paso) con los mismos elementos.
        if (_createdLayer && _sequence.IndexOf(track) >= 0 && !ReferenceEquals(track, _track))
        {
            _track = track;
        }

        foreach (var item in _items)
        {
            track.TryAdd(item);
        }

        _track = track;
    }

    /// <inheritdoc/>
    public void Undo()
    {
        if (_track is null)
        {
            return;
        }

        foreach (var item in _items)
        {
            _track.Remove(item);
        }

        if (_createdLayer && _track.Items.Count == 0)
        {
            _sequence.RemoveOverlayTrack(_track);
        }
    }
}
