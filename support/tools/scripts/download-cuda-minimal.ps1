<#
.SYNOPSIS
    ReferenceRAG 便携式微型 CUDA 12 核心库下载与部署脚本 (严格幂等)

.DESCRIPTION
    按需拉取并解压 ONNX Runtime GPU (CUDA 12) 与 LLamaSharp 所必需的核心加速动态链接库:
    - cublas64_12.dll
    - cublasLt64_12.dll
    - cudart64_12.dll (可选)
    
    幂等性规则:
    当目标目录中已存在有效且尺寸完整的 cublas64_12.dll 与 cublasLt64_12.dll 时，
    脚本将立即退出并跳过所有网络下载，杜绝重复下载与流量浪费。
    若需重新覆盖拉取，请指定 -Force 开关。
#>

[CmdletBinding()]
param(
    [string]$TargetDir = "",
    [string]$CustomUrl = "",
    [switch]$Force = $false,
    [switch]$SkipCudart = $false
)

$ErrorActionPreference = "Stop"

# 1. 计算默认路径
if ([string]::IsNullOrWhiteSpace($TargetDir)) {
    $scriptDir = $PSScriptRoot
    if ([string]::IsNullOrWhiteSpace($scriptDir)) {
        $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    }
    $rootDir = (Get-Item "$scriptDir\..\..\..").FullName
    $TargetDir = Join-Path $rootDir "runtimes\cuda"
}

$TargetDir = [System.IO.Path]::GetFullPath($TargetDir)

Write-Host ""
Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  ReferenceRAG - Portable Minimal CUDA 12 Downloader" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "目标安装路径: $TargetDir" -ForegroundColor Gray
Write-Host "强制覆盖模式: $Force" -ForegroundColor Gray

# 2. 严格幂等性检查 (Idempotency Check)
$cublasPath   = Join-Path $TargetDir "cublas64_12.dll"
$cublasLtPath = Join-Path $TargetDir "cublasLt64_12.dll"
$cudartPath   = Join-Path $TargetDir "cudart64_12.dll"

$minValidBytes = 10 * 1024 * 1024 # 10 MB

if ((-not $Force) -and (Test-Path $cublasPath) -and (Test-Path $cublasLtPath)) {
    try {
        $cublasSize = (Get-Item $cublasPath).Length
        $cublasLtSize = (Get-Item $cublasLtPath).Length
        
        if (($cublasSize -gt $minValidBytes) -and ($cublasLtSize -gt $minValidBytes)) {
            $cbMb = [math]::Round($cublasSize / 1MB, 2)
            $ltMb = [math]::Round($cublasLtSize / 1MB, 2)

            Write-Host ""
            Write-Host "[download-cuda] 检测到本地已存在完整的 CUDA 12 核心运行库 (幂等生效):" -ForegroundColor Green
            Write-Host "  - cublas64_12.dll   ($cbMb MB)" -ForegroundColor Green
            Write-Host "  - cublasLt64_12.dll ($ltMb MB)" -ForegroundColor Green
            if (Test-Path $cudartPath) {
                $rtKb = [math]::Round((Get-Item $cudartPath).Length / 1KB, 2)
                Write-Host "  - cudart64_12.dll   ($rtKb KB)" -ForegroundColor Green
            }
            Write-Host "[download-cuda] 本地运行库状态就绪，跳过网络请求与重复下载。" -ForegroundColor Green
            Write-Host "[download-cuda] 提示: 若需强制重新拉取，请附带 -Force 参数。" -ForegroundColor DarkGray
            exit 0
        }
        else {
            Write-Host "[download-cuda] 检测到已有动态库损坏或尺寸异常 (<10MB)，将执行重新下载..." -ForegroundColor Yellow
        }
    }
    catch {
        Write-Host "[download-cuda] 读取现有文件状态失败，将重新下载..." -ForegroundColor Yellow
    }
}

if (-not (Test-Path $TargetDir)) {
    New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null
}

# 3. 辅助下载与解压函数
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Download-And-Extract-Dlls {
    param(
        [string[]]$CandidateUrls,
        [string]$Description,
        [string[]]$DllPatterns
    )

    $downloadSuccess = $false
    $guidStr = [System.Guid]::NewGuid().ToString("N")
    $tempFile = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "cuda12-pack-$guidStr.tmp")

    foreach ($url in $CandidateUrls) {
        Write-Host ""
        Write-Host "[download-cuda] 正在从源下载 $Description..." -ForegroundColor Cyan
        Write-Host "  URL: $url" -ForegroundColor DarkCyan

        $curlExe = "$env:SystemRoot\System32\curl.exe"
        $code = 1

        if (Test-Path $curlExe) {
            & $curlExe -L --fail --retry 2 -# -o "$tempFile" "$url"
            $code = $LASTEXITCODE
        }

        if ($code -ne 0) {
            Write-Host "[download-cuda] curl 下载未完成，尝试 .NET WebClient 回退..." -ForegroundColor Yellow
            try {
                $wc = New-Object System.Net.WebClient
                $wc.DownloadFile($url, $tempFile)
                $wc.Dispose()
                $code = 0
            }
            catch {
                Write-Host "[download-cuda] WebClient 下载失败: $($_.Exception.Message)" -ForegroundColor Yellow
                $code = 1
            }
        }

        if (($code -eq 0) -and (Test-Path $tempFile) -and ((Get-Item $tempFile).Length -gt 1024)) {
            Write-Host "[download-cuda] 下载成功，正在解压目标 DLL 到: $TargetDir..." -ForegroundColor Green
            try {
                $zip = [System.IO.Compression.ZipFile]::OpenRead($tempFile)
                $extractedCount = 0
                foreach ($entry in $zip.Entries) {
                    $matched = $false
                    foreach ($pattern in $DllPatterns) {
                        if ($entry.Name -like $pattern) {
                            $matched = $true
                            break
                        }
                    }
                    if ($matched) {
                        $destPath = [System.IO.Path]::Combine($TargetDir, $entry.Name)
                        $entryMb = [math]::Round($entry.Length / 1MB, 2)
                        Write-Host "  -> 解压: $($entry.Name) ($entryMb MB)" -ForegroundColor Gray
                        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destPath, $true)
                        $extractedCount++
                    }
                }
                $zip.Dispose()

                if ($extractedCount -gt 0) {
                    $downloadSuccess = $true
                    break
                }
                else {
                    Write-Host "[download-cuda] 警告: 压缩包内未找到匹配的目标 DLL" -ForegroundColor Yellow
                }
            }
            catch {
                Write-Host "[download-cuda] 解压失败: $($_.Exception.Message)" -ForegroundColor Red
            }
        }
        else {
            Write-Host "[download-cuda] 当前镜像拉取未成功，尝试备选镜像..." -ForegroundColor Yellow
        }

        if (Test-Path $tempFile) {
            Remove-Item -Force $tempFile -ErrorAction SilentlyContinue
        }
    }

    if (Test-Path $tempFile) {
        Remove-Item -Force $tempFile -ErrorAction SilentlyContinue
    }

    return $downloadSuccess
}

# 4. 执行按需下载
if (-not [string]::IsNullOrWhiteSpace($CustomUrl)) {
    $ok = Download-And-Extract-Dlls -CandidateUrls @($CustomUrl) -Description "自定义 CUDA 压缩包" -DllPatterns @("cublas*.dll", "cudart*.dll")
    if (-not $ok) {
        Write-Host "[download-cuda] 错误: 从自定义 URL 下载或提取失败: $CustomUrl" -ForegroundColor Red
        exit 1
    }
}
else {
    # 4.1 下载 cublas / cublasLt 运行库 (清华源 -> 阿里源 -> 官方 PyPI -> NVIDIA 开发者中心)
    $cublasUrls = @(
        "https://pypi.tuna.tsinghua.edu.cn/packages/84/f7/985e9bdbe3e0ac9298fcc8cfa51a392862a46a0ffaccbbd56939b62a9c83/nvidia_cublas_cu12-12.6.4.1-py3-none-win_amd64.whl",
        "https://mirrors.aliyun.com/pypi/packages/84/f7/985e9bdbe3e0ac9298fcc8cfa51a392862a46a0ffaccbbd56939b62a9c83/nvidia_cublas_cu12-12.6.4.1-py3-none-win_amd64.whl",
        "https://files.pythonhosted.org/packages/84/f7/985e9bdbe3e0ac9298fcc8cfa51a392862a46a0ffaccbbd56939b62a9c83/nvidia_cublas_cu12-12.6.4.1-py3-none-win_amd64.whl",
        "https://developer.download.nvidia.com/compute/cuda/redist/libcublas/windows-x86_64/libcublas-windows-x86_64-12.8.4.1-archive.zip"
    )

    $okCublas = Download-And-Extract-Dlls -CandidateUrls $cublasUrls -Description "NVIDIA cuBLAS 12 (cublas64_12.dll / cublasLt64_12.dll)" -DllPatterns @("cublas64_12.dll", "cublasLt64_12.dll")
    if (-not $okCublas) {
        Write-Host "[download-cuda] 错误: cuBLAS 12 核心库下载失败，请检查网络或配置代理。" -ForegroundColor Red
        exit 1
    }

    # 4.2 下载 cudart 运行时 (870KB 轻量包，若已存在且有效则跳过)
    if (-not $SkipCudart) {
        $needCudart = $true
        if ((-not $Force) -and (Test-Path $cudartPath)) {
            if ((Get-Item $cudartPath).Length -gt 100 * 1024) {
                $needCudart = $false
            }
        }

        if ($needCudart) {
            $cudartUrls = @(
                "https://pypi.tuna.tsinghua.edu.cn/packages/fa/76/4c80fa138333cc975743fd0687a745fccb30d167f906f13c1c7f9a85e5ea/nvidia_cuda_runtime_cu12-12.6.77-py3-none-win_amd64.whl",
                "https://mirrors.aliyun.com/pypi/packages/fa/76/4c80fa138333cc975743fd0687a745fccb30d167f906f13c1c7f9a85e5ea/nvidia_cuda_runtime_cu12-12.6.77-py3-none-win_amd64.whl",
                "https://files.pythonhosted.org/packages/fa/76/4c80fa138333cc975743fd0687a745fccb30d167f906f13c1c7f9a85e5ea/nvidia_cuda_runtime_cu12-12.6.77-py3-none-win_amd64.whl"
            )
            $okCudart = Download-And-Extract-Dlls -CandidateUrls $cudartUrls -Description "NVIDIA CUDA Runtime 12 (cudart64_12.dll)" -DllPatterns @("cudart64_12.dll")
            if (-not $okCudart) {
                Write-Host "[download-cuda] 提示: cudart64_12.dll 未成功提取，但 cuBLAS 核心库已就绪。" -ForegroundColor Yellow
            }
        }
    }
}

# 5. 最终验证
if ((Test-Path $cublasPath) -and (Test-Path $cublasLtPath)) {
    $cSize = (Get-Item $cublasPath).Length
    $ltSize = (Get-Item $cublasLtPath).Length
    $cMb = [math]::Round($cSize / 1MB, 2)
    $ltMb = [math]::Round($ltSize / 1MB, 2)

    Write-Host ""
    Write-Host "==========================================================" -ForegroundColor Green
    Write-Host "  [download-cuda] 绿色微型 CUDA 12 核心库部署成功！" -ForegroundColor Green
    Write-Host "==========================================================" -ForegroundColor Green
    Write-Host "  安装目录: $TargetDir" -ForegroundColor Gray
    Write-Host "  - cublas64_12.dll   ($cMb MB)" -ForegroundColor Gray
    Write-Host "  - cublasLt64_12.dll ($ltMb MB)" -ForegroundColor Gray
    if (Test-Path $cudartPath) {
        $rtKb = [math]::Round((Get-Item $cudartPath).Length / 1KB, 2)
        Write-Host "  - cudart64_12.dll   ($rtKb KB)" -ForegroundColor Gray
    }
    Write-Host "  现在 NVIDIA 专属满血版无需安装 3GB+ CUDA Toolkit 即可开启 100% GPU 算力！" -ForegroundColor Green
    Write-Host ""
    exit 0
}
else {
    Write-Host "[download-cuda] 错误: 目标目录下缺少必要动态链接库文件。" -ForegroundColor Red
    exit 1
}
