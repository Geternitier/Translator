using System.Runtime.InteropServices;
using RapidOcrNet;
using SkiaSharp;
using Translator.Core;

namespace Translator.Inference;

public sealed class PaddleScreenOcr(string modelDirectory) : IOpticalCharacterRecognizer, IDisposable
{
    private readonly object sync = new();
    private RapidOcr? engine;
    private bool disposed;

    public ValueTask<string> RecognizeAsync(ScreenFrame frame, CancellationToken cancellationToken) =>
        new(Task.Run(() => Recognize(frame, cancellationToken), cancellationToken));

    private string Recognize(ScreenFrame frame, CancellationToken token)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            token.ThrowIfCancellationRequested();
            engine ??= CreateEngine();
            var info = new SKImageInfo(frame.Region.Width, frame.Region.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using var bitmap = new SKBitmap(info);
            byte[] pixels = frame.Pixels.ToArray();
            for (int y = 0; y < frame.Region.Height; y++)
                Marshal.Copy(pixels, y * frame.Stride, bitmap.GetPixels() + y * bitmap.RowBytes, info.RowBytes);
            token.ThrowIfCancellationRequested();
            string text = engine.Detect(bitmap, RapidOcrOptions.Default).StrRes ?? "";
            token.ThrowIfCancellationRequested();
            return text.Trim();
        }
    }

    private RapidOcr CreateEngine()
    {
        var models = RapidOcrModelSet.PPOCRv5Latin with
        {
            DetModelPath = Path.Combine(modelDirectory, "ch_PP-OCRv5_det_mobile.onnx"),
            ClsModelPath = Path.Combine(modelDirectory, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"),
            RecModelPath = Path.Combine(modelDirectory, "ch_PP-OCRv5_rec_mobile.onnx"),
            KeysPath = Path.Combine(modelDirectory, "ppocrv5_dict.txt")
        };
        if (!File.Exists(models.DetModelPath) || !File.Exists(models.ClsModelPath)
            || !File.Exists(models.RecModelPath) || !File.Exists(models.KeysPath))
            throw new FileNotFoundException("缺少英日 OCR 模型，请运行 scripts/setup-runtime.ps1。");
        var instance = new RapidOcr();
        try { instance.InitModels(models); return instance; }
        catch { instance.Dispose(); throw; }
    }

    public void Dispose()
    {
        lock (sync)
        {
            disposed = true;
            engine?.Dispose();
            engine = null;
        }
    }
}
