// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using System.Text.Json.Serialization;

namespace EditFlow.Core.Projects;

/// <summary>Preferencias del usuario que se recuerdan entre sesiones.</summary>
/// <param name="PlaybackDivisor">
/// Resolución de reproducción del preview: 1 (completa), 2, 4, 8 o 16.
/// </param>
public sealed record UserSettings(int PlaybackDivisor = 1)
{
    private static readonly int[] ValidDivisors = [1, 2, 4, 8, 16];

    /// <summary>Corrige lo que no tenga sentido: una preferencia mal escrita no debe romper nada.</summary>
    public UserSettings Sanitized() =>
        ValidDivisors.Contains(PlaybackDivisor) ? this : this with { PlaybackDivisor = 1 };
}

/// <summary>
/// Guarda las preferencias en los datos locales del usuario.
/// </summary>
/// <remarks>
/// Como la lista de recientes, es una comodidad: si el archivo falta o está corrupto se
/// arranca con los valores por defecto, y un fallo al escribir se ignora en silencio.
/// </remarks>
public sealed class UserSettingsStore
{
    private readonly string _filePath;

    /// <summary>Crea el almacén en el archivo indicado.</summary>
    public UserSettingsStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    /// <summary>Ubicación por defecto, en los datos locales del usuario.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EditFlow",
        "settings.json");

    /// <summary>Lee las preferencias; los valores por defecto si no hay o no se pueden leer.</summary>
    public UserSettings Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new UserSettings();
            }

            var settings = JsonSerializer.Deserialize(
                File.ReadAllText(_filePath), SettingsJson.Default.UserSettings);
            return settings?.Sanitized() ?? new UserSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new UserSettings();
        }
    }

    /// <summary>Guarda las preferencias; no falla si no se puede escribir.</summary>
    public void Save(UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(
                _filePath, JsonSerializer.Serialize(settings.Sanitized(), SettingsJson.Default.UserSettings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Una preferencia que no se guarda es un fastidio, no un error.
        }
    }
}

/// <summary>Contexto de serialización generado en compilación para las preferencias.</summary>
/// <remarks>
/// Igual que el del archivo de proyecto, y por el mismo motivo, pero aquí el motivo tiene
/// nombre propio: al publicar con recorte, <c>System.Text.Json</c> desactiva la serialización
/// por reflexión y lanza al primer uso. Eso dejaba la aplicación publicada muriendo nada más
/// abrirse, con un fallo que no aparecía al compilar ni en los tests.
/// </remarks>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(UserSettings))]
internal sealed partial class SettingsJson : JsonSerializerContext;
