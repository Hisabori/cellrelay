@echo off
setlocal
set "DOTNET_BUNDLE_EXTRACT_BASE_DIR=%~dp0.runtime"
start "" "%~dp0Desktop\CellRelay.exe"
