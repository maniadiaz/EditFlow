// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Media;
using EditFlow.Core.Timeline;

namespace EditFlow.Core.Projects;

/// <summary>
/// Un proyecto de edición: los medios importados y el montaje que se ha hecho con ellos.
/// </summary>
/// <remarks>
/// El proyecto no guarda video: solo qué archivos se usaron y qué intervalo de cada uno
/// se reproduce. Un montaje de una hora ocupa unos pocos kilobytes, y guardar es
/// instantáneo por grande que sea el material.
/// </remarks>
public sealed class EditProject
{
    private readonly List<MediaInfo> _media = [];

    /// <summary>Ruta del archivo <c>.editflow</c>, o <see langword="null"/> si nunca se guardó.</summary>
    public string? FilePath { get; set; }

    /// <summary>Medios importados, en el orden en que se añadieron.</summary>
    public IReadOnlyList<MediaInfo> Media => _media;

    /// <summary>Montaje completo: pista de video y pistas de audio.</summary>
    public EditSequence Sequence { get; } = new();

    /// <summary>Pista principal de video.</summary>
    /// <remarks>Atajo a <see cref="EditSequence.Video"/>, que es lo que casi todo el código usa.</remarks>
    public VideoTimeline Timeline => Sequence.Video;

    /// <summary>Indica si hay cambios sin guardar.</summary>
    public bool HasUnsavedChanges { get; private set; }

    /// <summary>Nombre para mostrar en el título de la ventana.</summary>
    public string DisplayName =>
        FilePath is null
            ? "Proyecto sin título"
            : Path.GetFileNameWithoutExtension(FilePath);

    /// <summary>Añade un medio al proyecto si no estaba ya.</summary>
    /// <returns>El medio del proyecto, que puede ser uno previo con la misma ruta.</returns>
    /// <remarks>
    /// Importar dos veces el mismo archivo no debe duplicar la entrada: el panel de medios
    /// se llenaría de repetidos y el usuario no sabría cuál es cuál.
    /// </remarks>
    public MediaInfo AddMedia(MediaInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        var existing = _media.FirstOrDefault(m =>
            string.Equals(m.Path, info.Path, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            return existing;
        }

        _media.Add(info);
        MarkDirty();
        return info;
    }

    /// <summary>Cómo están organizados los medios: carpetas y etiquetas de color.</summary>
    public MediaLibrary Library { get; } = new();

    /// <summary>
    /// Cambia un medio por otro en la lista del proyecto, conservando su sitio.
    /// </summary>
    /// <remarks>
    /// Solo toca la lista: de los clips se encarga <see cref="ReplaceMediaCommand"/>, que es quien
    /// sabe además cómo deshacerlo. Conservar la posición importa porque el panel de medios los
    /// muestra en el orden en que se importaron, y reconectar un archivo no debería mandarlo al final.
    /// </remarks>
    internal void SwapMedia(MediaInfo from, MediaInfo to)
    {
        var index = _media.FindIndex(m =>
            string.Equals(m.Path, from.Path, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
        {
            return;
        }

        // Reconectar a un archivo que ya estaba importado dejaría dos entradas iguales.
        var duplicate = _media.FindIndex(m =>
            string.Equals(m.Path, to.Path, StringComparison.OrdinalIgnoreCase));

        _media[index] = to;

        if (duplicate >= 0 && duplicate != index)
        {
            _media.RemoveAt(duplicate);
        }

        Library.Carry(from, to);
        MarkDirty();
    }

    /// <summary>Medios cuyo archivo no se encontró al abrir el proyecto.</summary>
    public IEnumerable<MediaInfo> OfflineMedia => _media.Where(m => m.IsOffline);

    /// <summary>Indica si falta algún archivo por reconectar.</summary>
    public bool HasOfflineMedia => _media.Any(m => m.IsOffline);

    /// <summary>Sustituye la lista de medios; usado al cargar un proyecto.</summary>
    internal void ReplaceMedia(IEnumerable<MediaInfo> media)
    {
        ArgumentNullException.ThrowIfNull(media);

        _media.Clear();
        _media.AddRange(media);
    }

    /// <summary>Vacía la organización de medios; usado al cargar un proyecto.</summary>
    internal void ClearLibrary() => Library.Clear();

    /// <summary>Se dispara cuando el proyecto pasa a tener, o deja de tener, cambios sin guardar.</summary>
    /// <remarks>
    /// El aviso vive aquí y no en quien llama porque hay varios caminos que ensucian el
    /// proyecto: importar un medio, editar la timeline o cargar otro archivo. Si cada uno
    /// tuviera que acordarse de avisar, bastaría olvidarlo en uno para que el título de
    /// la ventana dejara de mostrar que hay trabajo sin guardar.
    /// </remarks>
    public event EventHandler? UnsavedChangesChanged;

    /// <summary>Registra que el proyecto cambió desde el último guardado.</summary>
    public void MarkDirty() => SetUnsaved(true);

    /// <summary>Registra que el proyecto acaba de guardarse.</summary>
    public void MarkSaved() => SetUnsaved(false);

    private void SetUnsaved(bool value)
    {
        if (HasUnsavedChanges == value)
        {
            return;
        }

        HasUnsavedChanges = value;
        UnsavedChangesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Vacía el proyecto por completo.</summary>
    public void Clear()
    {
        _media.Clear();
        Timeline.Clear();
        foreach (var track in Sequence.AudioTracks.ToArray())
        {
            Sequence.RemoveAudioTrack(track);
        }

        FilePath = null;
        SetUnsaved(false);
    }
}
