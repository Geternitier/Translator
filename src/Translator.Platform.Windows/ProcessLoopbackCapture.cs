using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Translator.Core;

namespace Translator.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class ProcessLoopbackCapture : IApplicationAudioCapture
{
    public async IAsyncEnumerable<AudioFrame> CaptureAsync(AudioSource source,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
            throw new PlatformNotSupportedException("进程音频捕获需要 Windows 11。");
        string[] identity = source.Id.Split(':');
        if (identity.Length != 2 || !int.TryParse(identity[0], out int pid) || !long.TryParse(identity[1], out long started))
            throw new ArgumentException("音频来源无效。", nameof(source));
        using Process target = Process.GetProcessById(pid);
        if (target.StartTime.ToUniversalTime().Ticks != started) throw new InvalidOperationException("音频来源已退出。");

        Task<WasapiRecorder> startup = new WasapiRecorderBuilder()
            .WithProcessLoopback((uint)pid, ProcessLoopbackMode.IncludeTargetProcessTree)
            .WithFormat(new WaveFormat(AudioFrame.SampleRate, 16, 1))
            .BuildAsync();
        await using WasapiRecorder recorder = await WaitForRecorderAsync(startup, cancellationToken).ConfigureAwait(false);
        var frames = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(128)
        {
            SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest
        });
        long received = 0;
        target.EnableRaisingEvents = true;
        target.Exited += (_, _) => frames.Writer.TryComplete();
        if (target.HasExited) yield break;
        recorder.DataAvailable += (buffer, flags, _, _) =>
        {
            int length = buffer.Length / sizeof(short);
            if (length == 0) return;
            long start = Interlocked.Add(ref received, length) - length;
            short[] samples = flags.HasFlag(AudioClientBufferFlags.Silent)
                ? new short[length] : MemoryMarshal.Cast<byte, short>(buffer).ToArray();
            frames.Writer.TryWrite(new AudioFrame(TimeSpan.FromSeconds(start / (double)AudioFrame.SampleRate), samples));
        };
        recorder.RecordingStopped += (_, args) => frames.Writer.TryComplete(args.Exception);
        recorder.StartRecording();
        try
        {
            await foreach (AudioFrame frame in frames.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return frame;
        }
        finally { recorder.StopRecording(); }
    }

    private static async Task<WasapiRecorder> WaitForRecorderAsync(Task<WasapiRecorder> startup, CancellationToken token)
    {
        try { return await startup.WaitAsync(TimeSpan.FromSeconds(12), token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            _ = DisposeWhenReadyAsync(startup);
            throw;
        }
    }

    private static async Task DisposeWhenReadyAsync(Task<WasapiRecorder> startup)
    {
        try { await (await startup.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false); }
        catch (Exception) { }
    }
}
