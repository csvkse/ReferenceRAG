# ==============================================================================
# merge-appsettings.ps1
# Preserves user configuration (API keys, sources, data paths) when republishing
# ==============================================================================
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishedPath,

    [Parameter(Mandatory = $true)]
    [string]$BackupPath
)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

if (-not (Test-Path $BackupPath)) {
    Write-Host "[merge-appsettings] No backup found at $BackupPath, skipping merge."
    exit 0
}

if (-not (Test-Path $PublishedPath)) {
    Write-Warning "[merge-appsettings] Published file not found at $PublishedPath, cannot merge."
    exit 0
}

try {
    $backupRaw = [System.IO.File]::ReadAllText($BackupPath, [System.Text.Encoding]::UTF8)
    $publishedRaw = [System.IO.File]::ReadAllText($PublishedPath, [System.Text.Encoding]::UTF8)

    if ([string]::IsNullOrWhiteSpace($backupRaw) -or [string]::IsNullOrWhiteSpace($publishedRaw)) {
        Write-Warning "[merge-appsettings] Backup or published file is empty, skipping merge."
        exit 0
    }

    $backupObj = $backupRaw | ConvertFrom-Json
    $publishedObj = $publishedRaw | ConvertFrom-Json

    if ($null -eq $backupObj -or $null -eq $publishedObj) {
        Write-Warning "[merge-appsettings] JSON deserialize failed, skipping merge."
        exit 0
    }

    # Recursive merge: preserve existing user values ($backupObj) and supplement new properties from $publishedObj
    function Merge-JsonStructure($target, $source) {
        foreach ($prop in $source.PSObject.Properties) {
            $name = $prop.Name
            $targetProp = $target.PSObject.Properties[$name]
            if ($null -eq $targetProp) {
                # New property in published template, add it to target
                $target | Add-Member -NotePropertyName $name -NotePropertyValue $prop.Value
            } elseif ($prop.Value -is [System.Management.Automation.PSCustomObject] -and $targetProp.Value -is [System.Management.Automation.PSCustomObject]) {
                Merge-JsonStructure $targetProp.Value $prop.Value
            }
        }
    }

    Merge-JsonStructure $backupObj $publishedObj

    # Diagnostic tracking for preserved secrets and core paths
    $preserved = [System.Collections.Generic.List[string]]::new()

    if ($backupObj.ReferenceRAG -and $backupObj.ReferenceRAG.chat -and $backupObj.ReferenceRAG.chat.openAiApi -and -not [string]::IsNullOrWhiteSpace($backupObj.ReferenceRAG.chat.openAiApi.apiKey)) {
        $preserved.Add("ReferenceRAG.chat.openAiApi.apiKey")
    }
    if ($backupObj.Chat -and -not [string]::IsNullOrWhiteSpace($backupObj.Chat.ApiKey)) {
        $preserved.Add("Chat.ApiKey (legacy)")
    }
    if ($backupObj.ReferenceRAG -and $backupObj.ReferenceRAG.service -and -not [string]::IsNullOrWhiteSpace($backupObj.ReferenceRAG.service.apiKey)) {
        $preserved.Add("ReferenceRAG.service.apiKey")
    }
    if ($backupObj.ReferenceRAG -and $backupObj.ReferenceRAG.embedding -and -not [string]::IsNullOrWhiteSpace($backupObj.ReferenceRAG.embedding.apiKey)) {
        $preserved.Add("ReferenceRAG.embedding.apiKey")
    }
    if ($backupObj.ReferenceRAG -and $backupObj.ReferenceRAG.rerank -and -not [string]::IsNullOrWhiteSpace($backupObj.ReferenceRAG.rerank.apiKey)) {
        $preserved.Add("ReferenceRAG.rerank.apiKey")
    }
    if ($backupObj.ReferenceRAG -and $backupObj.ReferenceRAG.modelHub -and -not [string]::IsNullOrWhiteSpace($backupObj.ReferenceRAG.modelHub.hfToken)) {
        $preserved.Add("ReferenceRAG.modelHub.hfToken")
    }
    if ($backupObj.ReferenceRAG -and -not [string]::IsNullOrWhiteSpace($backupObj.ReferenceRAG.dataPath)) {
        $preserved.Add("ReferenceRAG.dataPath")
    }
    if ($backupObj.ReferenceRAG -and $backupObj.ReferenceRAG.sources -and $backupObj.ReferenceRAG.sources.Count -gt 0) {
        $preserved.Add("ReferenceRAG.sources ($($backupObj.ReferenceRAG.sources.Count) items)")
    }

    $jsonOut = $backupObj | ConvertTo-Json -Depth 100

    # Unescape \uXXXX unicode characters back to readable UTF-8
    $fixedJson = [System.Text.RegularExpressions.Regex]::Replace($jsonOut, '\\u([0-9a-fA-F]{4})', {
        param($m) [char][int]::Parse($m.Groups[1].Value, [System.Globalization.NumberStyles]::HexNumber)
    })

    [System.IO.File]::WriteAllText($PublishedPath, $fixedJson, [System.Text.Encoding]::UTF8)

    Write-Host "[merge-appsettings] Successfully restored and merged user config into: $PublishedPath"
    if ($preserved.Count -gt 0) {
        Write-Host "[merge-appsettings] Preserved protected items: $($preserved -join ', ')"
    }
}
catch {
    Write-Warning "[merge-appsettings] Merge failed: $($_.Exception.Message)"
}