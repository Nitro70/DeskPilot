using DeskPilot.Core.Abstractions;
using DeskPilot.Desktop.Linux;
using DeskPilot.Desktop.Linux.Services;
using SkiaSharp;

namespace DeskPilot.Linux.Tests;

public class FrameEncoderTests
{
    private static SKBitmap Solid(int w, int h, SKColor color)
    {
        var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        bmp.Erase(color);
        return bmp;
    }

    [Fact]
    public void Scales_and_encodes_jpeg_and_png()
    {
        using var src = Solid(400, 300, new SKColor(0x33, 0x66, 0xcc));
        var req = new CaptureRequest(new ScreenRect(10, 20, 400, 300), 200, 150, ImageFormatKind.Jpeg, 80, false, 0);
        var jpeg = FrameEncoder.Encode(src, req);
        Assert.Equal("image/jpeg", jpeg.MediaType);
        Assert.Equal((200, 150), (jpeg.Width, jpeg.Height));
        Assert.Equal(req.Source, jpeg.Source);
        using (var decoded = SKBitmap.Decode(jpeg.Data))
        {
            Assert.Equal(200, decoded.Width);
            var c = decoded.GetPixel(100, 75);
            Assert.InRange(c.Blue, 0xc0, 0xd8);
        }

        var png = FrameEncoder.Encode(src, req with { Format = ImageFormatKind.Png });
        Assert.Equal("image/png", png.MediaType);
        using var p = SKBitmap.Decode(png.Data);
        Assert.Equal(new SKColor(0x33, 0x66, 0xcc), p.GetPixel(50, 50));
    }

    [Fact]
    public void Draws_cursor_and_grid()
    {
        using var src = Solid(400, 300, SKColors.Gray);
        var req = new CaptureRequest(new ScreenRect(0, 0, 400, 300), 400, 300, ImageFormatKind.Png, 90, true, 100);
        var frame = FrameEncoder.Encode(src, req, new CursorOverlay(200, 150));
        using var bmp = SKBitmap.Decode(frame.Data);
        // Arrow body just right of and below the tip is white or black, not the gray background.
        Assert.NotEqual(SKColors.Gray, bmp.GetPixel(202, 160));
        // Grid line at x = 100.
        Assert.NotEqual(SKColors.Gray, bmp.GetPixel(100, 200));
        Assert.Equal(new[] { 100, 200, 300 }, FrameEncoder.GridPositions(400, 100));
        Assert.Empty(FrameEncoder.GridPositions(400, 0));
    }
}

public class LinuxProcessInfoTests : IDisposable
{
    private readonly string _proc = Path.Combine(Path.GetTempPath(), "fakeproc-" + Guid.NewGuid().ToString("N"));
    private readonly string _saved = LinuxProcessInfo.ProcRoot;

    public LinuxProcessInfoTests()
    {
        Directory.CreateDirectory(_proc);
        LinuxProcessInfo.ProcRoot = _proc;
    }

    public void Dispose()
    {
        LinuxProcessInfo.ProcRoot = _saved;
        try { Directory.Delete(_proc, true); } catch (IOException) { }
    }

    private void Fake(int pid, string comm, int realUid, int effUid)
    {
        var d = Path.Combine(_proc, pid.ToString());
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "comm"), comm + "\n");
        File.WriteAllText(Path.Combine(d, "status"), $"Name:\t{comm}\nUid:\t{realUid}\t{effUid}\t{effUid}\t{effUid}\nGid:\t0\t0\t0\t0\n");
    }

    [Fact]
    public void Reads_names_and_uids()
    {
        Fake(100, "gedit", 1000, 1000);
        Fake(101, "gparted", 0, 0);
        Fake(102, "setuidthing", 1000, 0);
        Assert.Equal("gedit", LinuxProcessInfo.GetProcessName(100));
        Assert.False(LinuxProcessInfo.IsElevated(100));
        Assert.True(LinuxProcessInfo.IsElevated(101));
        Assert.True(LinuxProcessInfo.IsElevated(102));
        Assert.Null(LinuxProcessInfo.GetProcessName(999));
    }

    [Fact]
    public void Detects_privilege_prompts()
    {
        Assert.True(LinuxProcessInfo.IsPrivilegePromptProcess("polkit-gnome-authentication-agent-1"));
        Assert.True(LinuxProcessInfo.IsPrivilegePromptProcess("polkit-gnome-au"));
        Assert.True(LinuxProcessInfo.IsPrivilegePromptProcess("lxqt-policykit-agent"));
        Assert.False(LinuxProcessInfo.IsPrivilegePromptProcess("firefox"));
        Assert.False(LinuxProcessInfo.IsPrivilegePromptActive());
        Fake(200, "pkexec", 1000, 0);
        Assert.True(LinuxProcessInfo.IsPrivilegePromptActive());
    }
}
