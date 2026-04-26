from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parent
PROJECT = ROOT / "src" / "Simulator.LoadLargeTerrain" / "LoadLargeTerrain.csproj"
DEFAULT_MODEL = ROOT / "maps" / "rmuc26map" / "RMUC2026_MAP.glb"
DEFAULT_ANNOTATIONS = ROOT / "maps" / "rmuc26map" / "try.json"

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="启动当前仓库内嵌的 LoadLargeTerrain 地图查看/编辑器。")
    parser.add_argument(
        "model",
        nargs="?",
        default=str(DEFAULT_MODEL),
        help="要加载的 .glb 模型路径，默认使用 maps/rmuc26map/RMUC2026_MAP.glb。",
    )
    parser.add_argument(
        "--annotations",
        default=str(DEFAULT_ANNOTATIONS),
        help="要加载的标注 JSON，默认使用 maps/rmuc26map/try.json。",
    )
    parser.add_argument(
        "--build-cache-only",
        action="store_true",
        help="只构建或校验地形缓存，不打开 OpenGL 窗口。",
    )
    parser.add_argument(
        "--release",
        action="store_true",
        help="使用 Release 配置运行。",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()

    if shutil.which("dotnet") is None:
        print("错误：PATH 中找不到 dotnet，请先安装 .NET 10 SDK。", file=sys.stderr)
        return 1

    if not PROJECT.exists():
        print(f"错误：找不到内嵌工程文件：{PROJECT}", file=sys.stderr)
        return 1

    model_path = Path(args.model).expanduser().resolve()
    if not model_path.exists():
        print(f"错误：找不到模型文件：{model_path}", file=sys.stderr)
        return 1

    command = [
        "dotnet",
        "run",
        "--project",
        str(PROJECT),
    ]

    if args.release:
        command.extend(["--configuration", "Release"])

    command.append("--")

    if args.build_cache_only:
        command.append("--build-cache-only")

    command.append(str(model_path))

    annotations_path = Path(args.annotations).expanduser().resolve()
    if annotations_path.exists():
        command.extend(["--annotations", str(annotations_path)])

    print("正在启动内嵌 LoadLargeTerrain：")
    print(" ".join(f'"{part}"' if " " in part else part for part in command), flush=True)

    try:
        return subprocess.call(command, cwd=ROOT)
    except KeyboardInterrupt:
        print("\n已中断。")
        return 130


if __name__ == "__main__":
    raise SystemExit(main())
