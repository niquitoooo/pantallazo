using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Pantallazo.Core;
using Pantallazo.Hotkeys;
using Pantallazo.Services;
using Pantallazo.Theming;

namespace Pantallazo.Views;

public partial class HomePage : UserControl
{
    private readonly App _app = App.Instance;

    public HomePage()
    {
        InitializeComponent();
        RecentList.ItemsSource = _app.History.Items;
        _app.History.Items.CollectionChanged += (_, _) => UpdateEmpty();
        _app.Hotkeys.StatusChanged += (_, _) => Dispatcher.BeginInvoke(Refresh);
        _app.Recording.PropertyChanged += OnRecordingChanged;
        Loaded += (_, _) => { _app.History.Prune(); Refresh(); };
        SizeChanged += (_, e) => Actions.Columns = e.NewSize.Width < 760 ? 1 : 3;
        Refresh();
    }

    private void Refresh()
    {
        var s = _app.Settings.Current;
        FullScreenKeys.ItemsSource = Keys(s, HotkeyAction.FullScreen);
        RegionKeys.ItemsSource = Keys(s, HotkeyAction.Region);
        RecordKeys.ItemsSource = Keys(s, HotkeyAction.Record);
        UpdateRecording();
        UpdateWarning();
        UpdateEmpty();
    }

    private static List<string> Keys(AppSettings s, HotkeyAction action)
    {
        var list = s.Hotkeys.Where(b => b.Action == action && b.Gesture.IsValid)
            .Select(b => s.PrintScreenPaused && b.Gesture.UsesPrintScreen ? $"{b.Gesture} (en pausa)" : b.Gesture.ToString())
            .ToList();
        return list.Count > 0 ? list : ["Sin atajo"];
    }

    private void UpdateWarning()
    {
        var failed = _app.Hotkeys.Failed.ToList();
        var paused = _app.Settings.Current.PrintScreenPaused;
        var usesBarePrintScreen = _app.Settings.Current.Hotkeys.Any(b =>
            b.Gesture is { Key: HotkeyGesture.VkPrintScreen, Modifiers: HotkeyModifiers.None });
        // En pausa, que Recortes tenga Impr Pant es justamente lo pedido: no se avisa.
        var snipping = !paused && usesBarePrintScreen && PrintScreenKey.Windows.IsUsedBySnippingTool();

        if (failed.Count == 0 && !snipping)
        {
            WarningCard.Visibility = Visibility.Collapsed;
            StatusText.Text = paused
                ? "Impr Pant está en pausa: Windows la usa como siempre. Se reactiva en Configuración → Atajos de teclado."
                : "Pantallazo está activo: los atajos funcionan aunque cierres esta ventana.";
            return;
        }
        WarningCard.Visibility = Visibility.Visible;
        FreePrintScreenButton.Visibility = snipping ? Visibility.Visible : Visibility.Collapsed;
        if (snipping)
        {
            WarningTitle.Text = "Impr Pant abre la Herramienta Recortes";
            WarningText.Text = "Windows le da la tecla Impr Pant a Recortes, así que puede no llegar a Pantallazo. " +
                               "Podés liberarla (se puede deshacer en Configuración) o usar otro atajo.";
        }
        else
        {
            WarningTitle.Text = failed.Count == 1 ? "Un atajo no se pudo activar" : $"{failed.Count} atajos no se pudieron activar";
            WarningText.Text = string.Join("\n", failed.Select(b =>
                $"{b.Gesture} ({ActionName(b.Action)}): {(_app.Hotkeys.FailureReasons.TryGetValue(b, out var r) ? r : "ocupado")}"));
        }
        StatusText.Text = "Pantallazo está activo, pero revisá el aviso de abajo.";
    }

    public static string ActionName(HotkeyAction a) => a switch
    {
        HotkeyAction.FullScreen => "pantalla completa",
        HotkeyAction.Region => "región",
        _ => "grabar",
    };

    private void OnRecordingChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(UpdateRecording);

    private void UpdateRecording()
    {
        var r = _app.Recording;
        switch (r.State)
        {
            case RecordingUiState.Recording or RecordingUiState.Paused:
                RecordTitle.Text = "Detener grabación";
                RecordSubtitle.Text = $"{r.StatusText} · {r.ElapsedText}";
                RecordIcon.Text = Icons.Stop;
                RecordIconCircle.SetResourceReference(Border.BackgroundProperty, "M3.Error");
                RecordIcon.SetResourceReference(TextBlock.ForegroundProperty, "M3.OnError");
                break;
            case RecordingUiState.Starting or RecordingUiState.Saving:
                RecordTitle.Text = r.StatusText;
                RecordSubtitle.Text = "Un momento…";
                break;
            default:
                RecordTitle.Text = "Iniciar grabación";
                RecordSubtitle.Text = "Video MP4 con el audio elegido.";
                RecordIcon.Text = Icons.Record;
                RecordIconCircle.SetResourceReference(Border.BackgroundProperty, "M3.TertiaryContainer");
                RecordIcon.SetResourceReference(TextBlock.ForegroundProperty, "M3.OnTertiaryContainer");
                break;
        }
        RecordCard.IsEnabled = r.State is RecordingUiState.Idle || r.IsRecording;
        if (r.State == RecordingUiState.Recording)
            StatusText.Text = $"Grabando ({r.ElapsedText}). Volvé a apretar el atajo o tocá la tarjeta para detener.";
        else if (r.IsPaused)
            StatusText.Text = $"En pausa ({r.ElapsedText}). Reanudá desde el indicador flotante o tocá la tarjeta para detener y guardar.";
        else if (r.State == RecordingUiState.Idle)
            UpdateWarning();   // vuelve el texto normal (antes quedaba "Grabando (00:00)" después de detener)
    }

    private void UpdateEmpty() =>
        EmptyText.Visibility = _app.History.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private async void OnFullScreen(object sender, RoutedEventArgs e) => await _app.Capture.CaptureFullScreenAsync(fromUi: true);
    private async void OnRegion(object sender, RoutedEventArgs e) => await _app.Capture.CaptureRegionAsync(fromUi: true);
    private async void OnRecord(object sender, RoutedEventArgs e) => await _app.Recording.ToggleAsync();
    private void OnOpenScreenshots(object sender, RoutedEventArgs e) => Shell.OpenFolder(_app.Capture.ScreenshotsFolder);
    private void OnOpenRecordings(object sender, RoutedEventArgs e) => Shell.OpenFolder(_app.Recording.RecordingsFolder);
    private void OnOpenShortcuts(object sender, RoutedEventArgs e) => _app.ShowMainWindow(MainPage.Settings);

    private void OnFreePrintScreen(object sender, RoutedEventArgs e)
    {
        if (PrintScreenKey.Windows.SetUsedBySnippingTool(false))
        {
            _app.ApplyHotkeys();
            ToastWindow.ShowInfo("Impr Pant liberada",
                "Recortes ya no usa Impr Pant. Si todavía se abre Recortes, cerrá sesión en Windows y volvé a entrar.");
        }
        else ToastWindow.ShowError("No se pudo liberar Impr Pant", "Revisá el registro de Pantallazo para más detalles.");
        Refresh();
    }

    private void OnOpenItem(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is HistoryItem i) Shell.Open(i.Path);
    }

    private void OnRenameItem(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is HistoryItem i) Rename(i);
    }

    private void OnDeleteItem(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is HistoryItem i) Delete(i);
    }

    /// <summary>Clic derecho en una fila: las mismas acciones que los botones, como menú.</summary>
    private void OnItemMenu(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not HistoryItem i) return;
        var res = Application.Current.Resources;
        var menu = new ContextMenu { Style = (Style)res["M3.Menu"], PlacementTarget = (UIElement)sender };
        MenuItem Item(string header, string icon, Action onClick)
        {
            var mi = new MenuItem { Header = header, Style = (Style)res["M3.MenuItem"] };
            M3.SetIcon(mi, icon);
            mi.Click += (_, _) => onClick();
            return mi;
        }
        menu.Items.Add(Item("Abrir", Icons.Open, () => Shell.Open(i.Path)));
        menu.Items.Add(Item("Mostrar en la carpeta", Icons.Folder, () => Shell.Reveal(i.Path)));
        menu.Items.Add(new Separator { Style = (Style)res["M3.MenuSeparator"] });
        menu.Items.Add(Item("Renombrar", Icons.Rename, () => Rename(i)));
        menu.Items.Add(Item("Eliminar", Icons.Delete, () => Delete(i)));
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void Rename(HistoryItem item)
    {
        if (!File.Exists(item.Path)) { _app.History.Prune(); return; }
        RenameDialog.Show(Window.GetWindow(this), item.Path, newPath => _app.History.Rename(item.Path, newPath));
    }

    /// <summary>A la Papelera, como Supr en el Explorador: se puede recuperar, por eso no se pregunta.</summary>
    private void Delete(HistoryItem item)
    {
        var problem = Shell.Recycle(item.Path);
        if (problem is null)
        {
            _app.History.Remove(item.Path);
            ToastWindow.ShowInfo("Enviado a la Papelera", item.Name);
        }
        else ToastWindow.ShowError($"No se pudo eliminar {item.Name}", problem);
    }

    private void OnRevealItem(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is HistoryItem i) Shell.Reveal(i.Path);
    }
}
