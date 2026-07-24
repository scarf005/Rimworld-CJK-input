use dbus::arg::messageitem::MessageItem;
use dbus::blocking::{BlockingSender, SyncConnection};
use dbus::channel::MatchingReceiver;
use dbus::message::MatchRule;
use dbus::Message;
use std::collections::VecDeque;
use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::Duration;

const INPUT_CONTEXT_INTERFACE: &str = "org.fcitx.Fcitx.InputContext1";
const MAX_CONTEXTS: usize = 64;
const MAX_PENDING_KEYS: usize = 128;

type NotifyCallback = usize;

#[derive(Default, Clone)]
struct ContextEntry {
    destination: String,
    path: String,
    hangul: bool,
}

#[derive(Default, Clone)]
struct PendingKey {
    client: String,
    serial: u32,
    context: u32,
    keyval: u32,
    state: u32,
    release: bool,
}

struct BridgeState {
    queue: VecDeque<String>,
    running: bool,
    worker_handle: Option<thread::JoinHandle<()>>,
    rimworld_pid: u32,
    contexts: Vec<ContextEntry>,
    next_sequence: u64,
    pending_keys: Vec<PendingKey>,
    rimworld_destinations: Vec<String>,
}

impl BridgeState {
    fn new() -> Self {
        Self {
            queue: VecDeque::new(),
            running: false,
            worker_handle: None,
            rimworld_pid: 0,
            contexts: vec![ContextEntry::default(); MAX_CONTEXTS],
            next_sequence: 0,
            pending_keys: vec![PendingKey::default(); MAX_PENDING_KEYS],
            rimworld_destinations: Vec::new(),
        }
    }
}

struct Bridge {
    state: Mutex<BridgeState>,
    notify: Mutex<NotifyCallback>,
    notify_data: Mutex<usize>,
    debug_logging: AtomicBool,
    restart_requested: AtomicBool,
    next_pending_key: AtomicU32,
}

fn busname_str(opt: Option<dbus::strings::BusName<'_>>) -> String {
    opt.map(|b| b.to_string()).unwrap_or_default()
}

fn path_str(opt: Option<dbus::strings::Path<'_>>) -> String {
    opt.map(|p| p.to_string()).unwrap_or_default()
}

fn member_str(opt: Option<dbus::strings::Member<'_>>) -> String {
    opt.map(|m| m.to_string()).unwrap_or_default()
}

impl Bridge {
    fn new() -> Arc<Self> {
        Arc::new(Self {
            state: Mutex::new(BridgeState::new()),
            notify: Mutex::new(0),
            notify_data: Mutex::new(0),
            debug_logging: AtomicBool::new(false),
            restart_requested: AtomicBool::new(false),
            next_pending_key: AtomicU32::new(0),
        })
    }

    fn enqueue_message(self: &Arc<Self>, msg: String) {
        // Suppress LOG: messages when debug logging is off
        if !self.debug_logging.load(Ordering::Relaxed) && msg.starts_with("LOG:") {
            return;
        }
        let mut state = self.state.lock().unwrap();
        let was_empty = state.queue.is_empty();
        state.queue.push_back(msg);
        drop(state);
        if was_empty {
            let cb = *self.notify.lock().unwrap();
            if cb != 0 {
                let data = *self.notify_data.lock().unwrap();
                unsafe {
                    let f: extern "C" fn(usize) = std::mem::transmute(cb);
                    f(data);
                }
            }
        }
    }

    fn enqueue_hex(self: &Arc<Self>, prefix: &str, text: &str) {
        let mut msg = String::with_capacity(prefix.len() + 1 + text.len() * 2);
        msg.push_str(prefix);
        msg.push(':');
        for &byte in text.as_bytes() {
            msg.push(char::from_digit((byte >> 4) as u32, 16).unwrap());
            msg.push(char::from_digit((byte & 0x0f) as u32, 16).unwrap());
        }
        self.enqueue_message(msg);
    }

    fn is_directional_key(keyval: u32) -> bool {
        matches!(keyval, 97 | 65 | 100 | 68 | 115 | 83 | 119 | 87)
    }

    fn handle_process_key(self: Arc<Self>, msg: &Message, context: u32, sequence: u64) {
        let mut iter = msg.iter_init();
        let keyval: u32 = iter.read().unwrap_or(0);
        let _keycode: u32 = iter.read().unwrap_or(0);
        let state: u32 = iter.read().unwrap_or(0);
        let release: bool = iter.read().unwrap_or(false);
        let _time: u32 = iter.read().unwrap_or(0);

        let hangul = {
            let st = self.state.lock().unwrap();
            if context > 0 && (context as usize) <= st.contexts.len() {
                st.contexts[context as usize - 1].hangul
            } else {
                false
            }
        };

        if hangul && Self::is_directional_key(keyval) {
            self.enqueue_message(format!(
                "EVENT:{}:{}:KEY:{}:{}",
                context, sequence, keyval, release as u8
            ));
        }

        if !self.debug_logging.load(Ordering::Relaxed) {
            return;
        }

        let serial = msg.get_serial().unwrap_or(0);
        let client = busname_str(msg.sender());

        let idx =
            self.next_pending_key.fetch_add(1, Ordering::Relaxed) as usize % MAX_PENDING_KEYS;
        {
            let mut st = self.state.lock().unwrap();
            st.pending_keys[idx] = PendingKey {
                client: client.clone(),
                serial,
                context,
                keyval,
                state,
                release,
                ..Default::default()
            };
        }

        self.enqueue_message(format!(
            "LOG:ProcessKeyEvent serial={} context={} keyval={} keycode={} state={} release={} time={} hangul={}",
            serial, context, keyval, _keycode, state, release, _time, hangul
        ));
    }

    fn log_process_key_reply(self: &Arc<Self>, msg: &Message) {
        if !self.debug_logging.load(Ordering::Relaxed) {
            return;
        }

        let dest = busname_str(msg.destination());
        let reply_serial = msg.get_reply_serial().unwrap_or(0);

        let state = self.state.lock().unwrap();
        if !state.rimworld_destinations.contains(&dest) {
            return;
        }

        let pending = state
            .pending_keys
            .iter()
            .find(|p| p.serial == reply_serial && p.client == dest)
            .cloned();
        drop(state);

        let pending = match pending {
            Some(p) => p,
            None => return,
        };

        if msg.msg_type() == dbus::MessageType::Error {
            let err_name = msg.get_items().first()
                .and_then(|i| if let MessageItem::Str(s) = i { Some(s.clone()) } else { None })
                .unwrap_or_else(|| "unknown".to_string());
            self.enqueue_message(format!(
                "LOG:ProcessKeyReply serial={} context={} keyval={} keycode={} release={} error={}",
                reply_serial, pending.context, pending.keyval, 0u32, pending.release, err_name
            ));
        } else {
            let mut iter = msg.iter_init();
            let accepted: bool = iter.read().unwrap_or(false);
            self.enqueue_message(format!(
                "LOG:ProcessKeyReply serial={} context={} keyval={} keycode={} state={} release={} accepted={}",
                reply_serial,
                pending.context,
                pending.keyval,
                0u32,
                pending.state,
                pending.release,
                accepted
            ));
        }
    }

    fn context_id(self: &Arc<Self>, client: &str, path: &str) -> u32 {
        let mut state = self.state.lock().unwrap();
        for (i, ctx) in state.contexts.iter().enumerate() {
            if ctx.destination == client && ctx.path == path {
                return (i + 1) as u32;
            }
        }
        for (i, ctx) in state.contexts.iter_mut().enumerate() {
            if ctx.destination.is_empty() {
                ctx.destination = client.to_string();
                ctx.path = path.to_string();
                ctx.hangul = false;
                let id = (i + 1) as u32;
                drop(state);
                self.enqueue_message(format!(
                    "LOG:context id={} destination={} path={}",
                    id, client, path
                ));
                return id;
            }
        }
        0
    }

    fn parse_preedit(items: &[MessageItem]) -> (String, i32) {
        if items.len() < 2 {
            return (String::new(), 0);
        }
        let cursor = match items.last() {
            Some(MessageItem::Int32(c)) => *c,
            _ => 0,
        };
        let text = match items.first() {
            Some(MessageItem::Array(arr)) => {
                let mut s = String::new();
                for entry in arr.iter() {
                    if let MessageItem::Struct(fields) = entry {
                        if let Some(MessageItem::Str(t)) = fields.first() {
                            s.push_str(t);
                        }
                    }
                }
                s
            }
            _ => String::new(),
        };
        (text, cursor)
    }

    fn handle_message(self: Arc<Self>, msg: Message) {
        let msg_type = msg.msg_type();
        if msg_type == dbus::MessageType::MethodReturn || msg_type == dbus::MessageType::Error {
            self.log_process_key_reply(&msg);
            return;
        }

        if msg.interface().map(|i| i.to_string()).as_deref() != Some(INPUT_CONTEXT_INTERFACE) {
            return;
        }

        let client = if msg_type == dbus::MessageType::Signal {
            busname_str(msg.destination())
        } else {
            busname_str(msg.sender())
        };

        {
            let state = self.state.lock().unwrap();
            if !state.rimworld_destinations.contains(&client) {
                return;
            }
        }

        let path = path_str(msg.path());
        let context = self.context_id(&client, &path);
        if context == 0 {
            return;
        }

        let member = member_str(msg.member());
        let sequence = {
            let mut state = self.state.lock().unwrap();
            state.next_sequence += 1;
            state.next_sequence
        };

        if msg_type == dbus::MessageType::MethodCall {
            match member.as_str() {
                "FocusIn" => {
                    self.enqueue_message(format!("LOG:FocusIn context={}", context));
                    self.enqueue_message(format!(
                        "EVENT:{}:{}:FOCUS:IN",
                        context, sequence
                    ));
                }
                "ProcessKeyEvent" => {
                    self.handle_process_key(&msg, context, sequence);
                }
                _ => {}
            }
            return;
        }

        if msg_type != dbus::MessageType::Signal {
            return;
        }

        match member.as_str() {
            "CurrentIM" => {
                let mut iter = msg.iter_init();
                let name: String = iter.read().unwrap_or_default();
                let unique: String = iter.read().unwrap_or_default();
                let lang: String = iter.read().unwrap_or_default();
                let hangul = unique == "hangul";
                {
                    let mut state = self.state.lock().unwrap();
                    if context > 0 && (context as usize) <= state.contexts.len() {
                        state.contexts[context as usize - 1].hangul = hangul;
                    }
                }
                self.enqueue_message(format!(
                    "LOG:CurrentIM context={} name={} unique={} lang={}",
                    context, name, unique, lang
                ));
                self.enqueue_message(format!(
                    "EVENT:{}:{}:ENGINE:{}",
                    context, sequence, unique
                ));
            }
            "CommitString" => {
                let mut iter = msg.iter_init();
                let text: String = iter.read().unwrap_or_default();
                self.enqueue_message(format!(
                    "LOG:CommitString context={} bytes={} text={}",
                    context,
                    text.len(),
                    text
                ));
                self.enqueue_hex(
                    &format!("EVENT:{}:{}:COMMIT", context, sequence),
                    &text,
                );
            }
            "UpdateFormattedPreedit" => {
                let items = msg.get_items();
                let (text, cursor) = Self::parse_preedit(&items);
                if !text.is_empty() || cursor > 0 {
                    self.enqueue_message(format!(
                        "LOG:Preedit context={} cursor={} bytes={} text={}",
                        context,
                        cursor,
                        text.len(),
                        text
                    ));
                    self.enqueue_hex(
                        &format!("EVENT:{}:{}:PREEDIT:{}", context, sequence, cursor),
                        &text,
                    );
                } else {
                    self.enqueue_message(format!(
                        "ERROR:Preedit context={} parse failed",
                        context
                    ));
                }
            }
            "NotifyFocusOut" => {
                self.enqueue_message(format!("LOG:NotifyFocusOut context={}", context));
                self.enqueue_message(format!(
                    "EVENT:{}:{}:FOCUS:OUT",
                    context, sequence
                ));
            }
            _ => {}
        }
    }

    fn monitor_worker(self: Arc<Self>) {
        let conn = match SyncConnection::new_session() {
            Ok(c) => c,
            Err(e) => {
                self.enqueue_message(format!("ERROR:session bus error={}", e));
                self.finish_worker();
                return;
            }
        };

        let conn = Arc::new(conn);
        let pid = { self.state.lock().unwrap().rimworld_pid };

        let _proxy = conn.with_proxy(
            "org.freedesktop.DBus",
            "/org/freedesktop/DBus",
            Duration::from_secs(1),
        );
        let list_names = Message::new_method_call(
            "org.freedesktop.DBus", "/org/freedesktop/DBus",
            "org.freedesktop.DBus", "ListNames");
        let names_msg = list_names.map_err(|e| dbus::Error::new_failed(&e))
            .and_then(|m| conn.send_with_reply_and_block(m, Duration::from_secs(1)));
        let names: Vec<String> = match names_msg {
            Ok(msg) => {
                let items = msg.get_items();
                items.first()
                    .and_then(|i| if let MessageItem::Array(arr) = i { Some(arr) } else { None })
                    .map(|arr| arr.iter().filter_map(|i| if let MessageItem::Str(s) = i { Some(s.clone()) } else { None }).collect())
                    .unwrap_or_default()
            }
            Err(e) => {
                self.enqueue_message(format!("ERROR:ListNames error={:?}", e));
                self.finish_worker();
                return;
            }
        };

        let mut destinations = Vec::new();
        for name in names {
            if !name.starts_with(':') {
                continue;
            }
            let pid_proxy = conn.with_proxy(
                "org.freedesktop.DBus",
                "/org/freedesktop/DBus",
                Duration::from_secs(1),
            );
            let result: Result<(u32,), _> = pid_proxy.method_call(
                "org.freedesktop.DBus",
                "GetConnectionUnixProcessID",
                (&name,),
            );
            if let Ok((remote_pid,)) = result {
                if remote_pid == pid {
                    self.enqueue_message(format!("LOG:target destination={} pid={}", name, pid));
                    destinations.push(name);
                }
            }
        }
        if destinations.is_empty() {
            self.enqueue_message(format!("ERROR:no D-Bus destination for pid={}", pid));
            self.finish_worker();
            return;
        }
        {
            let mut state = self.state.lock().unwrap();
            state.rimworld_destinations = destinations;
        }

        let _proxy = conn.with_proxy(
            "org.freedesktop.DBus",
            "/org/freedesktop/DBus",
            Duration::from_secs(1),
        );
        let dests = {
            self.state.lock().unwrap().rimworld_destinations.clone()
        };
        let debug = self.debug_logging.load(Ordering::Relaxed);

        let mut rules: Vec<String> = Vec::new();
        for dest in &dests {
            rules.push(format!(
                "type='signal',interface='org.fcitx.Fcitx.InputContext1',destination='{}'",
                dest
            ));
            rules.push(format!(
                "type='method_call',interface='org.fcitx.Fcitx.InputContext1',member='FocusIn',sender='{}'",
                dest
            ));
            rules.push(format!(
                "type='method_call',interface='org.fcitx.Fcitx.InputContext1',member='ProcessKeyEvent',sender='{}'",
                dest
            ));
            if debug {
                rules.push(format!("type='method_return',destination='{}'", dest));
                rules.push(format!("type='error',destination='{}'", dest));
            }
        }
        let become_msg = Message::new_method_call(
            "org.freedesktop.DBus", "/org/freedesktop/DBus",
            "org.freedesktop.DBus.Monitoring", "BecomeMonitor")
            .map(|m| m.append2(rules, 0u32));
        let result = become_msg.map_err(|e| dbus::Error::new_failed(&e))
            .and_then(|m| conn.send_with_reply_and_block(m, Duration::from_secs(1)));
        if let Err(e) = result {
            self.enqueue_message(format!("ERROR:BecomeMonitor error={:?}", e));
            self.finish_worker();
            return;
        }

        self.enqueue_message(format!("READY:{}", pid));

        let conn2 = conn.clone();
        conn2.start_receive(
            MatchRule::new(),
            Box::new({
                let bridge = self.clone();
                let bridge2 = bridge.clone();
                move |msg, _| {
                    bridge2.clone().handle_message(msg);
                    true
                }
            }),
        );

        while self.state.lock().unwrap().running
            && !self.restart_requested.load(Ordering::Relaxed)
        {
            if unsafe { libc::kill(pid as i32, 0) } != 0 {
                break;
            }
            conn2.process(Duration::from_millis(250)).ok();
        }

        self.finish_worker();
    }

    fn finish_worker(self: &Arc<Self>) {
        self.state.lock().unwrap().running = false;
        self.enqueue_message("STOPPED".to_string());
    }

    fn poll(self: &Arc<Self>, buffer: &mut [u8]) -> i32 {
        let mut state = self.state.lock().unwrap();
        match state.queue.pop_front() {
            Some(msg) => {
                let bytes = msg.as_bytes();
                let copy_len = bytes.len().min(buffer.len().saturating_sub(1));
                buffer[..copy_len].copy_from_slice(&bytes[..copy_len]);
                buffer[copy_len] = 0;
                copy_len as i32
            }
            None => 0,
        }
    }
}

static BRIDGE: std::sync::LazyLock<Arc<Bridge>> = std::sync::LazyLock::new(|| Bridge::new());

#[unsafe(no_mangle)]
pub unsafe extern "C" fn fcitx_bridge_set_notify(
    callback: Option<unsafe extern "C" fn(usize)>,
    user_data: usize,
) {
    let cb = callback.map(|f| f as usize).unwrap_or(0);
    *BRIDGE.notify.lock().unwrap() = cb;
    *BRIDGE.notify_data.lock().unwrap() = user_data;
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn fcitx_bridge_set_debug(enabled: i32) {
    let new_val = enabled != 0;
    let old_val = BRIDGE.debug_logging.swap(new_val, Ordering::Relaxed);
    if old_val != new_val && BRIDGE.state.lock().unwrap().running {
        BRIDGE.restart_requested.store(true, Ordering::Relaxed);
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn fcitx_bridge_start(pid: u32) -> i32 {
    let mut state = BRIDGE.state.lock().unwrap();
    if state.running {
        return 0;
    }
    if let Some(handle) = state.worker_handle.take() {
        drop(state);
        handle.join().ok();
        state = BRIDGE.state.lock().unwrap();
    }

    state.rimworld_pid = pid;
    state.next_sequence = 0;
    state.contexts = vec![ContextEntry::default(); MAX_CONTEXTS];
    state.pending_keys = vec![PendingKey::default(); MAX_PENDING_KEYS];
    state.rimworld_destinations.clear();
    BRIDGE.next_pending_key.store(0, Ordering::Relaxed);
    BRIDGE.restart_requested.store(false, Ordering::Relaxed);
    state.running = true;

    let bridge = BRIDGE.clone();
    let handle = thread::spawn(move || {
        bridge.monitor_worker();
    });
    state.worker_handle = Some(handle);
    drop(state);
    0
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn fcitx_bridge_poll(buffer: *mut u8, capacity: i32) -> i32 {
    if buffer.is_null() || capacity <= 1 {
        return -1;
    }
    let buf = unsafe { std::slice::from_raw_parts_mut(buffer, capacity as usize) };
    BRIDGE.poll(buf)
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn fcitx_bridge_stop() {
    let mut state = BRIDGE.state.lock().unwrap();
    state.running = false;
    if let Some(handle) = state.worker_handle.take() {
        drop(state);
        handle.join().ok();
    }
}
