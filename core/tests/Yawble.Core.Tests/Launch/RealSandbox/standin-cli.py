#!/usr/bin/python3 -I
# A stand-in for the claude / grok CLI, run by the real launcher INSIDE a real sandbox (RealSandboxLauncherTests).
# It does what a CLI does through the run's gateway (a model call with the placeholder, the bus through the real
# yawble-bridge), then tries the escapes the sandbox must stop, and writes what happened to $HOME/standin-out.json.
# It never reads the content of a file it was able to open: an open that succeeds is the finding.
# The scenario comes from scenario.json next to this file (a read-only toolchain folder), keyed by "team/member" or
# by member name. "busByRun" picks the bus calls by how many times this member has run (its HOME persists), the last
# entry repeating; "sleepSec" is work time before the bus calls. On a terminal (an interactive session on the sandbox's PTY)
# it prints a banner, echoes one line of input and exits.
import json, os, shutil, socket, subprocess, sys, time, urllib.error, urllib.request

HERE = os.path.dirname(os.path.realpath(__file__))
CLI = os.path.basename(sys.argv[0])
HOME = os.environ.get("HOME", "/")
OUT = os.path.join(HOME, "standin-out.json")
member = os.path.basename(os.path.dirname(HOME.rstrip("/")))
team = os.path.basename(os.path.dirname(os.path.dirname(os.path.dirname(HOME.rstrip("/")))))
with open(os.path.join(HERE, "scenario.json")) as f:
    scenarios = json.load(f)
scenario = scenarios.get(team + "/" + member, scenarios.get(member, {}))
if "runs" in scenario:  # one scenario per run of this member (the last repeats); the count persists in its home
    count_file = os.path.join(os.environ.get("HOME", "/"), "standin-runs")
    n = int(open(count_file).read()) if os.path.exists(count_file) else 0
    with open(count_file, "w") as f:
        f.write(str(n + 1))
    scenario = scenario["runs"][min(n, len(scenario["runs"]) - 1)]
out = {"cli": CLI, "member": member, "uid": os.getuid(), "pid": os.getpid(), "argv": sys.argv[1:],
       "proxy": os.environ.get("HTTP_PROXY") or os.environ.get("http_proxy"), "steps": {}}

def save():
    tmp = OUT + ".tmp"
    with open(tmp, "w") as f:
        json.dump(out, f, indent=1)
    os.replace(tmp, OUT)

def step(name, fn):
    try:
        out["steps"][name] = fn()
    except Exception as e:  # a step's failure is data for the test, not a crash
        out["steps"][name] = {"error": f"{type(e).__name__}: {e}"}
    save()

proxy = out["proxy"]
via_gateway = urllib.request.build_opener(urllib.request.ProxyHandler({"http": proxy, "https": proxy}))
direct = urllib.request.build_opener(urllib.request.ProxyHandler({}))

def http(opener, url, token=None, method="GET"):
    req = urllib.request.Request(url, method=method)
    if token:
        req.add_header("Authorization", "Bearer " + token)
    try:
        with opener.open(req, timeout=10) as r:
            return {"status": r.status, "body": r.read(2000).decode("utf-8", "replace")}
    except urllib.error.HTTPError as e:
        return {"status": e.code, "body": e.read(2000).decode("utf-8", "replace")}

def model_call():
    if CLI == "claude":
        return http(via_gateway, "http://model.yawble.internal/v1/models", os.environ["CLAUDE_CODE_OAUTH_TOKEN"])
    # grok login mode: the token comes from GROK_AUTH_PROVIDER_COMMAND (yawble-grok-auth) and grok 1.0.70 writes it
    # to an external-mode auth.json; then the model call with it.
    token = subprocess.run([os.environ["GROK_AUTH_PROVIDER_COMMAND"]], capture_output=True, text=True, timeout=30).stdout.strip()
    os.makedirs(os.environ["GROK_HOME"], exist_ok=True)
    with open(os.path.join(os.environ["GROK_HOME"], "auth.json"), "w") as f:
        json.dump({"key": token, "auth_mode": "external"}, f)
    r = http(via_gateway, "http://grok.yawble.internal/v1/models", token)
    r["tokenIsRunToken"] = token == os.environ.get("YAWBLE_RUN_TOKEN")
    return r

def bus(calls):
    bridge = shutil.which("yawble-bridge")
    p = subprocess.Popen([bridge], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    n = 0
    def rpc(method, params=None):
        nonlocal n
        n += 1
        msg = {"jsonrpc": "2.0", "id": n, "method": method}
        if params is not None:
            msg["params"] = params
        p.stdin.write(json.dumps(msg) + "\n"); p.stdin.flush()
        line = p.stdout.readline()
        if not line:
            raise RuntimeError("yawble-bridge closed stdout: " + p.stderr.read()[-2000:])
        return json.loads(line)
    rpc("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "standin-" + CLI, "version": "0"}})
    p.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n"); p.stdin.flush()
    texts = []
    for tool, args in calls:
        if tool == "sleep":  # holds the sandbox (and its run slot) open between bus calls (the run-limit tests)
            time.sleep(float(args["seconds"])); texts.append({"tool": tool, "isError": False}); continue
        r = rpc("tools/call", {"name": tool, "arguments": args})
        res = r.get("result") or {}
        texts.append({"tool": tool, "text": (res.get("content") or [{}])[0].get("text"), "isError": res.get("isError", False), "error": r.get("error")})
    p.stdin.close(); p.wait(10)
    return texts

def can_open(path):
    try:
        fd = os.open(path, os.O_RDONLY); os.close(fd); return {"exposed": True}
    except OSError as e:
        return {"exposed": False, "errno": e.errno, "error": e.strerror}

def can_list(path):
    try:
        return {"exposed": True, "entries": len(os.listdir(path))}
    except OSError as e:
        return {"exposed": False, "errno": e.errno, "error": e.strerror}

def can_create(path):
    # Left in place: the test then looks for it ON THE HOST. A create that only lands in the sandbox's own tmpfs
    # (bwrap's root, a denied folder's mask) succeeds in here and is no escape; one that reaches the host is.
    try:
        fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600); os.write(fd, b"written from the sandbox"); os.close(fd)
        return {"createdInSandbox": True}
    except OSError as e:
        return {"createdInSandbox": False, "errno": e.errno, "error": e.strerror}

def tcp(host, port):
    t0 = time.monotonic()
    try:
        with socket.create_connection((host, port), timeout=3):
            return {"exposed": True, "ms": int((time.monotonic() - t0) * 1000)}
    except OSError as e:
        return {"exposed": False, "error": f"{type(e).__name__}: {e}", "ms": int((time.monotonic() - t0) * 1000)}

def connect_via_gateway(target):
    host, port = proxy.split("//", 1)[1].rstrip("/").rsplit(":", 1)
    with socket.create_connection((host, int(port)), timeout=10) as s:
        s.sendall(f"CONNECT {target} HTTP/1.1\r\nHost: {target}\r\n\r\n".encode())
        s.settimeout(15)
        return {"statusLine": s.recv(512).decode("ascii", "replace").split("\r\n")[0]}

def guardrails(g):
    r = {}
    for p in g.get("write", []):
        r["write " + p] = can_create(p)
    for p in g.get("read", []):
        r["read " + p] = can_open(p)
    for p in g.get("list", []):
        r["list " + p] = can_list(p)
    for p in g.get("listNames", []):
        try:
            r["names " + p] = {"names": sorted(os.listdir(p))}
        except OSError as e:
            r["names " + p] = {"names": [], "errno": e.errno, "error": e.strerror}
    for hp in g.get("tcp", []):
        h, port = hp.rsplit(":", 1)
        r["tcp " + hp] = tcp(h.strip("[]"), int(port))
    for u in g.get("directHttp", []):
        try:
            r["http " + u] = {"exposed": True, **http(direct, u)}
        except Exception as e:
            r["http " + u] = {"exposed": False, "error": f"{type(e).__name__}: {e}"}
    for t in g.get("connect", []):
        try:
            r["connect " + t] = connect_via_gateway(t)
        except Exception as e:
            r["connect " + t] = {"error": f"{type(e).__name__}: {e}"}
    return r

def write_files(files):
    # The agent's work, written into its cwd (its own clone of the team repository).
    for rel, text in files.items():
        os.makedirs(os.path.dirname(rel) or ".", exist_ok=True)
        with open(rel, "w", newline="\n") as f:
            f.write(text)
    return sorted(files)

def git(commands):
    # Git as the agent runs it, in its cwd with the sandbox's own environment; every result is data.
    r = []
    for args in commands:
        p = subprocess.run(["git"] + args, capture_output=True, text=True, timeout=60, stdin=subprocess.DEVNULL)
        r.append({"args": args, "code": p.returncode, "out": p.stdout.strip(), "err": p.stderr.strip()[-500:]})
    return r


if sys.stdin.isatty():
    print(f"{CLI} stand-in, interactive, for {team}/{member} (uid {os.getuid()})", flush=True)
    sys.stdout.write("> "); sys.stdout.flush()
    line = sys.stdin.readline().strip()
    print(f"you said: {line}", flush=True)
    sys.exit(0)

runs_file = os.path.join(HOME, ".standin-runs")
try:
    with open(runs_file) as f:
        run_index = int(f.read().strip() or "0")
except OSError:
    run_index = 0
with open(runs_file, "w") as f:
    f.write(str(run_index + 1))
out["run"] = run_index
save()
print(f"{CLI} stand-in for {member}: {scenario.get('say', 'nothing to do')}", flush=True)
if scenario.get("model"):
    step("model", model_call)
if scenario.get("guardrails"):
    step("guardrails", lambda: guardrails(scenario["guardrails"]))
if scenario.get("sleepSec"):
    time.sleep(scenario["sleepSec"])
if scenario.get("files"):
    step("files", lambda: write_files(scenario["files"]))
if scenario.get("git"):
    step("git", lambda: git(scenario["git"]))
calls = scenario.get("bus")
if scenario.get("busByRun"):
    calls = scenario["busByRun"][min(run_index, len(scenario["busByRun"]) - 1)]
if calls:
    step("bus", lambda: bus([(c["tool"], c["args"]) for c in calls]))
if scenario.get("tree"):
    marker = scenario["tree"]
    # A detached grandchild in its own session (what an agent's `setsid ... &` does) and an ordinary child.
    subprocess.Popen(["setsid", "/usr/bin/python3", "-I", "-c", "import time; time.sleep(100000)", "detached-" + marker],
                     stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, start_new_session=True)
    subprocess.Popen(["/usr/bin/python3", "-I", "-c", "import time; time.sleep(100000)", "child-" + marker],
                     stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    out["tree"] = "started"; save()
    print("tree started", flush=True)
    while True:
        time.sleep(1)
out["done"] = True
save()
print("DONE", flush=True)
