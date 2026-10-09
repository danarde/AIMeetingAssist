@echo off
setlocal
set "EXE=%~dp0src\MeetingAssist.Spike\bin\Debug\net10.0-windows\spike.exe"
if not exist "%EXE%" (
  echo Not built yet. Run: dotnet build MeetingAssist.slnx
  exit /b 1
)
"%EXE%" %*
