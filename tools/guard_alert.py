#!/usr/bin/env python3
"""guard_alert.py - send a Discord alert when voice_rate_guard blocks a player.

Follows the log files of one or more Puck dedicated servers and looks for
lines that the server role of voice_rate_guard writes, for example:

  [2026-01-01 12:00:00.000] [WARNING] [voice_rate_guard] BLOCKED voice start:
  sampleRate=<value> (allowed 8000-48000) client=5 owner=5
  steamId=7656119... name="..." (+4 more blocked since last line)

Log files: every file that matches --log-glob (default *.log) in
<logs-root>/<server>/Logs/ and in <logs-root>/Logs/. The game writes
Logs/Puck.log by default; other names (--logPath, a log mod) also match.
<server> is the name of the server folder. If that folder has a
server_config.json, the alert also shows the server name from it.

Anti-spam:
  * One alert per (server, Steam ID, kind) when the first block arrives.
  * More blocks from the same player on the same server are counted for
    WINDOW seconds, then sent as ONE follow-up message. If nothing more
    arrives, nothing more is sent.
  * At most HOURLY_CAP messages per hour. Discord rate limits (HTTP 429) are
    respected.
  * Player names cannot ping anyone (allowed_mentions is empty).

Config (JSON, chmod 600), default ~/guard_alert/config.json:
  {"webhook_url": "https://discord.com/api/webhooks/...", "host_label": "host1"}
"host_label" can also be in a one-line file host_label next to config.json.

Start: run it from cron with flock, so only one copy runs:
  * * * * * /usr/bin/flock -n $HOME/guard_alert/run.lock /usr/bin/python3 $HOME/guard_alert/guard_alert.py --logs-root /path/to/servers

Test without Discord:  python3 guard_alert.py --dry-run --from-start --once --logs-root <dir>
"""
import argparse, glob, json, os, re, stat, sys, time, urllib.error, urllib.request
from datetime import datetime, timezone

HOME = os.path.expanduser("~")
DEFAULTS = dict(config=os.path.join(HOME, "guard_alert", "config.json"),
                log=os.path.join(HOME, "guard_alert", "guard_alert.log"))
WINDOW = 600          # seconds: follow-up summary period per player and server
HOURLY_CAP = 20       # messages per hour
POLL = 2.0            # seconds between log reads
RESCAN = 30.0         # seconds between scans for new log files
IDLE = 3600.0         # seconds: a file with no change for this long is read only after a scan sees a change
SKIP_SUFFIXES = (".gz", ".zip", ".xz", ".bz2", ".zst", ".7z")
COLOR = 0xD03B3B
MARK = "[voice_rate_guard] BLOCKED"
# reason=rate alone can come from a long network stall of a normal client, so
# it alerts only after this many blocked packets. Other reasons alert at once.
RATE_ALERT_MIN = 200
# reason=repeat: a normal client sends a few repeats when the player taps the
# talk key. The guard writes at most one line per 10 s, so the count on one
# line is a rate. Alert only when ONE line has this many repeats (about 5 per
# second). A total over the window does not count.
REPEAT_LINE_MIN = 50
# reason=rate-global names the player whose packet was dropped, not the cause.
NO_ALERT_REASONS = {"rate-global"}
DATA_TEXT = {
    "null": "empty (null) voice packets",
    "empty": "zero-length voice packets",
    "size": "oversized voice packets",
    "repeat": "the same voice packet again and again",
    "rate": "voice packets much faster than the game sends them",
}

LOG_PATH = None


def log(msg):
    line = f"{datetime.now(timezone.utc):%Y-%m-%d %H:%M:%S} UTC  {msg}"
    print(line, flush=True)
    if not LOG_PATH:
        return
    try:
        if os.path.exists(LOG_PATH) and os.path.getsize(LOG_PATH) > 1_000_000:
            os.replace(LOG_PATH, LOG_PATH + ".1")
        with open(LOG_PATH, "a", encoding="utf-8") as f:
            f.write(line + "\n")
    except OSError:
        pass


# ---------------------------------------------------------------- parsing
def field(pattern, text, default=None):
    m = re.search(pattern, text)
    return m.group(1) if m else default


def parse_block(line):
    """Return a dict for a BLOCKED line, or None. Each field is optional so a
    small change in the guard's wording does not stop the alert."""
    if MARK not in line:
        return None
    # "(+N more blocked since last line)": a new block plus N earlier ones.
    # "(final count: N more blocked since last line)": the guard writes the
    # pending count after 10 s without a line; it is not a new block.
    final = field(r"\(final count: (\d+) more blocked", line)
    more = field(r"\(\+(\d+) more blocked", line, "0")
    data = "BLOCKED voice data" in line
    reason = field(r"reason=([\w-]+)", line)
    if reason in NO_ALERT_REASONS:
        return None
    detail = reason or field(r"sampleRate=(\d+)", line, "?")
    return {
        # start: invalid sampleRate; churn: too many voice starts; data: voice packets.
        "kind": "data" if data else ("churn" if reason else "start"),
        "time": field(r"^\[([\d\- :.]+)\]", line, ""),
        "rate": detail,
        "allowed": field(r"\(allowed ([\d]+-[\d]+)\)", line, ""),
        "client": field(r"client=(\d+)", line, "?"),
        "steam": field(r"steamId=(\d+)", line, "unknown"),
        "name": field(r'name="(.*?)"(?:\s|$|\()', line, "unknown"),
        "count": int(final) if final else 1 + int(more),
        "raw": line.strip()[:400],
    }


def server_display_name(server_dir):
    try:
        with open(os.path.join(server_dir, "server_config.json"), encoding="utf-8") as f:
            name = json.load(f).get("name", "")
        name = re.sub(r"<[^>]*>", "", name)
        return re.sub(r"\s+", " ", name).strip()
    except (OSError, ValueError, AttributeError):
        return ""


def window_text():
    """The summary period as text, for the alert footer."""
    return f"{WINDOW // 60} min" if WINDOW % 60 == 0 else f"{WINDOW} s"


def esc(text):
    """Escape Discord markdown so a player name cannot format the message."""
    return re.sub(r"([\\*_~`|>#\[\]()])", r"\\\1", str(text))


# ---------------------------------------------------------------- discord
class Discord:
    def __init__(self, url, dry_run):
        self.url, self.dry_run = url, dry_run
        self.sent = []          # wall-clock times of sent messages (hourly cap)
        self.suppressed = 0
        self.not_before = 0.0   # Discord rate limit

    def post(self, embed):
        now = time.time()
        self.sent = [t for t in self.sent if now - t < 3600]
        if len(self.sent) >= HOURLY_CAP:
            self.suppressed += 1
            log(f"hourly cap reached, alert not sent: {embed.get('title')}")
            return False
        if self.suppressed:
            embed.setdefault("fields", []).append(
                {"name": "Not sent", "value": f"{self.suppressed} alert(s) held back by the hourly limit", "inline": False})
        payload = {"embeds": [embed], "allowed_mentions": {"parse": []}}
        if self.dry_run:
            print(json.dumps(payload, indent=2, ensure_ascii=False), flush=True)
            self.sent.append(now); self.suppressed = 0
            return True
        for attempt in range(4):
            wait = self.not_before - time.time()
            if wait > 0:
                time.sleep(min(wait, 60))
            req = urllib.request.Request(self.url, data=json.dumps(payload).encode("utf-8"),
                                         headers={"Content-Type": "application/json",
                                                  "User-Agent": "guard_alert/1.0"})
            try:
                with urllib.request.urlopen(req, timeout=15) as r:
                    r.read()
                self.sent.append(time.time()); self.suppressed = 0
                return True
            except urllib.error.HTTPError as e:
                body = e.read()[:300]
                if e.code == 429:
                    try:
                        retry = float(json.loads(body).get("retry_after", 2))
                    except ValueError:
                        retry = 2.0
                    self.not_before = time.time() + retry + 0.5
                    log(f"discord 429, retry in {retry:.1f} s")
                    continue
                log(f"discord HTTP {e.code}: {body!r}")
                if 400 <= e.code < 500:
                    return False      # bad request or bad URL: retry does not help
            except (urllib.error.URLError, OSError) as e:
                log(f"discord send failed: {e}")
            time.sleep(2 * (attempt + 1))
        return False


# ---------------------------------------------------------------- alerts
class Alerts:
    def __init__(self, discord, host):
        self.discord, self.host = discord, host
        self.open = {}   # (server, steam, kind) -> incident

    def block(self, server, server_dir, b):
        key = (server, b["steam"], b["kind"])
        inc = self.open.get(key)
        if inc is None:
            # sent_at None: no alert sent yet (rate or repeat blocks below their minimum).
            inc = self.open[key] = {"sent_at": None, "seen_at": time.time(), "pending": 0, "first": None,
                                    "last": None, "rates": {}, "info": b, "server_dir": server_dir, "total": 0,
                                    "repeat_line_max": 0}
        if b["rate"] == "repeat":
            inc["repeat_line_max"] = max(inc["repeat_line_max"], b["count"])
        inc["pending"] += b["count"]; inc["total"] += b["count"]
        inc["first"] = inc["first"] or b["time"]; inc["last"] = b["time"]
        inc["rates"][b["rate"]] = inc["rates"].get(b["rate"], 0) + b["count"]; inc["info"] = b
        if inc["sent_at"] is None and self.first_alert_due(b["kind"], inc):
            self.send(server, server_dir, b, inc["pending"], inc["first"], inc["last"], inc["rates"], follow_up=False)
            inc.update(sent_at=time.time(), pending=0, first=None, last=None, rates={})

    @staticmethod
    def first_alert_due(kind, inc):
        # Voice start blocks alert at once: no person presses the talk key that
        # fast. Voice packet rate blocks wait for RATE_ALERT_MIN in total, repeat
        # blocks for REPEAT_LINE_MIN on one line; other packet reasons alert at once.
        if kind in ("start", "churn"):
            return True
        rates = inc["rates"]
        if any(r not in ("rate", "repeat") for r in rates):
            return True
        return rates.get("rate", 0) >= RATE_ALERT_MIN or inc["repeat_line_max"] >= REPEAT_LINE_MIN

    def tick(self):
        now = time.time()
        for key, inc in list(self.open.items()):
            if inc["sent_at"] is None:
                if now - inc["seen_at"] >= WINDOW:
                    del self.open[key]          # stayed below the minimum: no alert
                continue
            if now - inc["sent_at"] < WINDOW:
                continue
            if inc["pending"]:
                self.send(key[0], inc["server_dir"], inc["info"], inc["pending"], inc["first"], inc["last"],
                          inc["rates"], follow_up=True, total=inc["total"])
                inc.update(sent_at=now, pending=0, first=None, last=None, rates={})
            else:
                del self.open[key]

    def send(self, server, server_dir, b, count, first, last, rates, follow_up, total=None):
        disp = server_display_name(server_dir)
        where = f"{self.host.upper()} {server}" + (f"\n{esc(disp)}" if disp else "")
        name = esc(b["name"])
        steam = b["steam"]
        # rates: detail (sampleRate or reason) -> blocked count in this message.
        order = sorted(rates, key=lambda r: (len(r), r))
        rate_txt = ", ".join(order)
        count_txt = ", ".join(f"{r} ({rates[r]})" for r in order)
        allowed = f" (allowed {b['allowed']})" if b["allowed"] else ""
        data = b["kind"] == "data"
        churn = b["kind"] == "churn"
        what = "voice packet(s)" if data else "voice start(s)"
        if follow_up:
            title = ("Voice packet attack continues (blocked)" if data
                     else "Voice start flood continues (blocked)" if churn
                     else "Voice crash attempts continue (blocked)")
            when = f"at {first}" if first == last else f"between {first} and {last}"
            desc = (f"**{name}** made {count} more blocked {what} on **{self.host.upper()} {server}** "
                    f"{when} UTC. Total so far: {total}.")
        elif data:
            kinds = sorted({r.split(":")[0] for r in rates})
            sent = " and ".join(DATA_TEXT.get(k, k) for k in kinds)
            title = "Voice packet attack blocked"
            desc = (f"**{name}** sent {sent} on **{self.host.upper()} {server}**. "
                    f"The game does not send these. The voice_rate_guard mod did not relay them. "
                    f"These packets can freeze other players' games or fill their log files.")
        elif churn:
            title = "Voice start flood blocked"
            desc = (f"**{name}** started voice chat much faster than a person can press the talk key on "
                    f"**{self.host.upper()} {server}**. The voice_rate_guard mod did not relay these starts. "
                    f"They fill the log files of the server and of all players.")
        else:
            title = "Voice crash attempt blocked"
            desc = (f"**{name}** tried to start voice chat with an invalid sample rate on "
                    f"**{self.host.upper()} {server}**. The voice_rate_guard mod blocked it. "
                    f"This value can freeze or crash other players' games.")
        detail = ({"name": "Reason", "value": count_txt, "inline": False} if data or churn
                  else {"name": "Sample rate", "value": f"{rate_txt} Hz{allowed}", "inline": False})
        embed = {
            "title": title, "description": desc, "color": COLOR,
            "fields": [
                {"name": "Account", "value": f"{name}\n[{steam}](https://steamcommunity.com/profiles/{steam})", "inline": True},
                {"name": "Server", "value": where, "inline": True},
                detail,
                {"name": "Blocked " + what.replace("(s)", "s"), "value": str(count), "inline": True},
                {"name": "Time (UTC)", "value": (first if first == last else f"{first} to {last}") or "?", "inline": True},
                {"name": "Client ID", "value": b["client"], "inline": True},
            ],
            "footer": {"text": f"voice_rate_guard - host {self.host}"
                               + (f" - next summary in {window_text()} if it continues" if not follow_up else "")},
            "timestamp": datetime.now(timezone.utc).isoformat(),
        }
        ok = self.discord.post(embed)
        log(f"{'sent' if ok else 'NOT sent'}: {title} | {server} | {steam} | count={count}")


# ---------------------------------------------------------------- tailing
class Tailer:
    """Follows every matching log file of every server.

    A file that exists at start-up is read from its end (only new lines). A
    file that appears later is read from its start. A file that is truncated
    or replaced (the game rewrites Logs/Puck.log at each start) is read again
    from its start."""

    def __init__(self, root, from_start, log_glob="*.log"):
        root = os.path.abspath(root)
        self.patterns = [os.path.join(root, "*", "Logs", log_glob), os.path.join(root, "Logs", log_glob)]
        self.from_start = from_start
        self.files = {}      # path -> {"server", "server_dir", "ino", "pos", "buf", "active"}
        self.first_scan = True

    def scan(self):
        now = time.time()
        seen = set()
        for pattern in self.patterns:
            for path in glob.glob(pattern):
                if path in seen or path.lower().endswith(SKIP_SUFFIXES):
                    continue
                try:
                    st = os.stat(path)
                except OSError:
                    continue
                if not stat.S_ISREG(st.st_mode):
                    continue
                seen.add(path)
                f = self.files.get(path)
                if f is None:
                    server_dir = os.path.dirname(os.path.dirname(path))
                    pos = st.st_size if self.first_scan and not self.from_start else 0
                    if not self.first_scan:
                        log(f"now following {path}")
                    f = self.files[path] = {"server": os.path.basename(server_dir), "server_dir": server_dir,
                                            "ino": st.st_ino, "pos": pos, "buf": b""}
                f["active"] = now - st.st_mtime < IDLE or st.st_size != f["pos"]
        for path in [p for p in self.files if p not in seen]:
            del self.files[path]
        self.first_scan = False

    def servers(self):
        return sorted({f["server"] for f in self.files.values()})

    def read(self):
        for path, f in self.files.items():
            if not f["active"]:
                continue
            try:
                st = os.stat(path)
                if st.st_ino != f["ino"] or st.st_size < f["pos"]:
                    f.update(ino=st.st_ino, pos=0, buf=b"")   # replaced or truncated
                if st.st_size == f["pos"]:
                    continue
                with open(path, "rb") as fh:
                    fh.seek(f["pos"])
                    data = fh.read(4_000_000)
                    f["pos"] = fh.tell()
            except OSError:
                continue
            data = f["buf"] + data
            lines = data.split(b"\n")
            f["buf"] = lines.pop()[-100_000:]
            for raw in lines:
                if MARK.encode() in raw:
                    yield f["server"], f["server_dir"], raw.decode("utf-8", "replace")


def main():
    global LOG_PATH, WINDOW
    for stream in (sys.stdout, sys.stderr):   # cron may give an ASCII console
        try:
            stream.reconfigure(errors="backslashreplace")
        except (AttributeError, ValueError):
            pass
    ap = argparse.ArgumentParser(description="Discord alerts for voice_rate_guard blocks.")
    ap.add_argument("--config", default=DEFAULTS["config"])
    ap.add_argument("--logs-root", default=".",
                    help="folder with one folder per server (<server>/Logs/), or one server folder (default: .)")
    ap.add_argument("--log-glob", default="*.log", help="log file name pattern in each Logs folder (default: *.log)")
    ap.add_argument("--log", default=DEFAULTS["log"], help="this script's own log file")
    ap.add_argument("--dry-run", action="store_true", help="print the Discord payload, send nothing")
    ap.add_argument("--from-start", action="store_true", help="read existing log content (testing)")
    ap.add_argument("--once", action="store_true", help="read once, flush summaries, exit (testing)")
    ap.add_argument("--window", type=int, default=WINDOW, help="summary period in seconds")
    a = ap.parse_args()
    WINDOW = a.window
    LOG_PATH = None if a.dry_run else a.log

    cfg = {}
    if os.path.exists(a.config):
        with open(a.config, encoding="utf-8") as f:
            cfg = json.load(f)
    label_file = os.path.join(os.path.dirname(os.path.abspath(a.config)), "host_label")
    if not cfg.get("host_label") and os.path.exists(label_file):
        with open(label_file, encoding="utf-8") as f:
            cfg["host_label"] = f.read().strip()
    url = cfg.get("webhook_url", "")
    host = cfg.get("host_label") or (os.uname().nodename if hasattr(os, "uname") else "unknown")
    if not a.dry_run and not url.startswith("https://"):
        log(f"no webhook_url in {a.config}; exiting")
        return 1

    discord = Discord(url, a.dry_run)
    alerts = Alerts(discord, host)
    tail = Tailer(a.logs_root, a.from_start, a.log_glob)
    tail.scan()
    log(f"started: host={host} servers={tail.servers()} files={len(tail.files)} window={WINDOW}s dry_run={a.dry_run}")
    last_scan = time.time()
    while True:
        for server, server_dir, line in tail.read():
            b = parse_block(line)
            if b:
                alerts.block(server, server_dir, b)
        if a.once:
            for inc in alerts.open.values():
                if inc["sent_at"] is not None:
                    inc["sent_at"] = 0          # force the summaries out now
            alerts.tick()
            return 0
        alerts.tick()
        if time.time() - last_scan >= RESCAN:
            tail.scan(); last_scan = time.time()
        time.sleep(POLL)


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        sys.exit(0)
    except Exception as e:  # keep a trace in the log; cron restarts the script
        log(f"crashed: {e!r}")
        raise
