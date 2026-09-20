using System.Reflection;

namespace EditFlow.Engine.Tests.Architecture;

/// <summary>
/// Protege la regla de oro de EditFlow: <c>EditFlow.Core</c> y
/// <c>EditFlow.Engine</c> no dependen de la interfaz gráfica.
///
/// Es lo que permite que toda la lógica de exportación sea testeable desde
/// consola y que esta suite corra headless en CI sobre Linux. Un
/// <c>ProjectReference</c> añadido por descuido rompería esa propiedad de
/// forma silenciosa, así que se verifica en cada build.
/// </summary>
public class LayeringTests
{
    [Theory]
    [InlineData("EditFlow.Core")]
    [InlineData("EditFlow.Engine")]
    public void Layer_does_not_reference_any_ui_framework(string assemblyName)
    {
        var assembly = LoadSibling(assemblyName);

        string[] forbidden = ["Avalonia", "LibVLCSharp", "SkiaSharp", "System.Windows"];

        var violations = assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => forbidden.Any(f =>
                name.Equals(f, StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(f + ".", StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        Assert.True(
            violations.Length == 0,
            $"{assemblyName} must stay free of UI dependencies, but references: " +
            string.Join(", ", violations));
    }

    private static Assembly LoadSibling(string assemblyName)
    {
        var directory = Path.GetDirectoryName(typeof(LayeringTests).Assembly.Location)!;
        var path = Path.Combine(directory, assemblyName + ".dll");

        Assert.True(File.Exists(path), $"Expected {assemblyName}.dll next to the test assembly at {path}");

        return Assembly.LoadFrom(path);
    }
}
