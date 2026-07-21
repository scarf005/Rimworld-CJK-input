#!/usr/bin/env python3
"""
fcitx5 IME bridge using raw D-Bus wire protocol (no dependencies).
Communicates with fcitx5's IBus D-Bus interface to process key events.

Protocol (stdin/stdout):
  Input:  KEY:<keyval>:<keycode>:<state>
  Input:  FOCUS:IN  /  FOCUS:OUT
  Input:  CURSOR:<x>:<y>:<w>:<h>
  Input:  RESET
  Input:  PING
  Input:  EXIT
  Output: COMMIT:<text>
  Output: PREEDIT:<text>:<cursor>
  Output: CONSUMED:<bool>
  Output: PONG
  Output: OK:<msg>
  Output: ERROR:<msg>
"""

import os
import sys
import socket
import struct
import select
import hashlib
import threading
import time

# ─── D-Bus protocol helpers ──────────────────────────────────────────────

NATIVE_ENDIAN = '>' if sys.byteorder == 'big' else '<'

# Message types
TYPE_METHOD_CALL = 1
TYPE_METHOD_RETURN = 2
TYPE_ERROR = 3
TYPE_SIGNAL = 4

# Header flags
NO_REPLY_EXPECTED = 1
NO_AUTO_START = 2

# Well-known message fields
PATH = 1
INTERFACE = 2
MEMBER = 3
ERROR_NAME = 4
REPLY_SERIAL = 5
DESTINATION = 6
SENDER = 7
SIGNATURE = 8
UNIX_FDS = 9


def align4(n):
    return (n + 3) & ~3


def marshal_string(s):
    b = s.encode('utf-8')
    return struct.pack(f'{NATIVE_ENDIAN}I', len(b)) + b + b'\x00' * (align4(len(b)) - len(b))


def marshal_signature(sig):
    n = len(sig)
    return bytes([n]) + sig.encode('ascii') + b'\x00'


def marshal_uint32(v):
    return struct.pack(f'{NATIVE_ENDIAN}I', v)


def marshal_boolean(v):
    return struct.pack(f'{NATIVE_ENDIAN}I', 1 if v else 0)


def marshal_body(sig, args):
    """Marshal arguments according to D-Bus type signature."""
    parts = []
    for ty, val in zip(sig, args):
        if ty == 's':
            parts.append(marshal_string(str(val)))
        elif ty == 'u':
            parts.append(marshal_uint32(int(val)))
        elif ty == 'o':
            parts.append(marshal_string(str(val)))
        elif ty == 'b':
            parts.append(marshal_boolean(val))
        elif ty == 'i':
            parts.append(struct.pack(f'{NATIVE_ENDIAN}i', int(val)))
        else:
            raise ValueError(f'Unsupported type: {ty}')
    return b''.join(parts)


def build_message(msg_type, flags, serial, path, iface, member, dest, sig, body_args):
    """Build a complete D-Bus message."""
    # Header fields
    fields = []
    if path:
        fields.append(bytes([PATH, 1, ord('o')]) + b'\x00' + marshal_string(path))
    if iface:
        fields.append(bytes([INTERFACE, 1, ord('s')]) + b'\x00' + marshal_string(iface))
    if member:
        fields.append(bytes([MEMBER, 1, ord('s')]) + b'\x00' + marshal_string(member))
    if dest:
        fields.append(bytes([DESTINATION, 1, ord('s')]) + b'\x00' + marshal_string(dest))
    if sig:
        fields.append(bytes([SIGNATURE, 1, ord('g')]) + b'\x00' + marshal_signature(sig))

    header_body = b''.join(fields)
    body = marshal_body(sig, body_args) if sig else b''

    header = struct.pack(f'{NATIVE_ENDIAN}BBBBI',
                         NATIVE_ENDIAN == '>',
                         msg_type,
                         flags,
                         1,  # protocol version
                         len(header_body),
                         serial)
    header += header_body
    header += b'\x00' * (align4(len(header)) - len(header))

    # Length includes header + body
    total = struct.pack(f'{NATIVE_ENDIAN}I', len(header) + len(body))
    return total + header + body


def demarshal_uint32(data, offset):
    return struct.unpack_from(f'{NATIVE_ENDIAN}I', data, offset)[0], offset + 4


def demarshal_string(data, offset):
    length, offset = demarshal_uint32(data, offset)
    s = data[offset:offset + length].decode('utf-8')
    offset = align4(offset + length)
    return s, offset


def demarshal_boolean(data, offset):
    v, offset = demarshal_uint32(data, offset)
    return v != 0, offset


def demarshal_body(sig, data, offset):
    """Unmarshal arguments from body data."""
    result = []
    for ty in sig:
        if ty == 's':
            v, offset = demarshal_string(data, offset)
        elif ty == 'u':
            v, offset = demarshal_uint32(data, offset)
        elif ty == 'b':
            v, offset = demarshal_boolean(data, offset)
        elif ty == 'o':
            v, offset = demarshal_string(data, offset)
        elif ty == 'i':
            v = struct.unpack_from(f'{NATIVE_ENDIAN}i', data, offset)[0]
            offset += 4
        else:
            raise ValueError(f'Unsupported type: {ty}')
        result.append(v)
    return result, offset


class DBusMessage:
    def __init__(self):
        self.endian = NATIVE_ENDIAN
        self.msg_type = 0
        self.flags = 0
        self.serial = 0
        self.path = ''
        self.interface = ''
        self.member = ''
        self.error_name = ''
        self.reply_serial = 0
        self.destination = ''
        self.sender = ''
        self.signature = ''
        self.body_data = b''


def parse_message(raw):
    if len(raw) < 16:
        return None
    endian_raw = raw[0]
    if endian_raw not in (ord('l'), ord('B')):
        return None
    endian = '>' if endian_raw == ord('B') else '<'

    msg_type = raw[1]
    flags = raw[2]
    version = raw[3]
    # body_length at offset 4-7
    body_len = struct.unpack_from(f'{endian}I', raw, 4)[0]
    serial = struct.unpack_from(f'{endian}I', raw, 8)[0]
    header_len = struct.unpack_from(f'{endian}I', raw, 12)[0]

    total = 16 + align4(header_len) + body_len
    if len(raw) < total:
        return None

    msg = DBusMessage()
    msg.endian = endian
    msg.msg_type = msg_type
    msg.flags = flags
    msg.serial = serial

    offset = 16
    end = offset + header_len
    while offset < end:
        field_type = raw[offset]
        offset += 1
        if field_type == 0:
            break
        sig = raw[offset:offset + 1].decode('ascii')
        offset += 2  # skip sig + padding
        if field_type == PATH:
            msg.path, offset = demarshal_string(raw, offset)
        elif field_type == INTERFACE:
            msg.interface, offset = demarshal_string(raw, offset)
        elif field_type == MEMBER:
            msg.member, offset = demarshal_string(raw, offset)
        elif field_type == ERROR_NAME:
            msg.error_name, offset = demarshal_string(raw, offset)
        elif field_type == REPLY_SERIAL:
            msg.reply_serial, offset = demarshal_uint32(raw, offset)
        elif field_type == DESTINATION:
            msg.destination, offset = demarshal_string(raw, offset)
        elif field_type == SENDER:
            msg.sender, offset = demarshal_string(raw, offset)
        elif field_type == SIGNATURE:
            length = raw[offset]
            offset += 1
            msg.signature = raw[offset:offset + length].decode('ascii')
            offset += length + 1  # + null
        elif field_type == UNIX_FDS:
            # skip
            offset += 4

    offset = 16 + align4(header_len)
    msg.body_data = raw[offset:offset + body_len]

    return msg


# ─── D-Bus connection ────────────────────────────────────────────────────

class DBusConnection:
    def __init__(self):
        self.sock = None
        self.serial = 0
        self.unique_name = ''
        self.ic_path = ''
        self.buffer = b''
        self.lock = threading.Lock()

    def next_serial(self):
        self.serial += 1
        return self.serial

    def connect(self):
        addr = os.environ.get('DBUS_SESSION_BUS_ADDRESS', '')
        if not addr:
            log('DBUS_SESSION_BUS_ADDRESS not set')
            return False

        # Parse address: unix:path=/run/user/1000/bus
        path = ''
        for part in addr.split(','):
            if part.startswith('unix:path='):
                path = part[10:]
            elif part.startswith('path='):
                path = part[5:]
        if not path:
            log(f'Cannot parse address: {addr}')
            return False

        self.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self.sock.connect(path)
        self.sock.setblocking(False)

        # AUTH
        # Send null byte first (required by D-Bus spec)
        self.sock.send(b'\x00')
        # AUTH EXTERNAL <hex-encoded-uid>
        uid_hex = format(os.getuid(), 'x')
        auth_cmd = f'AUTH EXTERNAL {uid_hex}\r\n'.encode()
        self.sock.send(auth_cmd)

        # Read auth response
        time.sleep(0.1)
        auth_resp = self._recv_some(timeout=1.0)
        if b'OK' not in auth_resp:
            log(f'Auth failed: {auth_resp}')
            return False

        # BEGIN
        self.sock.send(b'BEGIN\r\n')

        # Hello
        hello = build_message(
            TYPE_METHOD_CALL, 0, self.next_serial(),
            '/org/freedesktop/DBus', 'org.freedesktop.DBus', 'Hello',
            'org.freedesktop.DBus', '', []
        )
        self.sock.send(hello)

        reply = self._recv_message(timeout=2.0)
        if not reply:
            log('No reply to Hello')
            return False
        if reply.signature == 's':
            self.unique_name, _ = demarshal_body('s', reply.body_data, 0)
            log(f'Connected as {self.unique_name}')
            return True

        log(f'Unexpected Hello reply')
        return False

    def _recv_some(self, timeout=0.1):
        r, _, _ = select.select([self.sock], [], [], timeout)
        if r:
            data = self.sock.recv(4096)
            self.buffer += data
            return data
        return b''

    def _recv_message(self, timeout=0.5):
        deadline = time.time() + timeout
        while time.time() < deadline:
            self._recv_some(timeout=0.05)
            msg = parse_message(self.buffer)
            if msg:
                # Remove parsed message from buffer
                header_len = 16 + align4(struct.unpack_from(f'{NATIVE_ENDIAN}I', self.buffer, 12)[0])
                total = header_len + struct.unpack_from(f'{NATIVE_ENDIAN}I', self.buffer, 4)[0]
                total = 16 + align4(struct.unpack_from(f'{NATIVE_ENDIAN}I', self.buffer, 12)[0]) + struct.unpack_from(f'{NATIVE_ENDIAN}I', self.buffer, 4)[0]
                self.buffer = self.buffer[total:]
                return msg
        return None

    def drain_messages(self):
        """Receive all pending messages."""
        msgs = []
        while True:
            self._recv_some(timeout=0.0)
            msg = parse_message(self.buffer)
            if not msg:
                break
            header_len_raw = 16 + align4(struct.unpack_from(f'{NATIVE_ENDIAN}I', self.buffer, 12)[0])
            body_len = struct.unpack_from(f'{NATIVE_ENDIAN}I', self.buffer, 4)[0]
            total = header_len_raw + body_len
            self.buffer = self.buffer[total:]
            msgs.append(msg)
        return msgs

    def call_method(self, dest, path, iface, member, sig, args, timeout=1.0):
        with self.lock:
            serial = self.next_serial()
            msg = build_message(TYPE_METHOD_CALL, 0, serial, path, iface, member, dest, sig, args)
            self.sock.send(msg)

            deadline = time.time() + timeout
            while time.time() < deadline:
                reply = self._recv_message(timeout=0.05)
                if reply and reply.msg_type == TYPE_METHOD_RETURN and reply.reply_serial == serial:
                    return reply
                if reply and reply.msg_type == TYPE_ERROR and reply.reply_serial == serial:
                    log(f'D-Bus error: {reply.error_name}')
                    return None
                if reply and reply.msg_type == TYPE_SIGNAL:
                    self._handle_signal(reply)
            log(f'Timeout waiting for reply to {member}')
            return None

    def _handle_signal(self, msg):
        """Handle incoming signal (e.g., CommitText)."""
        if msg.interface == 'org.freedesktop.IBus.InputContext':
            if msg.member == 'CommitText':
                if msg.signature == 'v':
                    # Nested variant - for now just log raw data
                    log(f'SIGNAL CommitText (raw, len={len(msg.body_data)})')
                    text = self._extract_string_from_variant(msg.body_data, 0)
                    if text:
                        sys.stdout.write(f'COMMIT:{text}\n')
                        sys.stdout.flush()
            elif msg.member == 'UpdatePreeditText':
                log(f'SIGNAL UpdatePreeditText')
            elif msg.member == 'HidePreeditText':
                sys.stdout.write('HIDE\n')
                sys.stdout.flush()

    def _extract_string_from_variant(self, data, offset):
        """Try to extract a string from an IBus Text variant."""
        # IBus Text is complex. For now, try to find strings in the data.
        try:
            # Skip variant signature byte
            sig_len = data[offset]
            offset += 1
            offset += sig_len + 1  # sig + null

            # Try to read as IBusSerializable header
            # Then the Text object has a 'string' field
            # This is a rough extraction
            for i in range(offset, len(data) - 4):
                strlen = struct.unpack_from(f'{NATIVE_ENDIAN}I', data, i)[0]
                if 1 <= strlen <= 200 and i + 4 + strlen <= len(data):
                    candidate = data[i + 4:i + 4 + strlen]
                    try:
                        s = candidate.decode('utf-8')
                        if s and len(s) >= 1:
                            # Check if this looks like the commit text
                            return s
                    except:
                        pass
            return None
        except:
            return None

    def create_input_context(self):
        reply = self.call_method(
            'org.fcitx.Fcitx5', '/org/freedesktop/IBus',
            'org.freedesktop.IBus', 'CreateInputContext',
            's', ['fcitx5-im-bridge']
        )
        if reply and reply.signature == 'o':
            self.ic_path, _ = demarshal_body('o', reply.body_data, 0)
            log(f'Created IC: {self.ic_path}')
            return self.ic_path
        log('Failed to create input context')
        return ''

    def process_key_event(self, keyval, keycode, state):
        if not self.ic_path:
            log('No IC, cannot process key')
            return False
        reply = self.call_method(
            'org.fcitx.Fcitx5', self.ic_path,
            'org.freedesktop.IBus.InputContext', 'ProcessKeyEvent',
            'uuu', [keyval, keycode, state]
        )
        if reply and reply.signature == 'b':
            consumed, _ = demarshal_body('b', reply.body_data, 0)
            return consumed
        return False

    def focus_in(self):
        if not self.ic_path:
            return
        self.call_method(
            'org.fcitx.Fcitx5', self.ic_path,
            'org.freedesktop.IBus.InputContext', 'FocusIn',
            '', [], timeout=0.5
        )

    def focus_out(self):
        if not self.ic_path:
            return
        self.call_method(
            'org.fcitx.Fcitx5', self.ic_path,
            'org.freedesktop.IBus.InputContext', 'FocusOut',
            '', [], timeout=0.5
        )

    def set_cursor_location(self, x, y, w, h):
        if not self.ic_path:
            return
        self.call_method(
            'org.fcitx.Fcitx5', self.ic_path,
            'org.freedesktop.IBus.InputContext', 'SetCursorLocation',
            'iiii', [x, y, w, h], timeout=0.5
        )

    def reset(self):
        if not self.ic_path:
            return
        self.call_method(
            'org.fcitx.Fcitx5', self.ic_path,
            'org.freedesktop.IBus.InputContext', 'Reset',
            '', [], timeout=0.5
        )

    def destroy_ic(self):
        if not self.ic_path:
            return
        self.call_method(
            'org.fcitx.Fcitx5', self.ic_path,
            'org.freedesktop.IBus.InputContext', 'Destroy',
            '', [], timeout=0.5
        )
        self.ic_path = ''


# ─── Main ────────────────────────────────────────────────────────────────

def log(msg):
    sys.stderr.write(f'DBG:{msg}\n')
    sys.stderr.flush()


def main():
    log(f'fcitx5-ime-bridge starting (PID={os.getpid()})')

    # Connect to D-Bus
    dbus = DBusConnection()
    if not dbus.connect():
        log('Failed to connect to D-Bus')
        sys.exit(1)

    # Create input context
    ic_path = dbus.create_input_context()
    if not ic_path:
        log('Failed to create input context')
        sys.exit(1)
    sys.stdout.write(f'OK:IC created at {ic_path}\n')
    sys.stdout.flush()

    # Focus in
    dbus.focus_in()

    # Main loop
    log('Entering main loop')
    running = True
    last_poll = time.time()

    while running:
        # Check stdin for commands
        r, _, _ = select.select([sys.stdin], [], [], 0.02)
        if r:
            line = sys.stdin.readline()
            if not line:
                break
            line = line.strip()
            if not line:
                continue

            log(f'RECV: {line}')

            if line.startswith('KEY:'):
                parts = line[4:].split(':')
                if len(parts) >= 3:
                    keyval = int(parts[0])
                    keycode = int(parts[1])
                    state = int(parts[2])
                    consumed = dbus.process_key_event(keyval, keycode, state)
                    sys.stdout.write(f'CONSUMED:{"true" if consumed else "false"}\n')
                    sys.stdout.flush()
            elif line == 'FOCUS:IN':
                dbus.focus_in()
                sys.stdout.write('OK:focus in\n')
            elif line == 'FOCUS:OUT':
                dbus.focus_out()
                sys.stdout.write('OK:focus out\n')
            elif line.startswith('CURSOR:'):
                parts = line[7:].split(':')
                if len(parts) >= 4:
                    x, y, w, h = int(parts[0]), int(parts[1]), int(parts[2]), int(parts[3])
                    dbus.set_cursor_location(x, y, w, h)
            elif line == 'RESET':
                dbus.reset()
                sys.stdout.write('OK:reset\n')
            elif line == 'PING':
                sys.stdout.write('PONG\n')
                sys.stdout.flush()
            elif line == 'EXIT':
                running = False
                break

        # Poll for incoming D-Bus signals (CommitText, etc.)
        msgs = dbus.drain_messages()
        for msg in msgs:
            dbus._handle_signal(msg)

    log('Shutting down...')
    dbus.destroy_ic()
    log('Bridge exited')


if __name__ == '__main__':
    main()
