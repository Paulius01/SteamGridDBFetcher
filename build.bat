@echo off
rem Compiles SteamGridDBFetcher.exe using the C# compiler that ships with
rem Windows (.NET Framework 4.8) - no installs needed.
cd /d "%~dp0"
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe ^
  /out:SteamGridDBFetcher.exe /win32icon:app.ico ^
  /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll ^
  /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll ^
  SteamGridDBFetcher.cs
if errorlevel 1 (echo BUILD FAILED & pause) else echo Built SteamGridDBFetcher.exe
