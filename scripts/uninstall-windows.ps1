# Online Co-op for Cuphead (unofficial) - Windows uninstaller.
#
#   irm https://github.com/nikrabaev/cuphead-online-coop/releases/latest/download/uninstall-windows.ps1 | iex
#
# If Cuphead isn't found automatically, first run:  $env:CUPHEAD_DIR = 'D:\SteamLibrary\steamapps\common\Cuphead'
# To keep the BepInEx mod loader, first run:         $env:CUPHEAD_ONLINE_KEEP_BEPINEX = '1'
#
# Removes the mod and its settings. BepInEx is removed too unless other BepInEx mods are still installed.
# Saves are not touched.
& {
    $ErrorActionPreference = 'Stop'

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

    try {
        $game = Find-Cuphead
        if (-not $game -or -not (Test-Path (Join-Path $game 'Cuphead.exe'))) {
            throw "Cuphead was not found. Set `$env:CUPHEAD_DIR to the folder containing Cuphead.exe and run the command again."
        }
        Say "Found Cuphead in $game"
        if (Get-Process -Name 'Cuphead' -ErrorAction SilentlyContinue) { throw 'Cuphead is running. Quit the game and run the uninstaller again.' }

        foreach ($f in @('BepInEx\plugins\CupheadOnline.dll', 'BepInEx\config\dev.cupheadonline.mod.cfg')) {
            Remove-Item -Path (Join-Path $game $f) -Force -ErrorAction SilentlyContinue
        }
        Get-ChildItem -Path (Join-Path $game 'BepInEx') -Filter 'CupheadOnline-*.log' -ErrorAction SilentlyContinue | Remove-Item -Force
        Say 'Removed the mod'

        $otherMods = @(Get-ChildItem -Path (Join-Path $game 'BepInEx\plugins'), (Join-Path $game 'BepInEx\patchers') -Recurse -File -ErrorAction SilentlyContinue)
        if ($env:CUPHEAD_ONLINE_KEEP_BEPINEX -eq '1') {
            Say 'Keeping BepInEx (CUPHEAD_ONLINE_KEEP_BEPINEX=1)'
        } elseif ($otherMods.Count -gt 0) {
            Say 'Keeping BepInEx: other BepInEx mods are still installed'
        } else {
            foreach ($f in @('BepInEx', 'winhttp.dll', 'doorstop_config.ini', '.doorstop_version')) {
                Remove-Item -Path (Join-Path $game $f) -Recurse -Force -ErrorAction SilentlyContinue
            }
            $changelog = Join-Path $game 'changelog.txt'
            if ((Test-Path $changelog) -and (Select-String -Path $changelog -Pattern 'since v5' -Quiet)) {
                Remove-Item $changelog -Force
            }
            Say 'Removed BepInEx'
        }

        Write-Host ''
        Say 'Done. Cuphead is back to normal; your saves were not touched.'
    } catch {
        Write-Host "Error: $($_.Exception.Message)" -ForegroundColor Red
    }
}
