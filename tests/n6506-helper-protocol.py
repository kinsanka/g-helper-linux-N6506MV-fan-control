#!/usr/bin/env python3
"""Run with a helper compiled using -DN6506_TEST. No real EC access."""
import subprocess
import sys
import time

binary = sys.argv[1]
result = subprocess.run([binary, '--simulate'], input='probe\nread\nset 0 255\nread\nset 1 255\nread\nset 0 0\nauto\nquit\n', capture_output=True, text=True, check=True)
lines = result.stdout.splitlines()
assert lines[0] == 'probe 18 2 3400 3600 120 130', lines
assert 'state 1 6100 3600 255 130' in lines, lines
assert 'state 1 6100 6100 255 255' in lines, lines
assert lines.count('err command') == 2, lines
p = subprocess.Popen([binary, '--simulate'], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
p.stdin.write('set 0 64\n'); p.stdin.flush()
assert p.stdout.readline().strip() == 'ok'
for char in 'read  ':
    p.stdin.write(char); p.stdin.flush(); time.sleep(.55)
p.stdin.write('\nread\nquit\n'); p.stdin.flush()
out, err = p.communicate(timeout=8)
assert 'state 1 5600 5600 230 230' in out, (out, err)
assert 'high cooling applied' in err, err
print('PASS: simulated line protocol, channel isolation, zero/auto rejection and incomplete-command watchdog')

# Bounded calibration is the only route to zero; no mode writes or ordinary zero command.
commands = ['calibrate 0 0', 'calibration-begin', 'calibrate 0 100', 'set 0 0']
commands += ['calibrate 0 0', 'read'] + [f'calibrate 0 {p}' for p in range(1, 101)]
commands += ['read']
commands += ['calibrate 1 0', 'read'] + [f'calibrate 1 {p}' for p in range(1, 101)]
commands += ['read', 'calibration-end', 'read', 'quit']
r = subprocess.run([binary, '--simulate'], input='\n'.join(commands)+'\n', capture_output=True, text=True, check=True)
assert r.stdout.count('err calibration-sequence') == 2, r.stdout
assert 'state 1 1000 1000 0 0' in r.stdout, r.stdout
assert 'state 1 1000 1000 0 0' in r.stdout, r.stdout
assert r.stdout.rstrip().endswith('state 1 5600 5600 230 230'), r.stdout
print('PASS: both full 101-point sequences, zero confined to calibration, other fan zero, safe finish')

# Even regular heartbeats cannot leave a calibration point active indefinitely.
p = subprocess.Popen([binary, '--simulate'], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
def request(command):
    p.stdin.write(command+'\n'); p.stdin.flush(); return p.stdout.readline().strip()
assert request('calibration-begin') == 'ok'
assert request('calibrate 0 0') == 'ok'
start = time.monotonic()
while time.monotonic() - start < 7:
    time.sleep(.7)
    response = request('read')
assert response == 'err calibration-safety', response
assert request('calibration-end') == 'ok'
assert request('read') == 'state 1 5600 5600 230 230'
p.stdin.write('quit\n'); p.stdin.flush(); p.communicate(timeout=5)
assert p.returncode == 0
print('PASS: calibration independent point deadline restores cooling despite continued heartbeats')
