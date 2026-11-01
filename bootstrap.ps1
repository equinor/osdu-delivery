# OSDU Delivery on this laptop, from nothing: it fetches the code, builds it, starts everything, and runs both sample estates.
#
#   irm https://raw.githubusercontent.com/TahirRiaz/osdu-delivery/main/bootstrap.ps1 | iex
#
# It needs a SQL Server, or Docker Desktop to run one, and Windows' winget for what it offers to install (Git, Node.js; dev.bat
# offers .NET). The environment uses the public sample estates and synthetic data, and a stand-in for OSDU on this machine: it
# reaches no real OSDU. In a folder that already holds an environment of another kind it stops and says so, and changes nothing.
#
#   OSDU_DELIVERY_FOLDER    where the code goes (default: .\osdu-delivery)
#   OSDU_DELIVERY_BRANCH    the branch to fetch (default: main)
#   OSDU_DELIVERY_RESET     1 starts over: an earlier demo's databases and records are removed first
#
# Everything is inside a function and a failure is a thrown message: run through iex this is the person's own session, where
# 'exit' would close their window with the reason still on it, and a preference set out here would stay set.
function Invoke-OsduDeliveryBootstrap {
    $ErrorActionPreference = "Stop"
    $repository = "https://github.com/TahirRiaz/osdu-delivery.git"
    $folder = if ($env:OSDU_DELIVERY_FOLDER) { $env:OSDU_DELIVERY_FOLDER } else { "osdu-delivery" }
    $branch = if ($env:OSDU_DELIVERY_BRANCH) { $env:OSDU_DELIVERY_BRANCH } else { "main" }

    # A tool installed a moment ago is on no running terminal's PATH: this finds it where its installer puts it.
    function Find-Tool([string]$name, [string[]]$folders) {
        if (Get-Command $name -ErrorAction SilentlyContinue) { return $true }
        foreach ($candidate in $folders) {
            if (Test-Path (Join-Path $candidate "$name.exe")) {
                $env:PATH = "$candidate;$env:PATH"
                return $true
            }
        }

        return $false
    }

    function Install-Tool([string]$name, [string]$package, [string]$why, [string[]]$folders) {
        if (Find-Tool $name $folders) { return }
        Write-Host "$name is not installed. $why"
        if (-not (Get-Command winget -ErrorAction SilentlyContinue)) { throw "Install it ($package), then run this again." }
        $answer = Read-Host "Install it now with winget? [Y/n]"
        if ($answer -match '^(n|no)$') { throw "Install it ($package), then run this again." }
        winget install --id $package --exact --accept-package-agreements --accept-source-agreements
        if (-not (Find-Tool $name $folders)) { throw "winget did not leave $name this terminal can run; open a new terminal and run this again." }
    }

    Write-Host ""
    Write-Host "OSDU Delivery, on this laptop"
    Install-Tool "git" "Git.Git" "It fetches the code." @("$env:ProgramFiles\Git\cmd", "$env:LOCALAPPDATA\Programs\Git\cmd")
    Install-Tool "node" "OpenJS.NodeJS.LTS" "It runs the GUI and the stand-in for OSDU." @("$env:ProgramFiles\nodejs", "$env:LOCALAPPDATA\Programs\nodejs")

    if (Test-Path (Join-Path $folder "dev.env.example")) {
        Write-Host "Using the code already in $folder."
        & cmd /c "git -C `"$folder`" pull --ff-only --quiet 2>nul"
        if ($LASTEXITCODE -ne 0) { Write-Host "It could not be brought up to date, so it runs as it stands." }
    } elseif ((Test-Path $folder) -and (Get-ChildItem -Force $folder | Select-Object -First 1)) {
        throw "$folder holds something else. Set OSDU_DELIVERY_FOLDER to a new folder, then run this again."
    } else {
        Write-Host "Fetching the code into $folder ..."
        & git clone --depth 1 --branch $branch -c core.longpaths=true $repository $folder
        if ($LASTEXITCODE -ne 0) { throw "git could not fetch $repository; its output above says why." }
    }

    $arguments = if ($env:OSDU_DELIVERY_RESET -eq "1") { "demo reset" } else { "demo" }
    Push-Location $folder
    try {
        & cmd /c ".\dev.bat $arguments"
        if ($LASTEXITCODE -ne 0) { throw "dev.bat stopped; what it says above is why." }
    } finally {
        Pop-Location
    }
}

try {
    Invoke-OsduDeliveryBootstrap
} catch {
    Write-Host "[X] $($_.Exception.Message)" -ForegroundColor Red
}
