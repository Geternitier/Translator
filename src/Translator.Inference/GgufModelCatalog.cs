namespace Translator.Inference;

public enum ModelFileStatus
{
    Missing,
    InvalidHeader,
    Unreadable,
    Available
}

public sealed record ModelFile(
    string FileName, string Path, ModelFileStatus Status, long? SizeBytes = null)
{
    public string ModelName => System.IO.Path.GetFileNameWithoutExtension(FileName);
}

public sealed class GgufModelCatalog
{
    public IReadOnlyList<ModelFile> Inspect(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory, "*.gguf")
            .Where(path => string.Equals(Path.GetExtension(path), ".gguf", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .Select(path => InspectFile(Path.GetFileName(path), path))
            .ToArray();
    }

    public ModelFile Inspect(string directory, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName)
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || !string.Equals(Path.GetExtension(fileName), ".gguf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请选择模型目录中的 GGUF 文件。", nameof(fileName));
        return InspectFile(fileName, Path.Combine(directory, fileName));
    }

    private static ModelFile InspectFile(string fileName, string path)
    {
        try
        {
            using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> magic = stackalloc byte[4];
            ModelFileStatus status = stream.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false) == magic.Length
                && magic.SequenceEqual("GGUF"u8)
                ? ModelFileStatus.Available
                : ModelFileStatus.InvalidHeader;
            return new ModelFile(fileName, path, status, stream.Length);
        }
        catch (IOException exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new ModelFile(fileName, path, ModelFileStatus.Missing);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ModelFile(fileName, path, ModelFileStatus.Unreadable);
        }
    }
}
