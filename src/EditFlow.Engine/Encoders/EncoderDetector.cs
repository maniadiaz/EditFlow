using EditFlow.Engine.Execution;

namespace EditFlow.Engine.Encoders;

/// <summary>
/// Determina qué codificadores de video se pueden usar realmente en esta máquina.
/// </summary>
/// <remarks>
/// <para>
/// La detección tiene dos pasos, y <b>el segundo es el que la mayoría de aplicaciones
/// omite</b>: que un codificador aparezca en <c>ffmpeg -encoders</c> significa únicamente
/// que la build lo incluye, no que funcione. En una máquina con GPU NVIDIA, FFmpeg lista
/// igualmente <c>h264_qsv</c> y <c>h264_amf</c>; intentar usarlos falla al arrancar la
/// codificación, después de que el usuario ya configuró y lanzó la exportación.
/// </para>
/// <para>
/// Por eso cada candidato se somete a una codificación real de 0,2 segundos contra
/// <c>/dev/null</c>. Cuesta unos milisegundos y convierte un fallo tardío y confuso en
/// una opción desactivada con su motivo a la vista.
/// </para>
/// </remarks>
public sealed class EncoderDetector
{
    private readonly FFmpegTools _tools;

    /// <summary>Crea un detector que usará los ejecutables indicados.</summary>
    public EncoderDetector(FFmpegTools tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        _tools = tools;
    }

    /// <summary>
    /// Devuelve todos los codificadores conocidos, marcados como disponibles o no.
    /// </summary>
    /// <remarks>
    /// Las pruebas se ejecutan <b>en serie, nunca en paralelo</b>. Las GPU de consumo
    /// limitan el número de sesiones de codificación simultáneas (NVENC históricamente a
    /// entre tres y ocho), así que lanzar doce pruebas a la vez agotaría las sesiones y
    /// marcaría como no disponibles codificadores que sí funcionan.
    /// </remarks>
    public async Task<IReadOnlyList<EncoderInfo>> DetectAsync(CancellationToken cancellationToken = default)
    {
        var listed = await ListEncodersAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<EncoderInfo>(EncoderCatalog.Known.Count);

        foreach (var definition in EncoderCatalog.Known)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Un motor de otra plataforma no se prueba ni se muestra: en Windows,
            // VideoToolbox y VA-API solo podrían aparecer desactivados para siempre.
            if (!EncoderPlatforms.IsApplicable(definition.Backend))
            {
                continue;
            }

            if (!listed.Contains(definition.Name))
            {
                results.Add(new EncoderInfo(
                    definition.Name,
                    definition.Codec,
                    definition.Backend,
                    definition.DisplayName,
                    IsAvailable: false,
                    UnavailableReason: "Esta compilación de FFmpeg no incluye el codificador."));
                continue;
            }

            var probe = await ProbeAsync(definition.Name, cancellationToken).ConfigureAwait(false);

            results.Add(probe.Succeeded
                ? new EncoderInfo(
                    definition.Name,
                    definition.Codec,
                    definition.Backend,
                    definition.DisplayName,
                    IsAvailable: true)
                : new EncoderInfo(
                    definition.Name,
                    definition.Codec,
                    definition.Backend,
                    definition.DisplayName,
                    IsAvailable: false,
                    UnavailableReason: DescribeFailure(probe.StandardError, definition.Backend),
                    Diagnostics: probe.StandardError.Trim()));
        }

        return results;
    }

    /// <summary>Ejecuta <c>ffmpeg -encoders</c> y devuelve los nombres de codificadores de video.</summary>
    public async Task<IReadOnlySet<string>> ListEncodersAsync(CancellationToken cancellationToken = default)
    {
        var result = await ProcessRunner
            .RunAsync(_tools.FFmpegPath, ["-hide_banner", "-encoders"], cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"'ffmpeg -encoders' falló con código {result.ExitCode}: {result.StandardError.Trim()}");
        }

        return EncoderCatalog.ParseListedEncoders(result.StandardOutput);
    }

    /// <summary>
    /// Intenta codificar un fragmento mínimo con el codificador indicado.
    /// </summary>
    /// <remarks>
    /// El éxito se decide <b>por el código de salida</b>, nunca por si stderr quedó vacío.
    /// SVT-AV1 vuelca un banner informativo completo en stderr en cada codificación
    /// correcta; tomar "stderr no vacío" como error marcaría <c>libsvtav1</c> como no
    /// disponible en todas las máquinas.
    /// </remarks>
    public Task<ProcessResult> ProbeAsync(string encoderName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encoderName);

        var arguments = encoderName.EndsWith("_vaapi", StringComparison.Ordinal)
            ? VaapiProbeArguments(encoderName)
            : GenericProbeArguments(encoderName);

        return ProcessRunner.RunAsync(_tools.FFmpegPath, arguments, cancellationToken);
    }

    private static string[] GenericProbeArguments(string encoderName) =>
    [
        "-hide_banner",
        "-loglevel", "error",
        "-f", "lavfi",
        "-i", "color=c=black:s=256x256:r=30:d=0.2",
        "-c:v", encoderName,
        "-f", "null",
        "-",
    ];

    /// <summary>
    /// Argumentos de prueba para VA-API, que no admite el mismo tratamiento que el resto.
    /// </summary>
    /// <remarks>
    /// Los codificadores VA-API solo aceptan frames que ya residen en memoria de la GPU.
    /// Alimentarlos directamente desde un filtro de software falla con un mensaje sobre
    /// conversión de formatos entre filtros, que no tiene nada que ver con la verdadera
    /// causa y haría creer que el codificador está roto.
    ///
    /// Hace falta abrir el dispositivo con <c>-init_hw_device</c> y subir los frames con
    /// <c>hwupload</c>. Esta ruta es exclusiva de Linux y no ha podido probarse contra
    /// hardware real todavía; si falla, el motivo mostrado será el error tal cual de
    /// FFmpeg en lugar de una explicación traducida.
    /// </remarks>
    private static string[] VaapiProbeArguments(string encoderName) =>
    [
        "-hide_banner",
        "-loglevel", "error",
        "-init_hw_device", "vaapi=va:/dev/dri/renderD128",
        "-filter_hw_device", "va",
        "-f", "lavfi",
        "-i", "color=c=black:s=256x256:r=30:d=0.2",
        "-vf", "format=nv12,hwupload",
        "-c:v", encoderName,
        "-f", "null",
        "-",
    ];

    /// <summary>
    /// Traduce el error de FFmpeg a una explicación que el usuario pueda accionar.
    /// </summary>
    /// <remarks>
    /// "Error creating a MFX session: -9" es exacto pero inútil para quien solo quiere
    /// saber por qué no puede exportar con su tarjeta. El texto original se conserva
    /// aparte en <see cref="EncoderInfo.Diagnostics"/>.
    /// </remarks>
    internal static string DescribeFailure(string standardError, EncoderBackend backend)
    {
        var error = standardError ?? string.Empty;

        if (Contains(error, "MFX session") || Contains(error, "libmfx") || Contains(error, "QSV"))
        {
            return "Intel Quick Sync no está disponible: no se detectó una GPU Intel compatible o falta su driver.";
        }

        if (Contains(error, "amfrt64") || Contains(error, "amfrt32") || Contains(error, "AMF"))
        {
            return "AMD AMF no está disponible: no se detectó una GPU AMD o falta su driver.";
        }

        if (Contains(error, "Cannot load nvcuda") ||
            Contains(error, "Cannot load nvEncodeAPI") ||
            Contains(error, "no capable devices") ||
            Contains(error, "No NVENC capable devices"))
        {
            return "NVENC no está disponible: no se detectó una GPU NVIDIA compatible.";
        }

        if (Contains(error, "OpenEncodeSessionEx failed") || Contains(error, "out of memory"))
        {
            return "NVENC rechazó la sesión: puede que otra aplicación esté usando el codificador. Ciérrala y vuelve a intentarlo.";
        }

        if (Contains(error, "driver does not support") || Contains(error, "minimum required Nvidia driver"))
        {
            return "El driver de NVIDIA es demasiado antiguo para este codificador. Actualízalo.";
        }

        if (Contains(error, "Unknown encoder"))
        {
            return "Esta compilación de FFmpeg no incluye el codificador.";
        }

        if (backend == EncoderBackend.Vaapi &&
            (Contains(error, "Failed to open") ||
             Contains(error, "No such file or directory") ||
             Contains(error, "Device creation failed") ||
             Contains(error, "Impossible to convert between the formats")))
        {
            return "VA-API no está disponible: no se pudo abrir el dispositivo de renderizado (/dev/dri).";
        }

        var firstMeaningfulLine = error
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0 && !line.StartsWith("Svt[", StringComparison.Ordinal));

        return string.IsNullOrEmpty(firstMeaningfulLine)
            ? $"El codificador falló la prueba de funcionamiento ({backend})."
            : firstMeaningfulLine;
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
