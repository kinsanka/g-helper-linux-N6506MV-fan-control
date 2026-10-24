using System.Text.Json;

namespace GHelper.Linux.Fan;

public sealed record N6506CalibrationPoint(int Percent, int Duty, int Rpm, int MinRpm, int MaxRpm, int Samples, int ReadbackDuty = -1, int ElapsedMilliseconds = 1000);

/// <summary>Raw measurements remain untouched; a separate isotonic fit handles measurement noise.</summary>
public sealed class N6506Calibration
{
    public DateTimeOffset CompletedAt { get; }
    public N6506CalibrationPoint[][] Fans { get; }
    public int[][] MaximumSamples { get; }
    public N6506Calibration(DateTimeOffset completedAt, N6506CalibrationPoint[][] fans, int[][] maximumSamples)
    {
        if (fans.Length != 2 || fans.Any(f => f.Length != 101)) throw new ArgumentException("校准需要每个风扇完整的101点");
        for (int f = 0; f < 2; f++)
        {
            for (int p = 0; p <= 100; p++)
            {
                var x = fans[f][p];
                if (x.Percent != p || x.Duty != DutyAt(p) || x.Samples < 1 ||
                    x.MinRpm < 0 || x.MaxRpm > 20000 || x.Rpm < x.MinRpm || x.Rpm > x.MaxRpm ||
                    x.MaxRpm - x.MinRpm > Math.Max(120, x.Rpm * .05))
                    throw new ArgumentException("校准点不完整或转速无效");
            }
        }
        if (maximumSamples.Length != 2) throw new ArgumentException("缺少两只风扇的最高转速实测");
        for (int fan = 0; fan < 2; fan++) _ = N6506FanPolicy.MeasuredMaximum(maximumSamples[fan]);
        MaximumSamples = maximumSamples.Select(x => x.ToArray()).ToArray();
        CompletedAt = completedAt;
        Fans = fans.Select(x => x.ToArray()).ToArray();
    }
    public static int DutyAt(int percent) => (int)Math.Round(percent * 255.0 / 100, MidpointRounding.AwayFromZero);
    public int Maximum(int fan) => N6506FanPolicy.MeasuredMaximum(MaximumSamples[fan]);

    public int DutyForRpm(int fan, int target)
    {
        // Pool adjacent violators: preserve raw samples, use a monotone response for inversion.
        var points = Fans[fan].Where(x => x.Duty >= 64).ToArray();
        var means = new List<double>(); var counts = new List<int>();
        foreach (var x in points)
        {
            means.Add(x.Rpm); counts.Add(1);
            while (means.Count > 1 && means[^2] > means[^1])
            {
                int n = counts[^2] + counts[^1];
                means[^2] = (means[^2] * counts[^2] + means[^1] * counts[^1]) / n;
                counts[^2] = n; means.RemoveAt(means.Count - 1); counts.RemoveAt(counts.Count - 1);
            }
        }
        var fit = means.SelectMany((m, i) => Enumerable.Repeat(m, counts[i])).ToArray();
        if (target <= fit[0]) return 64;
        for (int i = 1; i < fit.Length; i++)
            if (target <= fit[i])
            {
                double fraction = fit[i] > fit[i - 1] ? (target - fit[i - 1]) / (fit[i] - fit[i - 1]) : 0;
                return Math.Clamp((int)Math.Round(points[i - 1].Duty + fraction * (points[i].Duty - points[i - 1].Duty)), 64, 255);
            }
        return 255;
    }
    public string ToJson()
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject(); w.WriteNumber("schema", 1); w.WriteString("board", "N6506MV");
            w.WriteString("bios", "N6506MV.313"); w.WriteString("completed_at", CompletedAt);
            w.WriteString("direction", "ascending_0_to_100_cpu_then_gpu_other_fan_zero");
            w.WriteNumber("sample_delay_ms", 1000);
            w.WriteString("measurement", "single_response_sample_after_hold_plus_separate_stable_maximum");
            w.WriteStartArray("maximum_samples");
            foreach (var samples in MaximumSamples) { w.WriteStartArray(); foreach (int sample in samples) w.WriteNumberValue(sample); w.WriteEndArray(); }
            w.WriteEndArray();
            w.WriteStartArray("fans");
            foreach (var fan in Fans)
            {
                w.WriteStartArray();
                foreach (var x in fan)
                {
                    w.WriteStartObject(); w.WriteNumber("percent", x.Percent); w.WriteNumber("duty", x.Duty);
                    w.WriteNumber("rpm", x.Rpm); w.WriteNumber("min_rpm", x.MinRpm); w.WriteNumber("max_rpm", x.MaxRpm);
                    w.WriteNumber("samples", x.Samples);
                    w.WriteNumber("readback_duty", x.ReadbackDuty < 0 ? x.Duty : x.ReadbackDuty);
                    w.WriteNumber("elapsed_ms", x.ElapsedMilliseconds); w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndArray(); w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
    public static N6506Calibration Parse(string json)
    {
        using var doc = JsonDocument.Parse(json); var r = doc.RootElement;
        if (r.GetProperty("schema").GetInt32() != 1 || r.GetProperty("board").GetString() != "N6506MV" ||
            r.GetProperty("bios").GetString() != "N6506MV.313") throw new ArgumentException("校准版本或机型不匹配");
        return new(r.GetProperty("completed_at").GetDateTimeOffset(), r.GetProperty("fans").EnumerateArray().Select(f =>
            f.EnumerateArray().Select(x => new N6506CalibrationPoint(x.GetProperty("percent").GetInt32(),
                x.GetProperty("duty").GetInt32(), x.GetProperty("rpm").GetInt32(), x.GetProperty("min_rpm").GetInt32(),
                x.GetProperty("max_rpm").GetInt32(), x.GetProperty("samples").GetInt32(), x.GetProperty("readback_duty").GetInt32(), x.GetProperty("elapsed_ms").GetInt32())).ToArray()).ToArray(),
            r.GetProperty("maximum_samples").EnumerateArray().Select(a => a.EnumerateArray().Select(x => x.GetInt32()).ToArray()).ToArray());
    }
}

public sealed class N6506CalibrationStore(string path)
{
    public string FilePath => path;
    public N6506Calibration? Load()
    {
        foreach (string candidate in new[] { path, path + ".bak" })
        {
            try { if (File.Exists(candidate)) return N6506Calibration.Parse(File.ReadAllText(candidate)); }
            catch (Exception ex) { Helpers.Logger.WriteLine("N6506 calibration load: " + ex.Message); }
        }
        return null;
    }
    public void Save(N6506Calibration calibration)
    {
        string json = calibration.ToJson();
        _ = N6506Calibration.Parse(json); // Validate before touching a previous measurement.
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json); file.Write(bytes); file.Flush(true);
            }
            if (File.Exists(path)) File.Copy(path, path + ".bak", true);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

/// <summary>One actual response sample per point after a one-second hold; CPU then GPU ascend from zero.</summary>
public sealed class N6506CalibrationRun
{
    public int Fan { get; private set; }
    public int Percent { get; private set; }
    public int CompletedPoints { get; private set; }
    public TimeSpan Elapsed { get; private set; }
    private TimeSpan _started;
    private readonly N6506CalibrationPoint[][] _points = [new N6506CalibrationPoint[101], new N6506CalibrationPoint[101]];
    private readonly int[][] _maximumSamples = [[], []];
    public bool Complete => CompletedPoints == 202;
    public void SetMaximum(int fan, int[] samples)
    {
        _ = N6506FanPolicy.MeasuredMaximum(samples); _maximumSamples[fan] = samples.ToArray();
    }
    public void BeginPoint(TimeSpan now) { _started = now; }
    public bool Observe(TimeSpan now, int[] rpm, int[] duty, int cpu, int gpu)
    {
        if (cpu <= 0 || cpu >= 75 || gpu >= 75) throw new InvalidOperationException("温度达到校准限制，已中止；旧校准保留");
        Elapsed = now - _started;
        if (Math.Abs(duty[Fan] - N6506Calibration.DutyAt(Percent)) > 2 || duty[1 - Fan] > 2)
            throw new InvalidOperationException("风扇控制量未按请求生效，校准已中止");
        if (Elapsed.TotalSeconds > 4) throw new InvalidOperationException($"{Percent}%取样超时，旧校准保留");
        if (Elapsed.TotalMilliseconds < 1000) return false;
        int value = rpm[Fan];
        if (value < 0 || value > 20000 || (Percent == 100 && value < 1000))
            throw new InvalidOperationException("转速读数异常，旧校准保留");
        // A real single response sample after 1000ms, not a claim of settled speed.
        _points[Fan][Percent] = new(Percent, N6506Calibration.DutyAt(Percent), value, value, value, 1, duty[Fan], (int)Elapsed.TotalMilliseconds);
        CompletedPoints++;
        if (!Complete)
        {
            if (Percent == 100) { Fan = 1; Percent = 0; }
            else Percent++;
        }
        return true;
    }
    public N6506Calibration Finish() => Complete ? new(DateTimeOffset.UtcNow, _points, _maximumSamples) : throw new InvalidOperationException("校准未完成");
}
