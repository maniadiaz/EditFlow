// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Engine.Encoders;
using EditFlow.Engine.Execution;

namespace EditFlow.Engine.Tests.Encoders;

public class EncoderDetectorTests
{
    // Mensajes de error reales capturados de FFmpeg n9.0 en una máquina con GPU NVIDIA
    // y sin hardware Intel ni AMD.

    [Fact]
    public void Explains_quick_sync_failure_in_plain_language()
    {
        const string stderr = "[h264_qsv @ 000001df3665ae40] Error creating a MFX session: -9.\n";

        var reason = EncoderDetector.DescribeFailure(stderr, EncoderBackend.QuickSync);

        Assert.Contains("Intel Quick Sync", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("MFX", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Explains_amf_failure_in_plain_language()
    {
        const string stderr = "[AMF @ 000001e1b2d259c0] DLL amfrt64.dll failed to open\n";

        var reason = EncoderDetector.DescribeFailure(stderr, EncoderBackend.Amf);

        Assert.Contains("AMD", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("amfrt64.dll", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Explains_missing_nvidia_gpu()
    {
        const string stderr = "[h264_nvenc @ 0000021] Cannot load nvcuda.dll\n";

        var reason = EncoderDetector.DescribeFailure(stderr, EncoderBackend.Nvenc);

        Assert.Contains("NVIDIA", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Distinguishes_exhausted_sessions_from_missing_hardware()
    {
        // Este caso es recuperable —basta cerrar la otra aplicación—, así que no debe
        // confundirse con "no tienes GPU NVIDIA", que llevaría al usuario a rendirse.
        const string stderr = "[h264_nvenc @ 0000021] OpenEncodeSessionEx failed: out of memory (10)\n";

        var reason = EncoderDetector.DescribeFailure(stderr, EncoderBackend.Nvenc);

        Assert.Contains("otra aplicación", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Suggests_updating_an_outdated_driver()
    {
        const string stderr = "[av1_nvenc @ 0000021] This driver does not support the required nvenc API version\n";

        var reason = EncoderDetector.DescribeFailure(stderr, EncoderBackend.Nvenc);

        Assert.Contains("driver", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Falls_back_to_the_first_meaningful_line()
    {
        const string stderr = "\n\n[libx264 @ 001] height not divisible by 2 (255)\n";

        var reason = EncoderDetector.DescribeFailure(stderr, EncoderBackend.Software);

        Assert.Equal("[libx264 @ 001] height not divisible by 2 (255)", reason);
    }

    [Fact]
    public void Skips_the_svt_av1_banner_when_summarising()
    {
        // SVT-AV1 antepone su propio banner a cualquier mensaje. Si se tomara la primera
        // línea sin filtrar, el usuario vería una fila de guiones como "motivo".
        const string stderr =
            "Svt[info]: -------------------------------------------\n" +
            "Svt[info]: SVT [version]: SVT-AV1 Encoder Lib\n" +
            "[libsvtav1 @ 001] Failed to initialize encoder\n";

        var reason = EncoderDetector.DescribeFailure(stderr, EncoderBackend.Software);

        Assert.Equal("[libsvtav1 @ 001] Failed to initialize encoder", reason);
    }

    [Fact]
    public void Produces_a_reason_even_with_empty_output()
    {
        var reason = EncoderDetector.DescribeFailure(string.Empty, EncoderBackend.Nvenc);

        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void Success_is_decided_by_exit_code_not_by_empty_stderr()
    {
        // SVT-AV1 escribe su banner en stderr incluso cuando la codificación va bien.
        // Si el éxito dependiera de que stderr esté vacío, libsvtav1 se marcaría como
        // no disponible en todas las máquinas.
        var noisyButFine = new ProcessResult(
            ExitCode: 0,
            StandardOutput: string.Empty,
            StandardError: "Svt[info]: SVT-AV1 Encoder Lib v3.1.0\nSvt[info]: Number of logical cores: 16\n");

        var quietButFailed = new ProcessResult(171, string.Empty, string.Empty);

        Assert.True(noisyButFine.Succeeded);
        Assert.False(quietButFailed.Succeeded);
    }
}
