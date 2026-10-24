/* Bounded N6506MV test. Never writes mode register 0x31.
 * Only selector 0x32 and duty 0x35 can be written; zero is confined to bounded calibration.
 * Exact machine/BIOS/table guards, saved-duty restoration, signal handling.
 * Protocol: Keyitdev/asus-fan-control-ec/HOW-IT-WORKS.md.
 * N6506_TEST builds have simulated self-tests; normal operation uses bounded stdin requests.
 */
#define _POSIX_C_SOURCE 200809L
#include <assert.h>
#include <dlfcn.h>
#include <fcntl.h>
#include <sys/file.h>
#include <poll.h>
#include <errno.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/io.h>
#include <time.h>
#include <unistd.h>

static volatile sig_atomic_t interrupted;
static int mock, mock_mode = 1, mock_version = 18, mock_count = 2;
static int mock_fan, mock_duty[2] = {120, 130}, mock_fail, mock_writes;
static int owned[2], saved[2];
static int calibration_zero_write;
static void on_signal(int sig) { (void)sig; interrupted = 1; }
static double seconds(void) {
    struct timespec ts;
    if (clock_gettime(CLOCK_MONOTONIC, &ts)) exit(2);
    return ts.tv_sec + ts.tv_nsec / 1e9;
}
static void delay_ms(long ms) {
    if (mock) return;
    struct timespec ts = {ms / 1000, ms % 1000 * 1000000};
    nanosleep(&ts, NULL);
}
static int wait_bit(unsigned mask, unsigned value, double deadline) {
    for (int n = 0; n < 1000 && seconds() < deadline; n++) {
        if ((inb(0x25d) & mask) == value) return 0;
        struct timespec ts = {0, 100000}; nanosleep(&ts, NULL);
    }
    return -1;
}
/* Internal transport. Request types are restricted before port access. */
static int transaction(int command, int direction, int reg, int value, int *reply) {
    if (command == 0xbb) {
        if (direction != 0 || reg != 0x50 || value != 0 || !reply) return -1;
    } else if (command == 0xdd) {
        if (direction == 0x02) {
            if (!reply || value != 0 || !(reg == 0x30 || reg == 0x31 ||
                    reg == 0x33 || reg == 0x34 || reg == 0x35)) return -1;
        } else if (direction == 0x82) {
            if (reply || !((reg == 0x32 && value >= 0 && value <= 1) ||
                           (reg == 0x35 && value >= (calibration_zero_write ? 0 : 1) && value <= 255))) return -1;
        } else return -1;
    } else return -1;
    if (mock) {
        if (mock_fail) return -1;
        if (command == 0xbb) *reply = mock_version;
        else if (direction == 0x82) {
            if (reg == 0x32) mock_fan = value;
            else { mock_duty[mock_fan] = value; mock_writes++; }
        } else if (reg == 0x30) *reply = mock_count;
        else if (reg == 0x31) *reply = mock_mode;
        else if (reg == 0x35) *reply = mock_duty[mock_fan];
        else {
            int rpm = 1000 + mock_duty[mock_fan] * 20;
            *reply = reg == 0x34 ? rpm >> 8 : rpm & 255;
        }
        return 0;
    }
    double deadline = seconds() + 0.5;
    if (inb(0x25d) & 1) return -1;
    unsigned char bytes[5] = {0xff, (unsigned char)command,
        (unsigned char)(command == 0xbb ? 0x50 : direction),
        (unsigned char)reg, (unsigned char)value};
    int length = command == 0xbb ? 3 : 5;
    for (int i = 0; i < length; i++) {
        if (wait_bit(2, 0, deadline)) return -1;
        outb(bytes[i], i < 2 ? 0x25d : 0x25c);
    }
    if (wait_bit(2, 0, deadline)) return -1;
    if (reply) {
        if (wait_bit(1, 1, deadline)) return -1;
        *reply = inb(0x25c);
    }
    delay_ms(20);
    return 0;
}
static int read_reg(int reg, int *value) { return transaction(0xdd, 0x02, reg, 0, value); }
static int select_fan(int fan) { return transaction(0xdd, 0x82, 0x32, fan, NULL); }
static int identity(void) {
    int version, count, mode;
    return transaction(0xbb, 0, 0x50, 0, &version) || read_reg(0x30, &count) ||
        read_reg(0x31, &mode) || version != 18 || count != 2 || mode != 1 ? -1 : 0;
}
static int sample(int fan, int *tach, int *duty) {
    int hi = 0, lo = 0, hi2 = 0, mode;
    if (read_reg(0x31, &mode) || mode != 1) {
        fprintf(stderr, "Sample channel %d: mode read/guard failed\n", fan); return -1;
    }
    if (select_fan(fan)) {
        fprintf(stderr, "Sample channel %d: selector transaction failed\n", fan); return -1;
    }
    int coherent = 0;
    for (int attempt = 0; attempt < 3; attempt++) {
        if (read_reg(0x34, &hi) || read_reg(0x33, &lo) || read_reg(0x34, &hi2)) {
            fprintf(stderr, "Sample channel %d: tach transaction failed\n", fan); return -1;
        }
        if (hi == hi2) { coherent = 1; break; }
    }
    if (!coherent || read_reg(0x35, duty)) {
        fprintf(stderr, "Sample channel %d: tach coherence/duty read failed\n", fan); return -1;
    }
    *tach = hi * 256 + lo;
    if (*tach < 0 || *tach > 20000 || *duty < 0 || *duty > 255) {
        fprintf(stderr, "Sample channel %d: range guard tach=%d duty=%d\n", fan, *tach, *duty); return -1;
    }
    return 0;
}
static int set_duty(int fan, int duty) {
    int mode;
    if (fan < 0 || fan > 1 || duty < (calibration_zero_write ? 0 : 1) || duty > 255) return -1;
    if (read_reg(0x31, &mode) || mode != 1 || select_fan(fan)) {
        fprintf(stderr, "Set channel %d: mode/selector transaction failed\n", fan); return -1;
    }
    int result = transaction(0xdd, 0x82, 0x35, duty, NULL);
    if (result) fprintf(stderr, "Set channel %d: duty transaction failed\n", fan);
    return result;
}
static int cpu_temperature(void) {
    if (mock) return 60000;
    char name[64], path[128];
    for (int n = 0; n < 100; n++) {
        snprintf(path, sizeof path, "/sys/class/hwmon/hwmon%d/name", n);
        FILE *f = fopen(path, "r"); if (!f) continue;
        int ok = fgets(name, sizeof name, f) != NULL; fclose(f);
        if (!ok || strcmp(name, "coretemp\n")) continue;
        snprintf(path, sizeof path, "/sys/class/hwmon/hwmon%d/temp1_input", n);
        f = fopen(path, "r"); if (!f) return -1;
        int temp = -1; if (fscanf(f, "%d", &temp) != 1) temp = -1;
        fclose(f); return temp > 0 && temp < 125000 ? temp : -1;
    }
    return -1;
}
static int restore(void) {
    int failed = 0;
    for (int fan = 0; fan < 2; fan++) {
        if (!owned[fan]) continue;
        int tach = -1, duty = -1;
        if (set_duty(fan, saved[fan]) || sample(fan, &tach, &duty)) failed = 1;
        printf("Restore channel %d: requested=%d readback=%d tach_raw=%d\n",
               fan, saved[fan], duty, tach);
        /* Firmware quantization is reported, not silently accepted as exact. */
        if (duty < saved[fan] - 8 || duty > saved[fan] + 8) failed = 1;
        if (!failed) owned[fan] = 0;
    }
    return failed ? -1 : 0;
}
static int exact_file(const char *path, const char *expected) {
    char buf[128] = {0}; FILE *f = fopen(path, "r"); if (!f) return 0;
    int ok = fgets(buf, sizeof buf, f) != NULL; fclose(f);
    buf[strcspn(buf, "\r\n")] = 0; return ok && !strcmp(buf, expected);
}
static int self_test(void) {
    mock = 1;
    int result; (void)result;
    assert(transaction(0xdd, 0x82, 0x31, 0, NULL));
    assert(transaction(0xdd, 0x82, 0x31, 1, NULL));
    assert(transaction(0xdd, 0x82, 0x35, 0, NULL));
    assert(select_fan(2));
    assert(identity() == 0);
    mock_mode = 0; assert(set_duty(0, 230)); assert(mock_writes == 0);
    mock_mode = 1; mock_version = 19; assert(identity()); mock_version = 18;
    for (int fan = 0; fan < 2; fan++) {
        int tach; (void)tach;
        assert(sample(fan, &tach, &saved[fan]) == 0);
        owned[fan] = 1; assert(set_duty(fan, 230) == 0);
    }
    assert(mock_duty[0] == 230 && mock_duty[1] == 230);
    assert(restore() == 0);
    assert(mock_duty[0] == 120 && mock_duty[1] == 130);
    owned[0] = 1; mock_fail = 1; assert(restore());
    assert(read_reg(0x31, &result));
    puts("PASS: mode writes/zero duty/out-of-range selector rejected; identity, channel isolation, restoration and transport-failure guards. Simulated only.");
    return 0;
}


static int n6506_high(int duty) {
    int bad = 0;
    for (int fan = 0; fan < 2; fan++) if (set_duty(fan, duty)) bad = 1;
    return bad ? -1 : 0;
}
static int n6506_state(int probe) {
    int rpm[2], duty[2], mode;
    if (read_reg(0x31, &mode) || mode != 1 ||
        sample(0, &rpm[0], &duty[0]) || sample(1, &rpm[1], &duty[1])) return -1;
    if (probe) printf("probe 18 2 %d %d %d %d\n", rpm[0], rpm[1], duty[0], duty[1]);
    else printf("state 1 %d %d %d %d\n", rpm[0], rpm[1], duty[0], duty[1]);
    return 0;
}
/* No arbitrary register API. A silent/closed pipe requests known high cooling.
 * Never calls legacy ec-fanctl auto or writes mode0, including on logout.
 */
int do_n6506_fanctl(void) {
    (void)restore; (void)self_test;
    if (!mock && (!exact_file("/sys/class/dmi/id/sys_vendor", "ASUSTeK COMPUTER INC.") ||
        !exact_file("/sys/class/dmi/id/board_name", "N6506MV") ||
        !exact_file("/sys/class/dmi/id/bios_version", "N6506MV.313"))) {
        puts("err model"); return 2;
    }
    int lock_fd = mock ? -1 : open("/run/lock/n6506-fan-control.lock", O_CREAT|O_RDWR|O_CLOEXEC|O_NOFOLLOW, 0600);
    if (!mock && (lock_fd < 0 || flock(lock_fd, LOCK_EX|LOCK_NB))) { puts("err busy"); return 2; }
    if (!mock && ioperm(0x25c, 2, 1)) { puts("err permission"); close(lock_fd); return 2; }
    setvbuf(stdout, NULL, _IOLBF, 0);
    if (identity()) { puts("err identity"); if (!mock) ioperm(0x25c,2,0); close(lock_fd); return 2; }
    struct sigaction action = {0}; action.sa_handler = on_signal;
    sigaction(SIGINT,&action,NULL); sigaction(SIGTERM,&action,NULL); sigaction(SIGHUP,&action,NULL);
    char buffer[128]; size_t used = 0;
    int watchdog_high = 0, quit = 0;
    double last_command = seconds();
    int calibration_active = 0, calibration_fault = 0, calibration_fan = -1, calibration_percent = -1;
    double calibration_deadline = 0, calibration_started = 0;
    while (!interrupted && !quit) {
        if (calibration_active) {
            int temp = cpu_temperature();
            if (temp < 0 || temp >= 75000 || seconds() >= calibration_deadline || seconds() - calibration_started > 900) {
                n6506_high(255); calibration_active = 0; calibration_fault = 1;
                fputs("N6506: calibration temperature/time guard; full cooling applied\n", stderr);
            }
        }
        if (!watchdog_high && seconds() - last_command >= 2.5) {
            n6506_high(230); watchdog_high=1;
            if (calibration_active) { calibration_active=0; calibration_fault=1; }
            fputs("N6506: incomplete/missing command heartbeat; high cooling applied\n", stderr);
        }
        struct pollfd fd = {.fd=STDIN_FILENO,.events=POLLIN};
        int ready = poll(&fd,1,calibration_active ? 250 : 2500);
        if (ready < 0) { if (errno == EINTR) continue; break; }
        if (!ready) continue;
        char chunk[128]; ssize_t got = read(STDIN_FILENO,chunk,sizeof chunk);
        if (got <= 0) break;
        for (ssize_t i=0;i<got && !quit;i++) {
            if (chunk[i] != '\n') {
                if (used >= sizeof buffer-1) { puts("err length"); quit=1; break; }
                buffer[used++]=chunk[i]; continue;
            }
            buffer[used]=0; used=0;
            int fan,duty; char trailing;
            int accepted=1;
            if (!strcmp(buffer,"quit")) { quit=1; break; }
            if (!strcmp(buffer,"probe")) {
                if (n6506_state(1)) puts("err telemetry");
            } else if (!strcmp(buffer,"read")) {
                if (calibration_fault) puts("err calibration-safety");
                else if (n6506_state(0)) puts("err telemetry");
            } else if (!strcmp(buffer,"high") || !strcmp(buffer,"calibration-end")) {
                calibration_active=0; calibration_fault=0;
                puts(n6506_high(230) ? "err write" : "ok");
            } else if (!strcmp(buffer,"calibration-begin")) {
                int cpu=cpu_temperature();
                if (calibration_active || cpu<0 || cpu>=65000) puts("err calibration-start");
                else {
                    calibration_zero_write=1;
                    int zero_failed=n6506_high(0);
                    calibration_zero_write=0;
                    if (zero_failed) { n6506_high(255); puts("err write"); continue; }
                    calibration_active=1; calibration_fault=0; calibration_fan=-1; calibration_percent=-1;
                    calibration_started=seconds(); calibration_deadline=seconds()+28; puts("ok");
                }
            } else if (sscanf(buffer,"calibrate %d %d %c",&fan,&duty,&trailing)==2) {
                int first=calibration_fan==-1 && fan==0 && duty==0;
                int next=fan==calibration_fan && duty==calibration_percent+1;
                int second=calibration_fan==0 && calibration_percent==100 && fan==1 && duty==0;
                int cpu=cpu_temperature();
                if (!calibration_active || fan<0 || fan>1 || duty<0 || duty>100 || !(first||next||second)) {
                    puts("err calibration-sequence"); accepted=0;
                } else if (cpu<0 || cpu>=75000) {
                    n6506_high(255); calibration_active=0; calibration_fault=1; puts("err calibration-safety");
                } else {
                    int value=(duty*255+50)/100;
                    calibration_zero_write=1;
                    int failed=set_duty(1-fan,0);
                    if (!failed) failed=set_duty(fan,value);
                    calibration_zero_write=0;
                    if (failed) {
                        n6506_high(255); calibration_active=0; calibration_fault=1; puts("err write");
                    } else {
                        calibration_fan=fan; calibration_percent=duty;
                        calibration_deadline=seconds()+(duty==100 ? 30 : 6); puts("ok");
                    }
                }
            } else if (sscanf(buffer,"set %d %d %c",&fan,&duty,&trailing)==2 &&
                       !calibration_active && !calibration_fault && fan>=0 && fan<2 && duty>=64 && duty<=255) {
                int cpu=cpu_temperature();
                if (cpu<0 || cpu>=90000) puts(n6506_high(255) ? "err write" : "ok");
                else puts(set_duty(fan,duty) ? "err write" : "ok");
            } else { puts("err command"); accepted=0; }
            if (accepted) { last_command=seconds(); watchdog_high=0; }
        }
    }
    int rc=n6506_high(230) ? 1 : 0;
    if (!mock) ioperm(0x25c,2,0);
    if (lock_fd >= 0) close(lock_fd);
    return rc;
}
#ifdef N6506_TEST
int main(int argc, char **argv) { if (argc==2 && !strcmp(argv[1], "--simulate")) { mock=1; return do_n6506_fanctl(); } return self_test(); }
#endif
