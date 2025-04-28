@echo off
REM This batch file activates the correct Anaconda environment and runs the TRELLIS script

REM Save the current directory
set ORIGINAL_DIR=%CD%

REM Get the directory of this batch file
set SCRIPT_DIR=%~dp0

REM Change to the script directory
cd /d %SCRIPT_DIR%

REM Configure environment variables for the Conda environment activation
set CONDA_ACTIVATION=C:\Users\DELL\anaconda3\Scripts\activate.bat
set CONDA_ENV=trellis

REM Activate the Conda environment and run the Python script
echo Activating Conda environment: %CONDA_ENV%
call "%CONDA_ACTIVATION%" %CONDA_ENV%

echo Running TRELLIS script with the following arguments:
echo %*

python trellis_wrapper.py %*

REM Return to the original directory
cd /d %ORIGINAL_DIR%