using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Translator.Core;

namespace Translator.Inference;

public sealed record LlamaOptions(
    string ModelDirectory, string ModelFileName, string ExecutableOverride, bool UseGpu, bool AllowCpuFallback);

public sealed class LlamaTranslator(Func<LlamaOptions> options, ILlamaRuntimeProvider runtimeProvider, LlamaTuning tuning,
    Func<bool>? keepWarm = null)
    : ITranslationService, ISubtitleParagraphTranslator, ISubtitlePrefill
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly GgufModelCatalog catalog = new();
    private readonly HttpClient http = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private Task? idleTask;
    private Server? server;
    private DateTime lastUsed;
    private DateTime lastBudgetCheck;
    private string status = "模型未加载";

    public string Status => Volatile.Read(ref status);

    public async Task PrepareAsync(CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        CancellationToken token = operation.Token;
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            idleTask ??= ReleaseAfterIdleAsync();
            await GetServerAsync(options(), token, checkVideoMemory: false).ConfigureAwait(false);
        }
        finally
        {
            lastUsed = DateTime.UtcNow;
            gate.Release();
        }
    }

    public ValueTask<string> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken) =>
        TranslateAsync(request, null, cancellationToken);

    public async Task TryPrefillAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        CancellationToken token = operation.Token;
        if (!gate.Wait(0, token)) return;
        try
        {
            Server? active = server;
            if (active is null || active.Process.HasExited || active.Options != options()) return;
            if (request.Text.Length > tuning.MaxInputCharacters) return;

            // Use the same chat template as the final request so llama-server can reuse the matching tokens.
            var body = CreateTranslationBody(request, stream: false, maxTokens: 0);
            using HttpRequestMessage message = CreateCompletionRequest(active, body);
            using HttpResponseMessage response = await http.SendAsync(message, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        finally { gate.Release(); }
    }

    public async IAsyncEnumerable<string> StreamTranslationAsync(TranslationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var updates = Channel.CreateBounded<string>(new BoundedChannelOptions(1)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest
        });
        Task producer = ProduceAsync();
        try
        {
            await foreach (string update in updates.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return update;
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await producer.ConfigureAwait(false);
        }

        async Task ProduceAsync()
        {
            try
            {
                await TranslateAsync(request, text => updates.Writer.TryWrite(text), stop.Token).ConfigureAwait(false);
                updates.Writer.TryComplete();
            }
            catch (Exception error) { updates.Writer.TryComplete(error); }
        }
    }

    private async ValueTask<string> TranslateAsync(TranslationRequest request, Action<string>? update, CancellationToken cancellationToken)
    {
        if (!TranslationText.NeedsTranslation(request.Text, request.DetectedLanguage)) return "";
        if (request.Text.Length > tuning.MaxInputCharacters) throw new InvalidOperationException("内容过长，请缩小选区或分段翻译。");

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        CancellationToken token = operation.Token;
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            idleTask ??= ReleaseAfterIdleAsync();
            Server active = await GetServerAsync(options(), token).ConfigureAwait(false);
            return await TranslateWithServerAsync(active, request, update, token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutException)
        {
            await StopServerAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            lastUsed = DateTime.UtcNow;
            gate.Release();
        }
    }

    public async Task ReleaseAsync()
    {
        await gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try { await StopServerAsync().ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyDictionary<long, string>> ReviseAsync(IReadOnlyList<SubtitleLine> paragraph,
        SourceLanguage language, CancellationToken cancellationToken)
    {
        if (paragraph.Count < 2) return new Dictionary<long, string>();

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        CancellationToken token = operation.Token;
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            idleTask ??= ReleaseAfterIdleAsync();
            Server active = await GetServerAsync(options(), token, checkVideoMemory: false).ConfigureAwait(false);
            var body = new
            {
                model = "translator", temperature = tuning.RevisionTemperature, max_tokens = tuning.ParagraphTokens,
                response_format = new { type = "json_object" },
                chat_template_kwargs = new { enable_thinking = false },
                messages = new[]
                {
                    new { role = "system", content = TranslationPrompts.ReviseSystem },
                    new { role = "user", content = TranslationPrompts.ReviseInput(paragraph, language) }
                }
            };
            string response = await ReadCompletionAsync(active, body, token).ConfigureAwait(false);
            return TranslationPrompts.ParseRevision(response, paragraph);
        }
        finally
        {
            lastUsed = DateTime.UtcNow;
            gate.Release();
        }
    }

    public async Task ReleaseObsoleteModelAsync()
    {
        await gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            if (server is { } active && active.Options != options()) await StopServerAsync().ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task<Server> GetServerAsync(LlamaOptions config, CancellationToken token, bool checkVideoMemory = true)
    {
        bool forceCpu = false;
        if (server is { } existing && !existing.Process.HasExited && existing.Options == config)
        {
            if (!checkVideoMemory || !config.AllowCpuFallback || !existing.Runtime.UseGpu
                || DateTime.UtcNow - lastBudgetCheck < TimeSpan.FromSeconds(tuning.MemoryCheckSeconds)) return existing;

            lastBudgetCheck = DateTime.UtcNow;
            int? remaining = await runtimeProvider.ReadFreeVideoMemoryMiBAsync(existing.Runtime, token).ConfigureAwait(false);
            if (remaining is null || remaining >= tuning.MinimumFreeGpuMiB) return existing;
            forceCpu = true;
        }

        await StopServerAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(config.ModelFileName))
            throw new InvalidOperationException("请在设置中选择模型目录和翻译模型。");
        ModelFile selected = catalog.Inspect(config.ModelDirectory, config.ModelFileName);
        if (selected.Status != ModelFileStatus.Available)
            throw new InvalidOperationException($"模型不可用，请检查设置中的模型目录。{selected.ModelName}");

        IReadOnlyList<LlamaRuntime> attempts = runtimeProvider.Resolve(config.ExecutableOverride,
            config.UseGpu && !forceCpu, config.AllowCpuFallback);
        lastBudgetCheck = DateTime.UtcNow;
        Exception? failure = null;
        var errors = new List<string>();
        foreach (LlamaRuntime runtime in attempts)
        {
            token.ThrowIfCancellationRequested();
            Volatile.Write(ref status, $"正在加载 {selected.ModelName} · {runtime.BackendName}");
            try
            {
                server = await StartServerAsync(selected, config, runtime, token).ConfigureAwait(false);
                Volatile.Write(ref status, $"{selected.ModelName} · {server.Backend}");
                return server;
            }
            catch (Exception exception) when (exception is InvalidOperationException or TimeoutException or IOException
                or UnauthorizedAccessException)
            {
                failure = exception;
                errors.Add($"{runtime.BackendName} 启动失败。{exception.Message}");
            }
            finally
            {
                if (server is null) Volatile.Write(ref status, "模型未加载");
            }
        }

        throw new InvalidOperationException($"无法启动本地翻译引擎。{string.Join(" ", errors)}", failure);
    }

    private async Task<Server> StartServerAsync(
        ModelFile model, LlamaOptions config, LlamaRuntime runtime, CancellationToken token)
    {
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        var endpoint = new Uri($"http://127.0.0.1:{port}/");
        string key = Guid.NewGuid().ToString("N");
        var start = CreateProcessInfo(runtime.Executable);
        string[] arguments =
        [
            "--host", "127.0.0.1", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--model", model.Path, "--alias", "translator", "--ctx-size", Number(tuning.ContextTokens), "--parallel", "1",
            "--n-gpu-layers", runtime.UseGpu ? "auto" : "0", "--split-mode", "none",
            "--fit", runtime.UseGpu ? "on" : "off", "--fit-target", Number(tuning.GpuReserveMiB),
            "--jinja", "--reasoning", "off", "--no-webui", "--log-verbosity", Number(tuning.LogVerbosity), "--api-key", key
        ];
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        string? device = runtime.UseGpu ? runtime.Device : "none";
        if (device is not null)
        {
            start.ArgumentList.Add("--device");
            start.ArgumentList.Add(device);
        }

        var process = new Process { StartInfo = start };
        string? latestError = null;
        string backend = runtime.BackendName;
        process.ErrorDataReceived += (_, args) => ObserveLog(args.Data);
        process.OutputDataReceived += (_, args) => ObserveLog(args.Data);

        void ObserveLog(string? line)
        {
            if (line is null) return;
            const string deviceMarker = "using device ";
            int position = line.IndexOf(deviceMarker, StringComparison.OrdinalIgnoreCase);
            if (runtime.UseGpu && runtime.Device is null && position >= 0)
            {
                string detected = line[(position + deviceMarker.Length)..].TrimStart().Split(' ', 2)[0];
                if (detected.Length > 0) Volatile.Write(ref backend, detected);
            }
            if (line.Contains("error", StringComparison.OrdinalIgnoreCase)) Volatile.Write(ref latestError, line);
        }

        try
        {
            try { process.Start(); }
            catch (System.ComponentModel.Win32Exception exception)
            {
                throw new IOException($"无法运行 {runtime.Executable}。{exception.Message}", exception);
            }

            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
            startup.CancelAfter(TimeSpan.FromSeconds(tuning.StartupSeconds));
            try
            {
                while (!process.HasExited)
                {
                    try
                    {
                        using var health = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, "health"));
                        health.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                        using HttpResponseMessage response = await http.SendAsync(health, startup.Token).ConfigureAwait(false);
                        if (response.IsSuccessStatusCode)
                            return new Server(process, endpoint, key, config, runtime, Volatile.Read(ref backend));
                    }
                    catch (HttpRequestException) { }
                    await Task.Delay(tuning.StartupPollMs, startup.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException("模型加载超时。");
            }

            throw new InvalidOperationException(Volatile.Read(ref latestError) ?? $"llama-server 退出，代码 {process.ExitCode}。");
        }
        catch
        {
            await TerminateAsync(process).ConfigureAwait(false);
            throw;
        }
    }

    private Task<string> TranslateWithServerAsync(Server active, TranslationRequest request, Action<string>? update,
        CancellationToken token)
    {
        int maxTokens = request.Source == TranslationSource.AudioSubtitle ? tuning.SubtitleTokens : tuning.TextTokens;
        return ReadCompletionAsync(active, CreateTranslationBody(request, update is not null, maxTokens), token, update);
    }

    private object CreateTranslationBody(TranslationRequest request, bool stream, int maxTokens) =>
        new
        {
            model = "translator",
            temperature = tuning.TranslationTemperature,
            max_tokens = maxTokens,
            stream,
            chat_template_kwargs = new { enable_thinking = false },
            messages = new[]
            {
                new { role = "system", content = TranslationPrompts.TranslateSystem },
                new { role = "user", content = TranslationPrompts.TranslateInput(request, tuning.ContextLineCharacters) }
            }
        };

    private static HttpRequestMessage CreateCompletionRequest(Server active, object body)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, new Uri(active.Endpoint, "v1/chat/completions"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", active.Key);
        message.Content = JsonContent.Create(body);
        return message;
    }

    private async Task<string> ReadCompletionAsync(Server active, object body, CancellationToken token, Action<string>? update = null)
    {
        using HttpRequestMessage message = CreateCompletionRequest(active, body);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(tuning.RequestSeconds));
        try
        {
            using HttpResponseMessage response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            string? translation;
            if (update is null)
            {
                using JsonDocument json = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
                translation = json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            }
            else
            {
                using var reader = new StreamReader(stream);
                var text = new StringBuilder();
                while (await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
                {
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                    string data = line[5..].Trim();
                    if (data.Length == 0) continue;
                    if (data == "[DONE]") break;
                    using var chunk = JsonDocument.Parse(data);
                    JsonElement choices = chunk.RootElement.GetProperty("choices");
                    if (choices.GetArrayLength() > 0 && choices[0].TryGetProperty("delta", out JsonElement delta)
                        && delta.TryGetProperty("content", out JsonElement content) && content.GetString() is { Length: > 0 } fragment)
                    {
                        text.Append(fragment);
                        if (text.ToString().Trim() is { Length: > 0 } current) update(current);
                    }
                }
                translation = text.ToString();
            }
            return string.IsNullOrWhiteSpace(translation) ? throw new InvalidOperationException("模型没有返回译文。") : translation.Trim();
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("翻译超时。");
        }
    }

    private async Task ReleaseAfterIdleAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(tuning.IdlePollSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
            {
                if (!gate.Wait(0)) continue;
                try
                {
                    if (keepWarm?.Invoke() != true && DateTime.UtcNow - lastUsed >= TimeSpan.FromSeconds(tuning.IdleSeconds))
                        await StopServerAsync().ConfigureAwait(false);
                }
                finally { gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private async Task StopServerAsync()
    {
        Server? previous = server;
        server = null;
        if (previous is not null) await TerminateAsync(previous.Process).ConfigureAwait(false);
        Volatile.Write(ref status, "模型未加载");
    }

    private static async Task TerminateAsync(Process process)
    {
        using (process)
        {
            try
            {
                if (process.HasExited) return;
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException) { }
        }
    }

    private static string Number(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static ProcessStartInfo CreateProcessInfo(string executable) => new(executable)
    {
        WorkingDirectory = Path.IsPathFullyQualified(executable) ? Path.GetDirectoryName(executable)! : Environment.CurrentDirectory,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardError = true,
        RedirectStandardOutput = true
    };

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        if (idleTask is not null) await idleTask.ConfigureAwait(false);
        await gate.WaitAsync().ConfigureAwait(false);
        try { await StopServerAsync().ConfigureAwait(false); }
        finally
        {
            gate.Release();
            http.Dispose();
            lifetime.Dispose();
        }
    }

    private sealed record Server(Process Process, Uri Endpoint, string Key, LlamaOptions Options, LlamaRuntime Runtime, string Backend);
}
