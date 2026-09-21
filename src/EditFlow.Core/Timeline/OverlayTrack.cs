// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

namespace EditFlow.Core.Timeline;

/// <summary>
/// Una capa de superposiciones: textos e imágenes con posición libre que no se solapan en el tiempo.
/// </summary>
/// <remarks>
/// Dos elementos a la vez en una misma pista se pintarían uno encima de otro sin que nada lo
/// indique. Para tener dos a la vez se usan dos pistas, y el orden de las pistas decide cuál va
/// delante. Es el mismo criterio que las pistas de audio.
/// </remarks>
public sealed class OverlayTrack
{
    private readonly List<OverlayItem> _items = [];

    /// <summary>Crea una pista con el nombre indicado.</summary>
    public OverlayTrack(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>Identidad estable de la pista.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Nombre visible, por ejemplo <c>T1</c>.</summary>
    public string Name { get; set; }

    /// <summary>Elementos ordenados por posición.</summary>
    public IReadOnlyList<OverlayItem> Items => _items;

    /// <summary>Oculta toda la pista: no se ve en el preview ni se exporta.</summary>
    public bool IsHidden { get; set; }

    /// <summary>Impide editar los elementos de la pista.</summary>
    public bool IsLocked { get; set; }

    /// <summary>Instante en que termina el último elemento.</summary>
    public TimeSpan End => _items.Count == 0 ? TimeSpan.Zero : _items.Max(i => i.End);

    /// <summary>Comprueba si un intervalo cabe sin chocar con ningún elemento.</summary>
    /// <param name="start">Inicio del intervalo.</param>
    /// <param name="duration">Duración del intervalo.</param>
    /// <param name="ignore">Elemento que se está moviendo y no cuenta como obstáculo.</param>
    /// <remarks>Dos intervalos que solo se tocan por el borde no chocan.</remarks>
    public bool CanPlace(TimeSpan start, TimeSpan duration, OverlayItem? ignore = null)
    {
        if (start < TimeSpan.Zero || duration < OverlayItem.MinimumDuration)
        {
            return false;
        }

        var end = start + duration;
        return !_items.Any(item =>
            !ReferenceEquals(item, ignore) && start < item.End && item.Start < end);
    }

    /// <summary>Añade un elemento; falla si choca con otro o la pista está bloqueada.</summary>
    public bool TryAdd(OverlayItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (IsLocked || !CanPlace(item.Start, item.Duration))
        {
            return false;
        }

        Insert(item);
        return true;
    }

    /// <summary>Elimina un elemento.</summary>
    public bool Remove(OverlayItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return !IsLocked && _items.Remove(item);
    }

    /// <summary>Mueve un elemento a otra posición de la misma pista.</summary>
    public bool TryMove(OverlayItem item, TimeSpan newStart) => TryPlace(item, newStart, item.Duration);

    /// <summary>Cambia posición y duración de un elemento a la vez.</summary>
    /// <returns><see langword="true"/> si se aplicó; falso si chocaba, era demasiado corto o la pista está bloqueada.</returns>
    public bool TryPlace(OverlayItem item, TimeSpan start, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (IsLocked || !_items.Contains(item) || !CanPlace(start, duration, item))
        {
            return false;
        }

        _items.Remove(item);
        item.SetPlacement(start, duration);
        Insert(item);
        return true;
    }

    /// <summary>Devuelve un elemento a una colocación anterior, sin comprobar choques: ya era válida.</summary>
    internal void Reinsert(OverlayItem item, TimeSpan start, TimeSpan duration)
    {
        _items.Remove(item);
        item.SetPlacement(start, duration);
        Insert(item);
    }

    private void Insert(OverlayItem item)
    {
        var index = _items.FindIndex(i => i.Start > item.Start);
        if (index < 0)
        {
            _items.Add(item);
        }
        else
        {
            _items.Insert(index, item);
        }
    }
}
