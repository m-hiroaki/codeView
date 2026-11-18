@echo off
setlocal EnableExtensions DisableDelayedExpansion
set "CODEVIEW_MODE=unit"
set "CODEVIEW_EXIT=1"
set "CODEVIEW_COVERAGE_ARGS="
if not "%~2"=="" goto usage
if "%~1"=="" goto args_ok
if /i "%~1"=="--coverage" goto coverage_args
if /i not "%~1"=="--performance" goto usage
set "CODEVIEW_MODE=performance"
goto args_ok
:coverage_args
set "CODEVIEW_MODE=coverage"
set CODEVIEW_COVERAGE_ARGS=--collect:"XPlat Code Coverage" --settings coverage.runsettings
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
set "CODEVIEW_PAYLOAD=%CD%\artifacts\publish\win-x64\codeView"
set "CODEVIEW_TEST_ROSLYN=%CODEVIEW_PAYLOAD%\language-servers\roslyn\Microsoft.CodeAnalysis.LanguageServer.exe"
set "CODEVIEW_TEST_CLANGD=%CODEVIEW_PAYLOAD%\language-servers\clangd\bin\clangd.exe"
call :has_bundle
if not errorlevel 1 goto bundle_ready
echo Publishing win-x64 for language server tests.
set "CODEVIEW_SEMANTIC_PROBE="
call "%~dp0build-windows.bat"
if errorlevel 1 goto failed
call :has_bundle
if errorlevel 1 goto bundle_missing
:bundle_ready
echo Using bundled language servers: "%CODEVIEW_PAYLOAD%"
set "CODEVIEW_RESULTS=%CD%\artifacts\test-results\windows"
if not "%CODEVIEW_MODE%"=="coverage" goto results_ready
set "CODEVIEW_HTML=%CODEVIEW_RESULTS%\coverage\html"
set "CODEVIEW_RESULTS=%CODEVIEW_RESULTS%\coverage\current"
if exist "%CODEVIEW_RESULTS%" rmdir /s /q "%CODEVIEW_RESULTS%"
if errorlevel 1 goto failed
if exist "%CODEVIEW_HTML%" rmdir /s /q "%CODEVIEW_HTML%"
if errorlevel 1 goto failed
:results_ready
if not exist "%CODEVIEW_RESULTS%" mkdir "%CODEVIEW_RESULTS%"
if errorlevel 1 goto failed
dotnet.exe restore codeView.slnx --locked-mode --disable-parallel -p:PublishAot=false -p:CodeViewPerformanceProbe=false -m:1 -nr:false
if errorlevel 1 goto failed
dotnet.exe test codeView.slnx -c Release --no-restore -p:PublishAot=false -p:PublishTrimmed=false -p:CodeViewPerformanceProbe=false -m:1 -nr:false --filter "%CODEVIEW_FILTER%" --results-directory "%CODEVIEW_RESULTS%" --logger "trx;LogFileName=%CODEVIEW_MODE%.trx" %CODEVIEW_COVERAGE_ARGS%
if errorlevel 1 goto failed
if "%CODEVIEW_MODE%"=="performance" goto success
if not "%CODEVIEW_MODE%"=="coverage" goto python_tests
dotnet.exe tool restore --tool-manifest "%CD%\.config\dotnet-tools.json"
if errorlevel 1 goto failed
dotnet.exe tool run reportgenerator -- "-reports:%CODEVIEW_RESULTS%\*\coverage.cobertura.xml" "-targetdir:%CODEVIEW_HTML%" "-sourcedirs:%CD%\src\codeView" -reporttypes:Html
if errorlevel 1 goto failed
if not exist "%CODEVIEW_HTML%\index.html" goto html_missing
:python_tests
py.exe -3 -m unittest discover -s tools -v
if errorlevel 1 goto failed
:success
echo Results: "%CODEVIEW_RESULTS%\%CODEVIEW_MODE%.trx"
if "%CODEVIEW_MODE%"=="coverage" echo Coverage: "%CODEVIEW_RESULTS%\[run-id]\coverage.cobertura.xml", coverage.info
if "%CODEVIEW_MODE%"=="coverage" echo HTML: "%CODEVIEW_HTML%\index.html"
popd
endlocal & exit /b 0
:failed
set "CODEVIEW_EXIT=%ERRORLEVEL%"
if "%CODEVIEW_EXIT%"=="0" set "CODEVIEW_EXIT=1"
echo ERROR: Tests failed. Check the output above. 1>&2
goto finish
:bundle_missing
echo ERROR: Published app or language servers are missing from "%CODEVIEW_PAYLOAD%". 1>&2
goto finish
:html_missing
echo ERROR: HTML coverage report was not generated. 1>&2
set "CODEVIEW_EXIT=1"
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
echo Usage: test-windows.bat [--performance ^| --coverage] 1>&2
endlocal & exit /b 2
:has_bundle
if not exist "%CODEVIEW_PAYLOAD%\codeView.exe" exit /b 1
if not exist "%CODEVIEW_TEST_ROSLYN%" exit /b 1
if not exist "%CODEVIEW_TEST_CLANGD%" exit /b 1
exit /b 0
