@echo off
cd /d "%~dp0"
if exist ".venv\Scripts\python.exe" (
    ".venv\Scripts\python.exe" open_csharp_project.py --action run --target threeD
) else (
    python open_csharp_project.py --action run --target threeD
)
