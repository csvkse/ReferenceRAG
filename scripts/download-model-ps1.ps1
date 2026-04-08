# ObsidianRAG 模型下载脚本 (PowerShell)
# 使用方法: powershell -ExecutionPolicy Bypass -File scripts/download-model-ps1.ps1

param(
    [string]$Model = "bge-small-zh-v1.5"
)

$ErrorActionPreference = "Stop"

Write-Host "=== ObsidianRAG 模型下载脚本 ===" -ForegroundColor Cyan
Write-Host ""

# 模型配置
$models = @{
    "bge-small-zh-v1.5" = @{
        Repo = "BAAI/bge-small-zh-v1.5"
        Files = @("model.onnx", "tokenizer.json", "tokenizer_config.json", "vocab.txt", "special_tokens_map.json")
        Dim = 384
    }
    "bge-base-zh-v1.5" = @{
        Repo = "BAAI/bge-base-zh-v1.5"
        Files = @("model.onnx", "tokenizer.json", "tokenizer_config.json", "vocab.txt", "special_tokens_map.json")
        Dim = 768
    }
    "bge-large-zh-v1.5" = @{
        Repo = "BAAI/bge-large-zh-v1.5"
        Files = @("model.onnx", "tokenizer.json", "tokenizer_config.json", "vocab.txt", "special_tokens_map.json")
        Dim = 1024
    }
}

if (-not $models.ContainsKey($Model)) {
    Write-Host "未知模型: $Model" -ForegroundColor Red
    Write-Host "可用模型: $($models.Keys -join ', ')"
    exit 1
}

$config = $models[$Model]
$modelDir = "models/$Model"
$repo = $config.Repo

Write-Host "模型: $Model ($($config.Dim) 维)" -ForegroundColor Yellow
Write-Host "仓库: $repo" -ForegroundColor Yellow
Write-Host "目录: $modelDir" -ForegroundColor Yellow
Write-Host ""

# 创建目录
if (-not (Test-Path $modelDir)) {
    New-Item -ItemType Directory -Path $modelDir -Force | Out-Null
    Write-Host "创建目录: $modelDir" -ForegroundColor Green
}

# 下载文件
$baseUrl = "https://huggingface.co/$repo/resolve/main"
$webClient = New-Object System.Net.WebClient

foreach ($file in $config.Files) {
    $url = "$baseUrl/$file"
    $dest = Join-Path $modelDir $file
    
    if (Test-Path $dest) {
        Write-Host "  ✓ 已存在: $file" -ForegroundColor Gray
        continue
    }
    
    Write-Host "  下载: $file ..." -NoNewline
    
    try {
        $webClient.DownloadFile($url, $dest)
        Write-Host " ✓" -ForegroundColor Green
    }
    catch {
        Write-Host " ✗" -ForegroundColor Red
        Write-Host "    错误: $_" -ForegroundColor Red
        
        # 尝试从 onnx 子目录下载
        if ($file -eq "model.onnx") {
            $altUrl = "$baseUrl/onnx/model.onnx"
            Write-Host "  尝试备用路径: onnx/model.onnx ..." -NoNewline
            try {
                $webClient.DownloadFile($altUrl, $dest)
                Write-Host " ✓" -ForegroundColor Green
            }
            catch {
                Write-Host " ✗" -ForegroundColor Red
            }
        }
    }
}

# 检查结果
Write-Host ""
if (Test-Path "$modelDir/model.onnx") {
    Write-Host "✅ 模型下载成功!" -ForegroundColor Green
    Write-Host ""
    Write-Host "文件列表:" -ForegroundColor Cyan
    Get-ChildItem $modelDir | ForEach-Object {
        $size = $_.Length / 1MB
        Write-Host "  $($_.Name) ($($size.ToString('F2')) MB)"
    }
    Write-Host ""
    Write-Host "配置路径:" -ForegroundColor Cyan
    Write-Host "  ModelPath: $((Resolve-Path $modelDir).Path)\model.onnx"
}
else {
    Write-Host "❌ 模型下载失败" -ForegroundColor Red
    Write-Host ""
    Write-Host "请手动下载:" -ForegroundColor Yellow
    Write-Host "  1. 访问 https://huggingface.co/$repo"
    Write-Host "  2. 下载以下文件到 $modelDir 目录:"
    foreach ($file in $config.Files) {
        Write-Host "     - $file"
    }
}
