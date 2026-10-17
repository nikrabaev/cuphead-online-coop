# Online Co-op for Cuphead (unofficial) - Windows installer (experimental: the mod is developed and tested on macOS).
#
#   irm https://github.com/nikrabaev/cuphead-online-coop/releases/latest/download/install-windows.ps1 | iex
#
# If Cuphead isn't found automatically, first run:  $env:CUPHEAD_DIR = 'D:\SteamLibrary\steamapps\common\Cuphead'
#
# What it does: downloads BepInEx 5.4.23.5 (the mod loader) from its official GitHub release and the mod from this
# project's latest release, verifies both against SHA-256 checksums and puts them into the Cuphead folder. On Windows
# BepInEx loads automatically (winhttp.dll next to the game), so no Steam launch options are needed.
& {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is very slow with the progress bar on PowerShell 5.1
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

    $Repo = 'nikrabaev/cuphead-online-coop'
    $ReleaseUrl = $env:CUPHEAD_ONLINE_RELEASE_URL
    $BepInEx = @{
        x64 = @{ Url = 'https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip'; Sha256 = '82f9878551030f54657792c0740d9d51a09500eeae1fba21106b0c441e6732c4' }
        x86 = @{ Url = 'https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x86_5.4.23.5.zip'; Sha256 = '37651c79e40d6f909572a4f461ac25350bb3ef8fe7fbd29f1aa8791a33b84c82' }
    }

    function Say([string]$Message) { Write-Host "==> $Message" -ForegroundColor Cyan }

    function Get-SteamPath {
        $candidates = @(
            @{ Key = 'HKCU:\Software\Valve\Steam'; Name = 'SteamPath' },
            @{ Key = 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam'; Name = 'InstallPath' },
            @{ Key = 'HKLM:\SOFTWARE\Valve\Steam'; Name = 'InstallPath' }
        )
        foreach ($c in $candidates) {
            $value = (Get-ItemProperty -Path $c.Key -Name $c.Name -ErrorAction SilentlyContinue).($c.Name)
            if ($value -and (Test-Path $value)) { return ($value -replace '/', '\') }
        }
        return "${env:ProgramFiles(x86)}\Steam"
    }

    function Find-Cuphead {
        if ($env:CUPHEAD_DIR) { return $env:CUPHEAD_DIR }
        $steam = Get-SteamPath
        $libraries = @($steam)
        $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) {
                $libraries += ($m.Groups[1].Value -replace '\\\\', '\')
            }
        }
        foreach ($lib in $libraries) {
            $dir = Join-Path $lib 'steamapps\common\Cuphead'
            if (Test-Path (Join-Path $dir 'Cuphead.exe')) { return $dir }
        }
        return $null
    }

    # 64-bit or 32-bit game? Read the machine field of the PE header.
    function Get-GameArchitecture([string]$Exe) {
        $stream = [IO.File]::OpenRead($Exe)
        try {
            $reader = New-Object IO.BinaryReader($stream)
            $stream.Position = 0x3C
            $stream.Position = $reader.ReadInt32() + 4
            $machine = $reader.ReadUInt16()
        } finally { $stream.Dispose() }
        switch ($machine) { 0x8664 { 'x64' } 0x14C { 'x86' } default { throw "Unknown Cuphead.exe architecture ($machine)." } }
    }

    function Get-Download([string]$Url, [string]$Path) {
        Invoke-WebRequest -Uri $Url -OutFile $Path -UseBasicParsing
    }

    function Assert-Sha256([string]$Path, [string]$Expected, [string]$What) {
        $actual = (Get-FileHash -Path $Path -Algorithm SHA256).Hash
        if ($actual -ne $Expected.ToUpperInvariant()) { throw "$What failed its checksum; nothing was changed." }
    }

    try {
        $game = Find-Cuphead
        if (-not $game -or -not (Test-Path (Join-Path $game 'Cuphead.exe'))) {
            throw "Cuphead was not found in your Steam libraries. Set `$env:CUPHEAD_DIR to the folder containing Cuphead.exe and run the command again."
        }
        Say "Found Cuphead in $game"
        if (Get-Process -Name 'Cuphead' -ErrorAction SilentlyContinue) { throw 'Cuphead is running. Quit the game and run the installer again.' }

        $tmp = Join-Path ([IO.Path]::GetTempPath()) ("cuphead-online-" + [Guid]::NewGuid())
        New-Item -ItemType Directory -Path $tmp | Out-Null
        try {
            # Download and verify everything before changing anything in the game folder.
            $zip = $null
            if ((Test-Path (Join-Path $game 'BepInEx\core\BepInEx.Preloader.dll')) -and (Test-Path (Join-Path $game 'winhttp.dll'))) {
                Say 'BepInEx is already installed'
            } else {
                $arch = Get-GameArchitecture (Join-Path $game 'Cuphead.exe')
                Say "Downloading BepInEx 5.4.23.5 ($arch, mod loader)"
                $zip = Join-Path $tmp 'bepinex.zip'
                Get-Download $BepInEx[$arch].Url $zip
                Assert-Sha256 $zip $BepInEx[$arch].Sha256 'BepInEx download'
            }

            # Pin one release for every file: "latest/download/..." links are cached separately and can briefly
            # point at different releases right after a new one is published.
            $tag = $null
            if (-not $ReleaseUrl) {
                try { $tag = (Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -UseBasicParsing).tag_name } catch { }
                $ReleaseUrl = if ($tag) { "https://github.com/$Repo/releases/download/$tag" } else { "https://github.com/$Repo/releases/latest/download" }
            }
            Say ("Downloading the mod" + $(if ($tag) { " $tag" } else { '' }))
            $dll = Join-Path $tmp 'CupheadOnline.dll'
            $sums = Join-Path $tmp 'SHA256SUMS.txt'
            Get-Download "$ReleaseUrl/CupheadOnline.dll" $dll
            Get-Download "$ReleaseUrl/SHA256SUMS.txt" $sums
            $line = Get-Content $sums | Where-Object { $_ -match '^([0-9a-fA-F]{64})\s+\*?CupheadOnline\.dll$' } | Select-Object -First 1
            if (-not $line) { throw 'The release has no checksum for CupheadOnline.dll; nothing was changed.' }
            Assert-Sha256 $dll ($line -split '\s+')[0] 'Mod download'

            if ($zip) { Expand-Archive -Path $zip -DestinationPath $game -Force }
            $plugins = Join-Path $game 'BepInEx\plugins'
            New-Item -ItemType Directory -Path $plugins -Force | Out-Null
            Copy-Item $dll (Join-Path $plugins 'CupheadOnline.dll') -Force
            Get-ChildItem -Path $game -Recurse -Include '*.dll' -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -like (Join-Path $game 'BepInEx\*') -or $_.Name -eq 'winhttp.dll' } |
                Unblock-File -ErrorAction SilentlyContinue
            Say "Mod installed into $plugins"
        } finally {
            Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
        }

        Write-Host ''
        Say 'Done. Start Cuphead from Steam as usual and press F8 in game to host or join.'
        Write-Host '    Your friend needs the mod too. To uninstall:'
        Write-Host '    irm https://github.com/nikrabaev/cuphead-online-coop/releases/latest/download/uninstall-windows.ps1 | iex'
    } catch {
        Write-Host "Error: $($_.Exception.Message)" -ForegroundColor Red
    }
}
