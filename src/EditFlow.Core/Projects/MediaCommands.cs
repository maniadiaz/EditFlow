// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Media;
using EditFlow.Core.Timeline;
using EditFlow.Core.Undo;

namespace EditFlow.Core.Projects;

/// <summary>
/// Cambia el archivo del que sale un medio, en el proyecto y en todo el montaje.
/// </summary>
/// <remarks>
/// <para>
/// Es la misma operación para las dos cosas que la piden: <b>reconectar</b> un archivo que se movió
/// o se renombró, y <b>sustituir</b> el material de un medio por otro distinto —otra toma, la versión
/// con color corregido, el render definitivo en lugar del borrador—. En ambos casos lo que hay que
/// hacer es idéntico: cambiar la fuente de cada clip, cada audio y cada capa que lo usaran, sin
/// tocar dónde están ni cómo están cortados.
/// </para>
/// <para>
/// Si el archivo nuevo es más corto, hay clips que se salen. Se acotan, y cada intervalo que se
/// toca queda anotado para que deshacer lo devuelva exactamente donde estaba: acotar pierde
/// información, y sin esta nota deshacer no podría recuperarla.
/// </para>
/// </remarks>
public sealed class ReplaceMediaCommand : IUndoableCommand
{
    private readonly EditProject _project;
    private readonly MediaInfo _from;
    private readonly MediaInfo _to;

    private readonly List<(Clip Clip, TimeSpan In, TimeSpan Out)> _clips = [];
    private readonly List<(AudioClip Clip, TimeSpan In, TimeSpan Out, TimeSpan Start)> _audio = [];
    private readonly List<(OverlayItem Item, TimeSpan SourceIn)> _overlays = [];

    private bool _prepared;

    /// <summary>Crea la operación.</summary>
    /// <param name="project">Proyecto.</param>
    /// <param name="from">Medio que se sustituye.</param>
    /// <param name="to">Medio que ocupa su lugar.</param>
    public ReplaceMediaCommand(EditProject project, MediaInfo from, MediaInfo to)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        _project = project;
        _from = from;
        _to = to;
    }

    /// <inheritdoc/>
    public string Description => _from.IsOffline ? "Reconectar el archivo" : "Sustituir el material";

    /// <summary>Cuántos elementos del montaje cambiaron de archivo.</summary>
    public int AffectedCount => _clips.Count + _audio.Count + _overlays.Count;

    /// <summary>Indica si alguno hubo que acortarlo porque el archivo nuevo es más corto.</summary>
    public bool Trimmed { get; private set; }

    /// <inheritdoc/>
    public void Execute()
    {
        if (!_prepared)
        {
            Collect();
            _prepared = true;
        }

        foreach (var (clip, _, _) in _clips)
        {
            clip.Source = _to;
        }

        foreach (var (clip, _, _, _) in _audio)
        {
            clip.Source = _to;
        }

        foreach (var (item, _) in _overlays)
        {
            item.Media = _to;
        }

        // Los intervalos se acotan después de cambiar la fuente: así lo que se acota es contra el
        // archivo nuevo, que es el que manda a partir de ahora.
        Trimmed = false;
        foreach (var (clip, sourceIn, sourceOut) in _clips)
        {
            Trimmed |= ClampClip(clip, sourceIn, sourceOut);
        }

        foreach (var (clip, sourceIn, sourceOut, start) in _audio)
        {
            Trimmed |= ClampAudio(clip, sourceIn, sourceOut, start);
        }

        foreach (var (item, sourceIn) in _overlays)
        {
            Trimmed |= ClampOverlay(item, sourceIn);
        }

        _project.SwapMedia(_from, _to);
    }

    /// <inheritdoc/>
    public void Undo()
    {
        foreach (var (clip, sourceIn, sourceOut) in _clips)
        {
            clip.Source = _from;
            clip.SetRange(sourceIn, sourceOut);
        }

        foreach (var (clip, sourceIn, sourceOut, start) in _audio)
        {
            clip.Source = _from;
            clip.SetRange(sourceIn, sourceOut, start);
        }

        foreach (var (item, sourceIn) in _overlays)
        {
            item.Media = _from;
            item.SetSourceIn(sourceIn);
        }

        _project.SwapMedia(_to, _from);
    }

    private void Collect()
    {
        foreach (var clip in _project.Timeline.Clips.Where(c => Uses(c.Source)))
        {
            _clips.Add((clip, clip.SourceIn, clip.SourceOut));
        }

        foreach (var track in _project.Sequence.AudioTracks)
        {
            foreach (var clip in track.Clips.Where(c => Uses(c.Source)))
            {
                _audio.Add((clip, clip.SourceIn, clip.SourceOut, clip.TimelineStart));
            }
        }

        foreach (var track in _project.Sequence.OverlayTracks)
        {
            foreach (var item in track.Items.Where(i => i.Media is { } media && Uses(media)))
            {
                _overlays.Add((item, item.SourceIn));
            }
        }
    }

    /// <remarks>
    /// Se compara por ruta y no por identidad: al abrir un proyecto, cada clip recibe la misma
    /// instancia, pero nada garantiza que siga siendo así tras otras operaciones.
    /// </remarks>
    private bool Uses(MediaInfo media) =>
        string.Equals(media.Path, _from.Path, StringComparison.OrdinalIgnoreCase);

    private bool ClampClip(Clip clip, TimeSpan sourceIn, TimeSpan sourceOut)
    {
        var available = _to.Duration;
        if (sourceOut <= available)
        {
            return false;
        }

        var start = sourceIn < available ? sourceIn : TimeSpan.Zero;
        var end = available;

        if (end - start < Clip.MinimumDuration)
        {
            start = TimeSpan.Zero;
            end = available < Clip.MinimumDuration ? available : Clip.MinimumDuration;
        }

        clip.SetRange(start, end);
        return true;
    }

    private bool ClampAudio(AudioClip clip, TimeSpan sourceIn, TimeSpan sourceOut, TimeSpan start)
    {
        var available = _to.Duration;
        if (sourceOut <= available)
        {
            return false;
        }

        var from = sourceIn < available ? sourceIn : TimeSpan.Zero;
        var to = available;

        if (to - from < AudioClip.MinimumDuration)
        {
            from = TimeSpan.Zero;
            to = available < AudioClip.MinimumDuration ? available : AudioClip.MinimumDuration;
        }

        clip.SetRange(from, to, start);
        return true;
    }

    private bool ClampOverlay(OverlayItem item, TimeSpan sourceIn)
    {
        var available = _to.Duration;
        if (sourceIn + item.Duration <= available)
        {
            return false;
        }

        var room = available - item.Duration;
        item.SetSourceIn(room > TimeSpan.Zero ? room : TimeSpan.Zero);
        return true;
    }
}

/// <summary>Crea una carpeta en el panel de medios.</summary>
public sealed class CreateBinCommand : IUndoableCommand
{
    private readonly MediaLibrary _library;
    private readonly string _name;
    private readonly MediaBin _parent;

    private MediaBin? _created;

    /// <summary>Crea la operación.</summary>
    public CreateBinCommand(MediaLibrary library, string name, MediaBin? parent = null)
    {
        ArgumentNullException.ThrowIfNull(library);

        _library = library;
        _name = name;
        _parent = parent ?? library.Root;
    }

    /// <summary>Carpeta creada, disponible tras ejecutar.</summary>
    public MediaBin? Result => _created;

    /// <inheritdoc/>
    public string Description => "Crear una carpeta";

    /// <inheritdoc/>
    public void Execute()
    {
        // Rehacer devuelve la misma carpeta, no una nueva: cualquier medio que se hubiera movido
        // a ella la sigue por identidad.
        if (_created is null)
        {
            _created = _library.CreateBin(_name, _parent);
        }
        else
        {
            _parent.Add(_created);
        }
    }

    /// <inheritdoc/>
    public void Undo()
    {
        if (_created is not null)
        {
            _library.RemoveBin(_created);
        }
    }
}

/// <summary>Cambia el nombre de una carpeta.</summary>
public sealed class RenameBinCommand : IUndoableCommand
{
    private readonly MediaBin _bin;
    private readonly string _name;
    private readonly string _previous;

    /// <summary>Crea la operación.</summary>
    public RenameBinCommand(MediaBin bin, string name)
    {
        ArgumentNullException.ThrowIfNull(bin);

        _bin = bin;
        _name = string.IsNullOrWhiteSpace(name) ? bin.Name : name.Trim();
        _previous = bin.Name;
    }

    /// <inheritdoc/>
    public string Description => "Renombrar la carpeta";

    /// <inheritdoc/>
    public void Execute() => _bin.Name = _name;

    /// <inheritdoc/>
    public void Undo() => _bin.Name = _previous;
}

/// <summary>Borra una carpeta y devuelve su contenido a la de arriba.</summary>
public sealed class RemoveBinCommand : IUndoableCommand
{
    private readonly MediaLibrary _library;
    private readonly MediaBin _bin;
    private readonly MediaBin _parent;

    private readonly List<MediaBin> _children = [];
    private readonly List<MediaInfo> _contents = [];
    private readonly EditProject? _project;

    /// <summary>Crea la operación.</summary>
    /// <param name="library">Organización de medios.</param>
    /// <param name="bin">Carpeta que se borra.</param>
    /// <param name="project">
    /// Proyecto, para poder devolver a su sitio los medios que había dentro al deshacer. Sin él,
    /// deshacer recupera la carpeta pero vacía.
    /// </param>
    public RemoveBinCommand(MediaLibrary library, MediaBin bin, EditProject? project = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(bin);

        _library = library;
        _bin = bin;
        _parent = bin.Parent ?? library.Root;
        _project = project;
    }

    /// <inheritdoc/>
    public string Description => "Borrar una carpeta";

    /// <inheritdoc/>
    public void Execute()
    {
        _children.Clear();
        _children.AddRange(_bin.Children);

        _contents.Clear();
        if (_project is not null)
        {
            _contents.AddRange(_project.Media.Where(m => ReferenceEquals(_library.BinOf(m), _bin)));
        }

        _library.RemoveBin(_bin);
    }

    /// <inheritdoc/>
    public void Undo()
    {
        _parent.Add(_bin);

        foreach (var child in _children)
        {
            _parent.Remove(child);
            _bin.Add(child);
        }

        foreach (var media in _contents)
        {
            _library.MoveToBin(media, _bin);
        }
    }
}

/// <summary>Mueve un medio a una carpeta.</summary>
public sealed class MoveMediaCommand : IUndoableCommand
{
    private readonly MediaLibrary _library;
    private readonly MediaInfo _media;
    private readonly MediaBin _destination;
    private readonly MediaBin _previous;

    /// <summary>Crea la operación.</summary>
    public MoveMediaCommand(MediaLibrary library, MediaInfo media, MediaBin destination)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(destination);

        _library = library;
        _media = media;
        _destination = destination;
        _previous = library.BinOf(media);
    }

    /// <inheritdoc/>
    public string Description => "Mover a una carpeta";

    /// <inheritdoc/>
    public void Execute() => _library.MoveToBin(_media, _destination);

    /// <inheritdoc/>
    public void Undo() => _library.MoveToBin(_media, _previous);
}

/// <summary>Marca un medio con un color.</summary>
public sealed class SetMediaLabelCommand : IUndoableCommand
{
    private readonly MediaLibrary _library;
    private readonly MediaInfo _media;
    private readonly MediaLabel _label;
    private readonly MediaLabel _previous;

    /// <summary>Crea la operación.</summary>
    public SetMediaLabelCommand(MediaLibrary library, MediaInfo media, MediaLabel label)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(media);

        _library = library;
        _media = media;
        _label = label;
        _previous = library.LabelOf(media);
    }

    /// <inheritdoc/>
    public string Description => _label == MediaLabel.None ? "Quitar la etiqueta" : "Etiquetar";

    /// <inheritdoc/>
    public void Execute() => _library.SetLabel(_media, _label);

    /// <inheritdoc/>
    public void Undo() => _library.SetLabel(_media, _previous);
}
