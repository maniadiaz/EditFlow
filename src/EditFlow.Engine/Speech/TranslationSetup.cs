// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using System.Runtime.InteropServices;

namespace EditFlow.Engine.Speech;

/// <summary>Modelo de lenguaje que traduce los subtítulos.</summary>
/// <param name="Id">Identificador corto.</param>
/// <param name="Label">Nombre que se muestra.</param>
/// <param name="FileName">Archivo del modelo.</param>
/// <param name="Url">De dónde se descarga.</param>
/// <param name="Sha256">Huella esperada del archivo.</param>
/// <param name="Bytes">Tamaño del archivo.</param>
public sealed record TranslationModel(string Id, string Label, string FileName, Uri Url, string Sha256, long Bytes)
{
    /// <summary>
    /// Qwen3.5 de 4 000 millones de parámetros, licencia Apache-2.0.
    /// </summary>
    /// <remarks>
    /// Se compararon tres modelos traduciendo el mismo diálogo del inglés al español: el de 1 700 millones
    /// (Qwen3) entiende mal frases enteras («she's not the only one who grieves» → «ella no es la única que
    /// duerme»); Qwen3 de 4 000 millones acierta la mayoría pero se equivoca de género; este acierta casi todo y
    /// da frases que suenan naturales. Es el más pequeño que traduce diálogo con calidad aceptable.
    /// </remarks>
    public static TranslationModel Default { get; } = new(
        "qwen3.5-4b",
        "Qwen3.5 4B",
        "Qwen3.5-4B-Q4_K_M.gguf",
        new Uri("https://huggingface.co/unsloth/Qwen3.5-4B-GGUF/resolve/main/Qwen3.5-4B-Q4_K_M.gguf"),
        "00fe7986ff5f6b463e62455821146049db6f9313603938a70800d1fb69ef11a4",
        2_740_937_888);
}

/// <summary>
/// Dónde vive el traductor de subtítulos y cómo se instala: el servidor de llama.cpp (MIT) y el modelo.
/// </summary>
/// <remarks>
/// Igual que Whisper: se guarda en la carpeta de datos del usuario, se descarga solo si se pide traducir
/// y cada archivo se verifica contra una huella SHA-256 fijada aquí antes de usarse.
/// </remarks>
public static class TranslationSetup
{
    /// <summary>Versión de los binarios de llama.cpp que se instalan.</summary>
    public const string RuntimeVersion = "b11090";

    private static readonly Uri RuntimeUrl = new(
        "https://github.com/ggml-org/llama.cpp/releases/download/" + RuntimeVersion + "/llama-" + RuntimeVersion + "-bin-win-cpu-x64.zip");

    private const string RuntimeSha256 = "dcfe8f2c33d9d7d0a895f68e2eab28d7c7fed8efd44c621f87c298a60cfb5d9c";
    private const long RuntimeBytes = 18_548_528;

    /// <summary>Si hay binarios de llama.cpp para este sistema. Por ahora, solo Windows de 64 bits.</summary>
    public static bool IsSupported =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && RuntimeInformation.OSArchitecture == Architecture.X64;

    /// <summary>Tamaño de la descarga de los binarios.</summary>
    public static long RuntimeDownloadBytes => RuntimeBytes;

    /// <summary>Ruta del servidor de llama.cpp si está instalado.</summary>
    public static string? LocateServer(string? root = null)
    {
        var path = Path.Combine(root ?? WhisperSetup.DefaultRoot, "llm", "bin", "llama-server.exe");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Ruta del archivo del modelo, exista o no.</summary>
    public static string ModelPath(TranslationModel model, string? root = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        return Path.Combine(root ?? WhisperSetup.DefaultRoot, "llm", "models", model.FileName);
    }

    /// <summary>Indica si el modelo ya está descargado.</summary>
    public static bool HasModel(TranslationModel model, string? root = null) => File.Exists(ModelPath(model, root));

    /// <summary>Descarga e instala el servidor de llama.cpp.</summary>
    public static async Task InstallRuntimeAsync(
        IProgress<double>? progress = null,
        string? root = null,
        Uri? source = null,
        string? expectedSha256 = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsSupported && source is null)
        {
            throw new InvalidOperationException("La traducción de subtítulos solo está disponible por ahora en Windows de 64 bits.");
        }

        root ??= WhisperSetup.DefaultRoot;
        var temporary = Path.Combine(root, "llm", "runtime.zip.part");

        await WhisperSetup.DownloadAsync(source ?? RuntimeUrl, temporary, expectedSha256 ?? RuntimeSha256, progress, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            ExtractRuntime(temporary, Path.Combine(root, "llm", "bin"));
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /// <summary>Descarga el modelo y comprueba que es el esperado antes de dejarlo en su sitio.</summary>
    public static async Task InstallModelAsync(
        TranslationModel model,
        IProgress<double>? progress = null,
        string? root = null,
        Uri? source = null,
        string? expectedSha256 = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);

        var destination = ModelPath(model, root);
        var temporary = destination + ".part";

        await WhisperSetup.DownloadAsync(source ?? model.Url, temporary, expectedSha256 ?? model.Sha256, progress, cancellationToken)
            .ConfigureAwait(false);

        File.Move(temporary, destination, overwrite: true);
    }

    /// <summary>Saca del zip el servidor y sus bibliotecas, sin rutas del archivo.</summary>
    internal static void ExtractRuntime(string zipPath, string destination)
    {
        Directory.CreateDirectory(destination);

        using var archive = ZipFile.OpenRead(zipPath);

        foreach (var entry in archive.Entries)
        {
            var name = entry.Name;

            // El servidor y todas las bibliotecas; el zip trae además una veintena de herramientas que no hacen
            // falta. Se usa el nombre sin carpeta, así ninguna entrada puede escribir fuera del destino.
            var needed = name.Equals("llama-server.exe", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

            if (needed)
            {
                entry.ExtractToFile(Path.Combine(destination, name), overwrite: true);
            }
        }

        if (!File.Exists(Path.Combine(destination, "llama-server.exe")))
        {
            throw new InvalidOperationException("El paquete descargado no contiene llama-server.exe.");
        }
    }
}
