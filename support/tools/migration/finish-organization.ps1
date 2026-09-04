param([switch]$Apply)
$ErrorActionPreference='Stop'
$repoPath=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
function Scoped([string]$relative) {
 $path=[IO.Path]::GetFullPath((Join-Path $repoPath $relative))
 if(!$path.StartsWith($repoPath+'\',[StringComparison]::OrdinalIgnoreCase)) {throw 'Outside repository'}
 return $path
}
$moves=[ordered]@{
 'support/docs/core-pipeline.md'='support/docs/architecture/core-pipeline.md'
 'support/docs/bm25-index.md'='support/docs/architecture/bm25-index.md'
 'support/docs/graph-construction.md'='support/docs/architecture/graph-construction.md'
 'support/docs/desktop-webview2-architecture.md'='support/docs/migration/legacy-desktop-webview2-architecture.md'
 'support/docs/infinitool-architecture-migration-feasibility.md'='support/docs/migration/infinitool-architecture-migration-feasibility.md'
 'support/docs/infinitool-migration-status.md'='support/docs/migration/infinitool-migration-status.md'
 'support/artifacts/migration-web'='support/artifacts/publish/web'
 'support/artifacts/migration-desktop'='support/artifacts/publish/desktop'
 'ReferenceRAG.sln'='support/artifacts/legacy/ReferenceRAG.sln'
 'ReferenceRAG.slnLaunch.user'='support/artifacts/legacy/ReferenceRAG.slnLaunch.user'
}
foreach($file in Get-ChildItem -LiteralPath (Scoped 'support/docs') -File) {
 if($file.Name -ne 'PREVIEW.md' -and !$moves.Contains('support/docs/'+$file.Name)) { $moves['support/docs/'+$file.Name]='support/docs/operations/'+$file.Name }
}
$deletes=@('support/artifacts/legacy/dashboard-vue/node_modules','support/artifacts/legacy/dashboard-vue/dist')
foreach($project in @('ReferenceRAG.Core','ReferenceRAG.Storage','ReferenceRAG.Service','ReferenceRAG.Desktop')) {
 foreach($cache in @('bin','obj')) {$deletes+='support/artifacts/legacy/src/'+$project+'/'+$cache}
}
foreach($entry in $moves.GetEnumerator()) {
 $source=Scoped $entry.Key; $destination=Scoped $entry.Value
 if(!(Test-Path -LiteralPath $source) -or (Test-Path -LiteralPath $destination)) {throw "Invalid move: $source -> $destination"}
 Write-Output "MOVE $source -> $destination"
}
foreach($relative in $deletes) {
 $path=Scoped $relative
 if(!(Test-Path -LiteralPath $path)) {continue}
 $links=@(Get-Item -LiteralPath $path -Force)+@(Get-ChildItem -LiteralPath $path -Recurse -Force -Attributes ReparsePoint)
 if($links | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) {throw "Reparse point in $path"}
 Write-Output "DELETE regenerable cache: $path"
}
if(!$Apply){return}
foreach($entry in $moves.GetEnumerator()) {
 $source=Scoped $entry.Key; $destination=Scoped $entry.Value
 New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
 Move-Item -LiteralPath $source -Destination $destination
}
foreach($relative in $deletes) { $path=Scoped $relative; if(Test-Path -LiteralPath $path) {Remove-Item -LiteralPath $path -Recurse -Force} }
