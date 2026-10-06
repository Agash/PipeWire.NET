#!/usr/bin/env pwsh
# Merges Cobertura coverage reports from several machines into one, and prints the totals.
#
# Each report records absolute source paths from the machine that produced it (D:\a\..., /home/runner/...,
# /Users/...), and the merge matches files by path, so the same file from two platforms would be counted
# twice rather than combined. The paths are rewritten to repository-relative form first.
#
# -Exclude drops the files matching any of its repository-relative wildcard patterns before the merge,
# so a gate can measure only the code a set of machines is able to run.
#
# Usage:  ./eng/merge-coverage.ps1 -Reports <cobertura files...> -Output merged.cobertura.xml [-Exclude <patterns...>]
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string[]]$Reports,
    [Parameter(Mandatory)] [string]$Output,
    [string[]]$Exclude = @()
)

$ErrorActionPreference = 'Stop'
$work = Join-Path ([IO.Path]::GetTempPath()) "pipewire-net-coverage-$PID"
New-Item -ItemType Directory -Force $work | Out-Null
try {
    $normalized = @()
    $index = 0
    foreach ($report in $Reports) {
        [xml]$xml = Get-Content -Raw $report
        foreach ($node in $xml.SelectNodes('//*[@filename]')) {
            $path = $node.GetAttribute('filename').Replace('\', '/')
            $match = [regex]::Match($path, '(?:^|/)((?:src|tests|samples)/.*)$')
            if ($match.Success) { $node.SetAttribute('filename', $match.Groups[1].Value) }
        }

        foreach ($class in @($xml.SelectNodes('//class'))) {
            $file = $class.GetAttribute('filename')
            if (@($Exclude | Where-Object { $file -like $_ }).Count -gt 0) { [void]$class.ParentNode.RemoveChild($class) }
        }

        foreach ($source in @($xml.SelectNodes('//sources/source'))) { $source.InnerText = '.' }
        $target = Join-Path $work "$((++$index)).cobertura.xml"
        $xml.Save($target)
        $normalized += $target
    }

    dnx dotnet-coverage@18.11.2 --yes -- merge --output $Output --output-format cobertura @normalized
    if ($LASTEXITCODE -ne 0) { throw "dotnet-coverage merge failed ($LASTEXITCODE)." }
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

[xml]$merged = Get-Content -Raw $Output
[pscustomobject]@{
    Lines          = [double]$merged.coverage.'line-rate'
    Branches       = [double]$merged.coverage.'branch-rate'
    LinesCovered   = [int]$merged.coverage.'lines-covered'
    LinesValid     = [int]$merged.coverage.'lines-valid'
    BranchesCovered = [int]$merged.coverage.'branches-covered'
    BranchesValid  = [int]$merged.coverage.'branches-valid'
}
