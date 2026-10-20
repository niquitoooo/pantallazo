using System.Windows;
using System.Windows.Media.Imaging;
using Pantallazo.Capture;
using Pantallazo.Config;
using Pantallazo.Core;
using Pantallazo.Logging;
using Pantallazo.Region;
using Pantallazo.Views;

namespace Pantallazo.Services;

/// <summary>Las dos capturas de imagen: pantalla completa y región. Guardan solas, sin diálogos.</summary>
public sealed class CaptureController(SettingsStore settings, HistoryStore history)
{
    private bool _busy;

    public string ScreenshotsFolder => settings.Current.ScreenshotsFolder ?? AppPaths.DefaultScreenshotsFolder;

    /// <summary>Se dispara antes de capturar para que la ventana principal se oculte si la captura vino de ella.</summary>
    public Func<Task>? BeforeCaptureFromUi { get; set; }
    public Action? AfterCaptureFromUi { get; set; }

    public async Task CaptureFullScreenAsync(bool fromUi = false)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            ToastWindow.CloseCurrent();
            if (fromUi && BeforeCaptureFromUi is not null) await BeforeCaptureFromUi();
            var foreground = ForegroundInfo.Get(); // para {app} y {ventana} del nombre
            CapturedImage image;
            try
            {
                image = ScreenGrabber.CaptureVirtualScreen(settings.Current.CaptureCursor);
            }
            catch (Exception ex)
            {
                Fail("No se pudo capturar la pantalla", ex);
                return;
            }
            finally
            {
                if (fromUi) AfterCaptureFromUi?.Invoke();
            }
            await SaveAsync(image, foreground);
        }
        finally { _busy = false; }
    }

    public async Task CaptureRegionAsync(bool fromUi = false)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            ToastWindow.CloseCurrent();
            if (fromUi && BeforeCaptureFromUi is not null) await BeforeCaptureFromUi();
            var foreground = ForegroundInfo.Get(); // antes de que aparezca el selector, que queda al frente
            CapturedImage frozen;
            // El puntero se guarda aparte (como ShareX): la pantalla se congela sin él para no taparla al seleccionar,
            // y si la opción está activa se dibuja después en la imagen final, donde estaba al apretar el atajo.
            using var cursor = settings.Current.CaptureCursor ? ScreenGrabber.TakeCursor() : null;
            try
            {
                // Como ShareX: primero se congela todo el escritorio (sin cursor) y se selecciona sobre esa imagen.
                frozen = ScreenGrabber.CaptureVirtualScreen(includeCursor: false);
            }
            catch (Exception ex)
            {
                if (fromUi) AfterCaptureFromUi?.Invoke();
                Fail("No se pudo capturar la pantalla", ex);
                return;
            }

            var s = settings.Current;
            var selector = new RegionSelectorWindow(frozen, new RegionSelectorOptions(
                s.RegionShowMagnifier, s.RegionDetectWindows, s.RegionShowCrosshair, s.RegionMagnifierPixels));
            var rect = selector.Select();
            if (fromUi) AfterCaptureFromUi?.Invoke();
            if (rect is null) return;
            var image = frozen.Crop(rect.Value);
            if (cursor is not null) image = ScreenGrabber.WithCursor(image, cursor);
            await SaveAsync(image, foreground);
        }
        catch (Exception ex)
        {
            Fail("No se pudo capturar la región", ex);
        }
        finally { _busy = false; }
    }

    /// <summary>Elegir una región sin guardar nada (para la grabación de región fija).</summary>
    public PixelRect? PickRegion()
    {
        var frozen = ScreenGrabber.CaptureVirtualScreen(includeCursor: false);
        var s = settings.Current;
        return new RegionSelectorWindow(frozen, new RegionSelectorOptions(
            s.RegionShowMagnifier, s.RegionDetectWindows, s.RegionShowCrosshair, s.RegionMagnifierPixels)).Select();
    }

    private async Task SaveAsync(CapturedImage image, ForegroundInfo.Info foreground)
    {
        var s = settings.Current;
        if (s.PlaySound) Sounds.PlayCapture();
        var folder = ScreenshotsFolder;
        var when = DateTime.Now;
        var fileName = FileNaming.Next(settings, folder, recording: false, when, foreground);
        string path;
        try
        {
            path = await Task.Run(() => ImageSaver.SavePng(image, folder, fileName));
        }
        catch (Exception ex)
        {
            Fail("No se pudo guardar la captura", ex);
            return;
        }
        Log.Info($"Captura guardada: {image.Width}×{image.Height} → {path}");
        if (s.CopyToClipboard) ImageSaver.CopyToClipboard(image);
        history.Add(new HistoryItem(path, HistoryKind.Screenshot, when));
        if (s.ShowNotification) ToastWindow.ShowSaved("Captura guardada", path, Thumbnail(image));
    }

    private static BitmapSource Thumbnail(CapturedImage image)
    {
        var src = image.ToBitmapSource();
        var scale = Math.Min(1.0, Math.Min(720.0 / image.Width, 380.0 / image.Height));
        if (scale >= 1) return src;
        var t = new TransformedBitmap(src, new System.Windows.Media.ScaleTransform(scale, scale));
        t.Freeze();
        return t;
    }

    private static void Fail(string title, Exception ex)
    {
        Log.Error(title, ex);
        ToastWindow.ShowError(title, ex.Message);
    }
}
