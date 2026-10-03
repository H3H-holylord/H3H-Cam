using System.IO;
namespace S8Cam;
public static class ToolPaths {
    public static string Find(string name, string configured = "") {
        if (!string.IsNullOrWhiteSpace(configured)) {
            if (File.Exists(configured)) return Path.GetFullPath(configured);
            throw new FileNotFoundException($"Файл не найден: {configured}");
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = new List<string> { AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "tools"),
            Path.Combine(local, "Android", "Sdk", "platform-tools"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "obs-studio", "bin", "64bit") };
        roots.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        foreach (var dir in roots) {
            var path = Path.Combine(dir.Trim('"'), name);
            if (File.Exists(path)) return path;
        }
        var winget = Path.Combine(local, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(winget)) {
            foreach (var package in Directory.EnumerateDirectories(winget, name == "adb.exe" ? "*PlatformTools*" : "*FFmpeg*")) {
                var match = Directory.EnumerateFiles(package, name, SearchOption.AllDirectories).FirstOrDefault();
                if (match != null) return match;
            }
        }
        throw new FileNotFoundException($"Не найден {name}. Укажите путь в разделе «Инструменты».");
    }
}

