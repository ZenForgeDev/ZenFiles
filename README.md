# ZenFiles

Moderne filutforsker for Windows 11 (WinUI 3) med innebygd SFTP-utforsker og terminal.

## Funksjoner
- Faner, dobbel rute (Ctrl+D), liste- og ikonvisning med miniatyrer for bilder/video
- Sortering, filtrering, skjulte filer, ny mappe, gi nytt navn (F2), slett (Del → papirkurv lokalt)
- Kopier (F5) / flytt (F6) til andre rute, dra-og-slipp mellom ruter og inn fra Windows Utforsker
- Overføring på tvers: lokal ↔ SFTP ↔ SFTP (overskriver aldri, gir «- kopi»)
- SSH/SFTP-tilkoblinger i sidepanelet (passord eller nøkkelfil). Passord lagres kryptert med Windows DPAPI.
  Vertsnøkkel huskes ved første tilkobling og tilkoblingen avvises hvis den senere endres.
- Terminalfaner (PowerShell/pwsh) og SSH-terminal (bruker Windows' innebygde `ssh.exe`),
  «Åpne terminal her» fra hvilken som helst mappe, lokal eller på serveren
- Snarveier: Ctrl+T ny fane, Ctrl+W lukk, Ctrl+D dobbel rute, Ctrl+L sti, Ctrl+Shift+T terminal

## Standard filbehandler
Bryteren «Standard filbehandler» nederst i sidepanelet får mapper og Win+E til å åpne i ZenFiles
(registeret under HKCU, ingen admin, opprinnelige verdier sikkerhetskopieres).
Angre: slå bryteren av, kjør `ZenFiles.exe --restore-default`, eller dobbeltklikk `Restore-Explorer.reg`.
Appen må bli liggende der den er når bryteren er på. Prøv med en publisert/stabil kopi, ikke bin\\Debug.

## Bygge
Krever Windows 10 1809+/11, .NET 8 SDK og Visual Studio 2022 med «.NET desktop development».

    dotnet restore
    dotnet build -c Debug -p:Platform=x64
    dotnet run -c Debug -p:Platform=x64

Eller åpne mappen i Visual Studio og velg x64.

## Merknader
- Terminalen bruker `EasyWindowsTerminalControl.WinUI` (uoffisiell, alfa). All bruk av den ligger i
  `Views/TerminalTab.cs`. Hvis API-et avviker, er det bare den filen som må justeres.
- Koden er skrevet uten mulighet til å kompilere, så små kompileringsfeil kan dukke opp ved første bygg.
