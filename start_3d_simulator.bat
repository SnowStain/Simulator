@echo off
cd /d "%~dp0"
if exist ".venv\Scripts\python.exe" (
    ".venv\Scripts\python.exe" simulator_3d.py
) else (
    python simulator_3d.py
)