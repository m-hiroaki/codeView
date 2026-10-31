@echo off
setlocal EnableExtensions DisableDelayedExpansion
set "CODEVIEW_STAGE="
set "CODEVIEW_EXIT=1"
if not "%~1"=="" goto usage
pushd "%~dp0"
if errorlevel 1 exit /b 1
where dotnet.exe >nul 2>&1
if errorlevel 1 goto sdk_missing
set "CODEVIEW_OUTPUT=%CD%\artifacts\publish\win-x64"
set "CODEVIEW_BUILD=%CD%\artifacts\build\win-x64"
set "CODEVIEW_STAGE=%CODEVIEW_BUILD%\stage-%RANDOM%-%RANDOM%"
if exist "%CODEVIEW_STAGE%" goto stage_exists
mkdir "%CODEVIEW_STAGE%"
if errorlevel 1 goto failed

rem Validate the checked-in graph; keep RID/AOT tool packages in a separate lock.
dotnet.exe restore src\codeView\codeView.csproj --locked-mode --disable-parallel -m:1 -nr:false
if errorlevel 1 goto failed
copy /y src\codeView\packages.lock.json "%CODEVIEW_STAGE%\publish.packages.lock.json" >nul
if errorlevel 1 goto failed
dotnet.exe publish src\codeView\codeView.csproj -c Release -r win-x64 --self-contained true --artifacts-path "%CODEVIEW_BUILD%" -o "%CODEVIEW_STAGE%\payload" -p:PublishAot=true -p:PublishTrimmed=true -p:PublishSingleFile=false -p:CodeViewPerformanceProbe=false -p:RestoreLockedMode=false "-p:NuGetLockFilePath=%CODEVIEW_STAGE%\publish.packages.lock.json" -p:IlcTreatWarningsAsErrors=true -p:ILLinkTreatWarningsAsErrors=true -m:1 -nr:false
if errorlevel 1 goto failed
if not exist "%CODEVIEW_STAGE%\payload\codeView.exe" goto executable_missing
if not exist "%CODEVIEW_OUTPUT%" mkdir "%CODEVIEW_OUTPUT%"
if errorlevel 1 goto failed
rem Replace only generated output after Native AOT publish succeeds.
if exist "%CODEVIEW_OUTPUT%\codeView" rmdir /s /q "%CODEVIEW_OUTPUT%\codeView"
if errorlevel 1 goto failed
move "%CODEVIEW_STAGE%\payload" "%CODEVIEW_OUTPUT%\codeView" >nul
if errorlevel 1 goto failed
copy /y "%CODEVIEW_STAGE%\publish.packages.lock.json" "%CODEVIEW_OUTPUT%\publish.packages.lock.json" >nul
if errorlevel 1 goto failed
rmdir /s /q "%CODEVIEW_STAGE%"
echo Built: "%CODEVIEW_OUTPUT%\codeView\codeView.exe"
popd
endlocal & exit /b 0

:failed
set "CODEVIEW_EXIT=%ERRORLEVEL%"
if "%CODEVIEW_EXIT%"=="0" set "CODEVIEW_EXIT=1"
echo ERROR: Build failed. Check the output above. 1>&2
goto cleanup
:sdk_missing
echo ERROR: Install the .NET SDK specified in global.json. 1>&2
goto cleanup
:stage_exists
rem Do not remove a staging directory that this invocation did not create.
set "CODEVIEW_STAGE="
echo ERROR: Staging directory already exists. Please run again. 1>&2
goto cleanup
:executable_missing
echo ERROR: Native AOT publish did not produce codeView.exe. 1>&2
:cleanup
if defined CODEVIEW_STAGE if exist "%CODEVIEW_STAGE%" rmdir /s /q "%CODEVIEW_STAGE%"
popd
endlocal & exit /b %CODEVIEW_EXIT%
:usage
echo Usage: build-windows.bat 1>&2
endlocal & exit /b 2
