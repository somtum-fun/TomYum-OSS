#!/usr/bin/env python3
"""TomYum pp library updater: runs on the host (systemd, outside Docker), next to the compose stack.

The admin panel shows which pp libraries the server runs and whether there's a newer one; updating
one rebuilds the containers that use it and then starts a pp rework. Containers never get control of
Docker: they ask through Redis, and this does only these fixed jobs.

  vanilla   ppy's pp (ppy.osu.Game.Rulesets.*), in TomYum (stable) and ppy's score processor
            (lazer, the website). They must match, so the version is the one the processor's newest
            commit uses; updating moves both, then reworks the vanilla modes.
  akatsuki  akatsuki-pp-rs (Relax/Autopilot) in the pp-sidecar; updating takes its newest commit,
            then reworks Relax and Autopilot.

Redis (the stack's, through `docker compose exec redis redis-cli`):
  tomyum:pp-libraries         JSON  what's running and what's newest (checked every 6 hours, or when asked)
  tomyum:pp-library-update    JSON  the update running or last run: library, state, steps, error
  tomyum:pp-library-requests  list  TomYum pushes {"action": "check"} or {"action": "update", "library", "actorId"}
  tomyum:pp-rework-requests   list  this pushes the rework to start once an update is live

Settings (environment): TOMYUM_DIR (the TomYum checkout; default the one this script is in), COMPOSE_DIR
(default TOMYUM_DIR/deploy/production), RUN_AS (default the user owning the checkout; builds and commits
run as them), CHECK_HOURS (6), CONTAINER_CLI (docker; "podman" on a podman host).
"""

from __future__ import annotations

import datetime as dt
import json
import os
import re
import subprocess
import sys
import time
import traceback
import urllib.request
from pathlib import Path

# Defaults: the checkout this script is in (deploy/pp-updater/updater.py), and the user who owns it.
TOMYUM_DIR = Path(os.environ.get("TOMYUM_DIR", str(Path(__file__).resolve().parents[2])))
COMPOSE_DIR = Path(os.environ.get("COMPOSE_DIR", str(TOMYUM_DIR / "deploy" / "production")))
RUN_AS = os.environ.get("RUN_AS") or __import__("pwd").getpwuid(TOMYUM_DIR.stat().st_uid).pw_name
CHECK_HOURS = float(os.environ.get("CHECK_HOURS", "6"))
# The container command: docker, or podman (which has `podman compose` and the same run options).
CONTAINER_CLI = os.environ.get("CONTAINER_CLI", "docker").split()

PERFORMANCE_CSPROJ = TOMYUM_DIR / "src" / "TomYum.Performance" / "TomYum.Performance.csproj"
PROCESSOR_DOCKERFILE = TOMYUM_DIR / "deploy" / "score-processor" / "Dockerfile"
SIDECAR_DIR = TOMYUM_DIR / "sidecars" / "pp-sidecar"
CARGO_LOCK = SIDECAR_DIR / "Cargo.lock"

PROCESSOR_REPO = "https://github.com/ppy/osu-queue-score-statistics"
PROCESSOR_CSPROJ = "osu.Server.Queues.ScoreStatisticsProcessor/osu.Server.Queues.ScoreStatisticsProcessor.csproj"
AKATSUKI_REPO = "https://github.com/osuAkatsuki/akatsuki-pp-rs"
NUGET_INDEX = "https://api.nuget.org/v3-flatcontainer/ppy.osu.game.rulesets.osu/index.json"

STATUS_KEY = "tomyum:pp-libraries"
JOB_KEY = "tomyum:pp-library-update"
REQUEST_KEY = "tomyum:pp-library-requests"
REWORK_KEY = "tomyum:pp-rework-requests"

VANILLA_MODES = [0, 1, 2, 3]
AKATSUKI_MODES = [4, 5, 6, 8]


def now() -> str:
    return dt.datetime.now(dt.timezone.utc).isoformat(timespec="seconds")


def log(message: str) -> None:
    print(f"{now()} {message}", flush=True)


def run(args: list[str], cwd: Path | None = None, as_user: bool = False, timeout: int = 3600) -> str:
    """Runs a command (as RUN_AS when asked), returning its output; raises with the tail of it on failure."""
    if as_user and os.geteuid() == 0:
        args = ["runuser", "-u", RUN_AS, "--", *args]
    result = subprocess.run(args, cwd=cwd, capture_output=True, text=True, timeout=timeout)
    if result.returncode != 0:
        tail = (result.stdout + result.stderr).strip().splitlines()[-15:]
        raise RuntimeError(f"`{' '.join(args[-6:])}` failed:\n" + "\n".join(tail))
    return result.stdout


def compose(*args: str, timeout: int = 3600) -> str:
    return run([*CONTAINER_CLI, "compose", *args], cwd=COMPOSE_DIR, timeout=timeout)


def redis(*args: str) -> str:
    return compose("exec", "-T", "redis", "redis-cli", "--raw", *args, timeout=60).strip()


def fetch(url: str) -> str:
    request = urllib.request.Request(url, headers={"User-Agent": "tomyum-pp-updater"})
    with urllib.request.urlopen(request, timeout=30) as response:
        return response.read().decode()


def head_commit(repo: str, branch: str) -> str:
    output = run(["git", "ls-remote", repo, f"refs/heads/{branch}"], timeout=60)
    return output.split()[0]


# What's running ---------------------------------------------------------------------------------

def current_vanilla() -> tuple[str, str]:
    """(ppy version in TomYum, the processor's commit)."""
    version = re.search(r'ppy\.osu\.Game\.Rulesets\.Osu" Version="([^"]+)"', PERFORMANCE_CSPROJ.read_text()).group(1)
    ref = re.search(r"^ARG PROCESSOR_REF=(\S+)", PROCESSOR_DOCKERFILE.read_text(), re.M).group(1)
    return version, ref


def current_akatsuki() -> tuple[str, str]:
    """(crate version, commit) from the sidecar's Cargo.lock."""
    lock = CARGO_LOCK.read_text()
    block = re.search(r'name = "akatsuki-pp"\nversion = "([^"]+)"\nsource = "[^"]*#([0-9a-f]+)"', lock)
    return block.group(1), block.group(2)


def processor_ppy_version(ref: str) -> str:
    csproj = fetch(f"https://raw.githubusercontent.com/ppy/osu-queue-score-statistics/{ref}/{PROCESSOR_CSPROJ}")
    return re.search(r'"ppy\.osu\.Game" Version="([^"]+)"', csproj).group(1)


def check() -> dict:
    """What's running and what's newest; written to Redis for the admin panel."""
    vanilla, processor_ref = current_vanilla()
    ak_version, ak_commit = current_akatsuki()
    status: dict = {"checkedAt": now()}

    try:
        latest_ref = head_commit(PROCESSOR_REPO, "master")
        latest_vanilla = processor_ppy_version(latest_ref)
        try:
            osu_release = json.loads(fetch(NUGET_INDEX))["versions"][-1]
        except Exception:  # noqa: BLE001 - only shown as a hint
            osu_release = None
        status["vanilla"] = {
            "library": "ppy.osu.Game.Rulesets (osu!'s own pp)",
            "current": vanilla, "processorRef": processor_ref,
            "latest": latest_vanilla, "latestProcessorRef": latest_ref,
            "osuRelease": osu_release,
            "available": latest_vanilla != vanilla or latest_ref != processor_ref,
            "modes": VANILLA_MODES,
        }
    except Exception as error:  # noqa: BLE001
        status["vanilla"] = {"library": "ppy.osu.Game.Rulesets (osu!'s own pp)", "current": vanilla,
                             "processorRef": processor_ref, "error": str(error), "available": False, "modes": VANILLA_MODES}

    try:
        latest_commit = head_commit(AKATSUKI_REPO, "main")
        latest_version = re.search(r'^version = "([^"]+)"', fetch(f"https://raw.githubusercontent.com/osuAkatsuki/akatsuki-pp-rs/{latest_commit}/Cargo.toml"), re.M).group(1)
        status["akatsuki"] = {
            "library": "akatsuki-pp-rs (Relax/Autopilot pp)",
            "current": ak_version, "commit": ak_commit,
            "latest": latest_version, "latestCommit": latest_commit,
            "available": latest_commit != ak_commit,
            "modes": AKATSUKI_MODES,
        }
    except Exception as error:  # noqa: BLE001
        status["akatsuki"] = {"library": "akatsuki-pp-rs (Relax/Autopilot pp)", "current": ak_version,
                              "commit": ak_commit, "error": str(error), "available": False, "modes": AKATSUKI_MODES}

    redis("SET", STATUS_KEY, json.dumps(status))
    log(f"checked: vanilla {vanilla} (latest {status['vanilla'].get('latest')}), akatsuki {ak_commit[:7]} (latest {status['akatsuki'].get('latestCommit', '?')[:7]})")
    return status


# Updating ---------------------------------------------------------------------------------------

class Job:
    def __init__(self, library: str, actor_id: int):
        self.data = {"library": library, "actorId": actor_id, "state": "running", "startedAt": now(), "steps": [], "error": None}
        self.save()

    def step(self, text: str) -> None:
        log(f"[{self.data['library']}] {text}")
        self.data["steps"].append({"at": now(), "text": text})
        self.save()

    def finish(self, state: str, error: str | None = None) -> None:
        self.data.update(state=state, error=error, finishedAt=now())
        self.save()

    def save(self) -> None:
        redis("SET", JOB_KEY, json.dumps(self.data))


def commit(paths: list[Path], message: str) -> None:
    """Commits the pins (locally; pushing is the owner's call). Nothing to commit when they match what's committed."""
    files = [str(p.relative_to(TOMYUM_DIR)) for p in paths]
    if not run(["git", "status", "--porcelain", "--", *files], cwd=TOMYUM_DIR, as_user=True).strip():
        return
    run(["git", "add", *files], cwd=TOMYUM_DIR, as_user=True)
    run(["git", "-c", "user.name=TomYum pp updater", "-c", "user.email=pp-updater@localhost",
         "commit", "-m", message, "--", *files], cwd=TOMYUM_DIR, as_user=True)


def try_commit(job: "Job", paths: list[Path], message: str) -> None:
    """The commit is bookkeeping: if it fails, the update still stands and the rework still runs."""
    try:
        commit(paths, message)
    except Exception as error:  # noqa: BLE001
        job.step(f"(Couldn't commit the new pin in git; do it by hand: {str(error).splitlines()[0]})")


def request_rework(library: str, modes: list[int], from_version: str, to_version: str, actor_id: int) -> None:
    redis("LPUSH", REWORK_KEY, json.dumps({
        "library": library, "modes": modes, "fromVersion": from_version, "toVersion": to_version,
        "reason": f"pp library updated: {to_version}", "actorId": actor_id,
    }))


def sidecar_version() -> dict:
    output = run([*CONTAINER_CLI, "run", "--rm", "--network", "tomyum_default", "curlimages/curl", "-s", "http://pp-sidecar:9000/version"], timeout=120)
    return json.loads(output)


def update_akatsuki(job: Job) -> None:
    version, commit_before = current_akatsuki()
    latest = head_commit(AKATSUKI_REPO, "main")
    if latest == commit_before:
        job.step("Already on the newest akatsuki-pp-rs.")
        job.finish("done")
        return

    job.step(f"Updating akatsuki-pp-rs {commit_before[:7]} → {latest[:7]} in Cargo.lock")
    uid = run(["id", "-u", RUN_AS]).strip()
    gid = run(["id", "-g", RUN_AS]).strip()
    try:
        run([*CONTAINER_CLI, "run", "--rm", "-u", f"{uid}:{gid}", "-e", "CARGO_HOME=/tmp/cargo", "-v", f"{SIDECAR_DIR}:/app", "-w", "/app",
             "rust:1-slim-bookworm", "cargo", "update", "-p", "akatsuki-pp"], timeout=1800)
        new_version, new_commit = current_akatsuki()
        job.step("Building the pp-sidecar")
        compose("build", "pp-sidecar")
    except Exception:
        run(["git", "checkout", "--", str(CARGO_LOCK.relative_to(TOMYUM_DIR))], cwd=TOMYUM_DIR, as_user=True)
        raise

    job.step("Restarting the pp-sidecar")
    compose("up", "-d", "pp-sidecar")
    for _ in range(30):
        time.sleep(2)
        try:
            if sidecar_version().get("commit") == new_commit:
                break
        except Exception:  # noqa: BLE001 - still starting
            pass
    else:
        raise RuntimeError("The new pp-sidecar didn't come up with the new version.")

    try_commit(job, [CARGO_LOCK], f"pp library: akatsuki-pp-rs {version} ({commit_before[:7]}) → {new_version} ({new_commit[:7]})")
    job.step("Live. Starting the Relax/Autopilot pp rework")
    request_rework("akatsuki", AKATSUKI_MODES, f"akatsuki-pp-rs {version} ({commit_before[:7]})", f"akatsuki-pp-rs {new_version} ({new_commit[:7]})", job.data["actorId"])
    job.finish("done")


def update_vanilla(job: Job) -> None:
    version, ref = current_vanilla()
    latest_ref = head_commit(PROCESSOR_REPO, "master")
    latest = processor_ppy_version(latest_ref)
    if latest == version and latest_ref == ref:
        job.step("Already on the newest pp (ppy's score processor and TomYum).")
        job.finish("done")
        return

    job.step(f"Moving ppy's pp {version} → {latest} (score processor {ref[:7]} → {latest_ref[:7]})")
    csproj_before, dockerfile_before = PERFORMANCE_CSPROJ.read_text(), PROCESSOR_DOCKERFILE.read_text()
    try:
        PERFORMANCE_CSPROJ.write_text(re.sub(r'(ppy\.osu\.Game\.Rulesets\.\w+" Version=")[^"]+(")', rf"\g<1>{latest}\g<2>", csproj_before))
        PROCESSOR_DOCKERFILE.write_text(re.sub(r"^ARG PROCESSOR_REF=\S+", f"ARG PROCESSOR_REF={latest_ref}", dockerfile_before, flags=re.M))
        job.step("Building TomYum (checking it compiles with the new pp)")
        run(["dotnet", "build", "-c", "Release", "src/TomYum.Server"], cwd=TOMYUM_DIR, as_user=True, timeout=1800)
        job.step("Building the server and the score processor images")
        compose("build", "server", "score-processor")
    except Exception:
        PERFORMANCE_CSPROJ.write_text(csproj_before)
        PROCESSOR_DOCKERFILE.write_text(dockerfile_before)
        raise

    job.step("Restarting TomYum and the score processor; updating the website's difficulty service")
    compose("up", "-d", "server", "score-processor")
    try:
        compose("pull", "beatmap-difficulty-lookup-cache")
        compose("up", "-d", "beatmap-difficulty-lookup-cache")
    except Exception as error:  # noqa: BLE001 - star ratings on the website only
        job.step(f"(The website's difficulty service couldn't be updated: {error})")
    time.sleep(20)

    try_commit(job, [PERFORMANCE_CSPROJ, PROCESSOR_DOCKERFILE], f"pp library: ppy {version} → {latest} (score processor {latest_ref[:7]})")
    job.step("Live. Starting the vanilla pp rework")
    request_rework("vanilla", VANILLA_MODES, f"ppy {version}", f"ppy {latest}", job.data["actorId"])
    job.finish("done")


def handle(request: dict) -> None:
    if request.get("action") == "check":
        check()
        return
    if request.get("action") != "update" or request.get("library") not in ("vanilla", "akatsuki"):
        log(f"ignored request: {request}")
        return

    job = Job(request["library"], int(request.get("actorId") or 0))
    try:
        (update_vanilla if request["library"] == "vanilla" else update_akatsuki)(job)
    except Exception as error:  # noqa: BLE001
        traceback.print_exc()
        job.finish("failed", str(error)[-1500:])
    finally:
        try:
            check()
        except Exception:  # noqa: BLE001
            traceback.print_exc()


def main() -> None:
    log(f"pp updater for {TOMYUM_DIR} (compose in {COMPOSE_DIR})")
    next_check = 0.0
    while True:
        try:
            if time.time() >= next_check:
                check()
                next_check = time.time() + CHECK_HOURS * 3600
            raw = redis("RPOP", REQUEST_KEY)
            if raw:
                handle(json.loads(raw))
                continue
        except Exception:  # noqa: BLE001 - keep running; tried again shortly
            traceback.print_exc()
            next_check = min(next_check, time.time() + 300)
        time.sleep(5)


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "check":
        print(json.dumps(check(), indent=2))
    else:
        main()
