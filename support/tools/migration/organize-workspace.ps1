param([switch]$Apply)
$ErrorActionPreference = 'Stop'
$repoPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (!(Test-Path -LiteralPath (Join-Path $repoPath 'Directory.Build.props'))) { $repoPath = [IO.Path]::GetFullPath((Join-Path $repoPath '..')) }
if (!(Test-Path -LiteralPath (Join-Path $repoPath 'Directory.Build.props'))) { throw 'Repository root not found' }
$moves = [ordered]@{
 'src/ReferenceRAG.Service/scripts'='support/tools/scripts'
 'src/ReferenceRAG.Service/appsettings.json'='support/config/local/appsettings.json'
 'src/ReferenceRAG.Service/appsettings.Development.json'='support/config/local/appsettings.Development.json'
 'src/ReferenceRAG.Service/appsettings.Development.Exsample.json'='support/config/examples/appsettings.Development.example.json'
 'src/ReferenceRAG.Service/ReferenceRAG.Service.http'='support/tools/http/ReferenceRAG.http'
 'src/ReferenceRAG.Service/dotnet-tools.json'='support/tools/dotnet-tools.json'
 'config/appsettings.Production.json'='support/config/local/docker/appsettings.Production.json'
 'Dockerfile'='support/deploy/docker/Dockerfile'
 'Dockerfile.gpu'='support/deploy/docker/Dockerfile.gpu'
 'docker-compose.yml'='support/deploy/docker/compose.yml'
 'appsettings.Docker.json'='support/config/examples/appsettings.Docker.example.json'
 'docs'='support/docs'
 'images'='support/docs/images'
 'PREVIEW.md'='support/docs/PREVIEW.md'
 'skill'='support/skill'
 'data'='support/data'
 'models'='support/models'
 'artifacts'='support/artifacts'
 'publish'='support/artifacts/publish/legacy'
 '.migration-fixture'='support/artifacts/test-runs/web'
 '.migration-desktop-fixture'='support/artifacts/test-runs/desktop'
 'resource'='support/artifacts/legacy/resource'
 'dashboard-vue'='support/artifacts/legacy/dashboard-vue'
 'src'='support/artifacts/legacy/src'
 'config'='support/artifacts/legacy/config'
 'tools/DatabaseInspector'='support/tools/DatabaseInspector'
 'tools/README.md'='support/tools/README.md'
 'tools/migration'='support/tools/migration'
}
foreach($log in Get-ChildItem -LiteralPath $repoPath -File -Force -Filter '*.log') { $moves[$log.Name]='support/artifacts/logs/migration/'+$log.Name }
function Resolve-Scoped([string]$relative) {
 $full=[IO.Path]::GetFullPath((Join-Path $repoPath $relative))
 if(!$full.StartsWith($repoPath+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw "Outside workspace: $full" }
 return $full
}
foreach($entry in $moves.GetEnumerator()) {
 $source=Resolve-Scoped $entry.Key; $destination=Resolve-Scoped $entry.Value
 if(!(Test-Path -LiteralPath $source)) { throw "Missing source: $source" }
 if(Test-Path -LiteralPath $destination) { throw "Destination exists: $destination" }
 if((Get-Item -LiteralPath $source -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point: $source" }
 Write-Output "$source -> $destination"
}
if(!$Apply) { return }
foreach($entry in $moves.GetEnumerator()) {
 $source=Resolve-Scoped $entry.Key; $destination=Resolve-Scoped $entry.Value
 New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
 Move-Item -LiteralPath $source -Destination $destination -ErrorAction Stop
}
# This directory must be empty after the explicit moves above.
[IO.Directory]::Delete((Join-Path $repoPath 'tools'),$false)
