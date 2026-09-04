param([switch]$Apply)
$ErrorActionPreference = 'Stop'
$repoPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$plan = @()
foreach ($project in @('ReferenceRAG.Core','ReferenceRAG.Storage','ReferenceRAG.Service')) {
    $sourceRoot = Join-Path $repoPath "src/$project"
    foreach ($relative in (& rg --files $sourceRoot -g '*.cs' -g 'FodyWeavers.xml')) {
        $sourcePath = [IO.Path]::GetFullPath($relative)
        if ($sourcePath -match '[\\/](bin|obj)[\\/]') { continue }
        $local = [IO.Path]::GetRelativePath($sourceRoot, $sourcePath).Replace('\','/')
        $destination = ''
        if ($project -eq 'ReferenceRAG.Core') {
            $business = $local -match '^Domains/(Chunking|Indexing|Graph|Search)/' -and $local -notmatch '/Interfaces/'
            if ($local -match 'QueryStatsModels.cs|MetricsCollector.cs|AlertService.cs') { $business = $false }
            if ($local -match 'IHybridSearchService.cs') { $business = $true }
            if ($local -eq 'FodyWeavers.xml') { continue }
            $base = if ($business) { 'Business/ReferenceRAG.Business' } else { 'Infrastructure/ReferenceRAG.Infrastructure' }
            $destination = "$base/" + $local.Replace('Domains/','Features/')
        } elseif ($project -eq 'ReferenceRAG.Storage') {
            if ($local -eq 'AssemblyInfo.cs') { continue }
            $destination = "Infrastructure/ReferenceRAG.Infrastructure/Features/Database/$local"
        } else {
            if ($local -eq 'Program.cs') { $destination = 'Host/ReferenceRAG.WebHost/Program.cs' }
            elseif ($local -match '^Services/(AutoIndexService|StartupSyncService|OrphanCleanupService|IndexService)\.cs$') {
                $destination = 'Business/ReferenceRAG.Business/Features/Indexing/Services/' + [IO.Path]::GetFileName($local)
            } elseif ($local -eq 'Services/MafChatService.cs') { $destination = 'Business/ReferenceRAG.Business/Features/Chat/Services/MafChatService.cs' }
            else { $destination = "Host/ReferenceRAG.ApiHost/$local" }
        }
        $targetPath = [IO.Path]::GetFullPath((Join-Path $repoPath $destination))
        if (!$sourcePath.StartsWith($repoPath + [IO.Path]::DirectorySeparatorChar) -or !$targetPath.StartsWith($repoPath + [IO.Path]::DirectorySeparatorChar)) { throw 'Outside workspace' }
        if (Test-Path -LiteralPath $targetPath) { throw "Target exists: $targetPath" }
        $cursor = Get-Item -LiteralPath $sourcePath
        while ($cursor -and $cursor.FullName -ne $repoPath) {
            if ($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point: $($cursor.FullName)" }
            $cursor = if ($cursor.PSIsContainer) { $cursor.Parent } else { $cursor.Directory }
        }
        $plan += [PSCustomObject]@{ Source = $sourcePath; Destination = $targetPath }
    }
}
if (!$Apply) { $plan | Group-Object { [IO.Path]::GetRelativePath($repoPath,$_.Destination).Split([IO.Path]::DirectorySeparatorChar)[0] } | Select-Object Name,Count; return }
foreach ($item in $plan) {
    $parentPath = Split-Path -Parent $item.Destination
    [IO.Directory]::CreateDirectory($parentPath) | Out-Null
    Move-Item -LiteralPath $item.Source -Destination $item.Destination
}
Write-Output "Moved $($plan.Count) source files; project/config/data files retained."
