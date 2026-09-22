// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

namespace EditFlow.Core.Timeline;

/// <summary>
/// El montaje completo: la pista principal de video, las pistas de audio y las capas de superposición.
/// </summary>
/// <remarks>
/// La pista de video sigue siendo una secuencia sin huecos, cuyas posiciones se derivan
/// del orden, y las pistas de audio tienen posición libre. Mezclar los dos modelos en uno
/// solo obligaría a validar continuamente que la pista de video no tenga huecos, cuando
/// hoy eso es imposible por construcción.
/// </remarks>
public sealed class EditSequence
{
    private readonly List<AudioTrack> _audioTracks = [];
    private readonly List<OverlayTrack> _overlayTracks = [];

    /// <summary>Pista principal de video.</summary>
    public VideoTimeline Video { get; } = new();

    /// <summary>Pistas de audio, de arriba abajo tal como se muestran.</summary>
    public IReadOnlyList<AudioTrack> AudioTracks => _audioTracks;

    /// <summary>
    /// Capas de textos e imágenes sobre el video, de arriba abajo tal como se muestran: la
    /// primera es la que queda delante.
    /// </summary>
    public IReadOnlyList<OverlayTrack> OverlayTracks => _overlayTracks;

    /// <summary>Duración total: la mayor entre la pista de video, las de audio y las de superposición.</summary>
    /// <remarks>
    /// Una música más larga que el video alarga el montaje. Ignorar las pistas de audio
    /// recortaría la exportación justo donde el video termina, cortando la música.
    /// </remarks>
    public TimeSpan Duration
    {
        get
        {
            var end = Video.Duration;
            foreach (var track in _audioTracks)
            {
                if (track.End > end)
                {
                    end = track.End;
                }
            }

            foreach (var track in _overlayTracks)
            {
                if (track.End > end)
                {
                    end = track.End;
                }
            }

            return end;
        }
    }

    /// <summary>La capa de subtítulos, o <see langword="null"/> si aún no hay ninguna.</summary>
    public OverlayTrack? SubtitleLayer => _overlayTracks.Find(t => t.IsSubtitles);

    /// <summary>Añade una capa de superposición encima de las demás, pero por debajo de la de subtítulos.</summary>
    public OverlayTrack AddOverlayTrack(string? name = null)
    {
        var track = new OverlayTrack(name ?? NextOverlayName());
        _overlayTracks.Insert(_overlayTracks.Count > 0 && _overlayTracks[0].IsSubtitles ? 1 : 0, track);
        return track;
    }

    /// <summary>Devuelve la capa de subtítulos, creándola delante de todas si no existía.</summary>
    /// <param name="created">Si se acaba de crear.</param>
    public OverlayTrack GetOrCreateSubtitleLayer(out bool created)
    {
        if (SubtitleLayer is { } existing)
        {
            created = false;
            return existing;
        }

        var track = new OverlayTrack(OverlayTrack.SubtitleLayerName) { IsSubtitles = true };
        _overlayTracks.Insert(0, track);
        created = true;
        return track;
    }

    /// <summary>Coloca la capa de subtítulos, si la hay, delante de todas.</summary>
    public void KeepSubtitleLayerOnTop()
    {
        var layer = SubtitleLayer;
        if (layer is not null && !ReferenceEquals(_overlayTracks[0], layer))
        {
            _overlayTracks.Remove(layer);
            _overlayTracks.Insert(0, layer);
        }
    }

    /// <summary>Inserta una capa ya creada en una posición.</summary>
    public void InsertOverlayTrack(int index, OverlayTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, _overlayTracks.Count);

        // La capa de subtítulos siempre va delante y ninguna otra puede colocarse por encima de ella.
        if (track.IsSubtitles)
        {
            index = 0;
        }
        else if (index == 0 && _overlayTracks.Count > 0 && _overlayTracks[0].IsSubtitles)
        {
            index = 1;
        }

        _overlayTracks.Insert(index, track);
    }

    /// <summary>Elimina una capa con todos sus elementos.</summary>
    public bool RemoveOverlayTrack(OverlayTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return _overlayTracks.Remove(track);
    }

    /// <summary>Posición de una capa, o -1 si no pertenece a la secuencia.</summary>
    public int IndexOf(OverlayTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);

        for (var i = 0; i < _overlayTracks.Count; i++)
        {
            if (ReferenceEquals(_overlayTracks[i], track))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Busca una capa donde quepa un intervalo, o crea una nueva si ninguna sirve.</summary>
    public OverlayTrack FindOrCreateOverlayTrackFor(TimeSpan start, TimeSpan duration)
    {
        foreach (var track in _overlayTracks)
        {
            if (!track.IsLocked && !track.IsSubtitles && track.CanPlace(start, duration))
            {
                return track;
            }
        }

        return AddOverlayTrack();
    }

    /// <summary>Indica si alguna pista está en solo.</summary>
    public bool AnySolo => _audioTracks.Exists(t => t.IsSolo);

    /// <summary>Añade una pista de audio al final.</summary>
    public AudioTrack AddAudioTrack(string? name = null)
    {
        var track = new AudioTrack(name ?? NextTrackName());
        _audioTracks.Add(track);
        return track;
    }

    /// <summary>Inserta una pista ya creada en una posición.</summary>
    public void InsertAudioTrack(int index, AudioTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, _audioTracks.Count);

        _audioTracks.Insert(index, track);
    }

    /// <summary>Elimina una pista con todos sus clips.</summary>
    public bool RemoveAudioTrack(AudioTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return _audioTracks.Remove(track);
    }

    /// <summary>Posición de una pista, o -1 si no pertenece a la secuencia.</summary>
    public int IndexOf(AudioTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);

        for (var i = 0; i < _audioTracks.Count; i++)
        {
            if (ReferenceEquals(_audioTracks[i], track))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Cambia una pista de posición.</summary>
    /// <returns><see langword="true"/> si se movió.</returns>
    /// <remarks>
    /// El destino se acota en lugar de rechazarse, porque el llamante habitual es un
    /// arrastre: soltar la pista más allá del extremo debe significar "ponla al final".
    /// </remarks>
    public bool MoveAudioTrack(AudioTrack track, int newIndex)
    {
        ArgumentNullException.ThrowIfNull(track);

        var current = IndexOf(track);
        if (current < 0)
        {
            return false;
        }

        newIndex = Math.Clamp(newIndex, 0, _audioTracks.Count - 1);
        if (newIndex == current)
        {
            return false;
        }

        _audioTracks.RemoveAt(current);
        _audioTracks.Insert(newIndex, track);
        return true;
    }

    /// <summary>
    /// Busca una pista donde quepa un intervalo, o crea una nueva si ninguna sirve.
    /// </summary>
    /// <remarks>
    /// Se prefieren las pistas existentes para no llenar la secuencia de pistas casi
    /// vacías, y se saltan las bloqueadas porque no admiten clips nuevos.
    /// </remarks>
    public AudioTrack FindOrCreateTrackFor(TimeSpan start, TimeSpan duration)
    {
        foreach (var track in _audioTracks)
        {
            if (!track.IsLocked && track.CanPlace(start, duration))
            {
                return track;
            }
        }

        return AddAudioTrack();
    }

    private string NextOverlayName()
    {
        for (var n = 1; ; n++)
        {
            var candidate = "T" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!_overlayTracks.Exists(t => t.Name == candidate))
            {
                return candidate;
            }
        }
    }

    private string NextTrackName()
    {
        // El primer número libre, no el número de pistas + 1: si se borró A1 y quedan A2
        // y A3, la siguiente debe ser A1 y no otra A4 que deje un hueco en la numeración.
        for (var n = 1; ; n++)
        {
            var candidate = "A" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!_audioTracks.Exists(t => t.Name == candidate))
            {
                return candidate;
            }
        }
    }
}
