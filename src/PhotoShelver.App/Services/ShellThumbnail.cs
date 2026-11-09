using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace PhotoShelver.App.Services;

/// <summary>Windows Shell thumbnails (same as Explorer: JPEG, HEIC, video, RAW with installed codecs).</summary>
internal static class ShellThumbnail
{
    private const int SIIGBF_BIGGERSIZEOK = 0x1;
    private const int SIIGBF_THUMBNAILONLY = 0x8;

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    /// <summary>Returns a frozen bitmap or null when no thumbnail is available. Call from an STA thread.</summary>
    public static BitmapSource? Get(string path, int size)
    {
        IShellItemImageFactory? factory = null;
        var hbitmap = IntPtr.Zero;
        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out factory);
            var hr = factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_THUMBNAILONLY | SIIGBF_BIGGERSIZEOK, out hbitmap);
            if (hr != 0 || hbitmap == IntPtr.Zero) return null;

            var source = Imaging.CreateBitmapSourceFromHBitmap(hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception ex) when (ex is COMException or FileNotFoundException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
        finally
        {
            if (hbitmap != IntPtr.Zero) DeleteObject(hbitmap);
            if (factory is not null) Marshal.ReleaseComObject(factory);
        }
    }
}
