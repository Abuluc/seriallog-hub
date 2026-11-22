<#
.SYNOPSIS
    Launches SerialLogHub.Api, SerialLogHub.Web, and SerialLogHub.Desktop locally in Development mode.
#>
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

Write-Host "Building SerialLogHub.sln..." -ForegroundColor Cyan
dotnet build "$root\SerialLogHub.sln"

Write-Host "Starting SerialLogHub.Api on http://localhost:8080 ..." -ForegroundColor Green
Start-Process pwsh -ArgumentList "-NoExit", "-Command", "cd '$root'; dotnet run --no-build --project src\SerialLogHub.Api --urls http://localhost:8080"

Start-Sleep -Seconds 2

Write-Host "Starting SerialLogHub.Web on http://localhost:3000 ..." -ForegroundColor Green
Start-Process pwsh -ArgumentList "-NoExit", "-Command", "cd '$root'; dotnet run --no-build --project src\SerialLogHub.Web --urls http://localhost:3000"

Start-Sleep -Seconds 2

Write-Host "Starting SerialLogHub.Desktop (COM Port Bridge)..." -ForegroundColor Green
Start-Process pwsh -ArgumentList "-NoExit", "-Command", "cd '$root'; dotnet run --no-build --project src\SerialLogHub.Desktop"

Write-Host "`nAll 3 SerialLog Hub applications launched. Open http://localhost:3000 in your browser." -ForegroundColor Yellow
