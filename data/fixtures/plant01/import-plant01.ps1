# Imports the PLANT-01 five-asset sample dataset into a running API through the FR02 import endpoints.
# P-101, BR-101, TECH-001..TECH-003 and the two maintenance windows are created by the application's startup seed;
# this script adds P-102..P-105, their parts, the service manual, their tasks and the maintenance history.
# Imports are insert-only and all-or-nothing, so running it again changes nothing: already-imported IDs are reported.
#
# Usage (with the API running):  powershell -File data/fixtures/plant01/import-plant01.ps1 [-ApiBase http://localhost:5083]
param([string]$ApiBase = "http://localhost:5083")

$ErrorActionPreference = "Stop"
$fixtures = $PSScriptRoot

$steps = @(
    @{ Path = "/api/imports/assets"; File = "01-assets.csv" },
    @{ Path = "/api/imports/spare-parts"; File = "02-spare-parts.csv" },
    @{ Path = "/api/imports/manuals"; File = "MAN-PLANT01-SERVICE-001.pdf"; Fields = @("documentId=MAN-PLANT01-SERVICE-001", "version=1.0") },
    @{ Path = "/api/imports/tasks"; File = "04-tasks.json" },
    @{ Path = "/api/imports/maintenance-history"; File = "05-maintenance-history.csv" }
)

foreach ($step in $steps) {
    $arguments = @("-s", "-w", "`n%{http_code}", "-F", "file=@$(Join-Path $fixtures $step.File)")
    foreach ($field in $step.Fields) { $arguments += @("-F", $field) }
    $output = & curl.exe @arguments "$ApiBase$($step.Path)"
    $status = $output[-1]
    $report = ($output | Select-Object -SkipLast 1) -join "`n"
    Write-Host "$($step.File) -> HTTP $status"
    Write-Host $report
    if ($status -ne "200") {
        Write-Host "Import stopped at $($step.File); nothing from this file was saved. Earlier files remain imported."
        exit 1
    }
}

Write-Host "PLANT-01 sample dataset imported."
