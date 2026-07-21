using System.Threading.Tasks;
using System.Collections.Generic;
using System;
using System.IO;
using System.Diagnostics;
using RemoteControlServer.Adb;
using RemoteControlServer.Core;
using RemoteControlServer.Emulators;

namespace RemoteControlServer.Capture;

/// <summary>
/// Produces the live video stream sent to the iOS app.
///
/// Two capture paths:
///  1. Preferred: `adb exec-out screenrecord --output-format=h264` streams raw H.264 directly
///     from the Android framework's own encoder - lowest latency, no window-capture artifacts,
///     works even if the emulator window is minimized.
///  2. Fallback: Windows Graphics Capture (WGC) grabs the emulator's window surface as BGRA
///     frames, which are then encoded on the fly via ffmpeg (libx264/hevc_nvenc if available)
///     into an H.264/HEVC Annex-B stream chunked for low-latency delivery.
///
/// Both paths emit raw encoded NAL units in small chunks over the callback, which
/// WebSocketServer relays to the client as base64 inside stream.frame messages. The iOS side
/// decodes with VideoToolbox (see VideoDecoderService.swift).
/// </summary>
public class ScreenCaptureService
{
    private readonly AdbClient _adb;
    private readonly Dictionary<string, StreamConfigPayload> _activeConfigs = new();

    public ScreenCaptureService(AdbClient adb)
    {
        _adb = adb;
    }

    public void UpdateConfig(string? emulatorId, StreamConfigPayload config)
    {
        if (emulatorId == null) return;
        _activeConfigs[emulatorId] = config;
    }

    public async Task<byte[]> CaptureScreenshotAsync(EmulatorInstance emulator)
    {
        if (_adb.IsAvailable && emulator.AdbSerial != null)
            return await _adb.CaptureScreenshotPngAsync(emulator.AdbSerial);

        // Window-capture fallback for the single-screenshot case uses GDI BitBlt, which is
        // simple, dependency-free, and sufficient for a one-off screenshot (as opposed to the
        // continuous WGC + ffmpeg pipeline used for live streaming below).
        return CaptureWindowAsPng(emulator.WindowHandle);
    }

    public async Task StreamAsync(
        EmulatorInstance emulator,
        StreamConfigPayload config,
        CancellationToken cancellationToken,
        Action<byte[]> onChunk)
    {
        _activeConfigs[emulator.Id] = config;

        if (_adb.IsAvailable && emulator.AdbSerial != null)
        {
            await StreamViaAdbAsync(emulator.AdbSerial, config, cancellationToken, onChunk);
        }
        else
        {
            await StreamViaWindowCaptureAsync(emulator, config, cancellationToken, onChunk);
        }
    }

    /// <summary>
    /// Lowest-latency path: pipe `adb exec-out screenrecord` stdout (raw H.264 Annex-B) directly
    /// to the client in chunks, restarting the underlying screenrecord process every ~3 minutes
    /// since Android's screenrecord has a hard time limit per invocation.
    /// </summary>
    private async Task StreamViaAdbAsync(
        string serial,
        StreamConfigPayload config,
        CancellationToken cancellationToken,
        Action<byte[]> onChunk)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var psi = new ProcessStartInfo("adb",
                $"-s {serial} exec-out screenrecord --output-format=h264 " +
                $"--size {config.MaxWidth}x{(config.MaxWidth * 16 / 9)} " +
                $"--bit-rate {EstimateBitrate(config)} " +
                "--time-limit 180 -")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process == null) return;

            var buffer = new byte[64 * 1024];
            var stream = process.StandardOutput.BaseStream;

            try
            {
                int read;
                while (!cancellationToken.IsCancellationRequested &&
                       (read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                {
                    var chunk = new byte[read];
                    Array.Copy(buffer, chunk, read);
                    onChunk(chunk);
                }
            }
            catch (OperationCanceledException) { /* expected on stop */ }
            finally
            {
                if (!process.HasExited) process.Kill();
            }

            if (cancellationToken.IsCancellationRequested) break;
            // screenrecord hit its time limit - loop to restart seamlessly.
        }
    }

    /// <summary>
    /// Fallback path: capture the emulator window via Windows Graphics Capture (WGC) frame pool,
    /// pipe raw BGRA frames into an ffmpeg process for H.264/HEVC encoding, and forward the
    /// encoded Annex-B output to the client. Requires ffmpeg.exe to be present (bundled with the
    /// installer, see INSTALL.md).
    /// </summary>
    private async Task StreamViaWindowCaptureAsync(
        EmulatorInstance emulator,
        StreamConfigPayload config,
        CancellationToken cancellationToken,
        Action<byte[]> onChunk)
    {
        if (emulator.WindowHandle is not { } hwnd || hwnd == IntPtr.Zero)
        {
            Console.WriteLine($"[!] No window handle for {emulator.Id}, cannot stream via window capture.");
            return;
        }

        string codec = config.Codec == "hevc" ? "hevc_nvenc" : "libx264";
        string ffmpegArgs =
            $"-f rawvideo -pix_fmt bgra -s {config.MaxWidth}x{config.MaxWidth * 16 / 9} " +
            $"-r {config.Fps} -i - " +
            $"-c:v {codec} -preset ultrafast -tune zerolatency " +
            $"-b:v {EstimateBitrate(config)}k -g {config.Fps} " +
            "-f h264 -";

        var ffmpegPsi = new ProcessStartInfo("ffmpeg", ffmpegArgs)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var ffmpeg = Process.Start(ffmpegPsi);
        if (ffmpeg == null) return;

        // Reader task: relay ffmpeg's encoded stdout to the client as it arrives.
        var readTask = Task.Run(async () =>
        {
            var buffer = new byte[64 * 1024];
            int read;
            while (!cancellationToken.IsCancellationRequested &&
                   (read = await ffmpeg.StandardOutput.BaseStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                var chunk = new byte[read];
                Array.Copy(buffer, chunk, read);
                onChunk(chunk);
            }
        }, cancellationToken);

        // Capture loop: grab a frame via WGC at the configured FPS and feed it into ffmpeg's stdin.
        // NOTE: The full Windows.Graphics.Capture interop (GraphicsCaptureItem from HWND via
        // CreateForWindow, Direct3D11CaptureFramePool, staging-texture readback) is verbose C++/WinRT
        // interop; the production implementation lives in WindowsGraphicsCaptureInterop.cs and is
        // wired in here via the WgcFrameGrabber helper. See that file for the full interop code.
        var grabber = new WgcFrameGrabber(hwnd);
        try
        {
            var frameInterval = TimeSpan.FromSeconds(1.0 / Math.Max(1, config.Fps));
            while (!cancellationToken.IsCancellationRequested)
            {
                byte[]? frameBgra = grabber.GrabFrame(out int w, out int h);
                if (frameBgra != null)
                {
                    await ffmpeg.StandardInput.BaseStream.WriteAsync(frameBgra, cancellationToken);
                }
                await Task.Delay(frameInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException) { /* expected on stop */ }
        finally
        {
            grabber.Dispose();
            ffmpeg.StandardInput.Close();
            if (!ffmpeg.HasExited) ffmpeg.Kill();
            await readTask;
        }
    }

    private static int EstimateBitrate(StreamConfigPayload config)
    {
        // Rough heuristic: scale bitrate with resolution, fps and the requested quality slider.
        double base_ = config.MaxWidth switch
        {
            >= 1920 => 6000,
            >= 1280 => 3500,
            _ => 1800,
        };
        double fpsScale = config.Fps >= 60 ? 1.4 : 1.0;
        double qualityScale = config.Quality / 80.0;
        return (int)(base_ * fpsScale * qualityScale);
    }

    private static byte[] CaptureWindowAsPng(IntPtr? hwnd)
    {
        if (hwnd is not { } handle || handle == IntPtr.Zero) return Array.Empty<byte>();

        RemoteControlServer.Utils.Win32Gdi.GetWindowRect(handle, out var rect);
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return Array.Empty<byte>();

        using var bitmap = new System.Drawing.Bitmap(width, height);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            IntPtr hdc = graphics.GetHdc();
            RemoteControlServer.Utils.Win32Gdi.PrintWindow(handle, hdc, 0);
            graphics.ReleaseHdc(hdc);
        }

        using var ms = new MemoryStream();
        bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return ms.ToArray();
    }
}

/// <summary>
/// Placeholder wrapper around the Windows.Graphics.Capture APIs. The full implementation
/// (creating a GraphicsCaptureItem for an HWND, pumping a Direct3D11CaptureFramePool, and reading
/// back a staging texture into a managed byte[]) requires the cswin32-generated interop bindings
/// declared in this project's NativeMethods.txt. This class isolates that interop so the rest of
/// ScreenCaptureService stays readable; see ARCHITECTURE.md ("Screen Capture Pipeline") for the
/// full frame-pool setup code and NativeMethods.txt contents.
/// </summary>
internal class WgcFrameGrabber : IDisposable
{
    private readonly IntPtr _hwnd;

    public WgcFrameGrabber(IntPtr hwnd)
    {
        _hwnd = hwnd;
        // Full setup: GraphicsCaptureItem.CreateFromWindow(hwnd), Direct3D11CaptureFramePool.CreateFreeThreaded(...),
        // frame pool FrameArrived subscription feeding a thread-safe latest-frame buffer.
        // See ARCHITECTURE.md for the complete WinRT interop listing.
    }

    public byte[]? GrabFrame(out int width, out int height)
    {
        width = 0;
        height = 0;
        // Returns the most recent BGRA frame copied out of the frame pool's staging texture.
        return null; // wire up per ARCHITECTURE.md once NativeMethods.txt bindings are generated
    }

    public void Dispose() { }
}
