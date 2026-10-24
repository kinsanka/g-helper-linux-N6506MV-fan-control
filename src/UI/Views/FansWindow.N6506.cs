using Avalonia.Controls;
using Avalonia.Interactivity;
using GHelper.Linux.Fan;
using GHelper.Linux.Helpers;

namespace GHelper.Linux.UI.Views;

public partial class FansWindow
{
    private bool _loadingN6506;
    private int _n6506DisplayedMode;

    private void LoadN6506Controls()
    {
        _loadingN6506 = true;
        try
        {
            _n6506DisplayedMode = Mode.Modes.GetCurrent();
            string modeName = Mode.Modes.GetName(_n6506DisplayedMode);
            panelN6506.IsVisible = true;
            chartCPU.CurveData = N6506FanService.Curve(0, _n6506DisplayedMode);
            chartGPU.CurveData = N6506FanService.Curve(1, _n6506DisplayedMode);
            chartCPU.RpmScale = N6506FanService.Maximum(0);
            chartGPU.RpmScale = N6506FanService.Maximum(1);
            chartCPU.MinimumPercent = chartGPU.MinimumPercent = 20;
            chartCPU.FanLabel = $"{modeName} · CPU 风扇 · 目标 RPM";
            chartGPU.FanLabel = $"{modeName} · GPU 风扇 · 目标 RPM";
            chartCPU.Disabled = chartGPU.Disabled = false;
            chartMid.IsVisible = false;
            checkApplyFans.Content = $"启用{modeName}曲线";
            checkApplyFans.IsChecked = N6506FanService.ModeEnabled(_n6506DisplayedMode);
            labelN6506CurveHint.Text = $"正在编辑{modeName}模式：拖动自动保存，曲线和启用状态按模式独立记忆。关闭曲线或退出时保留高风量。校准从0%递增至100%，每档等待1秒再取样，先CPU后GPU，每只101点，约4–6分钟。开始时双风扇0%，另一只保持0%；期间临时关闭睿频，结束恢复。请空闲运行，可取消。";
            buttonDisable.Content = "关闭当前曲线";
            labelManualFanStatus.IsVisible = true;
            labelManualFanStatus.Text = N6506FanService.CurrentStatus;
            var calibration = N6506FanService.FullCalibration;
            labelN6506Maximum.Text = $"CPU {N6506FanService.Maximum(0)} / GPU {N6506FanService.Maximum(1)} RPM" +
                (calibration != null ? $" · 各101点 · {calibration.CompletedAt.ToLocalTime():MM-dd HH:mm} · 已保存" :
                    N6506FanService.HasCalibration ? " · 仅测过上限，尚无完整校准" : " · 尚未校准");
            if (Height < 860) Height = 860;
        }
        finally { _loadingN6506 = false; }
    }

    private void ApplyN6506Curves()
    {
        if (chartCPU.CurveData is { Length: 16 } cpu) N6506FanService.SaveCurve(0, cpu, _n6506DisplayedMode);
        if (chartGPU.CurveData is { Length: 16 } gpu) N6506FanService.SaveCurve(1, gpu, _n6506DisplayedMode);
        N6506FanService.SetModeEnabled(_n6506DisplayedMode, true);
        N6506FanService.Sync(true);
        LoadN6506Controls();
    }

    private async void N6506Maximum_Click(object? sender, RoutedEventArgs e)
    {
        buttonN6506Maximum.IsEnabled = false;
        buttonN6506Cancel.IsVisible = true;
        try
        {
            var result = await Task.Run(() => N6506FanService.CalibrateAsync());
            LoadN6506Controls();
            labelManualFanStatus.Text = $"校准已保存：CPU {result.Cpu} RPM · GPU {result.Gpu} RPM";
        }
        catch (OperationCanceledException) { labelManualFanStatus.Text = "校准已取消，旧校准保留"; }
        catch (Exception ex)
        {
            Logger.WriteLine($"N6506 calibration: {ex.Message}");
            labelManualFanStatus.Text = "校准未完成，旧校准保留：" + ex.Message;
        }
        finally
        {
            buttonN6506Maximum.IsEnabled = true;
            buttonN6506Cancel.IsVisible = false;
        }
    }

    private void N6506Cancel_Click(object? sender, RoutedEventArgs e)
    {
        try { N6506FanService.CancelMaximumTest(); }
        catch (Exception ex) { labelManualFanStatus.Text = ex.Message; }
    }
}
