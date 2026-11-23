using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Translator.Core;

namespace Translator.Inference;

public sealed class NemoSpeakerDiarizer(string libraryPath, string modelPath) : ISpeakerDiarizer
{
    private const int SampleRate = AudioFrame.SampleRate;
    private static readonly TimeSpan History = TimeSpan.FromMinutes(2);
    private static readonly ConcurrentDictionary<string, Lazy<NativeDiarizer>> Libraries = new();

    public async IAsyncEnumerable<SpeakerSnapshot> TrackAsync(IAsyncEnumerable<AudioFrame> audio,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!File.Exists(libraryPath) || !File.Exists(modelPath))
            throw new FileNotFoundException("缺少说话者识别运行库或模型，请运行 setup-runtime.ps1 -SpeakerDiarization。");

        NativeDiarizer native = GetLibrary(libraryPath);
        IntPtr model = native.CreateModel(modelPath);
        try
        {
            native.Check(native.Open(model, out IntPtr stream));
            try
            {
                double secondsPerFrame = native.SecondsPerFrame(model);
                var silence = new float[SampleRate];
                TimeSpan? origin = null;
                long samplesWritten = 0;
                long lastFrameCount = 0;

                await foreach (AudioFrame frame in audio.WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    if (frame.Samples.IsEmpty) continue;
                    origin ??= frame.Offset;
                    long position = (long)Math.Round((frame.Offset - origin.Value).TotalSeconds * SampleRate);
                    long gap = position - samplesWritten;
                    if (gap > 5 * SampleRate)
                        throw new InvalidDataException("音频时间轴中断，说话者标注已停止。");
                    while (gap > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int count = (int)Math.Min(gap, silence.Length);
                        native.Check(native.Push(stream, silence, (nuint)count, SampleRate));
                        samplesWritten += count;
                        gap -= count;
                    }

                    int skip = (int)Math.Clamp(samplesWritten - position, 0, frame.Samples.Length);
                    if (skip == frame.Samples.Length) continue;
                    var samples = new float[frame.Samples.Length - skip];
                    for (int i = 0; i < samples.Length; i++)
                        samples[i] = frame.Samples.Span[skip + i] / 32768f;

                    native.Check(native.Push(stream, samples, (nuint)samples.Length, SampleRate));
                    samplesWritten += samples.Length;
                    long frames = native.FrameCount(stream);
                    if (frames <= lastFrameCount) continue;
                    lastFrameCount = frames;
                    double end = Math.Min(frames * secondsPerFrame, samplesWritten / (double)SampleRate);
                    yield return ReadSnapshot(native, stream, origin.Value, end);
                }

                if (origin is { } start)
                {
                    native.Check(native.Finish(stream));
                    yield return ReadSnapshot(native, stream, start, samplesWritten / (double)SampleRate);
                }
            }
            finally { native.Close(stream); }
        }
        finally { native.Destroy(model); }
    }

    private static NativeDiarizer GetLibrary(string path)
    {
        string fullPath = Path.GetFullPath(path);
        // The native backend retains process-wide worker state. Only model and stream handles
        // are closed between sessions; unloading the DLL after inference is unsafe on Windows.
        Lazy<NativeDiarizer> load = Libraries.GetOrAdd(fullPath, static name => new(() => new NativeDiarizer(name)));
        try { return load.Value; }
        catch
        {
            Libraries.TryRemove(new KeyValuePair<string, Lazy<NativeDiarizer>>(fullPath, load));
            throw;
        }
    }

    private static SpeakerSnapshot ReadSnapshot(NativeDiarizer native, IntPtr stream, TimeSpan origin, double seconds)
    {
        TimeSpan end = origin + TimeSpan.FromSeconds(seconds);
        TimeSpan start = end - origin > History ? end - History : origin;
        var spans = new List<SpeakerSpan>();
        foreach (NativeSegment segment in native.ReadSegments(stream))
        {
            TimeSpan left = origin + TimeSpan.FromSeconds(segment.Start);
            TimeSpan right = origin + TimeSpan.FromSeconds(segment.End);
            if (right <= start || left >= end || right <= left) continue;
            spans.Add(new SpeakerSpan(segment.Speaker, left < start ? start : left, right > end ? end : right));
        }
        return new SpeakerSnapshot(start, end, spans);
    }

    private sealed class NativeDiarizer
    {
        private readonly IntPtr library;
        private readonly Create create;
        private readonly LastError lastError;
        private readonly Segments segments;
        public readonly OpenStream Open;
        public readonly PointerOperation Destroy;
        public readonly PointerOperation Close;
        public readonly PushAudio Push;
        public readonly StatusOperation Finish;
        public readonly FrameCounter FrameCount;
        public readonly FrameDuration SecondsPerFrame;

        public NativeDiarizer(string path)
        {
            library = NativeLibrary.Load(Path.GetFullPath(path), typeof(NemoSpeakerDiarizer).Assembly,
                DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories);
            try
            {
                create = Export<Create>("nemo_speech_diar_create");
                Open = Export<OpenStream>("nemo_speech_diar_stream_open");
                Destroy = Export<PointerOperation>("nemo_speech_diar_destroy");
                Close = Export<PointerOperation>("nemo_speech_diar_stream_close");
                Push = Export<PushAudio>("nemo_speech_diar_stream_push_f32");
                Finish = Export<StatusOperation>("nemo_speech_diar_stream_finish");
                FrameCount = Export<FrameCounter>("nemo_speech_diar_frame_count");
                SecondsPerFrame = Export<FrameDuration>("nemo_speech_diar_seconds_per_frame");
                segments = Export<Segments>("nemo_speech_diar_segments");
                lastError = Export<LastError>("nemo_speech_asr_last_error");
            }
            catch { NativeLibrary.Free(library); throw; }
        }

        public IntPtr CreateModel(string path)
        {
            IntPtr modelPath = Marshal.StringToCoTaskMemUTF8(Path.GetFullPath(path));
            try
            {
                var config = new ModelConfig
                {
                    Size = (nuint)Marshal.SizeOf<ModelConfig>(),
                    ModelPath = modelPath,
                    Gpu = -1,
                    LeftContextFrames = -1
                };
                Check(create(in config, out IntPtr model));
                return model;
            }
            finally { Marshal.FreeCoTaskMem(modelPath); }
        }

        public NativeSegment[] ReadSegments(IntPtr stream)
        {
            Check(segments(stream, IntPtr.Zero, null, 0, out nuint count));
            if (count == 0) return [];
            var found = new NativeSegment[checked((int)count)];
            Check(segments(stream, IntPtr.Zero, found, count, out _));
            return found;
        }

        public void Check(int status)
        {
            if (status != 0)
                throw new InvalidOperationException(Marshal.PtrToStringUTF8(lastError()) ?? $"NeMo status {status}");
        }

        private T Export<T>(string name) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ModelConfig
    {
        public nuint Size;
        public IntPtr ModelPath;
        public int Gpu;
        public IntPtr Preset;
        public int ChunkFrames;
        public int RightContextFrames;
        public int LeftContextFrames;
        public int FifoFrames;
        public int SpeakerCacheFrames;
        public int UpdatePeriodFrames;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSegment
    {
        public double Start;
        public double End;
        public int Speaker;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Create(in ModelConfig config, out IntPtr model);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int OpenStream(IntPtr model, out IntPtr stream);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void PointerOperation(IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int StatusOperation(IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PushAudio(IntPtr stream, [In] float[] samples, nuint count, int rate);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long FrameCounter(IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate double FrameDuration(IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Segments(IntPtr stream, IntPtr config, [Out] NativeSegment[]? result, nuint capacity, out nuint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr LastError();
}
