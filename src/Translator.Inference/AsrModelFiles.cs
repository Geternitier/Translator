namespace Translator.Inference;

internal static class AsrModelFiles
{
    public static string? FindDirectory(string root, IReadOnlyList<string> files, Func<string, int>? preference = null)
    {
        if (ContainsModel(root)) return root;
        if (!Directory.Exists(root)) return null;
        return Directory.EnumerateDirectories(root)
            .Where(ContainsModel)
            .OrderBy(path => preference?.Invoke(Path.GetFileName(path)) ?? 0)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        bool ContainsModel(string directory) => files.All(name => File.Exists(Path.Combine(directory, name)));
    }

    public static string Require(string directory, string name, string model)
    {
        string path = Path.Combine(directory, name);
        if (!File.Exists(path))
            throw new FileNotFoundException($"缺少 {model} 模型文件 {name}。请下载并解压到 {directory}。", path);
        return path;
    }

    public static int CpuThreads => Math.Clamp(Environment.ProcessorCount / 2, 1, 6);
}
