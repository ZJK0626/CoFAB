@echo off
setlocal enabledelayedexpansion

:: Display header
echo ====================================================
echo TRELLIS 3D Generator Wrapper
echo ====================================================

:: Parse command line arguments
set MODE=image
set INPUT=
set OUTPUT_DIR=
set SEED=0
set SS_STEPS=12
set SS_GUIDANCE=7.5
set SLAT_STEPS=12
set SLAT_GUIDANCE=3.0

:parse_args
if "%~1"=="" goto :end_parse_args
if /i "%~1"=="--mode" set MODE=%~2& shift & shift & goto :parse_args
if /i "%~1"=="--input" set INPUT=%~2& shift & shift & goto :parse_args
if /i "%~1"=="--output_dir" set OUTPUT_DIR=%~2& shift & shift & goto :parse_args
if /i "%~1"=="--seed" set SEED=%~2& shift & shift & goto :parse_args
if /i "%~1"=="--ss_steps" set SS_STEPS=%~2& shift & shift & goto :parse_args
if /i "%~1"=="--ss_guidance" set SS_GUIDANCE=%~2& shift & shift & goto :parse_args
if /i "%~1"=="--slat_steps" set SLAT_STEPS=%~2& shift & shift & goto :parse_args
if /i "%~1"=="--slat_guidance" set SLAT_GUIDANCE=%~2& shift & shift & goto :parse_args
shift
goto :parse_args
:end_parse_args

:: Validate required arguments
if "!MODE!"=="" (
    echo ERROR: Mode is required
    exit /b 1
)

if "!INPUT!"=="" (
    echo ERROR: Input is required
    exit /b 1
)

if "!OUTPUT_DIR!"=="" (
    echo ERROR: Output directory is required
    exit /b 1
)

:: Create output directory if it doesn't exist
if not exist "!OUTPUT_DIR!" (
    mkdir "!OUTPUT_DIR!"
)

:: Display run information
echo Activating Conda environment: trellis
echo Running TRELLIS script with the following arguments:
echo --mode !MODE! --input "!INPUT!" --output_dir "!OUTPUT_DIR!" --seed !SEED! --ss_steps !SS_STEPS! --ss_guidance !SS_GUIDANCE! --slat_steps !SLAT_STEPS! --slat_guidance !SLAT_GUIDANCE!

:: Activate conda environment and run the script
call C:\Users\DELL\anaconda3\condabin\conda.bat activate trellis

:: Print Python environment info
echo Python executable: !CONDA_PREFIX!\python.exe
for /f "tokens=*" %%i in ('python -c "import sys; print(sys.version)"') do set PYTHON_VERSION=%%i
echo Python version: !PYTHON_VERSION!
echo Current working directory: %CD%
for /f "tokens=*" %%i in ('python -c "import sys; print(sys.path)"') do set PYTHONPATH=%%i
echo PYTHONPATH: !PYTHONPATH!

:: Set environment variables
set SPCONV_ALGO=native

:: Run the Python script
python %~dp0\trellis_wrapper.py ^
    --mode !MODE! ^
    --input "!INPUT!" ^
    --output_dir "!OUTPUT_DIR!" ^
    --seed !SEED! ^
    --ss_steps !SS_STEPS! ^
    --ss_guidance !SS_GUIDANCE! ^
    --slat_steps !SLAT_STEPS! ^
    --slat_guidance !SLAT_GUIDANCE!

:: Capture exit code
set EXIT_CODE=%ERRORLEVEL%

:: Return to the original conda environment
call conda deactivate

:: Exit with the Python script's exit code
exit /b %EXIT_CODE%