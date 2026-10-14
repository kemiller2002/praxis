@echo off
setlocal
set "HERE=%~dp0"
rem The self-contained binary embeds its own scaffold payload (DF-ROS-2026-A041).
"%HERE%praxis-bin.exe" %*
