@echo off
setlocal
set "HERE=%~dp0"
"%HERE%praxis-bin.exe" --package-root "%HERE%package" %*
