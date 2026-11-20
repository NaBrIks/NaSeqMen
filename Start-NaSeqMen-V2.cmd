@echo off
cd /d "%~dp0src\NaSeqMen.Host"
start "" "%~dp0src\NaSeqMen.Host\bin\Debug\net11.0-windows\NaSeqMen.Host.exe" --contentRoot "%~dp0src\NaSeqMen.Host"
timeout /t 1 /nobreak >nul
start "" "http://127.0.0.1:17980"
