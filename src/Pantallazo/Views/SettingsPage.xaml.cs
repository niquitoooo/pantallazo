using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;
using Pantallazo.Config;
using Pantallazo.Core;
using Pantallazo.Hotkeys;
using Pantallazo.Logging;
using Pantallazo.Services;
using Pantallazo.Theming;

namespace Pantallazo.Views;

public partial class SettingsPage : UserControl
{
    private static readonly (string? Hex, string Name)[] SeedColors =
    [
        (null, "Color de Windows"),
        ("#4F6CB8", "Azul"),
        ("#00897B", "Verde azulado"),
        ("#3E8E41", "Verde"),
        ("#B26A00", "Ámbar"),
        ("#C2185B", "Rosa"),
        ("#7B4FB8", "Violeta"),
        ("#6D6D6D", "Gris"),
    ];

    private readonly App _app = App.Instance;
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
        _app.Hotkeys.StatusChanged += (_, _) => Dispatcher.BeginInvoke(RefreshHotkeyStatus);
        _app.Settings.Changed += (_, _) => Dispatcher.BeginInvoke(UpdateQualitySummary);
        Loaded += (_, _) => Load();
    }

    private AppSettings S => _app.Settings.Current;

    public void ScrollToShortcuts() => ShortcutsTitle.BringIntoView();

    private void Load()
    {
        _loading = true;
        try
        {
            BuildHotkeys();
            PausePrintScreenSwitch.IsChecked = S.PrintScreenPaused;
            UpdateSnipping();
            ShotsFolder.Text = _app.Capture.ScreenshotsFolder;
            ShotTemplate.Text = S.ScreenshotNameTemplate;
            VideoTemplate.Text = S.RecordingNameTemplate;
            BuildTagChips();
            VideosFolder.Text = _app.Recording.RecordingsFolder;
            ThemeSystem.IsChecked = S.Theme == ThemePreference.System;
            ThemeLight.IsChecked = S.Theme == ThemePreference.Light;
            ThemeDark.IsChecked = S.Theme == ThemePreference.Dark;
            BuildSwatches();
            BuildCaptureOptions();
            BuildBehaviorOptions();
            UpdateQualitySummary();
            AboutText.Text = $"Pantallazo {typeof(App).Assembly.GetName().Version?.ToString(3)}";
        }
        finally { _loading = false; }
        UpdateTemplateExamples();
    }

    // ---------- Nombres de archivo ----------

    private TextBox? _lastTemplateBox;

    private void BuildTagChips()
    {
        TagChips.Children.Clear();
        foreach (var (tag, meaning) in NameTemplate.Tags)
        {
            // Sin foco propio: así el cursor sigue en el campo y la etiqueta entra donde estaba.
            var chip = new Button
            {
                Style = (Style)FindResource("M3.OutlinedButton"),
                Content = tag,
                ToolTip = meaning,
                Focusable = false,
                Padding = new Thickness(14, 0, 14, 0),
                Margin = new Thickness(0, 0, 8, 8),
            };
            chip.Click += (_, _) =>
            {
                var box = _lastTemplateBox ?? ShotTemplate;
                box.SelectedText = tag;
                box.CaretIndex = box.SelectionStart + box.SelectionLength;
                box.SelectionLength = 0;
                box.Focus();
            };
            TagChips.Children.Add(chip);
        }
    }

    private void OnTemplateFocus(object sender, RoutedEventArgs e) => _lastTemplateBox = sender as TextBox;

    /// <summary>Se guarda solo si la plantilla sirve; si no, el error queda a la vista y sigue la anterior.</summary>
    private void OnTemplateChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || ShotExample is null) return;
        var shot = ShotTemplate.Text;
        var video = VideoTemplate.Text;
        _app.Settings.Update(s =>
        {
            if (NameTemplate.Validate(shot) is null) s.ScreenshotNameTemplate = shot;
            if (NameTemplate.Validate(video) is null) s.RecordingNameTemplate = video;
        });
        UpdateTemplateExamples();
    }

    private void OnResetTemplates(object sender, RoutedEventArgs e)
    {
        ShotTemplate.Text = NameTemplate.DefaultScreenshot;
        VideoTemplate.Text = NameTemplate.DefaultRecording;
    }

    private void UpdateTemplateExamples()
    {
        ShowExample(ShotTemplate.Text, ShotExample, recording: false);
        ShowExample(VideoTemplate.Text, VideoExample, recording: true);
    }

    private void ShowExample(string template, TextBlock target, bool recording)
    {
        var problem = NameTemplate.Validate(template);
        if (problem is not null)
        {
            target.Text = problem;
            target.SetResourceReference(TextBlock.ForegroundProperty, "M3.Error");
            return;
        }
        var folder = recording ? _app.Recording.RecordingsFolder : _app.Capture.ScreenshotsFolder;
        target.Text = "Ejemplo: " + FileNaming.Preview(S, template, folder, recording, new ForegroundInfo.Info("Google Chrome", "YouTube"));
        target.SetResourceReference(TextBlock.ForegroundProperty, "M3.OnSurfaceVariant");
    }

    // ---------- Atajos ----------

    private readonly List<(HotkeyBinding Binding, HotkeyBox Box)> _boxes = [];

    private void BuildHotkeys()
    {
        HotkeyRows.Children.Clear();
        _boxes.Clear();
        var actions = new[]
        {
            (HotkeyAction.FullScreen, "Capturar pantalla completa", Icons.FullScreen),
            (HotkeyAction.Region, "Capturar una región", Icons.Region),
            (HotkeyAction.Record, "Iniciar o detener grabación", Icons.Record),
        };
        for (var a = 0; a < actions.Length; a++)
        {
            var (action, title, icon) = actions[a];
            if (a > 0) HotkeyRows.Children.Add(new Border { Style = (Style)FindResource("M3.Divider"), Margin = new Thickness(0, 10, 0, 10) });
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var ic = new TextBlock { Style = (Style)FindResource("M3.Icon"), Text = icon, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 0, 0) };
            ic.SetResourceReference(TextBlock.ForegroundProperty, "M3.OnSurfaceVariant");
            grid.Children.Add(ic);
            var label = new TextBlock { Style = (Style)FindResource("M3.BodyLarge"), Text = title, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4, 9, 12, 0) };
            Grid.SetColumn(label, 1);
            grid.Children.Add(label);

            var list = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(list, 2);
            var bindings = S.Hotkeys.Where(h => h.Action == action).ToList();
            foreach (var b in bindings)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6), HorizontalAlignment = HorizontalAlignment.Right };
                var box = new HotkeyBox { Gesture = b.Gesture };
                System.Windows.Automation.AutomationProperties.SetName(box, $"Atajo: {title}");
                var binding = b;
                box.CaptureStarted += (_, _) => _app.Hotkeys.Suspend();
                box.CaptureEnded += (_, _) => _app.Hotkeys.Resume();
                box.GestureChanged += (_, g) => ReplaceGesture(binding, g);
                row.Children.Add(box);
                if (bindings.Count > 1)
                {
                    var remove = new Button { Style = (Style)FindResource("M3.IconButton"), ToolTip = "Quitar esta combinación", Margin = new Thickness(4, 0, 0, 0) };
                    M3.SetIcon(remove, Icons.Delete);
                    remove.Click += (_, _) => RemoveBinding(binding);
                    row.Children.Add(remove);
                }
                list.Children.Add(row);
                _boxes.Add((b, box));
            }
            var add = new Button { Style = (Style)FindResource("M3.TextButton"), Content = "Agregar alternativa", HorizontalAlignment = HorizontalAlignment.Right };
            M3.SetIcon(add, Icons.Add);
            add.Click += (_, _) => AddBinding(action);
            list.Children.Add(add);
            grid.Children.Add(list);
            HotkeyRows.Children.Add(grid);
        }
        RefreshHotkeyStatus();
    }

    private void RefreshHotkeyStatus()
    {
        foreach (var (b, box) in _boxes)
        {
            // La lista del servicio puede tener otra instancia con los mismos valores: se compara por valor.
            var status = _app.Hotkeys.GetStatus(b);
            _app.Hotkeys.FailureReasons.TryGetValue(b, out var reason);
            box.SetStatus(status, reason);
        }
        var conflicts = HotkeyConflicts.Find(S.Hotkeys);
        ConflictBanner.Visibility = conflicts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ConflictText.Text = string.Join("\n", conflicts.Select(c =>
            $"{c[0].Gesture} está asignado a más de una acción ({string.Join(" y ", c.Select(x => HomePage.ActionName(x.Action)).Distinct())}). Solo funciona la primera."));
    }

    private void ReplaceGesture(HotkeyBinding old, HotkeyGesture g)
    {
        _app.Settings.Update(s =>
        {
            var i = s.Hotkeys.IndexOf(old);
            if (i >= 0) s.Hotkeys[i] = old with { Gesture = g };
        });
        _app.ApplyHotkeys();
        BuildHotkeys();
        UpdateSnipping();
    }

    private void AddBinding(HotkeyAction action)
    {
        _app.Settings.Update(s => s.Hotkeys.Add(new HotkeyBinding(action, HotkeyGesture.None)));
        BuildHotkeys();
    }

    private void RemoveBinding(HotkeyBinding b)
    {
        _app.Settings.Update(s => s.Hotkeys.Remove(b));
        _app.ApplyHotkeys();
        BuildHotkeys();
    }

    private void OnResetHotkeys(object sender, RoutedEventArgs e)
    {
        _app.Settings.Update(s => s.Hotkeys = [.. HotkeyDefaults.Create()]);
        _app.ApplyHotkeys();
        BuildHotkeys();
        UpdateSnipping();
    }

    private void OnPausePrintScreen(object sender, RoutedEventArgs e)
    {
        var paused = PausePrintScreenSwitch.IsChecked == true;
        _app.SetPrintScreenPaused(paused);
        BuildHotkeys();
        UpdateSnipping();
        if (paused) ToastWindow.ShowInfo("Impr Pant en pausa", "Windows la usa como siempre hasta que la reactives.");
        else ToastWindow.ShowInfo("Impr Pant reactivada", "Pantallazo vuelve a usar sus atajos con Impr Pant.");
    }

    private void UpdateSnipping()
    {
        // En pausa no se ofrece liberar ni devolver: la tecla queda como la tenía Windows.
        SnippingButton.Visibility = S.PrintScreenPaused ? Visibility.Collapsed : Visibility.Visible;
        if (S.PrintScreenPaused)
        {
            SnippingText.Text = "Impr Pant está en pausa: la usa Windows, como si Pantallazo no estuviera.";
            return;
        }
        var used = PrintScreenKey.Windows.IsUsedBySnippingTool();
        SnippingText.Text = used
            ? "Impr Pant está asignada a la Herramienta Recortes de Windows; mientras tanto puede no llegar a Pantallazo."
            : "Impr Pant está liberada para Pantallazo (la Herramienta Recortes no la usa).";
        SnippingButton.Content = used ? "Liberar Impr Pant" : "Devolver Impr Pant a Recortes";
    }

    private void OnToggleSnipping(object sender, RoutedEventArgs e)
    {
        var used = PrintScreenKey.Windows.IsUsedBySnippingTool();
        if (PrintScreenKey.Windows.SetUsedBySnippingTool(!used))
        {
            _app.ApplyHotkeys();
            ToastWindow.ShowInfo(used ? "Impr Pant liberada" : "Impr Pant devuelta a Recortes",
                "Si Windows no toma el cambio enseguida, cerrá sesión y volvé a entrar.");
        }
        UpdateSnipping();
    }

    // ---------- Carpetas ----------

    private string? PickFolder(string title, string current)
    {
        var dlg = new OpenFolderDialog { Title = title, InitialDirectory = Directory.Exists(current) ? current : null };
        return dlg.ShowDialog(Window.GetWindow(this)) == true ? dlg.FolderName : null;
    }

    private void OnChangeShotsFolder(object sender, RoutedEventArgs e)
    {
        if (PickFolder("Carpeta para las capturas", _app.Capture.ScreenshotsFolder) is not { } f) return;
        _app.Settings.Update(s => s.ScreenshotsFolder = f);
        ShotsFolder.Text = _app.Capture.ScreenshotsFolder;
    }

    private void OnChangeVideosFolder(object sender, RoutedEventArgs e)
    {
        if (PickFolder("Carpeta para las grabaciones", _app.Recording.RecordingsFolder) is not { } f) return;
        _app.Settings.Update(s => s.RecordingsFolder = f);
        VideosFolder.Text = _app.Recording.RecordingsFolder;
    }

    private void OnResetShotsFolder(object sender, RoutedEventArgs e)
    {
        _app.Settings.Update(s => s.ScreenshotsFolder = null);
        ShotsFolder.Text = _app.Capture.ScreenshotsFolder;
    }

    private void OnResetVideosFolder(object sender, RoutedEventArgs e)
    {
        _app.Settings.Update(s => s.RecordingsFolder = null);
        VideosFolder.Text = _app.Recording.RecordingsFolder;
    }

    private void OnOpenShotsFolder(object sender, RoutedEventArgs e) => Shell.OpenFolder(_app.Capture.ScreenshotsFolder);
    private void OnOpenVideosFolder(object sender, RoutedEventArgs e) => Shell.OpenFolder(_app.Recording.RecordingsFolder);

    // ---------- Apariencia ----------

    private void OnTheme(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var t = ThemeLight.IsChecked == true ? ThemePreference.Light : ThemeDark.IsChecked == true ? ThemePreference.Dark : ThemePreference.System;
        _app.Settings.Update(s => s.Theme = t);
        _app.Theme.Configure(t, S.SeedColor);
    }

    private void BuildSwatches()
    {
        Swatches.Children.Clear();
        foreach (var (hex, name) in SeedColors)
        {
            var color = hex is not null && ColorMath.TryParseHex(hex, out var rgb) ? rgb : ThemeManager.WindowsAccent() ?? new Rgb(0x4F, 0x6C, 0xB8);
            var selected = string.Equals(S.SeedColor, hex, StringComparison.OrdinalIgnoreCase);
            var swatch = new Grid { Width = 48, Height = 48 };
            var ring = new Border { CornerRadius = new CornerRadius(24), BorderThickness = new Thickness(selected ? 3 : 0) };
            ring.SetResourceReference(Border.BorderBrushProperty, "M3.OnSurface");
            var dot = new Border
            {
                CornerRadius = new CornerRadius(18),
                Width = 36,
                Height = 36,
                Background = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B)),
            };
            swatch.Children.Add(ring);
            swatch.Children.Add(dot);
            if (hex is null)
            {
                var w = new TextBlock { Style = (Style)FindResource("M3.Icon"), Text = Icons.Palette, Foreground = Brushes.White, FontSize = 14 };
                swatch.Children.Add(w);
            }
            if (selected)
            {
                var check = new TextBlock { Style = (Style)FindResource("M3.Icon"), Text = Icons.Check, Foreground = Brushes.White, FontSize = 14 };
                if (hex is not null) swatch.Children.Add(check);
            }
            var btn = new Button { Content = swatch, ToolTip = name, Margin = new Thickness(0, 0, 8, 8), Cursor = System.Windows.Input.Cursors.Hand };
            btn.Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
            var seed = hex;
            btn.Click += (_, _) =>
            {
                _app.Settings.Update(s => s.SeedColor = seed);
                _app.Theme.Configure(S.Theme, seed);
                BuildSwatches();
            };
            Swatches.Children.Add(btn);
        }
    }

    // ---------- Opciones con switch ----------

    private void BuildCaptureOptions()
    {
        CaptureOptions.Children.Clear();
        AddSwitch(CaptureOptions, "Incluir el puntero del mouse", "En la captura de pantalla completa y en la de región (donde estaba al apretar el atajo).", S.CaptureCursor, v => S.CaptureCursor = v);
        AddSwitch(CaptureOptions, "Copiar al portapapeles", "Además de guardar el PNG.", S.CopyToClipboard, v => S.CopyToClipboard = v);
        AddSwitch(CaptureOptions, "Sonido al capturar", null, S.PlaySound, v => S.PlaySound = v);
        AddSwitch(CaptureOptions, "Mostrar aviso al guardar", "Una vista previa chica abajo a la derecha.", S.ShowNotification, v => S.ShowNotification = v);
        AddSwitch(CaptureOptions, "Lupa en la selección de región", "La rueda del mouse cambia el aumento.", S.RegionShowMagnifier, v => S.RegionShowMagnifier = v);
        AddSwitch(CaptureOptions, "Detectar ventanas y controles", "Al pasar el mouse, un clic captura esa ventana.", S.RegionDetectWindows, v => S.RegionDetectWindows = v);
        AddSwitch(CaptureOptions, "Cruz a lo ancho de la pantalla", "Líneas guía que siguen al puntero.", S.RegionShowCrosshair, v => S.RegionShowCrosshair = v, last: true);
    }

    private void BuildBehaviorOptions()
    {
        BehaviorOptions.Children.Clear();
        AddSwitch(BehaviorOptions, "Iniciar con Windows", "Pantallazo arranca en segundo plano al iniciar sesión.", StartupManager.IsEnabled(), v =>
        {
            if (StartupManager.Set(v)) S.StartWithWindows = v;
        });
        AddSwitch(BehaviorOptions, "Al cerrar la ventana, seguir en la bandeja", "Los atajos siguen funcionando. Para salir, usá el menú de la bandeja.", S.CloseToTray, v => S.CloseToTray = v);
        AddSwitch(BehaviorOptions, "Abrir minimizado al iniciar con Windows", null, S.StartMinimized, v => S.StartMinimized = v, last: true);
    }

    private void AddSwitch(Panel host, string title, string? detail, bool value, Action<bool> apply, bool last = false)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 12, 0, 12) };
        var sw = new ToggleButton { Style = (Style)FindResource("M3.Switch"), IsChecked = value, VerticalAlignment = VerticalAlignment.Center };
        sw.Click += (_, _) => _app.Settings.Update(_ => apply(sw.IsChecked == true));
        DockPanel.SetDock(sw, Dock.Right);
        dock.Children.Add(sw);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        texts.Children.Add(new TextBlock { Style = (Style)FindResource("M3.BodyLarge"), Text = title });
        if (detail is not null) texts.Children.Add(new TextBlock { Style = (Style)FindResource("M3.BodySmall"), Text = detail, Margin = new Thickness(0, 2, 0, 0) });
        dock.Children.Add(texts);
        host.Children.Add(dock);
        if (!last) host.Children.Add(new Border { Style = (Style)FindResource("M3.Divider") });
    }

    private void UpdateQualitySummary()
    {
        if (QualitySummary is null) return;
        var q = S.Quality switch { RecordQuality.Low => "liviana", RecordQuality.High => "alta", _ => "equilibrada" };
        var res = S.Resolution switch { RecordResolution.P1080 => "1080p", RecordResolution.P720 => "720p", _ => "resolución original" };
        QualitySummary.Text = $"{res} · {S.Fps} fps · calidad {q} · H.264 en MP4";
    }

    private void OnOpenRecordPage(object sender, RoutedEventArgs e) => _app.ShowMainWindow(MainPage.Record);

    private void OnOpenLogs(object sender, RoutedEventArgs e) => Shell.OpenFolder(AppPaths.LogsFolder);
}
