@echo off
setlocal EnableExtensions DisableDelayedExpansion
set "CODEVIEW_MODE=unit"
set "CODEVIEW_EXIT=1"
if not "%~2"=="" goto usage
if "%~1"=="" goto args_ok
if /i not "%~1"=="--performance" goto usage
set "CODEVIEW_MODE=performance"
:args_ok
pushd "%~dp0"
if errorlevel 1 exit /b 1
where dotnet.exe >nul 2>&1
if errorlevel 1 goto sdk_missing
if "%CODEVIEW_MODE%"=="performance" goto performance
where py.exe >nul 2>&1
if errorlevel 1 goto python_missing
py.exe -3 --version >nul 2>&1
if errorlevel 1 goto python_missing
set "CODEVIEW_FILTER=TestCategory!=Performance"
goto run
:performance
set "CODEVIEW_FILTER=TestCategory=Performance"
:run
set "CODEVIEW_RESULTS=%CD%\artifacts\test-results\windows"
if not exist "%CODEVIEW_RESULTS%" mkdir "%CODEVIEW_RESULTS%"
if errorlevel 1 goto failed
dotnet.exe restore codeView.slnx --locked-mode --disable-parallel -p:PublishAot=false -p:CodeViewPerformanceProbe=false -m:1 -nr:false
if errorlevel 1 goto failed
dotnet.exe test codeView.slnx -c Release --no-restore -p:PublishAot=false -p:PublishTrimmed=false -p:CodeViewPerformanceProbe=false -m:1 -nr:false --filter "%CODEVIEW_FILTER%" --results-directory "%CODEVIEW_RESULTS%" --logger "trx;LogFileName=%CODEVIEW_MODE%.trx"
if errorlevel 1 goto failed
if "%CODEVIEW_MODE%"=="performance" goto success
py.exe -3 -m unittest discover -s tools -v
if errorlevel 1 goto failed
:success
echo Results: "%CODEVIEW_RESULTS%\%CODEVIEW_MODE%.trx"
popd
endlocal & exit /b 0
:failed
set "CODEVIEW_EXIT=%ERRORLEVEL%"
if "%CODEVIEW_EXIT%"=="0" set "CODEVIEW_EXIT=1"
echo ERROR: Tests failed. Check the output above. 1>&2
goto finish
:sdk_missing
echo ERROR: Install the .NET SDK specified in global.json. 1>&2
goto finish
:python_missing
echo ERROR: Install Python 3 with the py launcher for tools tests. 1>&2
:finish
popd
endlocal & exit /b %CODEVIEW_EXIT%
:usage
echo Usage: test-windows.bat [--performance] 1>&2
endlocal & exit /b 2
