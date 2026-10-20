using System.Windows;
using Pantallazo.Logging;
using Pantallazo.Services;

namespace Pantallazo;

public static class Program
{
    private const string MutexName = @"Local\Pantallazo-instancia-unica";
    private const string ShowEventName = @"Local\Pantallazo-mostrar";

    [STAThread]
    public static int Main(string[] args)
    {
        // Proceso hijo para averiguar qué encoder de video elige Windows (aislado por si el driver se cae).
        if (args.Contains(Recording.EncoderProbe.Argument))
            return Recording.EncoderProbe.RunChild(args);

        Log.Init();
        Recording.EncoderProbe.ProbeExecutablePath = Environment.ProcessPath;
        Recording.RecordingLog.Message += m => Log.Info($"[motor] {m}");

        if (args.Length > 1 && args[0] == AppIconArt.MakeIconArg)
        {
            AppIconArt.WriteIco(args[1]);
            return 0;
        }

        if (args.Length > 1 && args[0] == "--render-ui")
        {
            var r = new App();
            r.InitializeComponent();
            r.Startup += (_, _) => r.RenderUi(args[1], args.Length > 2 ? args[2] : null);
            return r.Run();
        }

        // El instalador y el desinstalador piden cerrar la instancia abierta (si está grabando, guarda primero).
        if (args.Contains(ExitArg)) return AskRunningInstanceToExit();

        using var mutex = new Mutex(true, MutexName, out var first);
        if (!first)
        {
            // Ya hay una instancia: se le pide que muestre su ventana y se sale.
            try
            {
                using var ev = EventWaitHandle.OpenExisting(ShowEventName);
                ev.Set();
            }
            catch { }
            return 0;
        }

        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        using var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        var app = new App();
        app.InitializeComponent();
        var silent = args.Contains(StartupManager.SilentArg);
        app.Startup += (_, _) =>
        {
            if (Environment.OSVersion.Version.Build < 17763)
            {
                MessageBox.Show("Pantallazo necesita Windows 10 versión 1809 o posterior.", "Pantallazo", MessageBoxButton.OK, MessageBoxImage.Error);
                app.Shutdown(1);
                return;
            }
            app.Start(silent);
            var waiter = new Thread(() =>
            {
                while (showEvent.WaitOne())
                    app.Dispatcher.BeginInvoke(() => app.ShowMainWindow());
            }) { IsBackground = true, Name = "Pantallazo - segunda instancia" };
            waiter.Start();
            var exitWaiter = new Thread(() =>
            {
                if (exitEvent.WaitOne())
                {
                    Log.Info("Se pidió cerrar Pantallazo desde afuera (instalador o desinstalador).");
                    app.Dispatcher.BeginInvoke(() => app.ExitApp());
                }
            }) { IsBackground = true, Name = "Pantallazo - pedido de salida" };
            exitWaiter.Start();
        };
        return app.Run();
    }

    public const string ExitArg = "--salir";
    private const string ExitEventName = @"Local\Pantallazo-salir";

    /// <summary>
    /// Avisa a la instancia abierta que salga y espera hasta 20 s a que termine.
    /// 0 = no había ninguna o ya cerró; 1 = sigue abierta.
    /// </summary>
    private static int AskRunningInstanceToExit()
    {
        if (!IsRunning()) return 0;
        try
        {
            using var ev = EventWaitHandle.OpenExisting(ExitEventName);
            ev.Set();
        }
        catch { return IsRunning() ? 1 : 0; }
        var deadline = Environment.TickCount64 + 20_000;
        while (Environment.TickCount64 < deadline)
        {
            // La app suelta el mutex un instante antes de que el proceso termine de cerrarse; mientras siga vivo el
            // .exe está bloqueado y el desinstalador no podría borrarlo. Se espera a las dos cosas.
            if (!IsRunning() && !OtherProcessAlive()) return 0;
            Thread.Sleep(200);
        }
        return 1;
    }

    private static bool OtherProcessAlive()
    {
        var self = Environment.ProcessId;
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("Pantallazo"))
        {
            using (p)
                if (p.Id != self && !p.HasExited) return true;
        }
        return false;
    }

    private static bool IsRunning()
    {
        if (!Mutex.TryOpenExisting(MutexName, out var m)) return false;
        m.Dispose();
        return true;
    }
}
