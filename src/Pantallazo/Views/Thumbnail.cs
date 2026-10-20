using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Pantallazo.Logging;
using Pantallazo.Services;

namespace Pantallazo.Views;

/// <summary>
/// Miniatura de un elemento de Recientes, cargada en segundo plano para no trabar la lista. Capturas: se lee el PNG
/// achicado. Grabaciones: la misma miniatura que muestra el Explorador (proveedor de Windows para MP4). Mientras
/// tanto, o si no hay miniatura, queda visible lo que esté debajo de la imagen (el ícono de video).
/// Uso: <c>&lt;Image v:Thumbnail.For="{Binding}" /&gt;</c>
/// </summary>
public static class Thumbnail
{
    private const int Height = 96;
    private static readonly ConcurrentDictionary<(string Path, long Stamp), BitmapSource> Cache = new();
    private static readonly SemaphoreSlim Gate = new(2); // pocas a la vez: el decodificador de video pesa

    public static readonly DependencyProperty ForProperty = DependencyProperty.RegisterAttached(
        "For", typeof(HistoryItem), typeof(Thumbnail), new PropertyMetadata(null, OnForChanged));

    public static HistoryItem? GetFor(DependencyObject o) => (HistoryItem?)o.GetValue(ForProperty);
    public static void SetFor(DependencyObject o, HistoryItem? value) => o.SetValue(ForProperty, value);

    private static async void OnForChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image image) return;
        image.Source = null;
        if (e.NewValue is not HistoryItem item) return;
        var source = await Task.Run(() => Load(item));
        // Si mientras tanto la fila pasó a mostrar otro archivo (renombrado, reciclada), no pisarla.
        if (source is not null && ReferenceEquals(GetFor(image), item)) image.Source = source;
    }

    private static async Task<BitmapSource?> Load(HistoryItem item)
    {
        try
        {
            var info = new FileInfo(item.Path);
            if (!info.Exists) return null;
            var key = (item.Path, info.LastWriteTimeUtc.Ticks ^ info.Length);
            if (Cache.TryGetValue(key, out var cached)) return cached;
            await Gate.WaitAsync();
            try
            {
                var bmp = item.Kind == HistoryKind.Screenshot ? FromImage(item.Path) : FromShell(item.Path);
                if (bmp is not null) Cache[key] = bmp;
                return bmp;
            }
            finally { Gate.Release(); }
        }
        catch (Exception ex)
        {
            Log.Warn($"Sin miniatura para {item.Name}: {ex.Message}");
            return null;
        }
    }

    private static BitmapSource FromImage(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad; // no deja el archivo bloqueado
        bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bmp.DecodePixelHeight = Height;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    // ---------- Miniatura del Explorador (IShellItemImageFactory) ----------

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out nint hbitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, nint bindCtx, [In] ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint obj);

    private static BitmapSource? FromShell(string path)
    {
        const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_THUMBNAILONLY = 0x8; // solo miniatura real, nunca el ícono genérico
        var iid = typeof(IShellItemImageFactory).GUID;
        SHCreateItemFromParsingName(path, 0, ref iid, out var factory);
        try
        {
            var hr = factory.GetImage(new SIZE { cx = Height * 16 / 9, cy = Height }, SIIGBF_BIGGERSIZEOK | SIIGBF_THUMBNAILONLY, out var hbitmap);
            if (hr != 0 || hbitmap == 0) return null;
            try
            {
                var bmp = Imaging.CreateBitmapSourceFromHBitmap(hbitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bmp.Freeze();
                return bmp;
            }
            finally { DeleteObject(hbitmap); }
        }
        finally { Marshal.ReleaseComObject(factory); }
    }
}
