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
        TaskScheduler.UnobservedTaskException += (_,args) => {
            Logger.Log($"Background task exception: {args.Exception}",LogLevel.Error);
            args.SetObserved();
        };
        Logger.Log($"Startup {typeof(App).Assembly.GetName().Version}; " +
            $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription}; " +
            $"{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}; " +
            $"{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}; " +
            $"safeMode={e.Args.Contains("--safe-mode")}");
    }

    protected override void OnExit(ExitEventArgs e) {
        Logger.Dispose();
        base.OnExit(e);
    }
}

