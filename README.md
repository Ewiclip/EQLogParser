# EQLogParser
Everquest Log Parser for Live/TLP servers with basic support for P99/EMU Servers.

Link to DOWNLOAD the latest Installer:</br>
https://github.com/kauffman12/EQLogParser/releases/download/2.4.7/EQLogParser-install-2.4.7.exe

Minimum Requirements:
1. Windows 10 x64
2. .Net 10.0 Desktop Runtime for x64 (any 10.0.x release)

For More Info Visit:
http://eqlogparser.kizant.net

## Building from source

Install the .NET 10 SDK for Windows x64, then run this from the repository root:

```powershell
dotnet build EQLogParser.sln
```

`global.json` selects a stable .NET 10 SDK (10.0.100 or later within 10.0).
It checks `local/dotnet` first for an optional repository-local SDK, then the
SDKs installed with the current `dotnet` host. The `local` folder is Git-ignored.
The Desktop Runtime alone is sufficient to run the app; building requires the SDK.
