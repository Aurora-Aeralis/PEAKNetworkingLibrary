# PEAKNetworkingLibrary

Networking library for PEAK mods that use Steam networking instead of PUN.

## Usage

Reference the package from a BepInEx mod project targeting PEAK. The runtime selects Steam networking when Steamworks is ready and falls back to the offline service when Steam is unavailable.

## Build

```powershell
dotnet build .\NetworkingLibrary.sln -c Release -m:1 -p:ThunderstorePackable=false
```
