@echo off
REM ===== Performance self-check launcher (no camera needed) =====
REM This sets PERF_SELFTEST=1 and starts the program. On startup the program
REM writes a [perf] report using fake data so you can verify the logging format.
REM After it starts, open the log viewer or check:  Log\<today>.txt
REM Look for the line starting with [perf] and the line starting with [selfcheck].

set PERF_SELFTEST=1

if exist "%~dp0bin\Debug\WindowsFormsApplication1.exe" (
    start "" "%~dp0bin\Debug\WindowsFormsApplication1.exe"
) else if exist "%~dp0bin\Release\WindowsFormsApplication1.exe" (
    start "" "%~dp0bin\Release\WindowsFormsApplication1.exe"
) else (
    echo WindowsFormsApplication1.exe not found under bin\Debug or bin\Release.
    echo Put this .cmd next to the .exe and run it, or set PERF_SELFTEST=1 manually.
    pause
)
