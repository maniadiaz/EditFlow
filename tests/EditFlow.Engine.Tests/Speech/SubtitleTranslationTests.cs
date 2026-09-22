// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using EditFlow.Engine.Speech;
using Xunit.Abstractions;

namespace EditFlow.Engine.Tests.Speech;

public class SubtitleTranslationTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromMilliseconds(Math.Round(seconds * 1000));

    // ------------------------------------------------------------ instrucciones

    [Fact]
    public void The_prompt_names_both_languages_and_asks_for_numbered_lines()
    {
        var prompt = SubtitleTranslator.BuildSystemPrompt("English", "French");

        Assert.Contains("from English into French", prompt, StringComparison.Ordinal);
        Assert.Contains("\"N. translation\"", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("vosotros", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Spanish_gets_a_note_to_avoid_vosotros_and_an_unknown_source_is_described()
    {
        var prompt = SubtitleTranslator.BuildSystemPrompt(null, "Spanish");

        Assert.Contains("from the original language into Spanish", prompt, StringComparison.Ordinal);
        Assert.Contains("never \"vosotros\"", prompt, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- respuesta

    [Theory]
    [InlineData("1. Hola\n2. Adiós", 2)]
    [InlineData("1) Hola\n2) Adiós", 2)]
    [InlineData("1: Hola\n2: Adiós", 2)]
    [InlineData("  1.   Hola  \n\n  2.  Adiós  \n", 2)]
    [InlineData("Aquí van las traducciones:\n1. Hola\n2. Adiós\nEspero que ayude.", 2)]
    public void Numbered_lines_are_read_however_the_model_formats_them(string reply, int expected)
    {
        var lines = SubtitleTranslator.ParseReply(reply);

        Assert.Equal(expected, lines.Count);
        Assert.Equal("Hola", lines[1]);
        Assert.Equal("Adiós", lines[2]);
    }

    [Fact]
    public void Quotes_tags_and_duplicates_are_cleaned_and_missing_numbers_stay_missing()
    {
        var lines = SubtitleTranslator.ParseReply("1. \"Hola\"\n3. <i>Adiós</i>\n1. otra vez\n4.   ");

        Assert.Equal(["Hola", "Adiós"], new[] { lines[1], lines[3] });
        Assert.False(lines.ContainsKey(2));
        Assert.False(lines.ContainsKey(4));
    }

    // ----------------------------------------------------------- por tandas

    private sealed class FakeServer(Func<int, string, string> answer) : HttpMessageHandler
    {
        public List<string> UserMessages { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            var user = document.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            UserMessages.Add(user);

            var reply = answer(UserMessages.Count, user);
            var json = JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content = reply } } } });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private static List<SpeechSegment> Segments(int count) =>
        Enumerable.Range(0, count).Select(i => new SpeechSegment(S(i * 3), S((i * 3) + 2), $"line {i}")).ToList();

    /// <summary>Traduce cada línea numerada de la petición anteponiéndole «ES:».</summary>
    private static string TranslateAll(string user) =>
        string.Join('\n', user.Split('\n')
            .Select(l => System.Text.RegularExpressions.Regex.Match(l, @"^(\d+)\. (.*)$"))
            .Where(m => m.Success)
            .Select(m => $"{m.Groups[1].Value}. ES:{m.Groups[2].Value}"));

    [Fact]
    public async Task The_subtitles_are_translated_in_batches_and_keep_their_times()
    {
        var server = new FakeServer((_, user) => TranslateAll(user));
        using var client = new HttpClient(server) { BaseAddress = new Uri("http://127.0.0.1:1/") };
        var progress = new List<double>();

        var result = await SubtitleTranslator.TranslateOverHttpAsync(
            client, Segments(30), "English", "Spanish", new Progress<double>(progress.Add), CancellationToken.None);

        Assert.Equal(3, server.UserMessages.Count);                      // 12 + 12 + 6
        Assert.Equal(30, result.Count);
        Assert.Equal("ES:line 0", result[0].Text);
        Assert.Equal("ES:line 29", result[29].Text);
        Assert.Equal(S(87), result[29].Start);                           // los tiempos no cambian
        Assert.Equal(S(89), result[29].End);
    }

    [Fact]
    public async Task Each_batch_after_the_first_carries_the_previous_lines_as_context()
    {
        var server = new FakeServer((_, user) => TranslateAll(user));
        using var client = new HttpClient(server) { BaseAddress = new Uri("http://127.0.0.1:1/") };

        await SubtitleTranslator.TranslateOverHttpAsync(client, Segments(15), null, "English", null, CancellationToken.None);

        Assert.DoesNotContain("Context", server.UserMessages[0], StringComparison.Ordinal);
        Assert.Contains("Context (already handled, do not translate):", server.UserMessages[1], StringComparison.Ordinal);
        Assert.Contains("line 11", server.UserMessages[1], StringComparison.Ordinal);      // la última de la tanda anterior
        Assert.Contains("1. line 12", server.UserMessages[1], StringComparison.Ordinal);    // la numeración reinicia
    }

    [Fact]
    public async Task A_line_the_model_skipped_is_asked_again_on_its_own()
    {
        var server = new FakeServer((call, user) =>
        {
            var all = TranslateAll(user);
            // La primera vez «se olvida» la línea 2.
            return call == 1
                ? string.Join('\n', all.Split('\n').Where(l => !l.StartsWith("2.", StringComparison.Ordinal)))
                : all;
        });
        using var client = new HttpClient(server) { BaseAddress = new Uri("http://127.0.0.1:1/") };

        var result = await SubtitleTranslator.TranslateOverHttpAsync(client, Segments(3), null, "Spanish", null, CancellationToken.None);

        Assert.Equal(["ES:line 0", "ES:line 1", "ES:line 2"], result.Select(r => r.Text).ToArray());
        Assert.Equal(2, server.UserMessages.Count);
    }

    [Fact]
    public async Task A_line_that_never_comes_back_keeps_its_original_text()
    {
        var server = new FakeServer((_, user) => string.Join('\n', TranslateAll(user).Split('\n')
            .Where(l => !l.Contains("line 1", StringComparison.Ordinal))));
        using var client = new HttpClient(server) { BaseAddress = new Uri("http://127.0.0.1:1/") };

        var result = await SubtitleTranslator.TranslateOverHttpAsync(client, Segments(3), null, "Spanish", null, CancellationToken.None);

        Assert.Equal("ES:line 0", result[0].Text);
        Assert.Equal("line 1", result[1].Text);                            // no se pierde el subtítulo
        Assert.Equal("ES:line 2", result[2].Text);
    }

    [Fact]
    public async Task Cancelling_stops_between_batches()
    {
        using var cancellation = new CancellationTokenSource();
        var server = new FakeServer((call, user) =>
        {
            cancellation.Cancel();
            return TranslateAll(user);
        });
        using var client = new HttpClient(server) { BaseAddress = new Uri("http://127.0.0.1:1/") };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SubtitleTranslator.TranslateOverHttpAsync(client, Segments(30), null, "Spanish", null, cancellation.Token));
    }

    // -------------------------------------------------------------- idiomas

    [Fact]
    public void Languages_are_found_by_code_and_the_targets_have_no_automatic_option()
    {
        Assert.Equal("English", SpeechLanguage.FindByCode("en")!.EnglishName);
        Assert.Equal("Spanish", SpeechLanguage.FindByCode("ES")!.EnglishName);
        Assert.Null(SpeechLanguage.FindByCode("auto"));
        Assert.Null(SpeechLanguage.FindByCode("xx"));
        Assert.Null(SpeechLanguage.FindByCode(null));
        Assert.DoesNotContain(SpeechLanguage.Targets, l => l.Code == "auto");
        Assert.Equal(SpeechLanguage.All.Count - 1, SpeechLanguage.Targets.Count);
    }
}

public class TranslationSetupTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("editflow-translator-");
    private HttpListener? _listener;

    public void Dispose()
    {
        _listener?.Close();
        try { _root.DeleteWithRetry(); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Only_the_server_and_the_libraries_are_taken_from_the_zip_without_folders()
    {
        var zipPath = Path.Combine(_root.FullName, "llama.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "llama-server.exe", "llama.dll", "ggml.dll", "libomp.dll", "llama-cli.exe", "llama-bench.exe", "../escape/evil.dll" })
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write("x");
            }
        }

        var destination = Path.Combine(_root.FullName, "llm", "bin");
        TranslationSetup.ExtractRuntime(zipPath, destination);

        var files = Directory.GetFiles(destination).Select(Path.GetFileName).OrderBy(n => n).ToArray();
        Assert.Equal(["evil.dll", "ggml.dll", "libomp.dll", "llama-server.exe", "llama.dll"], files);
        Assert.False(File.Exists(Path.Combine(_root.FullName, "escape", "evil.dll")));
        Assert.NotNull(TranslationSetup.LocateServer(_root.FullName));
    }

    [Fact]
    public void A_package_without_the_server_is_refused()
    {
        var zipPath = Path.Combine(_root.FullName, "bad.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            zip.CreateEntry("llama.dll");
        }

        Assert.Throws<InvalidOperationException>(() => TranslationSetup.ExtractRuntime(zipPath, Path.Combine(_root.FullName, "bin")));
    }

    [Fact]
    public void The_default_model_has_a_pinned_fingerprint_and_an_open_licence_source()
    {
        var model = TranslationModel.Default;

        Assert.Equal(64, model.Sha256.Length);
        Assert.Equal("https", model.Url.Scheme);
        Assert.True(model.Bytes > 1_000_000_000);
        Assert.EndsWith(".gguf", model.FileName, StringComparison.Ordinal);
        Assert.False(TranslationSetup.HasModel(model, _root.FullName));
    }

    [Fact]
    public async Task A_model_download_with_the_wrong_fingerprint_is_discarded()
    {
        var data = new byte[50_000];
        Random.Shared.NextBytes(data);
        var port = Random.Shared.Next(20_000, 40_000);
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();
        _ = Task.Run(async () =>
        {
            try
            {
                var context = await _listener.GetContextAsync();
                context.Response.ContentLength64 = data.Length;
                await context.Response.OutputStream.WriteAsync(data);
                context.Response.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                // El servidor de la prueba se cerró.
            }
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => TranslationSetup.InstallModelAsync(
            TranslationModel.Default, null, _root.FullName, new Uri($"http://localhost:{port}/m.gguf"), new string('0', 64)));

        Assert.False(TranslationSetup.HasModel(TranslationModel.Default, _root.FullName));
    }
}

[Trait("Category", "Integration")]
public class SubtitleTranslationIntegrationTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromMilliseconds(Math.Round(seconds * 1000));

    private readonly ITestOutputHelper _output;

    public SubtitleTranslationIntegrationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task With_the_real_model_installed_an_english_dialogue_comes_out_in_spanish()
    {
        if (TranslationSetup.LocateServer() is null || !TranslationSetup.HasModel(TranslationModel.Default))
        {
            _output.WriteLine("El traductor no está instalado en este equipo: se omite.");
            return;
        }

        var result = await new SubtitleTranslator().TranslateAsync(
        [
            new SpeechSegment(S(1), S(3), "I refuse to lose my son."),
            new SpeechSegment(S(4), S(6), "Trust me, we are your family."),
            new SpeechSegment(S(7), S(9), "I'm tired."),
        ],
        "English",
        "Spanish");

        foreach (var segment in result)
        {
            _output.WriteLine(segment.Text);
        }

        Assert.Equal(3, result.Count);
        Assert.Equal(S(4), result[1].Start);
        Assert.Contains("hijo", result[0].Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("familia", result[1].Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cansad", result[2].Text, StringComparison.OrdinalIgnoreCase);
    }
}
