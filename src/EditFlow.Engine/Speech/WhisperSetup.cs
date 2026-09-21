// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace EditFlow.Engine.Speech;

/// <summary>Un modelo de Whisper que se puede descargar.</summary>
/// <param name="Id">Identificador corto.</param>
/// <param name="Label">Nombre que se muestra.</param>
/// <param name="FileName">Archivo del modelo.</param>
/// <param name="Url">De dónde se descarga.</param>
/// <param name="Sha256">Huella esperada del archivo, en hexadecimal.</param>
/// <param name="Bytes">Tamaño del archivo.</param>
public sealed record WhisperModel(string Id, string Label, string FileName, Uri Url, string Sha256, long Bytes)
{
    /// <summary>El más pequeño: rápido y suficiente para voz clara. Se equivoca más en idiomas distintos del inglés.</summary>
    public static WhisperModel Base { get; } = new(
        "base",
        "Base · rápido",
        "ggml-base.bin",
        new Uri("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin"),
        "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe",
        147_951_465);

    /// <summary>Más preciso, sobre todo en español y con ruido; tarda unas tres veces más.</summary>
    public static WhisperModel Small { get; } = new(
        "small",
        "Small · más preciso",
        "ggml-small.bin",
        new Uri("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin"),
        "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b",
        487_601_967);

    /// <summary>Modelos ofrecidos, del más ligero al más preciso.</summary>
    public static IReadOnlyList<WhisperModel> All { get; } = [Base, Small];
}

/// <summary>
/// Dónde vive Whisper en el equipo y cómo se instala.
/// </summary>
/// <remarks>
/// <para>
/// Todo va en la carpeta de datos locales del usuario, no en el repositorio ni junto al ejecutable:
/// son unos 150 MB que no todo el mundo quiere, y así se descargan una sola vez y solo si se piden.
/// </para>
/// <para>
/// Cada descarga se verifica contra una huella SHA-256 fijada en el código. Un archivo que no
/// coincide se descarta y no se ejecuta ni se carga nunca.
/// </para>
/// </remarks>
public static class WhisperSetup
{
    /// <summary>Versión de los binarios de whisper.cpp que se instalan.</summary>
    public const string RuntimeVersion = "b5130";

    private static readonly Uri RuntimeUrl = new(
        "https://github.com/ggml-org/whisper.cpp/releases/download/" + RuntimeVersion + "/whisper-bin-x64.zip");

    private const string RuntimeSha256 = "f9ec6c52a2e949b62ab51fa21d0d497958f9e41c3010c157c4e42932d5316f3c";
    private const long RuntimeBytes = 8_573_270;

    /// <summary>Carpeta donde se instala todo lo de Whisper.</summary>
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EditFlow", "speech");

    /// <summary>Si hay binarios de Whisper para este sistema. Por ahora, solo Windows de 64 bits.</summary>
    public static bool IsSupported =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && RuntimeInformation.OSArchitecture == Architecture.X64;

    /// <summary>Tamaño de la descarga de los binarios, para avisar al usuario.</summary>
    public static long RuntimeDownloadBytes => RuntimeBytes;

    /// <summary>Ruta del programa <c>whisper-cli</c> si está instalado; si no, <see langword="null"/>.</summary>
    public static string? LocateCli(string? root = null)
    {
        var path = Path.Combine(root ?? DefaultRoot, "bin", "whisper-cli.exe");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Ruta del archivo de un modelo, exista o no.</summary>
    public static string ModelPath(WhisperModel model, string? root = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        return Path.Combine(root ?? DefaultRoot, "models", model.FileName);
    }

    /// <summary>Indica si un modelo ya está descargado.</summary>
    public static bool HasModel(WhisperModel model, string? root = null) => File.Exists(ModelPath(model, root));

    /// <summary>Descarga e instala los binarios de whisper.cpp.</summary>
    /// <param name="progress">Avance de 0 a 1.</param>
    /// <param name="root">Carpeta de instalación; por defecto la de datos del usuario.</param>
    /// <param name="source">De dónde bajar el zip; solo para pruebas.</param>
    /// <exception cref="InvalidOperationException">Si el sistema no está soportado o la huella no coincide.</exception>
    public static async Task InstallRuntimeAsync(
        IProgress<double>? progress = null,
        string? root = null,
        Uri? source = null,
        string? expectedSha256 = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsSupported && source is null)
        {
            throw new InvalidOperationException("Los subtítulos automáticos solo están disponibles por ahora en Windows de 64 bits.");
        }

        root ??= DefaultRoot;
        var temporary = Path.Combine(root, "runtime.zip.part");

        await DownloadAsync(source ?? RuntimeUrl, temporary, expectedSha256 ?? RuntimeSha256, progress, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            ExtractRuntime(temporary, Path.Combine(root, "bin"));
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /// <summary>Descarga un modelo y comprueba que es el esperado antes de dejarlo en su sitio.</summary>
    public static async Task InstallModelAsync(
        WhisperModel model,
        IProgress<double>? progress = null,
        string? root = null,
        Uri? source = null,
        string? expectedSha256 = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);

        var destination = ModelPath(model, root);
        var temporary = destination + ".part";

        await DownloadAsync(source ?? model.Url, temporary, expectedSha256 ?? model.Sha256, progress, cancellationToken)
            .ConfigureAwait(false);

        File.Move(temporary, destination, overwrite: true);
    }

    /// <summary>Saca del zip solo lo necesario para transcribir, sin rutas del archivo.</summary>
    internal static void ExtractRuntime(string zipPath, string destination)
    {
        Directory.CreateDirectory(destination);

        using var archive = ZipFile.OpenRead(zipPath);
        var extracted = 0;

        foreach (var entry in archive.Entries)
        {
            var name = entry.Name;

            // Solo el programa y las bibliotecas que necesita: el zip trae también decenas de
            // herramientas de prueba que no hacen falta. Se usa el nombre sin carpeta, así ninguna
            // entrada puede escribir fuera de la carpeta de destino.
            var needed = name.Equals("whisper-cli.exe", StringComparison.OrdinalIgnoreCase)
                || name.Equals("whisper.dll", StringComparison.OrdinalIgnoreCase)
                || name.Equals("llama.dll", StringComparison.OrdinalIgnoreCase)
                || (name.StartsWith("ggml", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));

            if (!needed)
            {
                continue;
            }

            entry.ExtractToFile(Path.Combine(destination, name), overwrite: true);
            extracted++;
        }

        if (!File.Exists(Path.Combine(destination, "whisper-cli.exe")))
        {
            throw new InvalidOperationException("El paquete descargado no contiene whisper-cli.exe.");
        }
    }

    private static async Task DownloadAsync(
        Uri url,
        string temporaryPath,
        string expectedSha256,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(temporaryPath)!);

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromHours(1) };
            using var response = await client
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength;

            using var hash = SHA256.Create();
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var target = File.Create(temporaryPath))
            {
                var buffer = new byte[128 * 1024];
                long done = 0;
                int read;

                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    hash.TransformBlock(buffer, 0, read, null, 0);
                    done += read;

                    if (total is > 0)
                    {
                        progress?.Report((double)done / total.Value);
                    }
                }
            }

            hash.TransformFinalBlock([], 0, 0);
            var actual = Convert.ToHexString(hash.Hash!).ToLowerInvariant();

            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "El archivo descargado no coincide con el esperado (huella SHA-256 distinta) y se descartó.");
            }

            progress?.Report(1);
        }
        catch
        {
            // Nada a medias en disco: ni una descarga cancelada ni una corrupta se dejan atrás.
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // En uso: se sobrescribirá en el próximo intento.
            }

            throw;
        }
    }
}
