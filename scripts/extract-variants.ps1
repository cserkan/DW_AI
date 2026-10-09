<#
.SYNOPSIS
  DriveWorks çıktı klasöründeki varyant montajlarını toplu olarak okur (ruleforge extract).

.EXAMPLE
  .\scripts\extract-variants.ps1 -Folder C:\DriveWorks\Cikti -Pattern "Konveyor*.SLDASM" -Recurse

.NOTES
  Script çalışmazsa ("bu sistemde komut dosyalarının çalıştırılması devre dışı"): önce şunu yazın, sonra tekrar deneyin:
      Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
  -Pattern sadece ANA montajları seçmeli; alt montajlar seçilirse onlar da ayrı varyant sanılır.
  SolidWorks açıksa ona bağlanır, değilse arka planda başlatır.
#>
param(
    [Parameter(Mandatory = $true)] [string] $Folder,
    [string] $Pattern = "*.SLDASM",
    [string] $OutDir = "varyantlar",
    [switch] $Recurse
)

$ErrorActionPreference = "Stop"
$exe = Join-Path $PSScriptRoot "..\src\RuleForge.Cli\bin\Release\net48\ruleforge.exe"
if (-not (Test-Path $exe)) {
    Write-Host "ruleforge.exe bulunamadı. Önce derleyin:  dotnet build -c Release" -ForegroundColor Red
    exit 1
}

$files = Get-ChildItem -Path $Folder -Filter $Pattern -File -Recurse:$Recurse |
    Where-Object { $_.Name -notlike "~$*" } |   # SolidWorks geçici dosyaları
    Sort-Object FullName
if ($files.Count -eq 0) {
    Write-Host "Eşleşen montaj yok: $Folder\$Pattern" -ForegroundColor Red
    exit 1
}

Write-Host "$($files.Count) montaj okunacak:"
$files | ForEach-Object { Write-Host "  $($_.FullName)" }

& $exe extract @($files | ForEach-Object { $_.FullName }) --out-dir $OutDir
exit $LASTEXITCODE
