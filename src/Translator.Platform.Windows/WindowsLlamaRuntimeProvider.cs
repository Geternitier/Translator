using System.ComponentModel;
using System.Diagnostics;
using Translator.Core;

namespace Translator.Platform.Windows;

public sealed class WindowsLlamaRuntimeProvider(string runtimeDirectory, WindowsTuning tuning) : ILlamaRuntimeProvider
{
    private const string ServerName = "llama-server.exe";
    private static readonly string[] CudaLibraries =
        ["llama-server-impl.dll", "ggml-cuda.dll", "cudart64_13.dll", "cublas64_13.dll", "cublasLt64_13.dll"];

    public IReadOnlyList<LlamaRuntime> Resolve(string configuredExecutable, bool preferGpu, bool allowCpuFallback)
    {
        var candidates = new List<LlamaRuntime>();
        if (!string.IsNullOrWhiteSpace(configuredExecutable))
        {
            if (preferGpu) candidates.Add(new(configuredExecutable, "自动设备", true));
            if (!preferGpu || allowCpuFallback) candidates.Add(new(configuredExecutable, "CPU", false));
            return candidates;
        }

        string? cuda = FindBundled("llama-cuda", CudaLibraries);
        string? vulkan = FindBundled("llama-vulkan", []);
        if (preferGpu)
        {
            if (cuda is not null) candidates.Add(new(cuda, "CUDA", true, tuning.CudaDevice));
            if (vulkan is not null) candidates.Add(new(vulkan, "Vulkan", true));
            if (candidates.Count == 0) candidates.Add(new(ServerName, "自动设备", true));
        }

        if (!preferGpu || allowCpuFallback)
            candidates.Add(new(vulkan ?? cuda ?? ServerName, "CPU", false));
        return candidates;
    }

    private string? FindBundled(string backendDirectory, IReadOnlyList<string> libraries)
    {
        string directory = Path.Combine(runtimeDirectory, backendDirectory);
        string executable = Path.Combine(directory, ServerName);
        return File.Exists(executable) && libraries.All(name => File.Exists(Path.Combine(directory, name)))
            ? executable : null;
    }

    public async ValueTask<int?> ReadFreeVideoMemoryMiBAsync(LlamaRuntime runtime, CancellationToken cancellationToken)
    {
        if (!runtime.UseGpu) return null;

        string systemExecutable = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
        var start = new ProcessStartInfo(File.Exists(systemExecutable) ? systemExecutable : "nvidia-smi.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        };
        start.ArgumentList.Add("--query-gpu=memory.free");
        start.ArgumentList.Add("--format=csv,noheader,nounits");
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(tuning.MemoryProbeMs));
        try
        {
            using Process? process = Process.Start(start);
            if (process is null) return null;
            try
            {
                string output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                // Do not guess which adapter is in use on a multi-GPU system.
                string[] entries = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return process.ExitCode == 0 && entries.Length == 1 && int.TryParse(entries[0], out int memory)
                    ? memory : null;
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(); }
                catch (InvalidOperationException) { }
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }
        }
        catch (Win32Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }
}
