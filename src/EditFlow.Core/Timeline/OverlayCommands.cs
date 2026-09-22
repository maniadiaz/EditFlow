// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Undo;

namespace EditFlow.Core.Timeline;

/// <summary>Añade una capa de superposición encima de las demás.</summary>
public sealed class AddOverlayTrackCommand : IUndoableCommand
{
    private readonly EditSequence _sequence;
    private OverlayTrack? _track;

    /// <summary>Crea la operación.</summary>
    public AddOverlayTrackCommand(EditSequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        _sequence = sequence;
    }

    /// <inheritdoc/>
    public string Description => "Añadir capa";

    /// <summary>Capa creada.</summary>
    public OverlayTrack? Result => _track;

    /// <inheritdoc/>
    public void Execute()
    {
        // Al rehacer se reinserta la misma capa, no una nueva: los elementos añadidos
        // después apuntan a ella.
        if (_track is null)
        {
            _track = _sequence.AddOverlayTrack();
        }
        else
        {
            _sequence.InsertOverlayTrack(0, _track);
        }
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

/// <summary>Elimina una capa de superposición con todo lo que tiene.</summary>
public sealed class RemoveOverlayTrackCommand : IUndoableCommand
{
    private readonly EditSequence _sequence;
    private readonly OverlayTrack _track;
    private int _index = -1;

    /// <summary>Crea la operación.</summary>
    public RemoveOverlayTrackCommand(EditSequence sequence, OverlayTrack track)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(track);
        _sequence = sequence;
        _track = track;
    }

    /// <inheritdoc/>
    public string Description => "Eliminar capa";

    /// <inheritdoc/>
    public void Execute()
    {
        _index = _sequence.IndexOf(_track);
        if (_index >= 0)
        {
            _sequence.RemoveOverlayTrack(_track);
        }
    }

    /// <inheritdoc/>
    public void Undo()
    {
        if (_index >= 0)
        {
            _sequence.InsertOverlayTrack(Math.Min(_index, _sequence.OverlayTracks.Count), _track);
        }
    }
}

/// <summary>Añade un texto o una imagen a una capa.</summary>
public sealed class AddOverlayItemCommand : IUndoableCommand
{
    private readonly OverlayTrack _track;
    private readonly OverlayItem _item;
    private bool _added;

    /// <summary>Crea la operación.</summary>
    public AddOverlayItemCommand(OverlayTrack track, OverlayItem item)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(item);
        _track = track;
        _item = item;
    }

    /// <inheritdoc/>
    public string Description => _item.Kind == OverlayKind.Text ? "Añadir texto" : "Añadir imagen";

    /// <summary>Indica si se llegó a añadir.</summary>
    public bool Added => _added;

    /// <inheritdoc/>
    public void Execute() => _added = _track.TryAdd(_item);

    /// <inheritdoc/>
    public void Undo()
    {
        if (_added)
        {
            _track.Remove(_item);
        }
    }
}

/// <summary>Elimina un texto o una imagen de su capa.</summary>
public sealed class RemoveOverlayItemCommand : IUndoableCommand
{
    private readonly OverlayTrack _track;
    private readonly OverlayItem _item;
    private bool _removed;

    /// <summary>Crea la operación.</summary>
    public RemoveOverlayItemCommand(OverlayTrack track, OverlayItem item)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(item);
        _track = track;
        _item = item;
    }

    /// <inheritdoc/>
    public string Description => "Eliminar superposición";

    /// <inheritdoc/>
    public void Execute() => _removed = _track.Remove(_item);

    /// <inheritdoc/>
    public void Undo()
    {
        if (_removed)
        {
            _track.TryAdd(_item);
        }
    }
}

/// <summary>Cambia la posición y la duración de un elemento: mover, recortar o alargar.</summary>
public sealed class PlaceOverlayItemCommand : IUndoableCommand
{
    private readonly OverlayTrack _track;
    private readonly OverlayItem _item;
    private readonly TimeSpan _start;
    private readonly TimeSpan _duration;
    private TimeSpan _originalStart, _originalDuration;
    private bool _applied;

    /// <summary>Crea la operación.</summary>
    public PlaceOverlayItemCommand(OverlayTrack track, OverlayItem item, TimeSpan start, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(item);
        _track = track;
        _item = item;
        _start = start;
        _duration = duration;
    }

    /// <inheritdoc/>
    public string Description => "Colocar superposición";

    /// <summary>Indica si el cambio se llegó a aplicar.</summary>
    public bool Applied => _applied;

    /// <inheritdoc/>
    public void Execute()
    {
        _originalStart = _item.Start;
        _originalDuration = _item.Duration;
        _applied = _track.TryPlace(_item, _start, _duration);
    }

    /// <inheritdoc/>
    public void Undo()
    {
        // Solo se deshace lo que ocurrió: un cambio rechazado por chocar con otro elemento
        // no movió nada, y "deshacerlo" lo movería desde donde nunca se fue.
        if (_applied)
        {
            _track.Reinsert(_item, _originalStart, _originalDuration);
        }
    }
}

/// <summary>Cambia el aspecto de un elemento: su texto, posición, tamaño o transparencia.</summary>
public sealed class SetOverlayLookCommand : IUndoableCommand
{
    private readonly OverlayItem _item;
    private readonly OverlayTransform _transform;
    private readonly TextStyle? _text;
    private OverlayTransform _previousTransform = new();
    private TextStyle? _previousText;

    /// <summary>Crea la operación.</summary>
    /// <param name="item">Elemento a cambiar.</param>
    /// <param name="transform">Nueva posición, tamaño y transparencia; se ajustan a su rango.</param>
    /// <param name="text">Nuevo estilo de texto; se ignora en una imagen.</param>
    public SetOverlayLookCommand(OverlayItem item, OverlayTransform transform, TextStyle? text = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(transform);
        _item = item;
        _transform = transform.Clamped();
        _text = text is null ? null : text with { Size = Math.Clamp(text.Size, TextStyle.MinimumSize, TextStyle.MaximumSize) };
    }

    /// <inheritdoc/>
    public string Description => "Cambiar aspecto";

    /// <inheritdoc/>
    public void Execute()
    {
        _previousTransform = _item.Transform;
        _previousText = _item.Text;

        _item.Transform = _transform;
        if (_text is not null && _item.Kind == OverlayKind.Text)
        {
            _item.Text = _text;
        }
    }

    /// <inheritdoc/>
    public void Undo()
    {
        _item.Transform = _previousTransform;
        _item.Text = _previousText;
    }
}

/// <summary>Cambia los fundidos de aparición y desaparición de un elemento superpuesto.</summary>
public sealed class SetOverlayFadeCommand : IUndoableCommand
{
    private readonly OverlayItem _item;
    private readonly TimeSpan _fadeIn;
    private readonly TimeSpan _fadeOut;
    private TimeSpan _previousIn;
    private TimeSpan _previousOut;

    /// <summary>Crea la operación.</summary>
    public SetOverlayFadeCommand(OverlayItem item, TimeSpan fadeIn, TimeSpan fadeOut)
    {
        ArgumentNullException.ThrowIfNull(item);
        _item = item;
        _fadeIn = fadeIn;
        _fadeOut = fadeOut;
    }

    /// <inheritdoc/>
    public string Description => "Cambiar fundidos";

    /// <inheritdoc/>
    public void Execute()
    {
        _previousIn = _item.FadeIn;
        _previousOut = _item.FadeOut;

        // Igual que con el fundido de un clip: se anulan los dos antes de fijar los nuevos,
        // porque cada uno se acota contra el otro y con los antiguos aún puestos se recortarían
        // por un valor que ya no debería contar.
        _item.FadeIn = TimeSpan.Zero;
        _item.FadeOut = TimeSpan.Zero;
        _item.FadeIn = _fadeIn;
        _item.FadeOut = _fadeOut;
    }

    /// <inheritdoc/>
    public void Undo()
    {
        _item.FadeIn = TimeSpan.Zero;
        _item.FadeOut = TimeSpan.Zero;
        _item.FadeIn = _previousIn;
        _item.FadeOut = _previousOut;
    }
}

/// <summary>
/// Sube un clip de la pista principal a una capa superior, dejando un hueco en su lugar.
/// </summary>
/// <remarks>
/// El hueco mantiene todo en su sitio: la duración de la pista principal no cambia y lo que hay
/// después no se corre. El clip pasa a ser un video superpuesto que empieza donde empezaba y,
/// al principio, ocupa el cuadro igual que antes; luego se puede reducir y colocar. Su sonido
/// sigue sonando desde la capa.
/// </remarks>
public sealed class LiftClipToLayerCommand : IUndoableCommand
{
    private readonly EditSequence _sequence;
    private readonly Clip _clip;
    private Clip? _gap;
    private OverlayItem? _item;
    private OverlayTrack? _track;
    private readonly OverlayTrack? _preferred;
    private bool _createdTrack;

    /// <summary>Crea la operación.</summary>
    /// <param name="sequence">Montaje.</param>
    /// <param name="clip">Clip de la pista principal que sube.</param>
    /// <param name="preferred">
    /// Capa a la que se prefiere subirlo (por ejemplo, donde se soltó al arrastrar). Si no cabe allí, o
    /// es la de subtítulos, se busca otra o se crea una nueva.
    /// </param>
    public LiftClipToLayerCommand(EditSequence sequence, Clip clip, OverlayTrack? preferred = null)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(clip);
        _sequence = sequence;
        _clip = clip;
        _preferred = preferred;
    }

    /// <summary>Un hueco no tiene nada que subir.</summary>
    public static bool CanLift(Clip clip) => clip is { IsGap: false };

    /// <inheritdoc/>
    public string Description => "Subir a capa superior";

    /// <summary>El video superpuesto creado.</summary>
    public OverlayItem? Item => _item;

    /// <summary>La capa en la que quedó.</summary>
    public OverlayTrack? Track => _track;

    /// <inheritdoc/>
    public void Execute()
    {
        if (_item is null)
        {
            var start = _sequence.Video.StartOf(_clip);
            _gap = Clip.CreateGap(_clip.Duration);

            _item = OverlayItem.CreateVideo(
                _clip.Source,
                _clip.SourceIn,
                start,
                _clip.Duration,
                playsAudio: _clip.HasOwnAudio,
                audioGainDb: _clip.AudioGainDb);

            // El color va con el clip: subirlo a una capa no debe cambiarle el aspecto.
            _item.Color = _clip.Color;

            if (_preferred is { IsLocked: false, IsSubtitles: false } wanted
                && _sequence.IndexOf(wanted) >= 0
                && wanted.CanPlace(start, _clip.Duration))
            {
                _track = wanted;
            }
            else
            {
                var before = _sequence.OverlayTracks.Count;
                _track = _sequence.FindOrCreateOverlayTrackFor(start, _clip.Duration);
                _createdTrack = _sequence.OverlayTracks.Count > before;
            }
        }
        else if (_track is not null && _sequence.IndexOf(_track) < 0)
        {
            // Al rehacer, la capa que se quitó al deshacer vuelve a su sitio.
            _sequence.InsertOverlayTrack(0, _track);
        }

        _sequence.Video.Replace(_clip, _gap!);
        _track!.TryAdd(_item);
    }

    /// <inheritdoc/>
    public void Undo()
    {
        if (_item is null || _track is null || _gap is null)
        {
            return;
        }

        _track.Remove(_item);
        _sequence.Video.Replace(_gap, _clip);

        if (_createdTrack && _track.Items.Count == 0)
        {
            _sequence.RemoveOverlayTrack(_track);
        }
    }
}

/// <summary>Cambia el volumen o silencia el sonido de un video superpuesto.</summary>
public sealed class SetOverlayAudioCommand : IUndoableCommand
{
    private readonly OverlayItem _item;
    private readonly bool _playsAudio;
    private readonly double _gainDb;
    private bool _previousPlays;
    private double _previousGain;

    /// <summary>Crea la operación.</summary>
    /// <param name="item">Video superpuesto.</param>
    /// <param name="playsAudio">Si su sonido entra en la mezcla.</param>
    /// <param name="gainDb">Volumen en dB, dentro del mismo rango que el de un clip.</param>
    public SetOverlayAudioCommand(OverlayItem item, bool playsAudio, double gainDb)
    {
        ArgumentNullException.ThrowIfNull(item);
        _item = item;
        _playsAudio = playsAudio && item.Media is { HasAudio: true };
        _gainDb = Math.Clamp(gainDb, AudioClip.MinimumGainDb, AudioClip.MaximumGainDb);
    }

    /// <inheritdoc/>
    public string Description => "Cambiar sonido de la capa";

    /// <inheritdoc/>
    public void Execute()
    {
        _previousPlays = _item.PlaysAudio;
        _previousGain = _item.AudioGainDb;
        _item.PlaysAudio = _playsAudio;
        _item.AudioGainDb = _gainDb;
    }

    /// <inheritdoc/>
    public void Undo()
    {
        _item.PlaysAudio = _previousPlays;
        _item.AudioGainDb = _previousGain;
    }
}

/// <summary>
/// Baja un video de una capa a la pista principal, en el hueco que hay bajo él.
/// </summary>
/// <remarks>
/// Es lo contrario de subirlo: solo se puede si ese tramo de la pista principal está vacío (un hueco) o si
/// el video empieza después de donde acaba la pista principal, porque la pista principal no admite
/// solapamientos. Pasa a ser un clip normal: ocupa el cuadro entero (pierde el tamaño y la posición que
/// tuviera en la capa) y conserva su sonido, su volumen y su color.
/// </remarks>
public sealed class LowerOverlayToMainCommand : IUndoableCommand
{
    // Por debajo de esto un trozo sobrante de hueco no merece existir.
    private static readonly TimeSpan Sliver = TimeSpan.FromMilliseconds(1);

    private readonly EditSequence _sequence;
    private readonly OverlayTrack _track;
    private readonly OverlayItem _item;
    private int _index;
    private List<Clip> _replaced = [];
    private List<Clip> _inserted = [];
    private Clip? _clip;
    private bool _planned;

    /// <summary>Crea la operación.</summary>
    public LowerOverlayToMainCommand(EditSequence sequence, OverlayTrack track, OverlayItem item)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(item);
        _sequence = sequence;
        _track = track;
        _item = item;
    }

    /// <inheritdoc/>
    public string Description => "Bajar a la pista principal";

    /// <summary>El clip que quedó en la pista principal.</summary>
    public Clip? Result => _clip;

    /// <summary>Indica si el video tiene sitio en la pista principal.</summary>
    public static bool CanLower(EditSequence sequence, OverlayItem item)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(item);
        return item is { Kind: OverlayKind.Video, Media: not null } && Plan(sequence, item) is not null;
    }

    private sealed record LowerPlan(int Index, List<Clip> Replaced, List<Clip> Inserted, Clip Clip);

    private static LowerPlan? Plan(EditSequence sequence, OverlayItem item)
    {
        var video = sequence.Video;
        var start = item.Start;
        var end = item.End;

        Clip Build() => new(item.Media!, item.SourceIn, item.SourceIn + item.Duration)
        {
            IsAudioMuted = !item.PlaysAudio,
            AudioGainDb = item.AudioGainDb,
            Color = item.Color,
        };

        // Dentro de un hueco: se parte en lo que sobra antes, el clip y lo que sobra después.
        var clipStart = TimeSpan.Zero;
        for (var i = 0; i < video.Clips.Count; i++)
        {
            var gap = video.Clips[i];
            var gapEnd = clipStart + gap.Duration;

            if (gap.IsGap && clipStart <= start + Sliver && end <= gapEnd + Sliver)
            {
                var inserted = new List<Clip>();
                if (start - clipStart > Sliver)
                {
                    inserted.Add(Clip.CreateGap(start - clipStart));
                }

                var clip = Build();
                inserted.Add(clip);

                if (gapEnd - end > Sliver)
                {
                    inserted.Add(Clip.CreateGap(gapEnd - end));
                }

                return new LowerPlan(i, [gap], inserted, clip);
            }

            clipStart = gapEnd;
        }

        // Después del final de la pista principal: se añade, con un hueco delante si hace falta.
        if (start >= video.Duration - Sliver)
        {
            var inserted = new List<Clip>();
            if (start - video.Duration > Sliver)
            {
                inserted.Add(Clip.CreateGap(start - video.Duration));
            }

            var clip = Build();
            inserted.Add(clip);
            return new LowerPlan(video.Clips.Count, [], inserted, clip);
        }

        return null;
    }

    /// <inheritdoc/>
    public void Execute()
    {
        if (!_planned)
        {
            var plan = Plan(_sequence, _item)
                ?? throw new InvalidOperationException("La pista principal no está libre bajo este video.");
            _index = plan.Index;
            _replaced = plan.Replaced;
            _inserted = plan.Inserted;
            _clip = plan.Clip;
            _planned = true;
        }

        _track.Remove(_item);
        _sequence.Video.Splice(_index, _replaced.Count, _inserted);
    }

    /// <inheritdoc/>
    public void Undo()
    {
        _sequence.Video.Splice(_index, _inserted.Count, _replaced);
        _track.TryAdd(_item);
    }
}
