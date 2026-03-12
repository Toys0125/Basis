# escape=`
FROM mcr.microsoft.com/windows/servercore:ltsc2022

WORKDIR C:\app
COPY ./StandaloneWindows64/. .

ENTRYPOINT ["C:\\app\\HeadlessWindowsServer.exe"]
