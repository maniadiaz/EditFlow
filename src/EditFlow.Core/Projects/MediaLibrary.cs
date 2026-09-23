// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Media;

namespace EditFlow.Core.Projects;

/// <summary>Colores con los que se puede marcar un medio o un clip.</summary>
/// <remarks>
/// Son los seis de siempre en cualquier editor. Sirven para lo que cada uno decida —las tomas
/// buenas, lo que falta revisar, de qué cámara viene— y por eso no llevan nombre de función: el
/// significado lo pone quien edita.
/// </remarks>
public enum MediaLabel
{
    /// <summary>Sin marcar.</summary>
    None,

    /// <summary>Rojo.</summary>
    Red,

    /// <summary>Naranja.</summary>
    Orange,

    /// <summary>Amarillo.</summary>
    Yellow,

    /// <summary>Verde.</summary>
    Green,

    /// <summary>Azul.</summary>
    Blue,

    /// <summary>Morado.</summary>
    Purple,
}

/// <summary>
/// Una carpeta del panel de medios, que puede contener otras.
/// </summary>
/// <remarks>
/// Es organización del proyecto, no del disco: mover un medio de carpeta no toca el archivo. Un
/// proyecto con cincuenta tomas, música y rótulos se vuelve inmanejable en una lista plana, y esa
/// es la única razón de que esto exista.
/// </remarks>
public sealed class MediaBin
{
    private readonly List<MediaBin> _children = [];

    internal MediaBin(string name, MediaBin? parent)
    {
        Name = string.IsNullOrWhiteSpace(name) ? "Carpeta" : name.Trim();
        Parent = parent;
    }

    /// <summary>Identidad estable, para seguirla entre operaciones y al guardar.</summary>
    public Guid Id { get; internal init; } = Guid.NewGuid();

    /// <summary>Nombre visible.</summary>
    public string Name { get; internal set; }

    /// <summary>Carpeta que la contiene, o <see langword="null"/> si es la raíz.</summary>
    public MediaBin? Parent { get; internal set; }

    /// <summary>Carpetas que contiene, en el orden en que se crearon.</summary>
    public IReadOnlyList<MediaBin> Children => _children;

    /// <summary>Indica si es la carpeta raíz, que no se puede borrar ni renombrar.</summary>
    public bool IsRoot => Parent is null;

    /// <summary>Esta carpeta y todas las que cuelgan de ella, de fuera adentro.</summary>
    public IEnumerable<MediaBin> AndDescendants()
    {
        yield return this;

        foreach (var child in _children)
        {
            foreach (var descendant in child.AndDescendants())
            {
                yield return descendant;
            }
        }
    }

    /// <summary>Ruta legible desde la raíz, por ejemplo <c>Medios / Cámara A</c>.</summary>
    public string DisplayPath => Parent is null || Parent.IsRoot
        ? Name
        : Parent.DisplayPath + " / " + Name;

    internal void Add(MediaBin child)
    {
        child.Parent = this;
        _children.Add(child);
    }

    internal bool Remove(MediaBin child) => _children.Remove(child);

    /// <summary>Indica si una carpeta es esta misma o cuelga de ella.</summary>
    /// <remarks>
    /// Sirve para impedir que alguien meta una carpeta dentro de sí misma arrastrándola sobre una
    /// de sus hijas, que dejaría el árbol con un ciclo y colgaría al recorrerlo.
    /// </remarks>
    public bool Contains(MediaBin other)
    {
        for (var bin = other; bin is not null; bin = bin.Parent)
        {
            if (ReferenceEquals(bin, this))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc/>
    public override string ToString() => DisplayPath;
}

/// <summary>
/// Cómo están organizados los medios de un proyecto: en qué carpeta está cada uno y con qué color
/// está marcado.
/// </summary>
/// <remarks>
/// Va aparte de <see cref="MediaInfo"/> a propósito. Aquel es un valor inmutable con los datos que
/// devuelve ffprobe, comparable por contenido y compartido por todos los clips que usan el mismo
/// archivo; la carpeta y la etiqueta, en cambio, son decisiones del proyecto que cambian sin que
/// el archivo cambie. Mezclarlas obligaría a reemplazar el medio entero —y con él todos sus
/// clips— cada vez que alguien lo arrastra a otra carpeta.
/// </remarks>
public sealed class MediaLibrary
{
    private readonly Dictionary<string, MediaBin> _bins = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MediaLabel> _labels = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Carpeta raíz, que siempre existe.</summary>
    public MediaBin Root { get; } = new("Todo", null);

    /// <summary>Todas las carpetas, de fuera adentro, empezando por la raíz.</summary>
    public IEnumerable<MediaBin> AllBins() => Root.AndDescendants();

    /// <summary>Crea una carpeta dentro de otra.</summary>
    /// <param name="name">Nombre visible.</param>
    /// <param name="parent">Carpeta que la contendrá; la raíz si se omite.</param>
    public MediaBin CreateBin(string name, MediaBin? parent = null)
    {
        var bin = new MediaBin(name, parent ?? Root);
        (parent ?? Root).Add(bin);
        return bin;
    }

    /// <summary>Mete una carpeta dentro de otra.</summary>
    /// <returns><see langword="false"/> si el movimiento dejaría el árbol con un ciclo.</returns>
    public bool MoveBin(MediaBin bin, MediaBin destination)
    {
        ArgumentNullException.ThrowIfNull(bin);
        ArgumentNullException.ThrowIfNull(destination);

        if (bin.IsRoot || bin.Contains(destination))
        {
            return false;
        }

        bin.Parent?.Remove(bin);
        destination.Add(bin);
        return true;
    }

    /// <summary>
    /// Borra una carpeta y devuelve sus medios y subcarpetas a la de arriba.
    /// </summary>
    /// <remarks>
    /// Borrar la carpeta no borra los medios: se quedan en el proyecto, un nivel más arriba. Que
    /// una operación de organización pudiera tirar material sería una trampa.
    /// </remarks>
    public bool RemoveBin(MediaBin bin)
    {
        ArgumentNullException.ThrowIfNull(bin);

        if (bin.IsRoot || bin.Parent is not { } parent)
        {
            return false;
        }

        foreach (var child in bin.Children.ToList())
        {
            bin.Remove(child);
            parent.Add(child);
        }

        foreach (var path in _bins.Where(e => ReferenceEquals(e.Value, bin)).Select(e => e.Key).ToList())
        {
            _bins[path] = parent;
        }

        return parent.Remove(bin);
    }

    /// <summary>Carpeta en la que está un medio; la raíz si nadie lo movió.</summary>
    public MediaBin BinOf(MediaInfo media)
    {
        ArgumentNullException.ThrowIfNull(media);
        return _bins.TryGetValue(media.Path, out var bin) ? bin : Root;
    }

    /// <summary>Mueve un medio a una carpeta.</summary>
    public void MoveToBin(MediaInfo media, MediaBin bin)
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(bin);

        if (bin.IsRoot)
        {
            _bins.Remove(media.Path);
        }
        else
        {
            _bins[media.Path] = bin;
        }
    }

    /// <summary>Etiqueta de color de un medio.</summary>
    public MediaLabel LabelOf(MediaInfo media)
    {
        ArgumentNullException.ThrowIfNull(media);
        return _labels.TryGetValue(media.Path, out var label) ? label : MediaLabel.None;
    }

    /// <summary>Marca un medio con un color.</summary>
    public void SetLabel(MediaInfo media, MediaLabel label)
    {
        ArgumentNullException.ThrowIfNull(media);

        if (label == MediaLabel.None)
        {
            _labels.Remove(media.Path);
        }
        else
        {
            _labels[media.Path] = label;
        }
    }

    /// <summary>
    /// Traslada la carpeta y la etiqueta de un medio a otro, al reconectarlo o sustituirlo.
    /// </summary>
    /// <remarks>
    /// Todo se guarda por ruta, así que sin esto reconectar un archivo lo devolvería a la raíz sin
    /// marcar, deshaciendo la organización justo cuando más molesta.
    /// </remarks>
    internal void Carry(MediaInfo from, MediaInfo to)
    {
        if (string.Equals(from.Path, to.Path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_bins.Remove(from.Path, out var bin))
        {
            _bins[to.Path] = bin;
        }

        if (_labels.Remove(from.Path, out var label))
        {
            _labels[to.Path] = label;
        }
    }

    /// <summary>Olvida lo que se sabía de un medio que ya no está en el proyecto.</summary>
    internal void Forget(MediaInfo media)
    {
        _bins.Remove(media.Path);
        _labels.Remove(media.Path);
    }

    /// <summary>Vacía la organización; se usa al cargar otro proyecto.</summary>
    internal void Clear()
    {
        _bins.Clear();
        _labels.Clear();

        foreach (var child in Root.Children.ToList())
        {
            Root.Remove(child);
        }
    }
}
