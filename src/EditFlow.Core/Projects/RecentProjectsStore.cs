// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;

namespace EditFlow.Core.Projects;

/// <summary>Un proyecto de la lista de recientes de la pantalla de inicio.</summary>
/// <param name="FilePath">Ruta del archivo <c>.editflow</c>.</param>
/// <param name="LastOpenedUtc">Última vez que se abrió o guardó.</param>
/// <param name="ClipCount">Clips de video del montaje.</param>
/// <param name="Duration">Duración del montaje.</param>
/// <param name="ThumbnailPath">Imagen de portada, si se pudo generar.</param>
public sealed record RecentProject(
    string FilePath,
    DateTime LastOpenedUtc,
    int ClipCount,
    TimeSpan Duration,
    string? ThumbnailPath = null)
{
    /// <summary>Nombre que se muestra: el del archivo, sin extensión.</summary>
    public string Name => Path.GetFileNameWithoutExtension(FilePath);
}

/// <summary>
/// Lista de proyectos usados hace poco, guardada en los datos locales del usuario.
/// </summary>
/// <remarks>
/// <para>
/// Es una comodidad, no un dato del proyecto: si el archivo se pierde o se corrompe, la
/// pantalla de inicio simplemente arranca vacía. Por eso ningún fallo de lectura o escritura
/// se propaga como excepción; una lista de recientes no debe impedir abrir el editor.
/// </para>
/// <para>
/// Vive fuera del repositorio y del proyecto a propósito: contiene rutas absolutas a los
/// archivos del usuario.
/// </para>
/// </remarks>
public sealed class RecentProjectsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly int _capacity;

    /// <summary>Crea la lista guardada en el archivo indicado.</summary>
    /// <param name="filePath">Dónde se persiste.</param>
    /// <param name="capacity">Cuántos proyectos se recuerdan como máximo.</param>
    public RecentProjectsStore(string filePath, int capacity = 24)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        _filePath = filePath;
        _capacity = capacity;
    }

    /// <summary>Ubicación por defecto, en los datos locales del usuario.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EditFlow",
        "recent-projects.json");

    /// <summary>Proyectos recientes, del más al menos reciente, sin los que ya no existen.</summary>
    public IReadOnlyList<RecentProject> Load()
    {
        var all = Read();

        // Un proyecto borrado o movido no debe aparecer como si se pudiera abrir.
        var alive = all.Where(p => File.Exists(p.FilePath)).ToList();

        if (alive.Count != all.Count)
        {
            Write(alive);
        }

        return alive;
    }

    /// <summary>Anota un proyecto como el más reciente, sustituyendo su entrada anterior.</summary>
    public void Record(RecentProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        var list = Read()
            .Where(p => !SamePath(p.FilePath, project.FilePath))
            .Prepend(project)
            .Take(_capacity)
            .ToList();

        Write(list);
    }

    /// <summary>Quita un proyecto de la lista. No borra el archivo del proyecto.</summary>
    /// <returns>La portada que tenía, para que quien la creó pueda eliminarla.</returns>
    public string? Remove(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var all = Read();
        var removed = all.FirstOrDefault(p => SamePath(p.FilePath, filePath));

        if (removed is not null)
        {
            Write(all.Where(p => !ReferenceEquals(p, removed)).ToList());
        }

        return removed?.ThumbnailPath;
    }

    // Windows y macOS no distinguen mayúsculas en las rutas; Linux sí, y ahí dos rutas que
    // solo difieren en ellas son archivos distintos.
    private static bool SamePath(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a),
            Path.GetFullPath(b),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private List<RecentProject> Read()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return [];
            }

            var stored = JsonSerializer.Deserialize<List<RecentProject>>(File.ReadAllText(_filePath), Options);

            // Se descarta cualquier entrada a medias: un JSON editado a mano o de una versión
            // futura no debe romper la pantalla de inicio.
            return stored?.Where(p => p is not null && !string.IsNullOrWhiteSpace(p.FilePath)).ToList() ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return [];
        }
    }

    private void Write(List<RecentProject> list)
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Se escribe a un temporal y se renombra: una caída a mitad no deja la lista a medias.
            var temp = _filePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(list, Options));
            File.Move(temp, _filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No poder recordar los recientes no es motivo para fallar.
        }
    }
}
