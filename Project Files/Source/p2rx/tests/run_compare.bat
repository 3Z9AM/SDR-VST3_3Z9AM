@echo off
rem Builds the Rust static library, compiles compare.c against it and runs it (PASS/FAIL).
setlocal
cd /d "%~dp0.."
cargo build --release --target-dir "%TEMP%\p2rx_test" || exit /b 1
call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat" >nul
cl /nologo /O2 /Fe"%TEMP%\p2rx_test\compare.exe" /Fo"%TEMP%\p2rx_test\\" tests\compare.c "%TEMP%\p2rx_test\release\p2rx.lib" kernel32.lib ntdll.lib userenv.lib ws2_32.lib dbghelp.lib || exit /b 1
"%TEMP%\p2rx_test\compare.exe"
