@echo off
REM ============================================================================
REM  AHC_AliceVoiceBackend - install the Alice Voice backend as a Windows service.
REM
REM  WHAT THIS IS
REM  The unified backend for the Alice Voice Universal App: the same /v1 surface
REM  the phone already talks to (STT, SOUL, Alice LOCAL, fleet, Piper, media) plus
REM  the app surface (/v1/app, /v1/app/tools, /v1/app/media, /v1/app/display), run
REM  headless so ANY frontend - App U's window, the web page, the phone, or one not
REM  written yet - is a client of it and nothing else.
REM
REM  HOW TO RUN IT
REM  From an ELEVATED command prompt (Run as administrator). NSSM writes
REM  Application/AppParameters/AppDirectory into HKLM: a non-elevated shell can
REM  install but then cannot start, stop or modify the service. Verify afterwards
REM  with the reg query at the bottom of this file.
REM
REM  CONVENTIONS OBSERVED (the fleet's own, not invented here)
REM    * Service name AHC_<PascalCase>, matching AHC_alice_voice, AHC_Dashboard,
REM      AHC_ShootRater, AHC_ollama and the rest on this host.
REM    * NSSM at C:\Tools\nssm\nssm.exe.
REM    * Start SERVICE_AUTO_START (0x2).
REM    * AppDirectory = the published app dir, never the runtime's own dir.
REM    * stdout/stderr captured to files so a fault is readable without a console.
REM    * DisplayName and Description set, because these are what Master sees.
REM
REM  ONE THING TO DECIDE BEFORE RUNNING - the port
REM  A fixed port needs a URL reservation owned by the account this service runs
REM  as. The live bridge holds http://100.64.237.3:8765/ for DAEMONLINKS\AHC, and
REM  only one process can own a prefix at a time. So either
REM    (a) this service runs as the AHC account and owns 8765, with App U's window
REM        switched to being a CLIENT of it (the direction of travel), or
REM    (b) this service takes its own port and gets its own reservation:
REM          netsh http add urlacl url=http://100.64.237.3:8766/ user=DAEMONLINKS\AHC
REM  Until one is chosen, leave the bind unset and the service will log the
REM  misconfiguration and stay up rather than crash-loop.
REM ============================================================================

setlocal
set NSSM=C:\Tools\nssm\nssm.exe
set SVC=AHC_AliceVoiceBackend
set APPDIR=D:\Apps\AliceVoice.Backend
set APP=%APPDIR%\AliceVoice.Universal.Service.exe
set LOGDIR=C:\Users\AHC.DAEMONLINKS\AppData\Local\AliceVoice\logs

if not exist "%NSSM%" (echo FAIL: no nssm at %NSSM% & exit /b 1)
if not exist "%APP%"  (echo FAIL: no service binary at %APP% - publish it first & exit /b 1)
if not exist "%LOGDIR%" mkdir "%LOGDIR%"

echo --- installing %SVC% ---
"%NSSM%" install %SVC% "%APP%"
if errorlevel 1 (echo FAIL: nssm install & exit /b 1)

"%NSSM%" set %SVC% Start SERVICE_AUTO_START
"%NSSM%" set %SVC% DisplayName "Alice Voice Backend"
"%NSSM%" set %SVC% Description "Headless Alice Voice backend (App U): /v1 STT, SOUL, Alice LOCAL, fleet, Piper, media and the app surface. Serves any frontend that talks to it."
"%NSSM%" set %SVC% AppDirectory "%APPDIR%"
"%NSSM%" set %SVC% AppStdout "%LOGDIR%\backend.out.log"
"%NSSM%" set %SVC% AppStderr "%LOGDIR%\backend.err.log"
"%NSSM%" set %SVC% AppRotateFiles 1
"%NSSM%" set %SVC% AppRotateBytes 1048576
REM Deliberately NOT setting AppExit: the default restart is wanted, and the process itself never
REM exits on a failed bridge bind (it logs and stays up) so a fault cannot become a respawn storm.

echo --- verifying what was written ---
reg query "HKLM\SYSTEM\CurrentControlSet\Services\%SVC%\Parameters" /reg:32 /s
reg query "HKLM\SYSTEM\CurrentControlSet\Services\%SVC%" /v Start

echo --- starting ---
"%NSSM%" start %SVC%
"%NSSM%" status %SVC%

echo.
echo Done. If Start failed with Access Denied, the shell was not elevated - the service will still
echo auto-start on the next boot.
endlocal
