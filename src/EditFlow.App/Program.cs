using System;
using Avalonia;

namespace EditFlow.App;

internal static class Program
{
    // No uses Avalonia, APIs de terceros ni código que dependa del
    // SynchronizationContext antes de que AppMain arranque: todavía no está
    // inicializado y puede romperse de formas difíciles de diagnosticar.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // Usado también por el diseñador visual: no cambiar la firma.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
