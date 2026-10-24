# N6506MV native fan integration

This local build adds a dedicated backend for ASUS N6506MV, BIOS N6506MV.313,
HealthyTable version 18 and two fans. Other models retain the existing backend.

## User interface

Open **Fans + Power**. The mode selector displays only the selected
profile's CPU/GPU curves and its enable checkbox. Silent / Balanced / Turbo each
retain separate curves and enable state. Selecting a profile also switches the
laptop's performance mode, matching the existing UI.
Dragging points saves that profile. Apply enables the current profile. Disable
turns off only that profile. Calibration now follows the user's explicit sequence:
start both fans at true0% duty; sweep CPU0..100% inclusive while GPU remains0%;
measure CPU stable maximum at100%; then reset CPU to0% and sweep GPU0..100%,
followed by its stable maximum. Each step writes duty, waits1000ms and then reads
actual RPM. EC transaction time adds overhead. This produces101 raw response
points per fan, in roughly4–6 minutes. A one-second response is not claimed to be
settled RPM. A second, monotone fit is used for RPM-to-duty inversion, with tach
feedback correcting remaining error. Normal profile control keeps duty64..255.

Only the time-bounded, strictly ordered calibration protocol accepts zero. No
mode register is written. Start requires CPU/GPU below65°C; CPU/GPU75°C, missing
CPU temperature, mode change, mismatched duty or communication failure aborts
the run and returns high cooling. The native process independently checks CPU
temperature, per-point and total deadlines and command heartbeats, including when
both fans are at0%. CPU/GPU are never swept simultaneously. The fan not being
measured is held at0%, as explicitly requested by the user.

At each fan's100% endpoint, the raw one-second point is recorded first. Then that
fan stays at100%, waits at least8 additional seconds and collects four readings.
The median is accepted only when their range is within6%; otherwise it keeps
sampling up to24seconds and then aborts. These maximum samples are stored
separately from the response points.

The complete table is atomically saved to
`~/.config/ghelper/n6506-fan-calibration.json`; the previous successful table is
retained as`.bak`. It contains board/BIOS, completion time, requested duty, actual
duty readback, RPM and elapsed measurement time for every point, plus maximum
samples. It survives restart, has no expiry, and is replaced only after both
fan sweeps and both stable maximum measurements succeed. Cancellation or failure
retains the previous table. No partial points are filled with generated values.
Existing peak-only calibration is explicitly labelled until a full run succeeds.

Curve percentages in `n6506_curve_<mode>_<fan>` represent a fraction of measured
maximum RPM, not PWM. `n6506_max_rpm_0/1` store ceilings, `n6506_calibrated` marks
successful measurement, and `n6506_curve_enabled_<mode>` stores independent
switches. Unmeasured ceilings are explicitly labelled reference values. The
controller uses tach feedback, a nonzero duty floor and slow decreases.

## Lifecycle and safeguards

`ManualFanService` routes this model to `N6506FanService`. It uses the installed
`gpu-helper ec-fanctl-n6506` over bounded request/response pipes. The native helper
validates machine/BIOS/table/mode and shares `/run/lock/n6506-fan-control.lock`
with the earlier temporary controller. It never writes register 0x31 and rejects
legacy `auto`, ordinary zero-duty commands and arbitrary register commands.
Zero duty is confined to the bounded calibration sequence.

Above the temperature threshold the app requests full cooling and keeps
monitoring. It returns to the curve after cooling, rather than exiting at a hot
sample. Missing GPU temperature falls back to CPU temperature. Lost/partial
heartbeats, EOF, logout and shutdown request known high cooling. Logging in
starts GHelper through its existing autostart and reapplies the selected profile.

**Disabled curves and app exit currently mean high cooling, not a verified
firmware-automatic handoff.** Mode0 previously stopped fans on this machine.
True suspend/resume remains untested; logout is not suspend. Software cannot
provide recovery guarantees if the EC or kernel fails.

## Build and tests

Build `vendor/gpu-helper/gpu-helper` including `n6506_fan.c` before publishing the
C# application, so its embedded helper matches the installed one. `build.sh`
includes the new translation unit. Native AOT is supported by the source; the
local deliverable was published self-contained without AOT.

Run the existing C# scenario suite, which now includes N6506 policy/config cases.
Compile `n6506_fan.c` with `-DN6506_TEST` for simulated transport tests; its
`--simulate` mode never accesses hardware and is used by the line-protocol test.
Normal application builds never enable the simulation switch.

Protocol reference: https://github.com/Keyitdev/asus-fan-control-ec/blob/master/HOW-IT-WORKS.md
This is a locally tested adaptation, not an upstream release or a universal EC driver.

Calibration temporarily disables CPU turbo boost and verifies it is disabled.
The previous value is restored on completion, cancellation, failure or shutdown;
no saved boost policy is changed. A newly selected mode retains its own policy.
An initial real run confirmed both fans reported0RPM atCPU5%, then reached the
75°C guard and aborted without replacing the existing peak-only calibration.
