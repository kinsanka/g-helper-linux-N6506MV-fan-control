namespace GHelper.Linux.Fan;

/// <summary>Pure curve/control policy; no hardware or configuration access.</summary>
public static class N6506FanPolicy
{
    public static byte[] DefaultCurve(int mode, int fan)
    {
        byte[] speeds = (mode, fan) switch
        {
            (1, 0) => [31, 37, 46, 54, 70, 100, 100, 100],
            (1, _) => [35, 42, 60, 90, 90, 90, 90, 90],
            (2, 0) => [25, 30, 35, 45, 60, 80, 100, 100],
            (2, _) => [25, 30, 40, 65, 85, 100, 100, 100],
            (_, 0) => [30, 35, 42, 50, 65, 85, 100, 100],
            _ => [30, 35, 50, 75, 90, 100, 100, 100]
        };
        return [30, 40, 50, 60, 70, 80, 90, 100, ..speeds];
    }

    public static bool ValidCurve(byte[] curve)
    {
        if (curve.Length != 16) return false;
        for (int i = 0; i < 8; i++)
            if (curve[i] < 20 || curve[i] > 100 || curve[i + 8] < 20 || curve[i + 8] > 100 ||
                (i > 0 && (curve[i] <= curve[i - 1] || curve[i + 8] < curve[i + 7]))) return false;
        return true;
    }

    public static int TargetRpm(byte[] curve, double temperature, int maximum)
    {
        if (!ValidCurve(curve) || maximum < 1000 || maximum > 20000 || !double.IsFinite(temperature))
            throw new ArgumentException("Invalid curve, temperature or calibration");
        return (int)Math.Round(ManualFanMath.Interpolate(curve, temperature) * maximum / 100.0);
    }

    public static int NextDuty(int current, int measured, int target)
    {
        int error = target - measured;
        if (Math.Abs(error) < 150) return Math.Clamp(current, 64, 255);
        int step = Math.Clamp(error / 35, -2, 12);
        if (step == 0) step = Math.Sign(error);
        return Math.Clamp(current + step, 64, 255);
    }

    public static int NextCalibratedDuty(int current, int measured, int target, int? calibratedDuty)
    {
        int feedback = NextDuty(current, measured, target);
        if (!calibratedDuty.HasValue || Math.Abs(target - measured) < 150) return feedback;
        int nominal = Math.Clamp(calibratedDuty.Value, 64, 255);
        // Feed forward only when it agrees with the measured error; feedback corrects drift.
        if (Math.Sign(nominal - current) != Math.Sign(target - measured)) return feedback;
        return Math.Clamp(nominal, Math.Max(64, current - 2), Math.Min(255, current + 12));
    }

    public static bool Protection(bool previous, int cpu, int gpu)
    {
        if (cpu <= 0 || cpu >= 85 || gpu >= 85) return true;
        return previous && (cpu >= 80 || gpu >= 78);
    }

    public static int MeasuredMaximum(IReadOnlyList<int> samples)
    {
        if (samples.Count < 3 || samples.Any(x => x < 1000 || x > 20000))
            throw new ArgumentException("Not enough valid samples");
        var sorted = samples.Order().ToArray();
        int median = sorted[sorted.Length / 2];
        if (sorted[^1] - sorted[0] > median * 0.06)
            throw new ArgumentException("Fan speed has not settled; test again");
        return median;
    }
}
