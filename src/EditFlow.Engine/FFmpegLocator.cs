using System.Runtime.InteropServices;

namespace EditFlow.Engine;

/// <summary>
/// Localiza los ejecutables de FFmpeg y ffprobe.
/// </summary>
/// <remarks>
/// El orden de búsqueda prioriza deliberadamente los binarios que EditFlow empaqueta
/// sobre los del sistema. Un FFmpeg instalado por el usuario puede estar compilado sin
/// <c>--enable-nvenc</c>, en cuyo caso la codificación por GPU desaparecería sin
/// explicación aparente. Solo se recurre al PATH como último recurso.
/// </remarks>
public static class FFmpegLocator
{
    private static readonly string FFmpegName = ExecutableName("ffmpeg");
    private static readonly string FFprobeName = ExecutableName("ffprobe");

    /// <summary>Localiza FFmpeg, o lanza una excepción con instrucciones si no aparece.</summary>
    /// <exception cref="FFmpegNotFoundException">Si no se encuentra en ninguna ubicación conocida.</exception>
    public static FFmpegTools Locate()
    {
        if (TryLocate(out var tools, out var searched))
        {
            return tools;
        }

        throw new FFmpegNotFoundException(
            $"""
             No se encontraron {FFmpegName} y {FFprobeName}.

             Ejecuta el script de descarga desde la raíz del repositorio:

                 pwsh tools/fetch-ffmpeg.ps1

             Ubicaciones consultadas:
             {string.Join(Environment.NewLine, searched.Select(s => "  - " + s))}
             """);
    }

    /// <summary>Intenta localizar FFmpeg sin lanzar excepciones.</summary>
    /// <param name="tools">Rutas encontradas, o <see langword="null"/>.</param>
    /// <param name="searchedLocations">Ubicaciones consultadas, para diagnóstico.</param>
    public static bool TryLocate(
        out FFmpegTools tools,
        out IReadOnlyList<string> searchedLocations)
    {
        var searched = new List<string>();

        foreach (var (directory, origin) in CandidateDirectories())
        {
            searched.Add(directory);

            var ffmpeg = Path.Combine(directory, FFmpegName);
            var ffprobe = Path.Combine(directory, FFprobeName);

            if (File.Exists(ffmpeg) && File.Exists(ffprobe))
            {
                tools = new FFmpegTools(ffmpeg, ffprobe, origin);
                searchedLocations = searched;
                return true;
            }
        }

        // Último recurso: el PATH del sistema.
        var fromPath = FindOnPath(FFmpegName);
        var probeFromPath = FindOnPath(FFprobeName);
        searched.Add("PATH del sistema");

        if (fromPath is not null && probeFromPath is not null)
        {
            tools = new FFmpegTools(fromPath, probeFromPath, "PATH del sistema");
            searchedLocations = searched;
            return true;
        }

        tools = null!;
        searchedLocations = searched;
        return false;
    }

    private static IEnumerable<(string Directory, string Origin)> CandidateDirectories()
    {
        var baseDirectory = AppContext.BaseDirectory;

        // 1. Empaquetado junto a la aplicación publicada.
        yield return (Path.Combine(baseDirectory, "ffmpeg"), "empaquetado con la aplicación");
        yield return (baseDirectory, "junto al ejecutable");

        // 2. Desarrollo: subir desde bin/<config>/<tfm>/ hasta encontrar tools/ffmpeg/<rid>/.
        var rid = RuntimeIdentifier();
        var directory = new DirectoryInfo(baseDirectory);

        for (var depth = 0; depth < 8 && directory is not null; depth++)
        {
            var candidate = Path.Combine(directory.FullName, "tools", "ffmpeg", rid);
            if (Directory.Exists(candidate))
            {
                yield return (candidate, $"repositorio (tools/ffmpeg/{rid})");
                yield break;
            }

            directory = directory.Parent;
        }
    }

    private static string RuntimeIdentifier()
    {
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            var other => other.ToString().ToLowerInvariant(),
        };

        var platform =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win" :
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" :
            "linux";

        return $"{platform}-{architecture}";
    }

    private static string ExecutableName(string name) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? name + ".exe" : name;

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(directory.Trim('"'), fileName);
            }
            catch (ArgumentException)
            {
                // Una entrada del PATH con caracteres inválidos no debe tumbar la búsqueda.
                continue;
            }

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}

/// <summary>Se lanza cuando no se encuentran los ejecutables de FFmpeg.</summary>
public sealed class FFmpegNotFoundException : Exception
{
    /// <inheritdoc cref="FFmpegNotFoundException"/>
    public FFmpegNotFoundException(string message) : base(message) { }

    /// <inheritdoc cref="FFmpegNotFoundException"/>
    public FFmpegNotFoundException() : base("No se encontró FFmpeg.") { }

    /// <inheritdoc cref="FFmpegNotFoundException"/>
    public FFmpegNotFoundException(string message, Exception innerException)
        : base(message, innerException) { }
}
