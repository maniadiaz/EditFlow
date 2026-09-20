using EditFlow.Engine;
using EditFlow.Engine.Encoders;
using Xunit.Abstractions;

namespace EditFlow.Engine.Tests.Integration;

/// <summary>
/// Prueba la detección contra el FFmpeg y el hardware reales de esta máquina.
/// </summary>
/// <remarks>
/// Marcada como <c>Category=Integration</c> para que CI la excluya: los agentes no
/// tienen FFmpeg descargado ni GPU. En local, con <c>tools/fetch-ffmpeg.ps1</c>
/// ejecutado, es la única forma de comprobar que la detección se corresponde con la
/// realidad — un test con datos simulados jamás habría revelado que SVT-AV1 escribe
/// en stderr aunque la codificación tenga éxito.
/// </remarks>
[Trait("Category", "Integration")]
public class EncoderDetectionIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public EncoderDetectionIntegrationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Reports_which_encoders_actually_work_on_this_machine()
    {
        if (!FFmpegLocator.TryLocate(out var tools, out var searched))
        {
            _output.WriteLine($"FFmpeg no disponible; ejecuta: {FFmpegLocator.FetchCommand}");
            _output.WriteLine("Consultado en: " + string.Join(", ", searched));
            return;
        }

        _output.WriteLine($"FFmpeg  : {tools.FFmpegPath}");
        _output.WriteLine($"Origen  : {tools.Origin}");
        _output.WriteLine(string.Empty);

        var detector = new EncoderDetector(tools);
        var encoders = await detector.DetectAsync(CancellationToken.None);

        foreach (var group in encoders.GroupBy(e => e.Codec))
        {
            _output.WriteLine($"--- {group.Key} ---");
            foreach (var encoder in group)
            {
                var mark = encoder.IsAvailable ? "[ OK ]" : "[ -- ]";
                _output.WriteLine($"  {mark} {encoder.Name,-20} {encoder.DisplayName}");
                if (!encoder.IsAvailable)
                {
                    _output.WriteLine($"         {encoder.UnavailableReason}");
                }
            }
            _output.WriteLine(string.Empty);
        }

        var available = encoders.Where(e => e.IsAvailable).ToArray();
        _output.WriteLine($"Disponibles: {available.Length} de {encoders.Count}");

        // Cualquier build de FFmpeg utilizable trae al menos un codificador por CPU.
        // Si esto falla, el problema es la build descargada, no el hardware.
        Assert.Contains(available, e => e.Backend == EncoderBackend.Software);

        // Un codificador disponible nunca debe llevar motivo de indisponibilidad,
        // ni uno no disponible quedarse sin explicación: la interfaz muestra ese texto.
        foreach (var encoder in encoders)
        {
            if (encoder.IsAvailable)
            {
                Assert.Null(encoder.UnavailableReason);
            }
            else
            {
                Assert.False(string.IsNullOrWhiteSpace(encoder.UnavailableReason),
                    $"{encoder.Name} no está disponible pero no explica por qué.");
            }
        }
    }
}
