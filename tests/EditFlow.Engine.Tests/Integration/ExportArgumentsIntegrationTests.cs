// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Engine;
using EditFlow.Engine.Encoders;
using EditFlow.Engine.Execution;
using EditFlow.Engine.Exporting;
using Xunit.Abstractions;

namespace EditFlow.Engine.Tests.Integration;

/// <summary>
/// Comprueba que FFmpeg acepta de verdad los argumentos que genera el builder.
/// </summary>
/// <remarks>
/// Los tests unitarios verifican que se produce la cadena esperada, lo cual no dice nada
/// sobre si el codificador la entiende. Una opción inventada, o válida para otra familia,
/// puede ignorarse en silencio y producir una exportación con ajustes que nadie pidió.
/// Aquí se codifica de verdad con cada combinación disponible.
/// </remarks>
[Trait("Category", "Integration")]
public class ExportArgumentsIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public ExportArgumentsIntegrationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Ffmpeg_accepts_every_generated_combination()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var detector = new EncoderDetector(tools);
        var available = (await detector.DetectAsync(CancellationToken.None))
            .Where(e => e.IsAvailable)
            .ToArray();

        Assert.NotEmpty(available);

        var temporaryDirectory = Directory.CreateTempSubdirectory("editflow-args-");
        var failures = new List<string>();

        try
        {
            foreach (var encoder in available)
            {
                foreach (var mode in Enum.GetValues<RateControlMode>())
                {
                    var outputPath = Path.Combine(
                        temporaryDirectory.FullName,
                        $"{encoder.Name}-{mode}.mp4");

                    var settings = new ExportSettings
                    {
                        OutputPath = outputPath,
                        Resolution = VideoResolution.P480,
                        EncoderName = encoder.Name,
                        RateControl = mode,
                        Quality = 65,
                        VideoBitrateKbps = QualityScale.SuggestedBitrateKbps(
                            VideoResolution.P480, encoder.Codec),
                        Speed = EncodingSpeed.Fastest,
                    };

                    var arguments = new List<string>
                    {
                        "-hide_banner", "-loglevel", "error", "-y",
                        "-f", "lavfi", "-i", "testsrc2=size=854x480:rate=30:duration=0.4",
                        "-f", "lavfi", "-i", "sine=frequency=440:duration=0.4",
                        "-shortest",
                    };
                    arguments.AddRange(FFmpegArgumentBuilder.BuildOutputArguments(settings));

                    var result = await ProcessRunner.RunAsync(
                        tools.FFmpegPath, arguments, CancellationToken.None);

                    if (result.Succeeded && new FileInfo(outputPath).Length > 0)
                    {
                        var size = new FileInfo(outputPath).Length / 1024;
                        _output.WriteLine($"  OK  {encoder.Name,-12} {mode,-16} {size,5} KB");
                    }
                    else
                    {
                        var detail = result.StandardError
                            .Split('\n')
                            .Select(l => l.Trim())
                            .FirstOrDefault(l => l.Length > 0 && !l.StartsWith("Svt[", StringComparison.Ordinal))
                            ?? $"exit {result.ExitCode}, archivo vacío";

                        _output.WriteLine($"  --  {encoder.Name,-12} {mode,-16} {detail}");
                        failures.Add($"{encoder.Name} / {mode}: {detail}");
                    }
                }
            }
        }
        finally
        {
            temporaryDirectory.Delete(recursive: true);
        }

        Assert.True(failures.Count == 0,
            "FFmpeg rechazó estas combinaciones:" + Environment.NewLine +
            string.Join(Environment.NewLine, failures));
    }
}
