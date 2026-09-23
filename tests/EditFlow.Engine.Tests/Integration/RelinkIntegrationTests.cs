// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using EditFlow.Core.Media;
using EditFlow.Core.Projects;
using EditFlow.Core.Timeline;
using EditFlow.Engine;
using EditFlow.Engine.Execution;
using EditFlow.Engine.Exporting;
using EditFlow.Engine.Probing;
using Xunit.Abstractions;

namespace EditFlow.Engine.Tests.Integration;

/// <summary>
/// Recorre el caso real que motiva todo el bloque: se monta un video, se guarda, alguien mueve el
/// archivo, se reabre el proyecto, se reconecta y se exporta.
/// </summary>
/// <remarks>
/// Es el camino donde antes se perdía el trabajo: los clips de un archivo que no aparecía se
/// descartaban al abrir, así que reconectarlo después no servía de nada porque ya no quedaba
/// montaje al que devolverle la imagen.
/// </remarks>
[Trait("Category", "Integration")]
public class RelinkIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public RelinkIntegrationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task A_file_that_moved_is_relinked_and_the_montage_exports_again()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-relink-");

        try
        {
            var original = Path.Combine(workspace.FullName, "toma.mp4");
            await MakeVideoAsync(tools, original, seconds: 6);

            var probe = new FFprobeService(tools);
            var media = await probe.ProbeAsync(original, CancellationToken.None);

            // --- se monta algo con él -----------------------------------------
            var project = new EditProject();
            project.AddMedia(media);
            project.Timeline.Append(new Clip(media, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)));

            var bin = project.Library.CreateBin("Cámara A");
            project.Library.MoveToBin(media, bin);
            project.Library.SetLabel(media, MediaLabel.Green);

            var projectPath = Path.Combine(workspace.FullName, "montaje.editflow");
            await ProjectSerializer.SaveAsync(project, projectPath, CancellationToken.None);

            // --- alguien mueve el archivo a otra carpeta ----------------------
            var elsewhere = Directory.CreateDirectory(Path.Combine(workspace.FullName, "movido"));
            var moved = Path.Combine(elsewhere.FullName, "toma.mp4");
            File.Move(original, moved);

            // --- se reabre: el montaje sigue, el archivo no ------------------
            var loaded = await ProjectSerializer.LoadAsync(projectPath, CancellationToken.None);

            Assert.True(loaded.HasMissingMedia);
            Assert.True(loaded.Project.HasOfflineMedia);

            var clip = Assert.Single(loaded.Project.Timeline.Clips);
            Assert.True(clip.Source.IsOffline);
            Assert.Equal(TimeSpan.FromSeconds(1), clip.SourceIn);
            Assert.Equal(TimeSpan.FromSeconds(5), clip.SourceOut);

            // La organización también sobrevive.
            var offline = Assert.Single(loaded.Project.OfflineMedia);
            Assert.Equal("Cámara A", loaded.Project.Library.BinOf(offline).Name);
            Assert.Equal(MediaLabel.Green, loaded.Project.Library.LabelOf(offline));

            // --- exportar se niega y dice qué falta ---------------------------
            var refused = Assert.Throws<ArgumentException>(() => Plan(loaded.Project));
            Assert.Contains("toma.mp4", refused.Message, StringComparison.Ordinal);

            // --- se reconecta -------------------------------------------------
            var found = await probe.ProbeAsync(moved, CancellationToken.None);
            var relink = new ReplaceMediaCommand(loaded.Project, offline, found);
            relink.Execute();

            Assert.Equal(1, relink.AffectedCount);
            Assert.False(relink.Trimmed);
            Assert.False(loaded.Project.HasOfflineMedia);

            // El corte no se movió ni un fotograma, y la carpeta y la etiqueta siguieron al archivo.
            var relinked = Assert.Single(loaded.Project.Timeline.Clips);
            Assert.Equal(moved, relinked.Source.Path);
            Assert.Equal(TimeSpan.FromSeconds(1), relinked.SourceIn);
            Assert.Equal(TimeSpan.FromSeconds(5), relinked.SourceOut);
            Assert.Equal("Cámara A", loaded.Project.Library.BinOf(relinked.Source).Name);
            Assert.Equal(MediaLabel.Green, loaded.Project.Library.LabelOf(relinked.Source));

            // --- y ahora sí exporta ------------------------------------------
            var output = Path.Combine(workspace.FullName, "final.mp4");
            var result = await new ExportJob(tools).RunAsync(
                loaded.Project.Sequence,
                Settings(output),
                null,
                CancellationToken.None);

            Assert.True(result.Succeeded, result.ErrorMessage);

            var exported = await probe.ProbeAsync(output, CancellationToken.None);
            _output.WriteLine($"Exportado tras reconectar: {exported.Duration.TotalSeconds:0.##} s");
            Assert.InRange(exported.Duration.TotalSeconds, 3.5, 4.5);
        }
        finally
        {
            workspace.DeleteWithRetry();
        }
    }

    [Fact]
    public async Task Replacing_footage_with_a_shorter_take_trims_instead_of_breaking_the_export()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out _))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            return;
        }

        var workspace = Directory.CreateTempSubdirectory("editflow-replace-");

        try
        {
            var longPath = Path.Combine(workspace.FullName, "larga.mp4");
            var shortPath = Path.Combine(workspace.FullName, "corta.mp4");
            await MakeVideoAsync(tools, longPath, seconds: 8);
            await MakeVideoAsync(tools, shortPath, seconds: 3);

            var probe = new FFprobeService(tools);
            var original = await probe.ProbeAsync(longPath, CancellationToken.None);

            var project = new EditProject();
            project.AddMedia(original);
            project.Timeline.Append(new Clip(original, TimeSpan.Zero, TimeSpan.FromSeconds(7)));

            var replacement = await probe.ProbeAsync(shortPath, CancellationToken.None);
            var command = new ReplaceMediaCommand(project, original, replacement);
            command.Execute();

            Assert.True(command.Trimmed);

            // Lo que importa: el grafo resultante sigue siendo exportable, no pide material que no
            // existe. Sin acotar, FFmpeg abortaría a mitad con un error de entrada.
            var output = Path.Combine(workspace.FullName, "final.mp4");
            var result = await new ExportJob(tools).RunAsync(
                project.Sequence, Settings(output), null, CancellationToken.None);

            Assert.True(result.Succeeded, result.ErrorMessage);

            var exported = await probe.ProbeAsync(output, CancellationToken.None);
            _output.WriteLine($"Exportado tras sustituir: {exported.Duration.TotalSeconds:0.##} s");
            Assert.True(exported.Duration.TotalSeconds <= 3.5);
        }
        finally
        {
            workspace.DeleteWithRetry();
        }
    }

    // ------------------------------------------------------------------ ayudas

    private static FilterGraphPlan Plan(EditProject project) =>
        FilterGraphBuilder.Build(project.Sequence, Settings("out.mp4"));

    private static ExportSettings Settings(string output) => new()
    {
        OutputPath = output,
        Resolution = VideoResolution.P480,
        EncoderName = "libx264",
        FrameRate = 30,
        Speed = EncodingSpeed.Fast,
    };

    private static async Task MakeVideoAsync(FFmpegTools tools, string path, double seconds)
    {
        var result = await ProcessRunner.RunAsync(
            tools.FFmpegPath,
            [
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i",
                string.Create(CultureInfo.InvariantCulture, $"testsrc2=size=640x360:rate=30:duration={seconds}"),
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                path,
            ],
            CancellationToken.None);

        Assert.True(result.Succeeded, result.StandardError);
    }
}
