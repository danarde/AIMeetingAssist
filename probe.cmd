@echo off
setlocal
set "EXE=%~dp0src\MeetingAssist.CaptureProbe\bin\Debug\net10.0-windows\captureprobe.exe"
if not exist "%EXE%" (
  echo Not built yet. Run: dotnet build MeetingAssist.slnx
  exit /b 1
)
start "" "%EXE%"
