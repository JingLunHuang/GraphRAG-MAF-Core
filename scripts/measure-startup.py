"""Measure fresh-process readiness and RSS on Linux; fixtures exclude model load."""
import argparse
import json
import os
from pathlib import Path
import socket
import statistics
import subprocess
import time
import urllib.request

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument("--runtime-root", type=Path, default=root)
parser.add_argument("--output", default="artifacts/startup-benchmark.json")
args = parser.parse_args()
runtime_root = args.runtime_root.resolve()
opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
commands = {
    "native-aot": [str(runtime_root / "artifacts/native/linux-x64/GraphRag.Api")],
    "jit": [str(runtime_root / ".runtime/dotnet-linux/dotnet"), str(runtime_root / "artifacts/jit/linux-x64/GraphRag.Api.dll")],
}
env = dict(os.environ, AI_PROVIDER="fixture", EMBEDDING_PROVIDER="fixture", GRAPH_STORE="memory")
runs = []
for scenario, command in commands.items():
    for iteration in range(5):
        with socket.socket() as probe:
            probe.bind(("127.0.0.1", 0))
            port = probe.getsockname()[1]
        env["ASPNETCORE_URLS"] = f"http://127.0.0.1:{port}"
        start = time.perf_counter()
        process = subprocess.Popen(command, env=env, cwd=runtime_root, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        try:
            deadline = start + 30
            while True:
                if process.poll() is not None:
                    raise RuntimeError(f"{scenario} exited before readiness")
                try:
                    with opener.open(f"http://127.0.0.1:{port}/health/ready", timeout=0.3) as response:
                        health = json.load(response)
                    if health["status"] == "ready":
                        break
                except OSError:
                    if time.perf_counter() > deadline:
                        raise TimeoutError(f"{scenario} readiness timed out")
                    time.sleep(0.002)
            elapsed = (time.perf_counter() - start) * 1000
            status = Path(f"/proc/{process.pid}/status").read_text()
            rss_kib = int(next(line.split()[1] for line in status.splitlines() if line.startswith("VmRSS:")))
            if health["nativeAot"] != (scenario == "native-aot"):
                raise RuntimeError("Health nativeAot flag does not match the process")
            runs.append({"scenario": scenario, "iteration": iteration + 1, "readinessMs": elapsed, "rssMiB": rss_kib / 1024})
        finally:
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()
report = {
    "artifactType": "measured-fresh-process-startup",
    "environment": "Ubuntu 24.04 / WSL2; warm filesystem cache; fixture provider; no model weights loaded",
    "runtimeFilesystem": "Windows mount (NTFS)" if str(runtime_root).startswith("/mnt/") else "Linux filesystem",
    "measurement": "parent process launch to HTTP readiness; RSS at readiness",
    "runs": runs,
    "medians": [{"scenario": scenario,
        "readinessMs": statistics.median(row["readinessMs"] for row in runs if row["scenario"] == scenario),
        "rssMiB": statistics.median(row["rssMiB"] for row in runs if row["scenario"] == scenario)} for scenario in commands],
}
output = root / args.output
output.parent.mkdir(exist_ok=True)
output.write_text(json.dumps(report, indent=2), encoding="utf-8")
print(json.dumps(report, indent=2))
