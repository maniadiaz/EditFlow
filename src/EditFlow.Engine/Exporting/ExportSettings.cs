namespace EditFlow.Engine.Exporting;

/// <summary>Cómo se reparte el bitrate a lo largo del video.</summary>
public enum RateControlMode
{
    /// <summary>
    /// Calidad constante: el bitrate sube y baja según lo exija cada escena.
    /// Es la mejor opción por defecto; el tamaño del archivo no se conoce de antemano.
    /// </summary>
    ConstantQuality,

    /// <summary>
    /// Bitrate variable con objetivo: se apunta a una media y se permiten picos.
    /// Útil cuando importa el tamaño final aproximado.
    /// </summary>
    VariableBitrate,

    /// <summary>
    /// Bitrate constante: se mantiene fijo pase lo que pase. Desperdicia bits en
    /// escenas simples, pero es lo que exigen algunas plataformas de emisión.
    /// </summary>
    ConstantBitrate,
}

/// <summary>Compromiso entre velocidad de codificación y eficiencia de compresión.</summary>
/// <remarks>
/// Cada familia de codificadores nombra sus presets de forma distinta —<c>p1</c> a
/// <c>p7</c> en NVENC, <c>ultrafast</c> a <c>veryslow</c> en x264, números del 0 al 13
/// en SVT-AV1— y en algunas el número sube con la calidad mientras en otras baja.
/// Esta escala unifica la intención y cada familia la traduce a lo suyo.
/// </remarks>
public enum EncodingSpeed
{
    /// <summary>Lo más rápido posible; el archivo saldrá notablemente mayor.</summary>
    Fastest,

    /// <summary>Rápido, con una pérdida de eficiencia moderada.</summary>
    Fast,

    /// <summary>Equilibrio recomendado entre tiempo y tamaño.</summary>
    Balanced,

    /// <summary>Prioriza la compresión; tarda bastante más.</summary>
    Quality,

    /// <summary>Máxima compresión posible; puede ser varias veces más lento.</summary>
    Slowest,
}

/// <summary>Resolución de salida.</summary>
/// <param name="Width">Ancho en píxeles.</param>
/// <param name="Height">Alto en píxeles.</param>
/// <param name="Label">Nombre con el que se muestra, por ejemplo <c>1080p</c>.</param>
public sealed record VideoResolution(int Width, int Height, string Label)
{
    /// <summary>854x480.</summary>
    public static VideoResolution P480 { get; } = new(854, 480, "480p");

    /// <summary>1280x720.</summary>
    public static VideoResolution P720 { get; } = new(1280, 720, "720p");

    /// <summary>1920x1080.</summary>
    public static VideoResolution P1080 { get; } = new(1920, 1080, "1080p");

    /// <summary>2560x1440.</summary>
    public static VideoResolution P1440 { get; } = new(2560, 1440, "1440p");

    /// <summary>3840x2160.</summary>
    public static VideoResolution P2160 { get; } = new(3840, 2160, "4K");

    /// <summary>Resoluciones ofrecidas, de menor a mayor.</summary>
    public static IReadOnlyList<VideoResolution> Presets { get; } =
        [P480, P720, P1080, P1440, P2160];

    /// <summary>Versión vertical de esta resolución, para contenido de móvil.</summary>
    public VideoResolution AsPortrait() => new(Height, Width, Label + " vertical");

    /// <inheritdoc/>
    public override string ToString() => $"{Label} ({Width}x{Height})";
}

/// <summary>Todo lo que define una exportación.</summary>
public sealed record ExportSettings
{
    /// <summary>Ruta del archivo a generar.</summary>
    public required string OutputPath { get; init; }

    /// <summary>Resolución del lienzo de salida.</summary>
    public required VideoResolution Resolution { get; init; }

    /// <summary>Nombre del codificador de FFmpeg, por ejemplo <c>h264_nvenc</c>.</summary>
    public required string EncoderName { get; init; }

    /// <summary>Fotogramas por segundo de salida.</summary>
    public double FrameRate { get; init; } = 30;

    /// <summary>Modo de control de tasa.</summary>
    public RateControlMode RateControl { get; init; } = RateControlMode.ConstantQuality;

    /// <summary>
    /// Calidad deseada, de 1 (peor) a 100 (mejor). Solo aplica en
    /// <see cref="RateControlMode.ConstantQuality"/>.
    /// </summary>
    /// <remarks>
    /// Es una escala normalizada, no el CRF de FFmpeg. Los codificadores usan rangos
    /// distintos —0 a 51 en x264, 0 a 63 en SVT-AV1— y con significados que no se
    /// corresponden: un CRF 23 en AV1 no equivale a un CRF 23 en H.264. Exponer el
    /// número nativo haría que cambiar de códec alterase la calidad en silencio.
    /// <see cref="QualityScale"/> traduce esta escala a la nativa de cada familia.
    /// </remarks>
    public int Quality { get; init; } = 65;

    /// <summary>
    /// Bitrate de video objetivo en kbps. Aplica en los modos
    /// <see cref="RateControlMode.VariableBitrate"/> y <see cref="RateControlMode.ConstantBitrate"/>.
    /// </summary>
    public int VideoBitrateKbps { get; init; }

    /// <summary>Compromiso entre velocidad y compresión.</summary>
    public EncodingSpeed Speed { get; init; } = EncodingSpeed.Balanced;

    /// <summary>Bitrate de la pista de audio AAC en kbps.</summary>
    public int AudioBitrateKbps { get; init; } = 192;

    /// <summary>
    /// Coloca el índice del MP4 al principio del archivo.
    /// </summary>
    /// <remarks>
    /// Sin esto, un MP4 subido a la web debe descargarse entero antes de empezar a
    /// reproducirse, porque el índice queda al final.
    /// </remarks>
    public bool OptimizeForStreaming { get; init; } = true;
}
