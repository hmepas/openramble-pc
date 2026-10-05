#!/usr/bin/env python3
"""Print the key names keyd's virtual keyboard emits, as OpenRamble sees them.

Run it, press the keys you want to use as the dictation key, Ctrl+C to stop.
Needs the `input` group. Physical keyboards grabbed by keyd emit nothing on their own
nodes; the app reads keyd's output, which is what this prints.
"""
import glob, os, select, struct, sys, time

NAMES = {29: 'LEFTCTRL', 42: 'LEFTSHIFT', 54: 'RIGHTSHIFT', 56: 'LEFTALT', 58: 'CAPSLOCK',
         97: 'RIGHTCTRL', 100: 'RIGHTALT', 125: 'LEFTMETA', 126: 'RIGHTMETA', 1: 'ESC'}
FMT = 'llHHi'; SIZE = struct.calcsize(FMT)

def keyboard_nodes():
    nodes = []
    for path in sorted(glob.glob('/sys/class/input/event*/device/name')):
        node = '/dev/input/' + path.split('/')[4]
        name = open(path).read().strip()
        nodes.append((node, name))
    return nodes

fds = {}
for node, name in keyboard_nodes():
    try:
        fds[os.open(node, os.O_RDONLY | os.O_NONBLOCK)] = f'{os.path.basename(node)} ({name})'
    except OSError:
        pass
print('watching:', ', '.join(fds.values()), file=sys.stderr)
print('press keys; Ctrl+C to stop', file=sys.stderr)
try:
    while True:
        ready, _, _ = select.select(list(fds), [], [], 0.5)
        for fd in ready:
            try:
                data = os.read(fd, SIZE * 64)
            except BlockingIOError:
                continue
            for i in range(0, len(data) - SIZE + 1, SIZE):
                _, _, typ, code, val = struct.unpack(FMT, data[i:i + SIZE])
                if typ == 1 and val in (0, 1):
                    print(f'{time.strftime("%H:%M:%S")} {fds[fd]}: {NAMES.get(code, code)} {"down" if val else "up"}', flush=True)
except KeyboardInterrupt:
    pass
