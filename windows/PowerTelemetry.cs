using System.Globalization;
using System.Text.Json;

namespace S8Cam;

public sealed record PowerTelemetry(
    int? Percent = null, string Status = "N/A", string Source = "N/A", double? VoltageV = null,
    double? CurrentMa = null, double? AverageCurrentMa = null, double? PowerW = null,
    double? TemperatureC = null, double? ChargeMah = null, double? EnergyWh = null,
    string Health = "N/A", string Technology = "N/A", TimeSpan? ChargeTimeRemaining = null,
    bool CurrentSignNormalized = false,
    double? CpuPercent = null, double? CpuTemperatureC = null) {
    public static PowerTelemetry From(JsonElement j) => new(
        Int(j,"batteryPercent"), Text(j,"batteryStatus"), Text(j,"batterySource"),
        Scale(Double(j,"batteryVoltageMv"),1000), Scale(Double(j,"batteryCurrentUa"),1000),
        Scale(Double(j,"batteryCurrentAverageUa"),1000), Scale(Double(j,"batteryPowerMw"),1000),
        Double(j,"batteryTemperatureC"), Scale(Double(j,"batteryChargeCounterUah"),1000),
        Scale(Double(j,"batteryEnergyCounterNwh"),1_000_000_000), Text(j,"batteryHealth"),
        Text(j,"batteryTechnology"), Long(j,"batteryChargeTimeRemainingMs") is { } ms ? TimeSpan.FromMilliseconds(ms) : null,
        Bool(j,"batteryCurrentSignNormalized"),
        Double(j, "cpuUsagePercent"), Double(j, "cpuTemperatureC"));
    public bool IsNetDischarging => Source != "Battery" && ((CurrentMa != null && CurrentMa < -80) || (PowerW != null && PowerW < -0.3));
    public string Headline => $"{(Percent is { } p ? p + "%" : "N/A")}  •  {(IsNetDischarging ? $"Разряд {CurrentMa:F0} mA" : Status)}";
    public string Compact => $"Батарея {(Percent is { } p ? p + "%" : "N/A")} · {Status} · {Source} · {Format(TemperatureC,"0.0","°C")}" +
        (CpuTemperatureC != null || CpuPercent != null ? $" · CPU {Format(CpuTemperatureC, "0.0", "°C")} ({Format(CpuPercent, "0.0", "%")})" : "");
    public string Details => string.Join("\n", new[] {
        $"CPU load            {Format(CpuPercent, "0.0", "%")}",
        $"CPU temperature     {Format(CpuTemperatureC, "0.0", "°C")}",
        $"Battery voltage     {Format(VoltageV,"0.000","V")}", $"Current             {Signed(CurrentMa,"mA")}",
        $"Average current     {Signed(AverageCurrentMa,"mA")}", $"Estimated power     {Signed(PowerW,"W")}",
        $"Charge counter      {Format(ChargeMah,"0","mAh")}", $"Energy counter      {Format(EnergyWh,"0.00","Wh")}",
        $"Battery temperature {Format(TemperatureC,"0.0","°C")}", $"Health / chemistry  {Health} / {Technology}",
        $"Charge time left    {(ChargeTimeRemaining is { } t ? t.ToString(@"h\:mm") : "N/A")}" }) +
        (CurrentSignNormalized ? "\nCurrent sign normalized using battery status" : "") +
        (CurrentMa is > -10 and < 10 and not 0 ? "\nOEM current value is unusually small; displayed using the Android µA contract" : "");
    private static double? Scale(double? v,double d)=>v/d;
    private static string Format(double? v,string f,string u)=>v is{}n?n.ToString(f,CultureInfo.InvariantCulture)+" "+u:"N/A";
    private static string Signed(double? v,string u)=>v is{}n?n.ToString("+0.00;-0.00;0.00",CultureInfo.InvariantCulture)+" "+u:"N/A";
    private static int? Int(JsonElement j,string n)=>j.TryGetProperty(n,out var x)&&x.ValueKind==JsonValueKind.Number&&x.TryGetInt32(out var v)?v:null;
    private static long? Long(JsonElement j,string n)=>j.TryGetProperty(n,out var x)&&x.ValueKind==JsonValueKind.Number&&x.TryGetInt64(out var v)?v:null;
    private static double? Double(JsonElement j,string n)=>j.TryGetProperty(n,out var x)&&x.ValueKind==JsonValueKind.Number&&x.TryGetDouble(out var v)?v:null;
    private static string Text(JsonElement j,string n)=>j.TryGetProperty(n,out var x)&&x.ValueKind==JsonValueKind.String?x.GetString()??"N/A":"N/A";
    private static bool Bool(JsonElement j,string n)=>j.TryGetProperty(n,out var x)&&x.ValueKind==JsonValueKind.True;
}

public sealed class PowerHistory {
    public sealed record Sample(DateTime Time, PowerTelemetry Value);
    private readonly List<Sample> samples=[];
    public IReadOnlyList<Sample> Samples=>samples;
    public void Add(PowerTelemetry value){var now=DateTime.Now;if(samples.Count>0&&now-samples[^1].Time<TimeSpan.FromMilliseconds(800))return;samples.Add(new(now,value));samples.RemoveAll(x=>now-x.Time>TimeSpan.FromMinutes(60));}
    public string Assessment(PowerTelemetry current){
        if(current.Percent is <=15)return "⚠ Низкий заряд батареи";
        if(current.TemperatureC is >=42)return "⚠ Высокая температура по порогу H3H Cam; это не предел производителя";
        if(current.IsNetDischarging)return $"⚠ USB-питание не покрывает расход (ток {current.CurrentMa:F0} mA). Батарея разряжается! Подключите к USB 3.0 или зарядному хабу.";
        var old=samples.FirstOrDefault(x=>DateTime.Now-x.Time>=TimeSpan.FromMinutes(2));
        if(old==null||old.Value.Percent==null||current.Percent==null)return "Оценка баланса питания появится через 2 минуты";
        var delta=current.Percent.Value-old.Value.Percent.Value;
        if(current.Source!="Battery"&&delta<=-1)return "⚠ USB-питание не покрывает расход (заряд падает). Погасите экран, снизьте FPS/битрейт или используйте powered USB hub.";
        return delta>=1?"Заряд увеличивается":"Power balanced · заряд примерно стабилен";
    }
}
