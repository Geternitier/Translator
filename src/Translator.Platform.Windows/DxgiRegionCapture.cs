using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using SharpGen.Runtime;
using Translator.Core;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using DxgiResult = Vortice.DXGI.ResultCode;

namespace Translator.Platform.Windows;

internal static class DxgiRegionCapture
{
    internal static void Watch(ScreenRegion region, ChannelWriter<ScreenFrame> frames, OcrTuning tuning, CancellationToken token)
    {
        try { WatchOutput(region, frames, tuning, token); }
        catch (SharpGenException error) when (error.HResult == DxgiResult.AccessLost.Code && !token.IsCancellationRequested)
        {
            if (!token.WaitHandle.WaitOne(tuning.DxgiRecoveryMs)) WatchOutput(region, frames, tuning, token);
        }
    }

    private static void WatchOutput(ScreenRegion region, ChannelWriter<ScreenFrame> frames, OcrTuning tuning, CancellationToken token)
    {
        nint monitor = (nint)long.Parse(region.DisplayId, CultureInfo.InvariantCulture);
        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint adapterIndex = 0; factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1? adapter).Success; adapterIndex++)
        {
            using (adapter!)
            {
                for (uint outputIndex = 0; adapter!.EnumOutputs(outputIndex, out IDXGIOutput? output).Success; outputIndex++)
                {
                    using (output!)
                    {
                        OutputDescription display = output!.Description;
                        if (display.Monitor != monitor) continue;
                        if (!display.AttachedToDesktop || display.Rotation != ModeRotation.Identity)
                            throw new NotSupportedException("DXGI capture requires an attached, unrotated output.");
                        using var captureOutput = output.QueryInterface<IDXGIOutput1>();
                        Capture(adapter, captureOutput, region, frames, tuning, token);
                        return;
                    }
                }
            }
        }

        throw new NotSupportedException("No DXGI output matches the selected monitor.");
    }

    private static void Capture(IDXGIAdapter1 adapter, IDXGIOutput1 output, ScreenRegion region,
        ChannelWriter<ScreenFrame> frames, OcrTuning tuning, CancellationToken token)
    {
        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0],
            out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
        using var captureDevice = device;
        using var captureContext = context;
        using IDXGIOutputDuplication duplication = output.DuplicateOutput(device);
        var mode = duplication.Description.ModeDescription;
        if (region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0
            || (long)region.X + region.Width > mode.Width || (long)region.Y + region.Height > mode.Height
            || mode.Format != Format.B8G8R8A8_UNorm)
            throw new NotSupportedException("The DXGI output does not match the capture region.");

        using ID3D11Texture2D staging = device.CreateTexture2D(new Texture2DDescription(
            Format.B8G8R8A8_UNorm, (uint)region.Width, (uint)region.Height, arraySize: 1, mipLevels: 1,
            bindFlags: BindFlags.None, usage: ResourceUsage.Staging, cpuAccessFlags: CpuAccessFlags.Read));
        var crop = new Box(region.X, region.Y, 0, region.X + region.Width, region.Y + region.Height, 1);
        var damage = new DesktopDamage();
        long startedAt = Stopwatch.GetTimestamp();
        bool first = true;

        while (!token.IsCancellationRequested)
        {
            Result acquired = duplication.AcquireNextFrame((uint)tuning.DxgiAcquireMs, out OutduplFrameInfo info, out IDXGIResource? resource);
            if (acquired == DxgiResult.WaitTimeout)
            {
                if (first && Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds >= tuning.DxgiFirstFrameMs)
                    throw new NotSupportedException("DXGI did not deliver an initial frame.");
                continue;
            }
            acquired.CheckError();

            try
            {
                using (resource)
                {
                    if (!first && !damage.Intersects(duplication, info, region)) continue;
                    if (resource is null) throw new NotSupportedException("DXGI returned an empty desktop resource.");
                    using var desktop = resource.QueryInterface<ID3D11Texture2D>();
                    context.CopySubresourceRegion(staging, 0, 0, 0, 0, desktop, 0, crop);
                    frames.TryWrite(ReadPixels(context, staging, region));
                    first = false;
                }

                // Keep ownership until the next acquire. Further desktop updates accumulate in DXGI.
                token.WaitHandle.WaitOne(tuning.DxgiReadbackMs);
            }
            finally { duplication.ReleaseFrame().CheckError(); }
        }
    }

    private static ScreenFrame ReadPixels(ID3D11DeviceContext context, ID3D11Texture2D source, ScreenRegion region)
    {
        int stride = checked(region.Width * 4);
        byte[] pixels = new byte[checked(stride * region.Height)];
        MappedSubresource mapped = context.Map(source, 0, MapMode.Read);
        try
        {
            int pitch = checked((int)mapped.RowPitch);
            for (int row = 0; row < region.Height; row++)
                Marshal.Copy(mapped.DataPointer + row * pitch, pixels, row * stride, stride);
        }
        finally { context.Unmap(source, 0); }
        return new ScreenFrame(region, stride, pixels);
    }

    private sealed class DesktopDamage
    {
        private static readonly uint RectSize = (uint)Marshal.SizeOf<RawRect>();
        private static readonly uint MoveSize = (uint)Marshal.SizeOf<OutduplMoveRect>();
        private RawRect[] dirty = [];
        private OutduplMoveRect[] moved = [];

        public bool Intersects(IDXGIOutputDuplication duplication, OutduplFrameInfo frame, ScreenRegion region)
        {
            if (frame.LastPresentTime == 0) return false;
            if (frame.TotalMetadataBufferSize == 0) return true;

            uint total = frame.TotalMetadataBufferSize;
            if (dirty.Length < total / RectSize) Array.Resize(ref dirty, checked((int)(total / RectSize)));
            if (moved.Length < total / MoveSize) Array.Resize(ref moved, checked((int)(total / MoveSize)));
            duplication.GetFrameDirtyRects((uint)dirty.Length * RectSize, dirty, out uint dirtyBytes).CheckError();
            for (int i = 0; i < dirtyBytes / RectSize; i++)
                if (InRegion(dirty[i], region)) return true;

            if (total < MoveSize) return false;
            duplication.GetFrameMoveRects((uint)moved.Length * MoveSize, moved, out uint movedBytes).CheckError();
            for (int i = 0; i < movedBytes / MoveSize; i++)
            {
                var move = moved[i];
                RawRect destination = move.DestinationRect;
                var source = new RawRect(move.SourcePoint.X, move.SourcePoint.Y,
                    move.SourcePoint.X + destination.Right - destination.Left,
                    move.SourcePoint.Y + destination.Bottom - destination.Top);
                if (InRegion(destination, region) || InRegion(source, region)) return true;
            }
            return false;
        }

        private static bool InRegion(RawRect rectangle, ScreenRegion region) =>
            rectangle.Left < region.X + region.Width && rectangle.Right > region.X
            && rectangle.Top < region.Y + region.Height && rectangle.Bottom > region.Y;
    }
}
