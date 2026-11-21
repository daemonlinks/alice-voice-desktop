@echo off
REM ============================================================================
REM  AHC_AliceVoiceBackend - install the Alice Voice backend as a Windows service.
REM
REM  RUN FROM AN ELEVATED PROMPT. It needs admin for two things: writing the
REM  service into HKLM, and adding the URL reservation its port requires.
REM
REM  WHAT THIS IS
REM  The unified backend for the Alice Voice Universal App: the same /v1 surface
REM  the phone already talks to - STT, SOUL, Alice LOCAL, fleet, Piper, media -
REM  plus the app surface, run headless, so ANY frontend - App U's window, the web
REM  page, the phone, or one not written yet - is a client of it and nothing else.
REM  Same code as the app, minus the face.
REM
REM  ITS OWN PORT - DECIDED
REM  The live bridge owns 100.64.237.3:8765. This service takes 8766, so the app
REM  window and the service can run at the same time while the window is switched
REM  over to being a client of this. A FIXED Tailscale port needs a URL reservation
REM  owned by the account the process runs as, or HttpListener.Start fails with
REM  Access is denied 5 - established by running the service, not by assuming. So
REM  the urlacl step below is not optional. It is reserved for DAEMONLINKS\AHC, the
REM  account that already owns 8765.
REM
REM  CONVENTIONS OBSERVED - the fleet's own, read off the live host, not invented
REM    * Name AHC_<PascalCase>, like AHC_alice_voice, AHC_Dashboard, AHC_ollama,
REM      AHC_ShootRater.
REM    * NSSM at C:\Tools\nssm\nssm.exe.
REM    * Start SERVICE_AUTO_START.
REM    * AppDirectory = the published app dir, never the runtime's own dir.
REM    * stdout/stderr captured and rotated, so a fault is readable with no console.
REM    * DisplayName and Description set, because these are what Master sees.
REM    * Account DAEMONLINKS\AHC, as AHC_ShootRater uses - never LocalSystem, which
REM      resolves AppData to systemprofile and would lose the settings, the SOUL and
REM      the media roots.
REM
REM  ONE STEP CANNOT BE AUTOMATED HERE - the account's password. See the end.
REM ============================================================================

setlocal
set NSSM=C:\Tools\nssm\nssm.exe
set SVC=AHC_AliceVoiceBackend
set APPDIR=D:\Apps\AliceVoice.Backend
set APP=%APPDIR%\AliceVoice.Universal.Service.exe
set LOGDIR=C:\Users\AHC.DAEMONLINKS\AppData\Local\AliceVoice\logs
set BIND=100.64.237.3:8766
set ACCOUNT=DAEMONLINKS\AHC
set OUT=%LOGDIR%\install-AHC_AliceVoiceBackend.log

if not exist "%LOGDIR%" mkdir "%LOGDIR%"

(
  echo ==== AHC_AliceVoiceBackend install, %DATE% %TIME% ====

  if not exist "%NSSM%" ( echo FAIL: no nssm at %NSSM% & exit /b 1 )
  if not exist "%APP%"  ( echo FAIL: no service binary at %APP% - publish it first & exit /b 1 )

  echo.
  echo --- 1. URL reservation for http://%BIND% ---
  netsh http delete urlacl url=http://%BIND%/ >nul 2>&1
  netsh http add urlacl url=http://%BIND%/ user=%ACCOUNT%
  if errorlevel 1 ( echo FAIL: could not reserve the URL & exit /b 1 )
  netsh http show urlacl url=http://%BIND%/

  echo.
  echo --- 2. service ---
  "%NSSM%" install %SVC% "%APP%" >nul 2>&1
  "%NSSM%" set %SVC% Application "%APP%"
  "%NSSM%" set %SVC% AppDirectory "%APPDIR%"
  "%NSSM%" set %SVC% Start SERVICE_AUTO_START
  "%NSSM%" set %SVC% DisplayName "Alice Voice Backend"
  "%NSSM%" set %SVC% Description "Headless Alice Voice backend for the Universal App: /v1 STT, SOUL, Alice LOCAL, fleet, Piper, media, and the app surface. Serves any frontend that talks to it."
  "%NSSM%" set %SVC% AppStdout "%LOGDIR%\backend.out.log"
  "%NSSM%" set %SVC% AppStderr "%LOGDIR%\backend.err.log"
  "%NSSM%" set %SVC% AppRotateFiles 1
  "%NSSM%" set %SVC% AppRotateBytes 1048576
  REM The port lives in the service's own environment, so the app's shared
  REM appsettings.json keeps 8765 and the two never fight over the same key.
  "%NSSM%" set %SVC% AppEnvironmentExtra ALICEVOICE_BRIDGE_BIND=%BIND%
  REM AppExit is deliberately left at its default restart: the process itself never
  REM exits on a failed bind, so a fault cannot become a respawn storm.

  echo.
  echo --- 3. what was written ---
  reg query "HKLM\SYSTEM\CurrentControlSet\Services\%SVC%\Parameters" /reg:32 /s
  reg query "HKLM\SYSTEM\CurrentControlSet\Services\%SVC%" /v Start

  echo.
  echo --- 4. start ---
  "%NSSM%" start %SVC%
  "%NSSM%" status %SVC%
  sc query %SVC%

  echo.
  echo ==== REMAINING STEP, BY HAND, ONCE ====
  echo The service must run as %ACCOUNT% so it resolves the same AppData, settings,
  echo SOUL and media roots as the app. NSSM needs that account's password, which is
  echo deliberately not handled by this script. Set it once, either way:
  echo     services.msc - AHC_AliceVoiceBackend - Log On - This account
  echo or, from an elevated prompt:
  echo     "%NSSM%" set %SVC% ObjectName %ACCOUNT%
  echo which will then prompt for the password. Restart the service afterwards.
  echo Until it is set the service runs as LocalSystem, which the reservation does
  echo not cover, so it logs Access is denied and stays up rather than crash-looping.
) > "%OUT%" 2>&1

echo Install output written to %OUT%
endlocal
