<#
.SYNOPSIS
  Varyant klasörlerindeki ÜST montajları otomatik bulup okur (ruleforge extract-variants kısayolu).
  Alt montajlar otomatik atlanır; ad filtresi (Pattern) gerekmez.

.EXAMPLE
  .\scripts\extract-variants.ps1 -Folder "C:\DriveWorks\Sonuclar"
  .\scripts\extract-variants.ps1 -Folder "C:\DriveWorks\Sonuclar" -List

.NOTES
  Doğrudan exe ile de çalışır (script izni gerekmez):
      .\src\RuleForge.Cli\bin\Release\net48\ruleforge.exe extract-variants "C:\DriveWorks\Sonuclar"
#>
param(
    [Parameter(Mandatory = $true)] [string] $Folder,
    [string] $OutDir = "varyantlar",
    [switch] $List,
    # Eski sürümle uyumluluk için kabul edilir, artık kullanılmıyor.
    [string] $Pattern,
    [switch] $Recurse
)

$exe = Join-Path $PSScriptRoot "..\src\RuleForge.Cli\bin\Release\net48\ruleforge.exe"
if (-not (Test-Path $exe)) {
    Write-Host "ruleforge.exe bulunamadı. Önce derleyin:  dotnet build -c Release" -ForegroundColor Red
    exit 1
}

$arguments = @("extract-variants", $Folder, "--out-dir", $OutDir)
if ($List) { $arguments += "--list" }
& $exe @arguments
exit $LASTEXITCODE
