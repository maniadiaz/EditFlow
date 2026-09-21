// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;

namespace EditFlow.Core.Text;

/// <summary>Describe un instante pasado en lenguaje cotidiano: «hace 5 min», «ayer».</summary>
public static class RelativeTime
{
    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-ES");

    /// <summary>Texto para un instante respecto de otro.</summary>
    /// <param name="then">Instante a describir, en UTC.</param>
    /// <param name="now">Instante de referencia, en UTC.</param>
    public static string Describe(DateTime then, DateTime now)
    {
        var elapsed = now - then;

        // Un reloj adelantado o un archivo del «futuro» no debe producir «hace -3 min».
        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return "hace un momento";
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            return $"hace {(int)elapsed.TotalMinutes} min";
        }

        if (elapsed < TimeSpan.FromDays(1))
        {
            return $"hace {(int)elapsed.TotalHours} h";
        }

        if (elapsed < TimeSpan.FromDays(2))
        {
            return "ayer";
        }

        if (elapsed < TimeSpan.FromDays(7))
        {
            return $"hace {(int)elapsed.TotalDays} días";
        }

        return then.ToLocalTime().ToString("d MMM yyyy", Spanish);
    }
}
