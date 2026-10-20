using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Pantallazo.Core;
using Pantallazo.Theming;

namespace Pantallazo.Views;

/// <summary>
/// Diálogo M3 para renombrar una captura o grabación. Se edita el nombre sin la extensión (la extensión queda fija
/// al lado); Enter acepta y Esc cancela. Devuelve la ruta nueva o null si se canceló.
/// </summary>
public sealed class RenameDialog : Window
{
    private readonly string _path;
    private readonly string _extension;
    private readonly TextBox _box;
    private readonly TextBlock _error;
    private Action<string>? _renamed;

    public string? NewPath { get; private set; }

    private RenameDialog(string path)
    {
        _path = path;
        _extension = System.IO.Path.GetExtension(path);

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Title = "Renombrar";
        FontFamily = (FontFamily)Application.Current.Resources["M3.Font"];

        var title = new TextBlock { Text = "Renombrar", Style = Res<Style>("M3.HeadlineSmall"), Margin = new Thickness(0, 0, 0, 16) };

        _box = new TextBox
        {
            Style = Res<Style>("M3.TextBox"),
            Text = System.IO.Path.GetFileNameWithoutExtension(path),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        System.Windows.Automation.AutomationProperties.SetName(_box, "Nombre nuevo");
        _box.TextChanged += (_, _) => _error!.Visibility = Visibility.Collapsed;
        var ext = new TextBlock { Text = _extension, Style = Res<Style>("M3.BodyLarge"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        ext.SetResourceReference(TextBlock.ForegroundProperty, "M3.OnSurfaceVariant");
        var field = new DockPanel();
        DockPanel.SetDock(ext, Dock.Right);
        field.Children.Add(ext);
        field.Children.Add(_box);

        _error = new TextBlock { Style = Res<Style>("M3.BodySmall"), Margin = new Thickness(4, 6, 0, 0), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        _error.SetResourceReference(TextBlock.ForegroundProperty, "M3.Error");

        var cancel = new Button { Content = "Cancelar", Style = Res<Style>("M3.TextButton"), IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => Close();
        var ok = new Button { Content = "Renombrar", Style = Res<Style>("M3.TextButton"), IsDefault = true };
        ok.Click += (_, _) => Accept();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var stack = new StackPanel();
        stack.Children.Add(title);
        stack.Children.Add(field);
        stack.Children.Add(_error);
        stack.Children.Add(buttons);

        var card = new Border
        {
            CornerRadius = new CornerRadius(28),
            Padding = new Thickness(24),
            Margin = new Thickness(16),
            Width = 460,
            Child = stack,
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.3, Direction = 270 },
        };
        card.SetResourceReference(Border.BackgroundProperty, "M3.SurfaceContainerHigh");
        card.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource == card) DragMove(); };
        Content = card;

        Loaded += (_, _) =>
        {
            _box.Focus();
            _box.SelectAll();
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private static T Res<T>(string key) => (T)Application.Current.Resources[key];

    private void Accept()
    {
        var name = _box.Text.Trim();
        var problem = FileNames.ValidateNewName(name);
        var target = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_path)!, name + _extension);
        if (problem is null && string.Equals(target, _path, StringComparison.Ordinal))
        {
            Close(); // mismo nombre: nada que hacer
            return;
        }
        if (problem is null && !string.Equals(target, _path, StringComparison.OrdinalIgnoreCase) && File.Exists(target))
            problem = "Ya hay un archivo con ese nombre en la carpeta.";
        if (problem is null)
        {
            try
            {
                File.Move(_path, target);
                NewPath = target;
                // Antes de cerrar: al cerrarse, la ventana principal recupera el foco y revisa la lista; si todavía
                // tuviera el nombre viejo (que ya no existe) lo sacaría.
                _renamed?.Invoke(target);
                Close();
                return;
            }
            catch (FileNotFoundException) { problem = "El archivo ya no existe."; }
            catch (IOException) { problem = "No se pudo renombrar: puede que esté abierto en otra aplicación."; }
            catch (UnauthorizedAccessException) { problem = "No hay permiso para renombrar el archivo."; }
        }
        _error.Text = problem;
        _error.Visibility = Visibility.Visible;
        _box.Focus();
    }

    /// <summary>
    /// Muestra el diálogo sobre <paramref name="owner"/>. <paramref name="renamed"/> recibe la ruta nueva apenas se
    /// renombra. Devuelve la ruta nueva, o null si no se renombró.
    /// </summary>
    public static string? Show(Window? owner, string path, Action<string>? renamed = null)
    {
        var dialog = new RenameDialog(path) { Owner = owner, _renamed = renamed };
        if (owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
        return dialog.NewPath;
    }
}
