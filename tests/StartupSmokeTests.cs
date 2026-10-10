using System.Diagnostics;
using System.IO;
using System.Text.Json;
using S8Cam;

internal static class StartupSmokeTests {
    public static async Task Run(string executable,string root) {
        executable=Path.GetFullPath(executable);root=Path.GetFullPath(root);
        var version=FileVersionInfo.GetVersionInfo(executable).FileVersion!;
        Directory.CreateDirectory(root);
        var results=new List<object>();
        foreach(var scenario in new[]{"fresh","missing-libusb","missing-adb","safe-mode"}) {
            var folder=Path.Combine(root,scenario+"-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var exe=Path.Combine(folder,"H3HCam Receiver.exe");
            File.Copy(executable,exe);
            if(scenario!="missing-libusb")File.Copy(Path.Combine(Path.GetDirectoryName(executable)!,"libusb-1.0.dll"),Path.Combine(folder,"libusb-1.0.dll"));
            var profile=Path.Combine(folder,"profile");Directory.CreateDirectory(profile);
            if(scenario is "missing-adb" or "safe-mode") {
                var settings=new Settings {Transport="usb",AutoStart=true,AdbPath=Path.Combine(folder,"not-installed","adb.exe")};
                File.WriteAllText(Path.Combine(profile,"settings.json"),JsonSerializer.Serialize(settings));
            }
            var info=new ProcessStartInfo(exe) {UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=folder};
            info.Environment["H3HCAM_DATA_DIR"]=profile;
            info.ArgumentList.Add("--tray");
            if(scenario=="safe-mode")info.ArgumentList.Add("--safe-mode");
            using var process=Process.Start(info)!;
            var timer=Stopwatch.StartNew();
            try {
                while(timer.Elapsed.TotalSeconds<12) {
                    await Task.Delay(250);
                    if(process.HasExited)throw new Exception($"Startup {scenario}: exited after {timer.Elapsed.TotalSeconds:F2}s, code={process.ExitCode}");
                }
                var logFile=Directory.GetFiles(Path.Combine(profile,"logs"),"h3hcam-*.log").Single();
                using var logStream=new FileStream(logFile,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
                using var logReader=new StreamReader(logStream);
                var log=logReader.ReadToEnd();
                if(!log.Contains($"Startup {version};")||log.Contains("Unhandled Exception"))throw new Exception("Startup did not complete cleanly: "+scenario);
                if(scenario=="safe-mode"&&!log.Contains("safeMode=True"))throw new Exception("Safe mode was not applied");
                if(scenario=="missing-libusb"&&!log.Contains("libusb-1.0.dll"))throw new Exception("Missing libusb was not reported");
                results.Add(new {Scenario=scenario,AliveSeconds=timer.Elapsed.TotalSeconds});
                Console.WriteLine($"PASS startup {scenario}: alive >12 seconds, isolated settings/logs");
            } finally {Processes.Kill(process);}
        }
        File.WriteAllText(Path.Combine(root,"startup-results.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions {WriteIndented=true}));
    }
}
