using GHelper.Linux.Fan;
using GHelper.Linux.Helpers;

namespace GHelper.Linux.Tests;

public static class N6506FanTests
{
    public static void RunAll()
    {
        Harness.Scenario("N6506_HotProtection_RecoversAfterCooling", _ =>
        {
            Harness.Assert(N6506FanPolicy.Protection(false, 95, 51), "95C must protect");
            Harness.Assert(N6506FanPolicy.Protection(true, 83, 51), "hysteresis keeps protection");
            Harness.Assert(!N6506FanPolicy.Protection(true, 79, 51), "cooling resumes curve");
            Harness.Assert(!N6506FanPolicy.Protection(false, 55, -1), "sleeping GPU uses CPU fallback");
            Harness.Assert(N6506FanPolicy.Protection(false, -1, 40), "missing CPU temperature protects");
        });
        Harness.Scenario("N6506_PerModeCurvesAndEnable_AreIndependent", sandbox =>
        {
            AppConfig.ResetForTest(Path.Combine(sandbox.TempRoot, "n6506-modes"));
            AppConfig.Set("performance_mode", 1);
            byte[] custom = N6506FanPolicy.DefaultCurve(1, 0);
            custom[8] = 32;
            N6506FanService.SaveCurve(0, custom);
            N6506FanService.SetModeEnabled(1, true);
            N6506FanService.SetModeEnabled(0, false);
            N6506FanService.SetModeEnabled(2, true);
            AppConfig.Set("performance_mode", 0);
            Harness.Assert(!N6506FanService.ModeEnabled(0), "balanced remains disabled");
            Harness.AssertEqual((byte)30, N6506FanService.Curve(0)[8], "balanced curve unchanged");
            AppConfig.Set("performance_mode", 1);
            Harness.AssertEqual((byte)32, N6506FanService.Curve(0)[8], "turbo keeps its custom curve");
            Harness.Assert(N6506FanService.ModeEnabled(1) && N6506FanService.ModeEnabled(2), "other switches persist");
        });
        Harness.Scenario("N6506_DisplayedProfileSaveAndReset_DoNotTouchActiveProfile", sandbox =>
        {
            AppConfig.ResetForTest(Path.Combine(sandbox.TempRoot, "n6506-editor"));
            AppConfig.Set("performance_mode", 0);
            byte[] turbo = N6506FanPolicy.DefaultCurve(1, 0); turbo[8] = 32;
            byte[] silentGpu = N6506FanPolicy.DefaultCurve(2, 1); silentGpu[8] = 27;
            N6506FanService.SaveCurve(0, turbo, 1);
            N6506FanService.SaveCurve(1, silentGpu, 2);
            Harness.AssertEqual((byte)32, N6506FanService.Curve(0, 1)[8], "displayed turbo edit retained");
            Harness.AssertEqual((byte)27, N6506FanService.Curve(1, 2)[8], "silent GPU edit independent");
            Harness.AssertEqual((byte)30, N6506FanService.Curve(0, 0)[8], "active balanced unchanged");
            N6506FanService.ResetCurrentCurves(1);
            Harness.AssertEqual(N6506FanPolicy.DefaultCurve(1, 0)[8], N6506FanService.Curve(0, 1)[8], "reset only displayed profile");
            Harness.AssertEqual((byte)27, N6506FanService.Curve(1, 2)[8], "reset preserves other profile");
            Harness.AssertEqual(0, AppConfig.Get("performance_mode"), "editing does not change active mode");
        });
        Harness.Scenario("N6506_InvalidCurveRejected", _ =>
        {
            byte[] data = N6506FanPolicy.DefaultCurve(1, 0);
            data[1] = data[0];
            Harness.Assert(!N6506FanPolicy.ValidCurve(data), "duplicate temperatures rejected");
            data = N6506FanPolicy.DefaultCurve(1, 0); data[8] = 0;
            Harness.Assert(!N6506FanPolicy.ValidCurve(data), "zero-speed curve rejected");
            data = N6506FanPolicy.DefaultCurve(1, 0); data[10] = 21;
            Harness.Assert(!N6506FanPolicy.ValidCurve(data), "descending curve rejected");
        });
        Harness.Scenario("N6506_MaximumTest_RejectsUnstableSamples", _ =>
        {
            Harness.AssertEqual(7850, N6506FanPolicy.MeasuredMaximum([7800, 7850, 7890]), "stable median");
            bool rejected = false;
            try { N6506FanPolicy.MeasuredMaximum([1000, 6000, 7800]); }
            catch (ArgumentException) { rejected = true; }
            Harness.Assert(rejected, "spin-up cannot be saved as calibration");
        });
        Harness.Scenario("N6506_Control_NonzeroBoundedAndSlowFall", _ =>
        {
            Harness.AssertEqual(64, N6506FanPolicy.NextDuty(64, 6000, 2000), "nonzero floor");
            Harness.AssertEqual(255, N6506FanPolicy.NextDuty(250, 2000, 8000), "upper bound");
            Harness.AssertEqual(118, N6506FanPolicy.NextDuty(120, 6000, 3000), "slow fall");
            Harness.AssertEqual(132, N6506FanPolicy.NextDuty(120, 2000, 6000), "fast rise");
        });
    }
}
