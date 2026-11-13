@echo off
rem After Ctrl+C, cmd asks "Terminate batch job (Y/N)?". Running Python from a subroutine with
rem cmd's input and output on NUL stops that, while handles 7, 8 and 9 keep the real ones for
rem Python. The arguments stay in a variable because call would mangle % and ^.
setlocal
set "MROOM_PY=python"
if exist "%~dp0.venv\Scripts\python.exe" set "MROOM_PY=%~dp0.venv\Scripts\python.exe"
set MROOM_ARGS=%*
call :run 7<&0 8>&1 9>&2 <nul >nul 2>nul
exit /b %errorlevel%

:run
"%MROOM_PY%" "%~dp0mroom.py" %MROOM_ARGS% <&7 >&8 2>&9
exit /b %errorlevel%
