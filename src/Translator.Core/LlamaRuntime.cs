namespace Translator.Core;

public sealed record LlamaRuntime(string Executable, string BackendName, bool UseGpu, string? Device = null);

public interface ILlamaRuntimeProvider
{
    IReadOnlyList<LlamaRuntime> Resolve(string configuredExecutable, bool preferGpu, bool allowCpuFallback);
    ValueTask<int?> ReadFreeVideoMemoryMiBAsync(LlamaRuntime runtime, CancellationToken cancellationToken);
}
