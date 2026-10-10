namespace S8Cam;

public sealed class ThermalGuard {
    private Settings settings;
    private readonly Action<int, double, string> onThrottle;
    private readonly Action<int, double> onRestore;

    public bool IsThrottled { get; private set; }
    public int? OriginalBitrate { get; private set; }
    public double LastCpuTemperature { get; private set; }

    public ThermalGuard(
        Settings settings,
        Action<int, double, string> onThrottle,
        Action<int, double> onRestore) {
        this.settings = settings;
        this.onThrottle = onThrottle;
        this.onRestore = onRestore;
    }

    public void UpdateSettings(Settings newSettings) {
        settings = newSettings;
        if (!settings.ThermalGuard && IsThrottled && OriginalBitrate.HasValue) {
            var orig = OriginalBitrate.Value;
            IsThrottled = false;
            OriginalBitrate = null;
            onRestore(orig, LastCpuTemperature);
        }
    }

    public void UpdateTelemetry(double? cpuTemp, double? batteryTemp) {
        if (!settings.ThermalGuard) {
            if (IsThrottled && OriginalBitrate.HasValue) {
                var orig = OriginalBitrate.Value;
                IsThrottled = false;
                OriginalBitrate = null;
                onRestore(orig, cpuTemp ?? 0);
            }
            return;
        }

        // Only throttle if genuine CPU temperature is available (or critical battery temp >= 45°C)
        double temp;
        if (cpuTemp.HasValue && cpuTemp.Value > 10.0) {
            temp = cpuTemp.Value;
        } else if (batteryTemp.HasValue && batteryTemp.Value >= 45.0) {
            temp = batteryTemp.Value;
        } else {
            return;
        }
        LastCpuTemperature = temp;

        var threshold = settings.ThermalThresholdC;
        if (!IsThrottled && temp >= threshold) {
            IsThrottled = true;
            OriginalBitrate = settings.BitrateMbps;
            var reduced = Math.Max(6, (int)(settings.BitrateMbps * 0.6));
            onThrottle(reduced, temp, L.Format("s_21e8bfa5ba27", temp, threshold));
        } else if (IsThrottled && temp <= threshold - 3) {
            IsThrottled = false;
            var orig = OriginalBitrate ?? settings.BitrateMbps;
            OriginalBitrate = null;
            onRestore(orig, temp);
        }
    }

    public void Reset() {
        IsThrottled = false;
        OriginalBitrate = null;
    }
}
