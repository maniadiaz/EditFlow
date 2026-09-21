// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EditFlow.Engine.Proxies;

/// <summary>Carpeta donde viven las copias de edición y la regla que las asocia a su original.</summary>
/// <remarks>
/// <para>
/// La clave de cada copia sale de la ruta del original, su tamaño y su fecha de
/// modificación. Si el archivo cambia, la clave cambia y la copia vieja deja de encontrarse
/// sola: nadie edita sobre una versión desfasada de su video sin enterarse.
/// </para>
/// <para>
/// Las copias no se guardan junto al proyecto sino en una caché aparte del usuario. Son
/// derivadas y reconstruibles, así que no deben viajar al copiar o compartir un proyecto ni
/// acabar por accidente en un repositorio.
/// </para>
/// </remarks>
public sealed class ProxyCache
{
    /// <summary>Se sube al cambiar cómo se genera la copia, para invalidar las anteriores.</summary>
    public const int Version = 1;

    private const string Extension = ".mp4";
    private const string PartialSuffix = ".partial";

    /// <summary>Crea la caché en la carpeta indicada.</summary>
    public ProxyCache(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = directory;
    }

    /// <summary>Carpeta por defecto, en los datos locales del usuario.</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EditFlow",
        "proxies");

    /// <summary>Carpeta de la caché.</summary>
    public string Directory { get; }

    /// <summary>Ruta que corresponde a la copia de un original, o <see langword="null"/> si no existe.</summary>
    public string? PathFor(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var info = new FileInfo(sourcePath);
        if (!info.Exists)
        {
            return null;
        }

        var identity = string.Create(
            CultureInfo.InvariantCulture,
            $"{Version}|{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Path.Combine(Directory, Convert.ToHexString(hash, 0, 16).ToLowerInvariant() + Extension);
    }

    /// <summary>Obtiene la copia ya generada de un original, si existe y está completa.</summary>
    public bool TryGet(string sourcePath, out string proxyPath)
    {
        proxyPath = string.Empty;

        var candidate = PathFor(sourcePath);
        if (candidate is null)
        {
            return false;
        }

        var info = new FileInfo(candidate);
        if (!info.Exists || info.Length == 0)
        {
            return false;
        }

        proxyPath = candidate;
        return true;
    }

    /// <summary>Ruta temporal en la que se escribe una copia antes de darla por buena.</summary>
    public static string PartialPathFor(string proxyPath) => proxyPath + PartialSuffix;

    /// <summary>Anota que una copia se acaba de usar, para que la limpieza conserve las recientes.</summary>
    public void Touch(string proxyPath)
    {
        try
        {
            File.SetLastWriteTimeUtc(proxyPath, DateTime.UtcNow);
        }
        catch (IOException)
        {
            // Solo afecta al orden de limpieza.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Suma el tamaño de todas las copias.</summary>
    public long SizeBytes() => Files().Sum(f => f.Length);

    /// <summary>
    /// Borra copias, de la menos usada a la más reciente, hasta que quepan en el límite.
    /// </summary>
    /// <returns>Cuántos bytes se liberaron.</returns>
    /// <remarks>
    /// Los temporales de una generación interrumpida se eliminan siempre: no sirven para nada.
    /// </remarks>
    public long TrimTo(long maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);

        long freed = 0;

        var all = Files().ToList();

        foreach (var partial in all.Where(f => f.Name.EndsWith(PartialSuffix, StringComparison.Ordinal)
                                               && f.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-1)))
        {
            freed += TryDelete(partial);
        }

        var proxies = all
            .Where(f => f.Name.EndsWith(Extension, StringComparison.Ordinal))
            .OrderBy(f => f.LastWriteTimeUtc)
            .ToList();

        var total = proxies.Sum(f => f.Length);

        foreach (var proxy in proxies)
        {
            if (total <= maxBytes)
            {
                break;
            }

            var length = proxy.Length;
            var removed = TryDelete(proxy);
            total -= removed > 0 ? length : 0;
            freed += removed;
        }

        return freed;
    }

    private IEnumerable<FileInfo> Files()
    {
        var directory = new DirectoryInfo(Directory);
        return directory.Exists ? directory.EnumerateFiles() : [];
    }

    private static long TryDelete(FileInfo file)
    {
        try
        {
            var length = file.Length;
            file.Delete();
            return length;
        }
        catch (IOException)
        {
            // En uso: se intentará en otra limpieza.
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
