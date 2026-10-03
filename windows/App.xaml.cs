using System.Windows;

namespace S8Cam;

public partial class App : Application {
    public static RollingLogger Logger { get; } = new();

    protected override void OnStartup(StartupEventArgs e) {
        base.OnStartup(e);
        using (var process = System.Diagnostics.Process.GetCurrentProcess()) {
            Processes.PrioritizeVideo(process, message => Logger.Log(message));
        }
        DispatcherUnhandledException += (s, args) => {
            Logger.Log($"UI Unhandled Exception: {args.Exception}", LogLevel.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (s, args) => {
            Logger.Log($"Domain Unhandled Exception: {args.ExceptionObject}", LogLevel.Error);
        };
    }

    protected override void OnExit(ExitEventArgs e) {
        Logger.Dispose();
        base.OnExit(e);
    }
}

