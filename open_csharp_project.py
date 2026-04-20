#!/usr/bin/env python3

"""One-click launcher for running or opening C# projects.

Usage examples:
  python open_csharp_project.py
    python open_csharp_project.py --action open --ide vscode
    python open_csharp_project.py --target runtime --app-args --demo on
"""

from __future__ import annotations

import argparse
import os
import shutil
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Callable


@dataclass(frozen=True)
class TargetSpec:
    path: Path
    run_project: Path | None = None


def repo_root() -> Path:
    return Path(__file__).resolve().parent


def project_targets(root: Path) -> dict[str, TargetSpec]:
    three_d = root / "src" / "Simulator.ThreeD" / "Simulator.ThreeD.csproj"
    runtime = root / "src" / "Simulator.Runtime" / "Simulator.Runtime.csproj"
    return {
        # Running "solution" maps to the default runnable app (ThreeD).
        "solution": TargetSpec(path=root / "Simulator.sln", run_project=three_d),
        "threeD": TargetSpec(path=three_d, run_project=three_d),
        "runtime": TargetSpec(path=runtime, run_project=runtime),
        "core": TargetSpec(path=root / "src" / "Simulator.Core" / "Simulator.Core.csproj"),
        "assets": TargetSpec(path=root / "src" / "Simulator.Assets" / "Simulator.Assets.csproj"),
        "editors": TargetSpec(path=root / "src" / "Simulator.Editors" / "Simulator.Editors.csproj"),
    }


def run_detached(command: list[str]) -> bool:
    try:
        kwargs = {
            "stdin": subprocess.DEVNULL,
            "stdout": subprocess.DEVNULL,
            "stderr": subprocess.DEVNULL,
            "cwd": str(repo_root()),
            "close_fds": True,
        }

        if os.name == "nt":
            # Keep the launcher process independent from this Python process.
            kwargs["creationflags"] = subprocess.DETACHED_PROCESS | subprocess.CREATE_NEW_PROCESS_GROUP  # type: ignore[attr-defined]

        subprocess.Popen(command, **kwargs)
        return True
    except Exception:
        return False


def locate_dotnet() -> str | None:
    return shutil.which("dotnet")


def run_with_dotnet(
    target_name: str,
    target_spec: TargetSpec,
    configuration: str,
    no_build: bool,
    app_args: list[str],
) -> bool:
    run_project = target_spec.run_project
    if run_project is None:
        print(
            f"Target '{target_name}' is not runnable. Use 'threeD' or 'runtime'.",
            file=sys.stderr,
        )
        return False

    dotnet = locate_dotnet()
    if not dotnet:
        print("dotnet CLI not found in PATH.", file=sys.stderr)
        return False

    command = [
        dotnet,
        "run",
        "--project",
        str(run_project),
        "-c",
        configuration,
    ]
    if no_build:
        command.append("--no-build")
    if app_args:
        command.append("--")
        command.extend(app_args)

    return run_detached(command)


def locate_vscode() -> str | None:
    code = shutil.which("code")
    if code:
        return code

    candidates = [
        Path(os.environ.get("LOCALAPPDATA", "")) / "Programs" / "Microsoft VS Code" / "Code.exe",
        Path(os.environ.get("ProgramFiles", "")) / "Microsoft VS Code" / "Code.exe",
    ]
    for candidate in candidates:
        if candidate.is_file():
            return str(candidate)
    return None


def locate_vswhere() -> Path | None:
    installer = (
        Path(os.environ.get("ProgramFiles(x86)", ""))
        / "Microsoft Visual Studio"
        / "Installer"
        / "vswhere.exe"
    )
    return installer if installer.is_file() else None


def locate_devenv() -> str | None:
    devenv = shutil.which("devenv")
    if devenv:
        return devenv

    vswhere = locate_vswhere()
    if not vswhere:
        return None

    try:
        result = subprocess.run(
            [
                str(vswhere),
                "-latest",
                "-products",
                "*",
                "-requires",
                "Microsoft.Component.MSBuild",
                "-find",
                "Common7\\IDE\\devenv.exe",
            ],
            check=True,
            capture_output=True,
            text=True,
        )
    except Exception:
        return None

    path = result.stdout.strip().splitlines()
    if not path:
        return None

    resolved = Path(path[0].strip())
    return str(resolved) if resolved.is_file() else None


def locate_rider() -> str | None:
    rider = shutil.which("rider64") or shutil.which("rider")
    if rider:
        return rider

    candidates = [
        Path(os.environ.get("ProgramFiles", "")) / "JetBrains" / "Rider" / "bin" / "rider64.exe",
        Path(os.environ.get("LOCALAPPDATA", "")) / "Programs" / "Rider" / "bin" / "rider64.exe",
    ]
    for candidate in candidates:
        if candidate.is_file():
            return str(candidate)
    return None


def open_with_vscode(target: Path, root: Path) -> bool:
    code = locate_vscode()
    if not code:
        return False

    # Open workspace root so C# tooling can load all projects.
    return run_detached([code, str(root), "--reuse-window"])


def open_with_visual_studio(target: Path, root: Path) -> bool:
    devenv = locate_devenv()
    if not devenv:
        return False

    launch_target = target if target.suffix.lower() == ".sln" else (root / "Simulator.sln")
    return run_detached([devenv, str(launch_target)])


def open_with_rider(target: Path, root: Path) -> bool:
    rider = locate_rider()
    if not rider:
        return False

    launch_target = target if target.exists() else (root / "Simulator.sln")
    return run_detached([rider, str(launch_target)])


def open_with_system_default(target: Path) -> bool:
    if os.name == "nt":
        try:
            os.startfile(str(target))  # type: ignore[attr-defined]
            return True
        except Exception:
            return False

    return run_detached(["xdg-open", str(target)])


def parse_args(target_keys: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Run or open the C# project quickly.")
    parser.add_argument(
        "--action",
        choices=["run", "open"],
        default="run",
        help="Choose run (dotnet run) or open (IDE).",
    )
    parser.add_argument(
        "--ide",
        choices=["auto", "vscode", "vs", "rider", "default"],
        default="auto",
        help="Which IDE to use when --action open.",
    )
    parser.add_argument(
        "--target",
        choices=target_keys,
        default=None,
        help="Which C# target to run/open.",
    )
    parser.add_argument(
        "--configuration",
        choices=["Debug", "Release"],
        default="Debug",
        help="Build configuration when --action run.",
    )
    parser.add_argument(
        "--no-build",
        action="store_true",
        help="Skip build step when --action run.",
    )
    parser.add_argument(
        "--app-args",
        nargs=argparse.REMAINDER,
        help="Arguments passed to the C# app when --action run.",
    )
    return parser.parse_args()


def main() -> int:
    root = repo_root()
    targets = project_targets(root)
    args = parse_args(list(targets.keys()))

    target_name = args.target
    if target_name is None:
        target_name = "threeD" if args.action == "run" else "solution"

    target_spec = targets[target_name]
    target = target_spec.path
    if not target.exists():
        print(f"Target does not exist: {target}", file=sys.stderr)
        return 2

    if args.action == "run":
        app_args = list(args.app_args or [])
        if app_args and app_args[0] == "--":
            app_args = app_args[1:]

        if run_with_dotnet(
            target_name=target_name,
            target_spec=target_spec,
            configuration=args.configuration,
            no_build=bool(args.no_build),
            app_args=app_args,
        ):
            print(f"Started '{target_name}' via dotnet run.")
            return 0
        return 1

    openers: dict[str, Callable[[Path, Path], bool]] = {
        "vscode": open_with_vscode,
        "vs": open_with_visual_studio,
        "rider": open_with_rider,
    }

    if args.ide == "default":
        success = open_with_system_default(target)
        if success:
            print(f"Opened with system default app: {target}")
            return 0
        print("Failed to open with system default app.", file=sys.stderr)
        return 1

    if args.ide == "auto":
        order = ["vscode", "vs", "rider"]
    else:
        order = [args.ide]

    for ide_name in order:
        opener = openers[ide_name]
        if opener(target, root):
            print(f"Opened '{target_name}' via {ide_name}.")
            return 0

    if open_with_system_default(target):
        print(f"Fallback: opened with system default app: {target}")
        return 0

    print(
        "Unable to open project. Install VS Code, Visual Studio, or Rider, "
        "or pass --ide default.",
        file=sys.stderr,
    )
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
