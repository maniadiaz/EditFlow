// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using Avalonia;

namespace EditFlow.App;

internal static class Program
{
    // No uses Avalonia, APIs de terceros ni código que dependa del
    // SynchronizationContext antes de que AppMain arranque: todavía no está
    // inicializado y puede romperse de formas difíciles de diagnosticar.
    /// <summary>
    /// Archivos indicados al arrancar, por ejemplo al soltarlos sobre el ejecutable o
    /// al abrir un video con EditFlow desde el explorador.
    /// </summary>
    public static IReadOnlyList<string> StartupFiles { get; private set; } = [];

    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint milliseconds);

    [STAThread]
    public static void Main(string[] args)
    {
        // Windows resuelve por defecto los temporizadores a 15,6 ms. El reproductor espera cada
        // fotograma con un temporizador, y a 60 fotogramas por segundo (16,7 ms) esa resolución
        // los entrega a tirones. Con 1 ms, cada uno sale a su hora.
        if (OperatingSystem.IsWindows())
        {
            _ = TimeBeginPeriod(1);
        }

        StartupFiles = args.Where(File.Exists).ToArray();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Usado también por el diseñador visual: no cambiar la firma.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
