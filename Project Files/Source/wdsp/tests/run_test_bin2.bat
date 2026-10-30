@echo off
rem Builds and runs the BIN2 filter test (needs Visual Studio 2026 C++ tools).
setlocal
set ROOT=%~dp0..
set LIB_DIR=%ROOT%\..\..\lib
set OUT=%TEMP%\wdsp_test_bin2
if not exist "%OUT%" mkdir "%OUT%"
call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
cl /nologo /O2 /I"%ROOT%" /I"%LIB_DIR%\portaudio_VS2008\include" /I"%LIB_DIR%\NR_Algorithms_x64" /Fo"%OUT%\\" /Fe"%OUT%\test_bin2.exe" "%~dp0test_bin2.c" "%ROOT%\fir.c" "%ROOT%\impulse_cache.c" /link "%LIB_DIR%\fftw_x64\libfftw3-3.lib" >"%OUT%\build.log" || (type "%OUT%\build.log" & exit /b 1)
copy /y "%LIB_DIR%\fftw_x64\libfftw3-3.dll" "%OUT%" >nul
"%OUT%\test_bin2.exe"
