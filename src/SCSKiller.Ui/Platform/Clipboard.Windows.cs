using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace SCSKiller.Ui.Platform;

/// <summary>The clipboard through Win32, rendered at once so it stays after the app quits. Avalonia's puts a top-down DIB,
/// from which Windows makes no CF_BITMAP, so apps that paste a CF_BITMAP get a black image.</summary>
static class NativeClipboard
{
    const uint CfDib = 8, CfUnicodeText = 13, GmemMoveable = 2;

    /// <summary>The image as PNG and as a DIB, and the text; false where this isn't Windows.</summary>
    public static bool TrySet(TopLevel owner, Bitmap? image, string? text)
    {
        var data = new List<(uint Format, byte[] Bytes)>();
        if (image != null)
        {
            using var png = new MemoryStream();
            image.Save(png, PngBitmapEncoderOptions.Default);
            data.Add((RegisterClipboardFormatW("PNG"), png.ToArray()));
            data.Add((CfDib, Dib(image)));
        }
        if (text != null) data.Add((CfUnicodeText, Encoding.Unicode.GetBytes(text + "\0")));

        // the clipboard's owner: with none, SetClipboardData fails
        nint hwnd = owner.TryGetPlatformHandle()?.Handle ?? throw new InvalidOperationException("The window has no handle");
        for (int tries = 1; !OpenClipboard(hwnd); tries++)
        {
            if (tries == 10) throw new IOException("Another app is using the clipboard");
            Thread.Sleep(20);
        }
        try
        {
            if (!EmptyClipboard()) throw new Win32Exception();
            foreach (var (format, bytes) in data)
            {
                var h = GlobalAlloc(GmemMoveable, (nuint)bytes.Length);
                if (h == 0) throw new OutOfMemoryException();
                var at = GlobalLock(h);
                if (at == 0)
                {
                    GlobalFree(h);
                    throw new OutOfMemoryException();
                }
                Marshal.Copy(bytes, 0, at, bytes.Length);
                GlobalUnlock(h);
                if (SetClipboardData(format, h) == 0)
                {
                    GlobalFree(h);
                    throw new Win32Exception();
                }
            }
        }
        finally { CloseClipboard(); }
        return true;
    }

    /// <summary>A BITMAPINFOHEADER and 32-bit rows, bottom-up: the order Windows makes a CF_BITMAP from.</summary>
    static byte[] Dib(Bitmap image)
    {
        var (w, h) = (image.PixelSize.Width, image.PixelSize.Height);
        using var copy = new WriteableBitmap(image.PixelSize, image.Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var buffer = copy.Lock();
        image.CopyPixels(buffer);
        var dib = new byte[40 + w * h * 4];
        BinaryPrimitives.WriteInt32LittleEndian(dib, 40);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), w);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), h);
        BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(12), 1);
        BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(14), 32);
        for (int y = 0; y < h; y++) Marshal.Copy(buffer.Address + (h - 1 - y) * buffer.RowBytes, dib, 40 + y * w * 4, w * 4);
        return dib;
    }

    [DllImport("user32.dll", SetLastError = true)] static extern bool OpenClipboard(nint hwnd);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] static extern nint SetClipboardData(uint format, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterClipboardFormatW(string name);
    [DllImport("kernel32.dll")] static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] static extern nint GlobalLock(nint h);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(nint h);
    [DllImport("kernel32.dll")] static extern nint GlobalFree(nint h);
}
