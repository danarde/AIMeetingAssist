@echo off
setlocal
set "EXE=%~dp0src\MeetingAssist.App\bin\Debug\net10.0-windows\meetingassist.exe"
if not exist "%EXE%" (
  echo Not built yet. Run: dotnet build MeetingAssist.slnx
  exit /b 1
)
start "" "%EXE%"
