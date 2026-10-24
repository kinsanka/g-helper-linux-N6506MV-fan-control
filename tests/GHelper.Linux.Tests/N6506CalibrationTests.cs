using GHelper.Linux.Fan;

namespace GHelper.Linux.Tests;

public static class N6506CalibrationTests
{
    private static N6506Calibration Table()
    {
        return new(DateTimeOffset.UtcNow, Enumerable.Range(0, 2).Select(f => Enumerable.Range(0, 101).Select(p =>
        {
            int rpm = (int)(p * p * (f == 0 ? 1.0 : .8));
            return new N6506CalibrationPoint(p, N6506Calibration.DutyAt(p), rpm, rpm, rpm, 3);
        }).ToArray()).ToArray(), [[10000, 10000, 10000], [8000, 8000, 8000]]);
    }
    public static void RunAll()
    {
        Harness.Scenario("N6506_Calibration_101RawPointsAndNonlinearInverse", _ =>
        {
            var t = Table();
            Harness.AssertEqual(101, t.Fans[0].Length, "includes both endpoints");
            Harness.AssertEqual(0, t.Fans[0][0].Duty, "zero point measured");
            Harness.AssertEqual(255, t.Fans[0][100].Duty, "full point measured");
            Harness.Assert(Math.Abs(t.DutyForRpm(0, 2500) - 128) <= 1, "50% PWM produces 25% RPM on nonlinear sample");
            Harness.AssertEqual(64, t.DutyForRpm(0, 0), "normal control still nonzero");
            Harness.AssertEqual(255, t.DutyForRpm(0, 20000), "above max bounded");
        });
        Harness.Scenario("N6506_Calibration_RawNoiseKept_MonotoneInverse", _ =>
        {
            var raw = Table().Fans;
            raw[0][51] = new(51, N6506Calibration.DutyAt(51), 2490, 2490, 2490, 3);
            var t = new N6506Calibration(DateTimeOffset.UtcNow, raw, Table().MaximumSamples);
            Harness.AssertEqual(2490, t.Fans[0][51].Rpm, "raw dip kept for inspection");
            int previous = 64;
            for (int rpm = 0; rpm <= 10000; rpm += 100)
            {
                int duty = t.DutyForRpm(0, rpm);
                Harness.Assert(duty >= previous, "noise does not invert the response"); previous = duty;
            }
        });
        Harness.Scenario("N6506_Calibration_PersistsReloads_OnlyCompleteReplaces", sandbox =>
        {
            string file = Path.Combine(sandbox.TempRoot, "calibration.json");
            var store = new N6506CalibrationStore(file); var old = Table(); store.Save(old);
            string before = File.ReadAllText(file);
            var incomplete = new N6506CalibrationRun(); bool rejected = false;
            try { store.Save(incomplete.Finish()); } catch (InvalidOperationException) { rejected = true; }
            Harness.Assert(rejected && File.ReadAllText(file) == before, "incomplete run leaves previous file identical");
            var loaded = new N6506CalibrationStore(file).Load()!;
            Harness.AssertEqual(loaded.Fans[1][41].Rpm, old.Fans[1][41].Rpm, "reload all raw points");
            store.Save(new N6506Calibration(old.CompletedAt.AddDays(1), old.Fans, old.MaximumSamples));
            Harness.AssertEqual(before, File.ReadAllText(file + ".bak"), "previous successful table retained");
            File.WriteAllText(file, "corrupt");
            Harness.AssertEqual(old.CompletedAt, store.Load()!.CompletedAt, "recover valid backup");
        });
        Harness.Scenario("N6506_Calibration_SweepStartsAtZero_HoldsOneSecondAndRecordsBothFans", _ =>
        {
            var run = new N6506CalibrationRun(); double now = 0;
            for (int n = 0; n < 202; n++)
            {
                Harness.AssertEqual(n / 101, run.Fan, "CPU completes before GPU starts");
                Harness.AssertEqual(n % 101, run.Percent, "strictly ascending from true zero");
                int fan = run.Fan, percent = run.Percent;
                run.BeginPoint(TimeSpan.FromSeconds(now));
                int[] rpm = [0, 0], duty = [0, 0];
                rpm[fan] = percent * (fan == 0 ? 80 : 60);
                duty[fan] = N6506Calibration.DutyAt(percent);
                Harness.Assert(!run.Observe(TimeSpan.FromSeconds(now + .999), rpm, duty, 55, 40), "must hold at least one second");
                Harness.Assert(run.Observe(TimeSpan.FromSeconds(now + 1), rpm, duty, 55, 40), "one actual sample after one second advances");
                now += 1;
            }
            run.SetMaximum(0, [8000, 8000, 8000]); run.SetMaximum(1, [6000, 6000, 6000]);
            var table = run.Finish();
            Harness.AssertEqual(0, table.Fans[0][0].Rpm, "stopped fan raw zero allowed in calibration");
            Harness.AssertEqual(8000, table.Maximum(0), "CPU peak");
            Harness.AssertEqual(6000, table.Maximum(1), "GPU peak");
        });
        Harness.Scenario("N6506_Calibration_ThermalMismatchAndTimeoutAbort", _ =>
        {
            foreach (int scenario in new[] { 0, 1, 2 })
            {
                var run = new N6506CalibrationRun(); run.BeginPoint(TimeSpan.Zero); bool rejected = false;
                try { run.Observe(TimeSpan.FromSeconds(scenario == 2 ? 5 : 1), [8000, 6000],
                    scenario == 1 ? [128, 0] : [0, 0], scenario == 0 ? 75 : 55, 40); }
                catch (InvalidOperationException) { rejected = true; }
                Harness.Assert(rejected, "unsafe or unstable point cannot be saved");
                Harness.AssertEqual(0, run.CompletedPoints, "failed point not counted");
            }
        });
        Harness.Scenario("N6506_CalibratedFeedForward_BoundedAndFeedbackRetained", _ =>
        {
            Harness.AssertEqual(132, N6506FanPolicy.NextCalibratedDuty(120, 3000, 6000, 200), "fast rise remains bounded");
            Harness.AssertEqual(118, N6506FanPolicy.NextCalibratedDuty(120, 6000, 3000, 80), "slow fall remains bounded");
            Harness.AssertEqual(120, N6506FanPolicy.NextCalibratedDuty(120, 5950, 6000, 200), "measured target takes precedence");
            Harness.AssertEqual(132, N6506FanPolicy.NextCalibratedDuty(120, 3000, 6000, 80), "feedback corrects contradictory old calibration");
        });
    }
}
