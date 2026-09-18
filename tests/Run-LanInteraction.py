"""Two-desktop regression using the user-authorized newline-JSON maintenance channel.
Credentials are environment variables; test artifacts contain no credentials.
"""
import argparse
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import time
import uuid


def quote(value):
    return "'" + str(value).replace("'", "''") + "'"


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", required=True)
    parser.add_argument("--maintenance-port", type=int, default=17246)
    parser.add_argument("--port", type=int, default=17248)
    parser.add_argument("--remote-directory", required=True)
    parser.add_argument("--local-directory", required=True)
    parser.add_argument("--preset", default="h264_nvenc_swdec")
    parser.add_argument("--output", required=True)
    parser.add_argument("--input", action="store_true", help="Explicitly enable automated keyboard/mouse injection; off by default")
    args = parser.parse_args()
    maintenance_token = os.environ["FRD_TCS_TOKEN"]
    session_token = os.environ["FRD_SESSION_TOKEN"]
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    local = Path(args.local_directory).resolve()
    remote = args.remote_directory.rstrip("/\\").replace("\\", "/")
    remote_fixture = remote + "/interaction-" + uuid.uuid4().hex[:8]
    local_fixture = output / "fixture"
    local_fixture.mkdir()
    results = []
    controller = fixture = None
    remote_host_pid = None
    remote_fixture_started = False

    def remote_command(script, timeout=30):
        request = {"session_id": uuid.uuid4().hex, "token": maintenance_token,
                   "command": ["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", '$ErrorActionPreference="Stop"; ' + script], "timeout": timeout}
        with socket.create_connection((args.host, args.maintenance_port), timeout=10) as client:
            client.settimeout(timeout + 10)
            client.sendall((json.dumps(request) + "\n").encode("utf-8"))
            response = json.loads(client.makefile("rb").readline())
        if response.get("session_id") != request["session_id"] or not response.get("ok") or response.get("returncode") != 0:
            raise RuntimeError("Maintenance command failed: " + response.get("stderr", ""))
        return response["stdout"].strip()

    def remote_launch(exe, arguments, hidden=False):
        script = "$s=[Diagnostics.ProcessStartInfo]::new(); "
        script += f"$s.FileName={quote(exe)}; $s.WorkingDirectory={quote(remote)}; $s.Arguments={quote(arguments)}; "
        script += "$s.UseShellExecute=$true; $s.WindowStyle=[Diagnostics.ProcessWindowStyle]::" + ("Hidden" if hidden else "Normal") + "; "
        return int(remote_command(script + "$p=[Diagnostics.Process]::Start($s); $p.Id; [Environment]::Exit(0)"))

    def remote_state():
        text = remote_command(f"if(Test-Path -LiteralPath {quote(remote_fixture + '/state.json')}){{Get-Content -LiteralPath {quote(remote_fixture + '/state.json')} -Raw -Encoding UTF8}}")
        return json.loads(text) if text else None

    def local_state():
        path = local_fixture / "state.json"
        return json.loads(path.read_text(encoding="utf-8-sig")) if path.exists() else None

    def wait(read, predicate, seconds=12):
        start = time.monotonic()
        while time.monotonic() - start < seconds:
            value = read()
            if value is not None and predicate(value):
                return value
            time.sleep(.1)
        raise TimeoutError("Regression condition not reached")

    def command(side, kind, **values):
        item = {"id": uuid.uuid4().hex, "kind": kind, **values}
        if side == "remote":
            text = json.dumps(item, ensure_ascii=True)
            remote_command(f"[IO.File]::WriteAllText({quote(remote_fixture + '/command.json')},{quote(text)},[Text.Encoding]::UTF8)")
            read = remote_state
        else:
            path = local_fixture / "command.json"
            path.with_suffix(".tmp").write_text(json.dumps(item), encoding="utf-8")
            path.with_suffix(".tmp").replace(path)
            read = local_state
        state = wait(read, lambda s: s["lastCommand"] == item["id"])
        if state["commandError"]:
            raise RuntimeError(state["commandError"])
        return state

    def check(value, name, **details):
        results.append({"name": name, "passed": bool(value), **details})
        if not value:
            raise AssertionError(name)
        print("PASS " + name, flush=True)

    try:
        remote_command(f"$p={quote(remote + '/codec-config.json')}; $c=Get-Content -LiteralPath $p -Raw -Encoding UTF8 | ConvertFrom-Json; "
                       f"$c.initialPreset={quote(args.preset)}; $c.transmissionScale=1; [IO.File]::WriteAllText($p,($c | ConvertTo-Json -Depth 20),[Text.Encoding]::UTF8)")
        print("Remote native clipboard short regression", flush=True)
        native_report = remote + "/native-clipboard-lan.json"
        remote_command(f"& {quote(remote + '/InteractionTests.exe')} --clipboard {quote(native_report)}; if($LASTEXITCODE -ne 0){{throw 'Remote native clipboard regression failed'}}", 25)
        result = json.loads(remote_command(f"Get-Content -LiteralPath {quote(native_report)} -Raw -Encoding UTF8"))
        check(result["passed"], "Remote Windows clipboard read/write and restore")
        remote_host_pid = remote_launch(remote + "/FRD.exe", f'--host --listen {args.host} --port {args.port} --token {session_token}')
        fixture_mode = "--fixture" if args.input else "--clipboard-fixture"
        remote_launch(remote + "/InteractionTests.exe", f'{fixture_mode} "{remote_fixture}"', hidden=True)
        remote_fixture_started = True
        fixture = subprocess.Popen([str(local / "InteractionTests.exe"), fixture_mode, str(local_fixture)], creationflags=subprocess.CREATE_NO_WINDOW)
        state = wait(remote_state, lambda s: s.get("ready"))
        wait(local_state, lambda s: s.get("ready"))
        if args.input:
            check(state.get("visible") and state.get("targetReady"), "Remote fixture is visible and owns target coordinates before input")
        command("local", "beginClipboard")
        command("remote", "beginClipboard")
        script = {key: state[key] for key in ("sourceWidth", "sourceHeight", "left", "top", "width", "height")}
        script.update(report=str(output / "controller.json"), holdUntilFile=str(output / "finish"), clipboard=True, input=args.input)
        (output / "script.json").write_text(json.dumps(script), encoding="utf-8")
        with (output / "controller.err.log").open("w", encoding="utf-8") as errors, (output / "controller.out.log").open("w", encoding="utf-8") as logs:
            controller = subprocess.Popen([str(local / "FRD.exe"), "--connect", args.host, "--port", str(args.port), "--token", session_token,
                "--interaction-script", str(output / "script.json"), "--report", str(output / "ui.json")], cwd=local, stderr=errors, stdout=logs)
        def read_controller():
            report = output / "controller.json"
            if controller.poll() is not None and not report.exists():
                raise RuntimeError("Controller exited before interaction report")
            return json.loads(report.read_text(encoding="utf-8-sig")) if report.exists() else None
        result = wait(read_controller, lambda s: True, 40)
        check(result["passed"], "Physical LAN bitrate and transmission scale controls")
        target = remote_state()
        (output / "target-input.json").write_text(json.dumps(target), encoding="utf-8")
        verification = Path(__file__).with_name("Verify-Interaction.ps1")
        if args.input:
            subprocess.run(["powershell.exe", "-NoProfile", "-File", str(verification), "-ControllerReport", str(output / "controller.json"),
                "-FixtureReport", str(output / "target-input.json"), "-OutputReport", str(output / "input-verified.json")], check=True)
            check(True, "Remote target: nine actual clicks, key pairs and wheel events; no local focus assist")
        else:
            check(not result.get("inputTrace"), "Keyboard/mouse injection disabled")
        for source, destination in (("local", "remote"), ("remote", "local")):
            text = f"FRD {source} to {destination} 中文🙂\r\nline two\t{uuid.uuid4().hex}"
            command(source, "writeText", text=text)
            received = wait(lambda: command(destination, "readClipboard")["clipboard"], lambda c: c.get("text") == text)
            check(received["text"] == text, source + " → " + destination + " Unicode multiline clipboard text")
            command(source, "writeFiles")
            original = command(source, "readClipboard")["clipboard"]
            received = wait(lambda: command(destination, "readClipboard")["clipboard"], lambda c: c.get("digests") == original["digests"])
            check(received["digests"] == original["digests"] and len(received["paths"]) == 3,
                  source + " → " + destination + " files, duplicate names, nested and empty directories, SHA256")
            check(received["paths"] != original["paths"], source + " → " + destination + " receiver publishes its own staged paths")
            paste = command(destination, "pasteFiles", digests=original["digests"])["clipboard"]
            check(paste["passed"] and paste["filesReadable"] and paste["replacement"] and paste["directoryMerge"],
                  source + " → " + destination + " Windows Shell copy, replace, merge and reopen validation")
        (output / "finish").touch()
        controller.wait(timeout=20)
        ui = json.loads((output / "ui.json").read_text(encoding="utf-8-sig"))
        check(controller.returncode == 0 and ui["Passed"], "Physical LAN real GPU presentation and clean shutdown", receivedFrames=ui["ReceivedFrames"], presentedFrames=ui["GpuConfirmedFrames"])
    finally:
        (output / "finish").touch()
        if controller is not None and controller.poll() is None:
            try:
                controller.wait(timeout=20)
            except subprocess.TimeoutExpired:
                controller.terminate()
                controller.wait(timeout=5)
                print("Controller forced to close after timeout", flush=True)
        # Stop synchronization BEFORE restoring original clipboards, so backups cannot be forwarded.
        cleanup_errors = []
        try:
            if fixture is not None:
                (local_fixture / "stop").touch()
                fixture.wait(timeout=8)
                closed = json.loads((local_fixture / "closed.json").read_text(encoding="utf-8-sig"))
                if not closed.get("ClipboardRestored"):
                    raise RuntimeError("Local clipboard restoration not confirmed")
        except Exception as error:
            print("Local fixture cleanup failed: " + str(error), file=sys.stderr)
            cleanup_errors.append(error)
        try:
            if remote_fixture_started:
                remote_command(f"[IO.File]::WriteAllText({quote(remote_fixture + '/stop')},'done')")
                closed = remote_command(f"$p={quote(remote_fixture + '/closed.json')}; for($i=0;$i -lt 40 -and -not(Test-Path -LiteralPath $p);$i++){{Start-Sleep -Milliseconds 100}}; if(Test-Path -LiteralPath $p){{Get-Content -LiteralPath $p -Raw -Encoding UTF8}}")
                if not closed or not json.loads(closed).get("ClipboardRestored"):
                    raise RuntimeError("Remote clipboard restoration not confirmed")
        except Exception as error:
            print("Remote fixture cleanup failed: " + str(error), file=sys.stderr)
            cleanup_errors.append(error)
        try:
            if remote_host_pid is not None:
                remote_command(f"$p=Get-Process -Id {remote_host_pid} -ErrorAction SilentlyContinue; if($p){{[void]$p.CloseMainWindow(); if(-not $p.WaitForExit(8000)){{throw 'Regression host did not close'}}}}")
        except Exception as error:
            print("Remote host cleanup failed: " + str(error), file=sys.stderr)
            cleanup_errors.append(error)
        if cleanup_errors:
            raise ExceptionGroup("Regression cleanup failed", cleanup_errors)
    check(True, "Both original clipboards restored and all regression processes closed")
    (output / "verified.json").write_text(json.dumps({"passed": True, "checks": results}, ensure_ascii=False, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
