@echo off
rem Builds and runs the parallel VST path test (needs Visual Studio 2026 C++ tools).
setlocal
set OUT=%TEMP%\vst_test_lane_mix
if not exist "%OUT%" mkdir "%OUT%"
call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
cl /nologo /O2 /EHsc /Fo"%OUT%\\" /Fe"%OUT%\test_lane_mix.exe" "%~dp0test_lane_mix.cpp" >"%OUT%\build.log" || (type "%OUT%\build.log" & exit /b 1)
"%OUT%\test_lane_mix.exe"
