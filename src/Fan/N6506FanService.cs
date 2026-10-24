using System.Diagnostics;
using GHelper.Linux.Helpers;
using GHelper.Linux.Platform.Linux;

namespace GHelper.Linux.Fan;

/// <summary>
/// N6506MV/BIOS313 only. Mode register is never written. Disabled/exit/error
/// means known high cooling, not an unverified claim of firmware automatic mode.
/// The root helper owns a command watchdog and the shared EC-controller lock.
/// </summary>
public static class N6506FanService
{
    private static readonly Lazy<bool> Supported = new(() =>
        SysfsHelper.ReadAttribute("/sys/class/dmi/id/board_name")?.Trim() == "N6506MV" &&
        SysfsHelper.ReadAttribute("/sys/class/dmi/id/bios_version")?.Trim() == "N6506MV.313");
    public static bool IsSupported => Supported.Value;
    private static readonly object Gate = new();
    private static System.Timers.Timer? _timer;
    private static Connection? _helper;
    private static int _busy;
    private static int[] _rpm = [-1, -1];
    private static long _lastReadTicks;
    private static bool _protection;
    private static int[] _target = [0, 0];
    private static DateTime[] _fall = [DateTime.MinValue, DateTime.MinValue];
    private static DateTime _retryAfter;
    private static TaskCompletionSource<(int Cpu, int Gpu)>? _calibration;
    private static N6506CalibrationRun? _calibrationRun;
    private static int _calibrationMode;
    private static bool? _calibrationBoostRestore;
    private static CancellationTokenSource? _calibrationCancel;
    private static readonly Stopwatch CalibrationClock = new();
    private static readonly N6506CalibrationStore CalibrationStore = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "ghelper", "n6506-fan-calibration.json"));
    private static readonly Lazy<N6506Calibration?> InitialCalibration = new(CalibrationStore.Load);
    private static N6506Calibration? _newCalibration;
    public static N6506Calibration? FullCalibration => _newCalibration ?? InitialCalibration.Value;
    public static string CalibrationFilePath => CalibrationStore.FilePath;
    private static string _status = "";
    private static DateTime _lastLog;
    public static event Action<string>? StatusChanged;
    public static bool Running => _helper != null && ModeEnabled(Mode.Modes.GetCurrent());
    public static bool Calibrating => _calibration != null;
    public static string CurrentStatus => _status;

    public static bool ModeEnabled(int mode) => AppConfig.Get($"n6506_curve_enabled_{mode}", mode == 1 ? 1 : 0) == 1;
    public static void SetModeEnabled(int mode, bool enabled) => AppConfig.Set($"n6506_curve_enabled_{mode}", enabled ? 1 : 0);
    public static int Maximum(int fan) => FullCalibration?.Maximum(fan) ?? Math.Clamp(AppConfig.Get($"n6506_max_rpm_{fan}", fan == 0 ? 8100 : 6200), 1000, 20000);
    public static bool HasCalibration => AppConfig.Get("n6506_calibrated", 0) == 1;
    private static string CurveKey(int mode, int fan) => $"n6506_curve_{mode}_{fan}";

    public static byte[] Curve(int fan, int? mode = null)
    {
        int selected = mode ?? Mode.Modes.GetCurrent();
        try
        {
            string? saved = AppConfig.GetString(CurveKey(selected, fan));
            if (saved != null)
            {
                byte[] data = AppConfig.StringToBytes(saved);
                if (N6506FanPolicy.ValidCurve(data)) return data;
            }
        }
        catch (Exception e) { Logger.WriteLine($"N6506 invalid saved curve: {e.Message}"); }
        return N6506FanPolicy.DefaultCurve(selected, fan);
    }

    public static void SaveCurve(int fan, byte[] data, int? mode = null)
    {
        if (fan is < 0 or > 1 || !N6506FanPolicy.ValidCurve(data)) throw new ArgumentException("Invalid fan curve");
        AppConfig.Set(CurveKey(mode ?? Mode.Modes.GetCurrent(), fan), BitConverter.ToString(data));
    }

    public static void ResetCurrentCurves(int? mode = null)
    {
        int selected = mode ?? Mode.Modes.GetCurrent();
        for (int fan = 0; fan < 2; fan++) SaveCurve(fan, N6506FanPolicy.DefaultCurve(selected, fan), selected);
    }

    public static int CachedRpm(int fan) => fan is >= 0 and < 2 &&
        DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastReadTicks) < TimeSpan.FromSeconds(8).Ticks ? Volatile.Read(ref _rpm[fan]) : -1;

    private static void Status(string text)
    {
        _status = text;
        StatusChanged?.Invoke(text);
        if ((DateTime.UtcNow - _lastLog).TotalSeconds >= 10) { Logger.WriteLine("N6506: " + text); _lastLog = DateTime.UtcNow; }
    }

    public static void Sync(bool interactive = false)
    {
        if (!IsSupported) return;
        _ = Task.Run(() =>
        {
            lock (Gate)
            {
                try
                {
                    if (!ModeEnabled(Mode.Modes.GetCurrent()) && !Calibrating)
                    {
                        StopCore();
                        Status("当前模式未启用自定义曲线 · 保持已验证的高风量");
                        return;
                    }
                    EnsureTimer();
                    _helper ??= Connection.Open(interactive);
                    _target = [0, 0];
                    _fall = [DateTime.MinValue, DateTime.MinValue];
                }
                catch (Exception e)
                {
                    Logger.WriteLine($"N6506 start: {e.Message}");
                    Status("风扇连接失败，将重试；高风量保护保持有效");
                    _retryAfter = DateTime.UtcNow.AddSeconds(5);
                }
            }
        });
    }

    private static void EnsureTimer()
    {
        if (_timer != null) return;
        _timer = new System.Timers.Timer(1500);
        _timer.Elapsed += (_, _) => Tick();
        _timer.Start();
    }

    public static void Stop()
    {
        lock (Gate) StopCore();
    }

    private static void StopCore()
    {
        _timer?.Stop(); _timer?.Dispose(); _timer = null;
        _calibrationCancel?.Cancel();
        _calibration?.TrySetCanceled(); _calibration = null; _calibrationRun = null;
        _helper?.Dispose(); _helper = null;
        RestoreCalibrationBoost();
        Interlocked.Exchange(ref _lastReadTicks, 0);
    }

    public static Task<(int Cpu, int Gpu)> CalibrateAsync()
    {
        lock (Gate)
        {
            if (Calibrating) throw new InvalidOperationException("风扇校准已在运行");
            int cpu = App.Wmi?.DeviceGet(0x00120094) ?? -1;
            int gpu = App.Wmi?.DeviceGet(0x00120097) ?? -1;
            if (cpu <= 0 || cpu >= 65 || gpu >= 65) throw new InvalidOperationException("请空闲运行，CPU/GPU低于65°C后校准");
            try
            {
                _calibrationMode = Mode.Modes.GetCurrent();
                _calibrationBoostRestore = App.Power?.GetCpuBoost();
                App.Power?.SetCpuBoost(false);
                if (App.Power?.GetCpuBoost() == true) throw new InvalidOperationException("无法暂时关闭睿频，校准未开始");
                _helper ??= Connection.Open(true);
                _helper.BeginCalibration();
                var run = new N6506CalibrationRun(); _calibrationRun = run; CalibrationClock.Restart();
                var cancel = new CancellationTokenSource(); _calibrationCancel = cancel;
                var completion = new TaskCompletionSource<(int Cpu, int Gpu)>(TaskCreationOptions.RunContinuationsAsynchronously);
                _calibration = completion;
                EnsureTimer();
                Status("校准已开始 · 临时关闭睿频 · CPU先、GPU后，每档1秒，取消保留旧表");
                _ = Task.Run(() => CalibrationLoop(run, completion, cancel.Token));
                return completion.Task;
            }
            catch
            {
                _calibrationRun = null; _helper?.Dispose(); _helper = null; RestoreCalibrationBoost(); throw;
            }
        }
    }

    private static async Task CalibrationLoop(N6506CalibrationRun run,
        TaskCompletionSource<(int Cpu, int Gpu)> completion, CancellationToken cancel)
    {
        try
        {
            while (!run.Complete)
            {
                lock (Gate)
                {
                    cancel.ThrowIfCancellationRequested();
                    if (_calibration != completion) return;
                    if (Mode.Modes.GetCurrent() != _calibrationMode)
                        throw new InvalidOperationException("性能模式改变，校准已中止");
                    _helper!.CalibrationPoint(run.Fan, run.Percent);
                    run.BeginPoint(CalibrationClock.Elapsed);
                }
                await Task.Delay(1000, cancel);
                int completedFan = -1;
                lock (Gate)
                {
                    cancel.ThrowIfCancellationRequested();
                    if (_calibration != completion) return;
                    var state = _helper!.Read();
                    int cpu = App.Wmi?.DeviceGet(0x00120094) ?? -1;
                    int gpu = App.Wmi?.DeviceGet(0x00120097) ?? -1;
                    int fan = run.Fan, percent = run.Percent;
                    for (int f = 0; f < 2; f++) Volatile.Write(ref _rpm[f], state.Rpm[f]);
                    Interlocked.Exchange(ref _lastReadTicks, DateTime.UtcNow.Ticks);
                    if (!run.Observe(CalibrationClock.Elapsed, state.Rpm, state.Duty, cpu, gpu))
                        throw new InvalidOperationException("取样间隔不足，校准已中止");
                    Logger.WriteLine($"N6506 calibration point: fan={fan} percent={percent} duty={state.Duty[fan]} other_duty={state.Duty[1 - fan]} rpm={state.Rpm[fan]} elapsed_ms={(int)run.Elapsed.TotalMilliseconds} cpu_c={cpu} gpu_c={gpu}");
                    if (percent == 100) completedFan = fan;
                    Status($"校准 {run.CompletedPoints}/202点 · {(fan == 0 ? "CPU" : "GPU")} 控制量{percent}% · " +
                        $"CPU {state.Rpm[0]} / GPU {state.Rpm[1]} RPM · 写入后等待1秒取样");
                }
                if (completedFan >= 0) run.SetMaximum(completedFan, await MeasureMaximum(completedFan, completion, cancel));
            }
            lock (Gate)
            {
                cancel.ThrowIfCancellationRequested();
                if (_calibration != completion) return;
                _helper!.EndCalibration();
                var table = run.Finish(); CalibrationStore.Save(table);
                _newCalibration = table;
                AppConfig.Set("n6506_max_rpm_0", table.Maximum(0)); AppConfig.Set("n6506_max_rpm_1", table.Maximum(1));
                AppConfig.Set("n6506_calibrated", 1);
                _calibration = null; _calibrationRun = null; _target = [0, 0];
                RestoreCalibrationBoost();
                completion.TrySetResult((table.Maximum(0), table.Maximum(1)));
                Status("校准完成 · 两个风扇各101个实测点已永久保存");
            }
        }
        catch (OperationCanceledException) { completion.TrySetCanceled(); }
        catch (Exception e)
        {
            lock (Gate)
            {
                if (_calibration == completion)
                {
                    _calibration = null; _calibrationRun = null;
                    _helper?.Dispose(); _helper = null;
                    RestoreCalibrationBoost();
                    Status("校准中止，旧校准保留：" + e.Message);
                }
                completion.TrySetException(e);
            }
        }
        finally { if (!cancel.IsCancellationRequested) Sync(); }
    }

    private static async Task<int[]> MeasureMaximum(int fan,
        TaskCompletionSource<(int Cpu, int Gpu)> completion, CancellationToken cancel)
    {
        var clock = Stopwatch.StartNew(); var samples = new List<int>();
        while (clock.Elapsed.TotalSeconds < 24)
        {
            await Task.Delay(1000, cancel);
            lock (Gate)
            {
                cancel.ThrowIfCancellationRequested();
                if (_calibration != completion) throw new OperationCanceledException("校准已取消");
                if (Mode.Modes.GetCurrent() != _calibrationMode) throw new InvalidOperationException("模式改变，校准已中止");
                var state = _helper!.Read();
                int cpu = App.Wmi?.DeviceGet(0x00120094) ?? -1;
                int gpu = App.Wmi?.DeviceGet(0x00120097) ?? -1;
                if (cpu <= 0 || cpu >= 75 || gpu >= 75 || state.Duty[fan] < 245 || state.Duty[1 - fan] > 2 || state.Rpm[fan] < 500)
                    throw new InvalidOperationException("最高转速测试触发保护，旧校准保留");
                for (int f = 0; f < 2; f++) Volatile.Write(ref _rpm[f], state.Rpm[f]);
                Interlocked.Exchange(ref _lastReadTicks, DateTime.UtcNow.Ticks);
                Status($"{(fan == 0 ? "CPU" : "GPU")} 100% · 等待稳定最高转速 · {state.Rpm[fan]} RPM");
                if (clock.Elapsed.TotalSeconds < 8) continue;
                samples.Add(state.Rpm[fan]); if (samples.Count > 4) samples.RemoveAt(0);
                if (samples.Count < 4) continue;
                try { _ = N6506FanPolicy.MeasuredMaximum(samples); return samples.ToArray(); }
                catch (ArgumentException) { }
            }
        }
        throw new InvalidOperationException("100%转速未稳定，旧校准保留");
    }

    private static void RestoreCalibrationBoost()
    {
        bool? restore = _calibrationBoostRestore; _calibrationBoostRestore = null;
        // A newly selected performance mode owns its own boost policy.
        if (restore.HasValue && Mode.Modes.GetCurrent() == _calibrationMode)
            App.Power?.SetCpuBoost(restore.Value);
    }

    public static void CancelMaximumTest()
    {
        lock (Gate)
        {
            _calibrationCancel?.Cancel();
            _calibration?.TrySetCanceled(); _calibration = null; _calibrationRun = null;
            try { _helper?.EndCalibration(); }
            catch (Exception e) { Logger.WriteLine("N6506 cancel: " + e.Message); _helper?.Dispose(); _helper = null; }
            RestoreCalibrationBoost();
            Status("校准已取消 · 上一份校准保持不变");
        }
        Sync();
    }

    private static void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0) return;
        try
        {
            lock (Gate)
            {
                if (_timer == null || Calibrating) return;
                if (!ModeEnabled(Mode.Modes.GetCurrent()) && !Calibrating) { StopCore(); return; }
                if (_helper == null)
                {
                    if (DateTime.UtcNow < _retryAfter) return;
                    _helper = Connection.Open(false);
                }
                var state = _helper.Read();
                for (int fan = 0; fan < 2; fan++) Volatile.Write(ref _rpm[fan], state.Rpm[fan]);
                Interlocked.Exchange(ref _lastReadTicks, DateTime.UtcNow.Ticks);
                int cpu = App.Wmi?.DeviceGet(0x00120094) ?? -1;
                int gpu = App.Wmi?.DeviceGet(0x00120097) ?? -1;
                _protection = N6506FanPolicy.Protection(_protection, cpu, gpu);
                if (_protection)
                {
                    _helper.Set(0, 255); _helper.Set(1, 255);
                    Status($"高温保护 · CPU {cpu}°C / GPU {(gpu > 0 ? gpu.ToString() : "休眠")} · 降温后自动恢复曲线");
                    return;
                }
                double hottest = Math.Max(cpu, gpu > 0 ? gpu : cpu);
                int shift = Math.Clamp(AppConfig.Get("fan_shift", 50), 0, 100);
                for (int fan = 0; fan < 2; fan++)
                {
                    double own = fan == 0 || gpu <= 0 ? cpu : gpu;
                    double effective = own + (hottest - own) * shift / 100.0;
                    int wanted = N6506FanPolicy.TargetRpm(Curve(fan), effective, Maximum(fan));
                    if (wanted >= _target[fan]) { _target[fan] = wanted; _fall[fan] = DateTime.MinValue; }
                    else
                    {
                        if (_fall[fan] == DateTime.MinValue) _fall[fan] = DateTime.UtcNow;
                        if ((DateTime.UtcNow - _fall[fan]).TotalSeconds >= 10) { _target[fan] = wanted; _fall[fan] = DateTime.MinValue; }
                    }
                    int duty = N6506FanPolicy.NextCalibratedDuty(state.Duty[fan], state.Rpm[fan], _target[fan],
                        FullCalibration?.DutyForRpm(fan, _target[fan]));
                    if (duty != state.Duty[fan]) _helper.Set(fan, duty);
                }
                Status($"CPU {state.Rpm[0]} → {_target[0]} RPM · GPU {state.Rpm[1]} → {_target[1]} RPM");
            }
        }
        catch (Exception e)
        {
            lock (Gate)
            {
                Logger.WriteLine($"N6506 fan update: {e.Message}");
                _calibration?.TrySetException(e); _calibration = null; _calibrationRun = null;
                if (_timer != null) _timer.Interval = 1500;
                _helper?.Dispose(); _helper = null;
                _retryAfter = DateTime.UtcNow.AddSeconds(5);
                Status("风扇连接中断 · 保持高风量，正在重新连接");
            }
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private sealed class Connection : IDisposable
    {
        private readonly Process _process;
        private Connection(Process process) => _process = process;
        public static Connection Open(bool interactive)
        {
            if (!Gpu.NVidia.NvidiaProcessScanner.EnsureHelper()) throw new IOException("风扇辅助程序未安装");
            foreach (string executable in interactive ? new[] { "sudo", "pkexec" } : new[] { "sudo" })
            {
                var info = new ProcessStartInfo(executable) { RedirectStandardInput = true, RedirectStandardOutput = true,
                    RedirectStandardError = true, UseShellExecute = false };
                if (executable == "sudo") info.ArgumentList.Add("-n");
                info.ArgumentList.Add(SysfsHelper.GpuHelperPath);
                info.ArgumentList.Add("ec-fanctl-n6506");
                var p = Process.Start(info) ?? throw new IOException("无法启动风扇辅助程序");
                _ = Task.Run(async () => { while (await p.StandardError.ReadLineAsync() is { } line) Logger.WriteLine($"N6506 helper: {line}"); });
                var connection = new Connection(p);
                try
                {
                    string line = connection.Request("probe", executable == "pkexec" ? 90 : 5);
                    var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length != 7 || fields[0] != "probe" || fields[1] != "18" || fields[2] != "2")
                        throw new IOException("本机风扇协议验证失败: " + line);
                    return connection;
                }
                catch { connection.Dispose(); }
            }
            throw new IOException("无法连接本机风扇控制器");
        }
        private string Request(string command, int seconds = 4)
        {
            if (_process.HasExited) throw new IOException("辅助程序已退出");
            _process.StandardInput.WriteLine(command); _process.StandardInput.Flush();
            string? line = _process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(seconds)).GetAwaiter().GetResult();
            if (line == null || line.StartsWith("err")) throw new IOException(line ?? "风扇连接已关闭");
            return line;
        }
        public (int[] Rpm, int[] Duty) Read()
        {
            var fields = Request("read").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 6 || fields[0] != "state" || fields[1] != "1") throw new IOException("风扇状态异常");
            var values = fields.Skip(2).Select(int.Parse).ToArray();
            if (values.Take(2).Any(x => x < 0 || x > 20000) || values.Skip(2).Any(x => x < 0 || x > 255))
                throw new IOException("风扇读数异常");
            return ([values[0], values[1]], [values[2], values[3]]);
        }
        public void Set(int fan, int duty)
        {
            if (fan is < 0 or > 1 || duty is < 64 or > 255) throw new ArgumentOutOfRangeException(nameof(duty));
            if (Request($"set {fan} {duty}") != "ok") throw new IOException("风扇写入失败");
        }
        public void BeginCalibration() { if (Request("calibration-begin") != "ok") throw new IOException("校准启动失败"); }
        public void CalibrationPoint(int fan, int percent)
        {
            if (fan is < 0 or > 1 || percent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(percent));
            if (Request($"calibrate {fan} {percent}") != "ok") throw new IOException("校准控制量请求失败");
        }
        public void EndCalibration() { if (Request("calibration-end") != "ok") throw new IOException("校准退出失败"); }
        public void High() { if (Request("high") != "ok") throw new IOException("高风量请求失败"); }
        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.StandardInput.WriteLine("quit"); _process.StandardInput.Flush(); _process.StandardInput.Close();
                    if (!_process.WaitForExit(4000)) _process.Kill();
                }
            }
            catch (Exception e) { Logger.WriteLine($"N6506 close: {e.Message}"); }
        }
    }
}
