<#
.SYNOPSIS
    ReferenceRAG 凭据安全红线与自动化检测门禁 (Security & Credential Governance Gate)

.DESCRIPTION
    采用策略模式 (Strategy Pattern)、适配器模式 (Adapter Pattern) 与状态机模式 (State Machine Pattern) 设计。
    支持客户端 Git Pre-commit 增量暂存区检测 (-StagedOnly) 与 全量代码库审计模式。

.PARAMETER StagedOnly
    仅检测 Git 暂存区中的新增代码（用于 Pre-commit 钩子，毫秒级响应）

.PARAMETER TargetPath
    指定检测的单个文件或目录路径
#>

[CmdletBinding()]
param(
    [switch]$StagedOnly,
    [string]$TargetPath = ""
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Split-Path -Parent $ScriptDir
Set-Location $RepoRoot

# ==============================================================================
# 1. 适配器模式 (Adapter Pattern): 抹平不同环境的数据源输入
# ==============================================================================

class ScannedChunk {
    [string]$FilePath
    [int]$LineNumber
    [string]$Content

    ScannedChunk([string]$filePath, [int]$lineNumber, [string]$content) {
        $this.FilePath = $filePath
        $this.LineNumber = $lineNumber
        $this.Content = $content
    }
}

class GitStagedDiffAdapter {
    static [System.Collections.Generic.List[ScannedChunk]] ExtractStagedChunks([string]$repoRoot) {
        $chunks = [System.Collections.Generic.List[ScannedChunk]]::new()
        
        $diffOutput = git diff --cached --unified=0
        if (-not $diffOutput) { return $chunks }

        $currentFile = ""
        $currentLine = 0
        $hunkRegex = [regex]'^@@\s+-[0-9]+(?:,[0-9]+)?\s+\+([0-9]+)(?:,[0-9]+)?\s+@@'

        foreach ($line in $diffOutput) {
            if ($line -match '^diff --git a/(.+) b/(.+)$') {
                $currentFile = $matches[2]
                $currentLine = 0
                continue
            }
            if ($line -match '^---' -or $line -match '^\+\+\+') {
                continue
            }
            $hunkMatch = $hunkRegex.Match($line)
            if ($hunkMatch.Success) {
                $currentLine = [int]$hunkMatch.Groups[1].Value
                continue
            }
            if ($line.StartsWith('+') -and -not $line.StartsWith('+++')) {
                $addedContent = $line.Substring(1)
                $chunks.Add([ScannedChunk]::new($currentFile, $currentLine, $addedContent))
                $currentLine++
            }
            elseif (-not $line.StartsWith('-')) {
                $currentLine++
            }
        }
        return $chunks
    }
}

class FileSystemAdapter {
    static [System.Collections.Generic.List[ScannedChunk]] ExtractTrackedChunks([string]$repoRoot, [string]$targetSubPath) {
        $chunks = [System.Collections.Generic.List[ScannedChunk]]::new()
        $ignoredExtensions = @(".dll", ".exe", ".pdb", ".png", ".jpg", ".jpeg", ".ico", ".woff", ".woff2", ".ttf", ".eot", ".bin", ".whl", ".zip", ".tar", ".gz")
        $ignoredSubstrings = @("wwwroot/vendor/", "node_modules/", "packages/", ".codegraph/", "runtimes/", "docs_secret/", "/docs_secret/", "env/", ".envdev", "job-intel.log")

        $files = git -c core.quotepath=false ls-files
        if ($targetSubPath) {
            $files = $files | Where-Object { $_ -like "*$targetSubPath*" }
        }

        foreach ($relFile in $files) {
            $norm = $relFile.Replace('\', '/')
            if ($ignoredSubstrings | Where-Object { $norm -like "*$_*" }) { continue }
            $ext = [System.IO.Path]::GetExtension($relFile).ToLower()
            if ($ext -in $ignoredExtensions) { continue }

            $fullPath = Join-Path $repoRoot $relFile
            if (-not (Test-Path $fullPath)) { continue }

            $lines = Get-Content -Path $fullPath -Encoding UTF8 -ErrorAction SilentlyContinue
            if (-not $lines) { continue }

            for ($i = 0; $i -lt $lines.Count; $i++) {
                $chunks.Add([ScannedChunk]::new($relFile, $i + 1, $lines[$i]))
            }
        }
        return $chunks
    }
}

# ==============================================================================
# 2. 策略模式 (Strategy Pattern): 独立可扩展的安全检测规则
# ==============================================================================

class ViolationRecord {
    [string]$FilePath
    [int]$LineNumber
    [string]$RuleName
    [string]$MatchedSnippet
    [string]$Suggestion

    ViolationRecord([string]$file, [int]$line, [string]$rule, [string]$snippet, [string]$sugg) {
        $this.FilePath = $file
        $this.LineNumber = $line
        $this.RuleName = $rule
        $this.MatchedSnippet = $snippet
        $this.Suggestion = $sugg
    }
}

class SecurityRuleStrategyBase {
    [ViolationRecord] Evaluate([ScannedChunk]$chunk) {
        return $null
    }
}

# 策略 1: OpenAI 及 LLM 真实 API Key 策略
class OpenAiKeyRuleStrategy : SecurityRuleStrategyBase {
    [ViolationRecord] Evaluate([ScannedChunk]$chunk) {
        $content = $chunk.Content
        if ($content -match '\bsk-[a-zA-Z0-9_\-]{20,}\b') {
            $val = $matches[0]
            # 白名单：允许符合规范的标准占位符
            if ($val -like "sk-proj-test-*" -or $val -like "sk-test-*" -or $val -like "sk-mock-*" -or $val -like "lwt_live_*") {
                return $null
            }
            # 测试用例目录中的 mock 允许特定测试键命名
            if ($chunk.FilePath -like "tests/*" -and ($val -like "*key*" -or $val -like "*secret*")) {
                return $null
            }
            # 规则定义及安全设计文档允许声明占位符
            if ($chunk.FilePath -eq "AGENT.md" -or $chunk.FilePath -like "*security*") {
                return $null
            }
            return [ViolationRecord]::new(
                $chunk.FilePath, $chunk.LineNumber,
                "OpenAI Key Leak",
                $val,
                "请使用标准占位符 'sk-proj-test-secret-key-1234567890' 或从 /env/.envdev 动态加载！"
            )
        }
        return $null
    }
}

# 策略 2: EasyTier 组网机密策略
class EasyTierRuleStrategy : SecurityRuleStrategyBase {
    [ViolationRecord] Evaluate([ScannedChunk]$chunk) {
        $content = $chunk.Content
        $regex = [regex]'(?i)\bnetwork[_-]?secret\b\s*[:=]\s*["\u0027]([^"\u0027]+)["\u0027]'
        if ($regex.IsMatch($content)) {
            $val = $regex.Match($content).Groups[1].Value
            if ($val -eq "demo_secret_token" -or $val -like "mock_*") { return $null }
            if ($chunk.FilePath -eq "AGENT.md" -or $chunk.FilePath -like "*security*") { return $null }
            return [ViolationRecord]::new(
                $chunk.FilePath, $chunk.LineNumber,
                "EasyTier Network Secret Leak",
                $val,
                "EasyTier 密钥必须使用标准占位符 'demo_secret_token'！"
            )
        }
        return $null
    }
}

# 策略 3: Cloudflare / 穿透隧道 ID 策略
class TunnelIdRuleStrategy : SecurityRuleStrategyBase {
    [ViolationRecord] Evaluate([ScannedChunk]$chunk) {
        $content = $chunk.Content
        if ($content -match '(?i)\btunnel_[0-9a-fA-F]{16,}\b') {
            $val = $matches[0]
            if ($val -eq "tunnel_0123456789abcdef0123456789abcdef") { return $null }
            if ($chunk.FilePath -eq "AGENT.md" -or $chunk.FilePath -like "*security*") { return $null }
            return [ViolationRecord]::new(
                $chunk.FilePath, $chunk.LineNumber,
                "Tunnel ID Leak",
                $val,
                "穿透隧道 ID 统一使用虚拟占位符 'tunnel_0123456789abcdef0123456789abcdef'！"
            )
        }
        return $null
    }
}

# 策略 4: 内部网络真实 IP 与非法对端地址策略
class NetworkAddressRuleStrategy : SecurityRuleStrategyBase {
    [ViolationRecord] Evaluate([ScannedChunk]$chunk) {
        $content = $chunk.Content
        if ($chunk.FilePath -eq "AGENT.md" -or $chunk.FilePath -like "*security*") { return $null }
        $ipRegex = [regex]'\b(?!(?:127\.0\.0\.1|0\.0\.0\.0|255\.255\.255\.255)\b)(?:[1-9][0-9]{0,2}\.){3}[1-9][0-9]{0,2}\b'
        
        $match = $ipRegex.Match($content)
        while ($match.Success) {
            $ip = $match.Value
            $start = $match.Index
            $end = $start + $match.Length
            
            # 过滤语义版本号 (如 12.6.4.1 或 13.2.6.4.7)
            $isVersion = $false
            if ($start -gt 0 -and ($content[$start - 1] -eq '.' -or $content[$start - 1] -eq '-')) { $isVersion = $true }
            if ($end -lt $content.Length -and ($content[$end] -eq '.' -or $content[$end] -eq '-')) { $isVersion = $true }
            if ($content -match '(?i)version|cu12|whl|archive\.zip|assembly|whatwg|libcublas|TensorRT') { $isVersion = $true }

            if (-not $isVersion) {
                $parts = $ip.Split('.')
                if ($parts.Count -eq 4) {
                    $valid = $true
                    foreach ($p in $parts) {
                        $n = 0
                        if (-not [int]::TryParse($p, [ref]$n) -or $n -lt 0 -or $n -gt 255) { $valid = $false; break }
                    }
                    if ($valid) {
                        return [ViolationRecord]::new(
                            $chunk.FilePath, $chunk.LineNumber,
                            "Non-Local IP Address Leak",
                            $ip,
                            "禁止写入真实主机/对端 IP！请使用 RFC 5737 规范地址 '127.0.0.1' 或 'example.com'！"
                        )
                    }
                }
            }
            $match = $match.NextMatch()
        }
        return $null
    }
}

# 策略 5: 真实管理员密码分配策略
class PasswordAssignmentRuleStrategy : SecurityRuleStrategyBase {
    [ViolationRecord] Evaluate([ScannedChunk]$chunk) {
        $content = $chunk.Content
        $regex = [regex]'(?i)\b(?:WEB_ADMIN_PASSWORD|admin[_-]?password)\b\s*[:=]\s*["\u0027]([^"\u0027]{4,})["\u0027]'
        if ($regex.IsMatch($content)) {
            $val = $regex.Match($content).Groups[1].Value
            if ($val -in @("your_secure_password_here", "<ADMIN_PASSWORD>", "REDACTED", "[REDACTED]")) { return $null }
            if ($chunk.FilePath -eq "AGENT.md" -or $chunk.FilePath -like "*security*") { return $null }
            return [ViolationRecord]::new(
                $chunk.FilePath, $chunk.LineNumber,
                "Admin Password Leak",
                $regex.Match($content).Value,
                "系统/管理员密码必须使用占位符 'your_secure_password_here' 或 '<ADMIN_PASSWORD>'！"
            )
        }
        return $null
    }
}

# 策略 6: 私钥块特征策略
class PrivateKeyBlockRuleStrategy : SecurityRuleStrategyBase {
    [ViolationRecord] Evaluate([ScannedChunk]$chunk) {
        $content = $chunk.Content
        if ($content -match '-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----') {
            if ($content -like "*MOCK_KEY_DATA*" -or $chunk.FilePath -like "*Test*") {
                return $null
            }
            if ($chunk.FilePath -eq "AGENT.md" -or $chunk.FilePath -like "*security*") {
                return $null
            }
            return [ViolationRecord]::new(
                $chunk.FilePath, $chunk.LineNumber,
                "Private Key Block Leak",
                "-----BEGIN ... PRIVATE KEY-----",
                "严禁提交私钥数据！测试用例请使用带有 'MOCK_KEY_DATA' 的纯内存 Mock 凭据！"
            )
        }
        return $null
    }
}

# ==============================================================================
# 3. 状态机模式 (State Machine Pattern): 驱动安全门禁执行生命周期
# ==============================================================================

enum GateState {
    Initializing
    ExtractingChunks
    EvaluatingStrategies
    Triaged
    VerdictRendered
}

class SecurityGateStateMachine {
    [GateState]$State
    [System.Collections.Generic.List[SecurityRuleStrategyBase]]$Strategies
    [System.Collections.Generic.List[ScannedChunk]]$Chunks
    [System.Collections.Generic.List[ViolationRecord]]$Violations

    SecurityGateStateMachine() {
        $this.State = [GateState]::Initializing
        $this.Strategies = [System.Collections.Generic.List[SecurityRuleStrategyBase]]::new()
        $this.Chunks = [System.Collections.Generic.List[ScannedChunk]]::new()
        $this.Violations = [System.Collections.Generic.List[ViolationRecord]]::new()

        # 注册规则策略
        $this.Strategies.Add([OpenAiKeyRuleStrategy]::new())
        $this.Strategies.Add([EasyTierRuleStrategy]::new())
        $this.Strategies.Add([TunnelIdRuleStrategy]::new())
        $this.Strategies.Add([NetworkAddressRuleStrategy]::new())
        $this.Strategies.Add([PasswordAssignmentRuleStrategy]::new())
        $this.Strategies.Add([PrivateKeyBlockRuleStrategy]::new())
    }

    [int] Run([bool]$stagedOnly, [string]$repoRoot, [string]$targetPath) {
        # 1. 状态流转: ExtractingChunks
        $this.State = [GateState]::ExtractingChunks
        if ($stagedOnly) {
            Write-Host " [Security Gate] Mode: Staged Diff (Pre-commit Incremental Check)" -ForegroundColor Cyan
            $this.Chunks = [GitStagedDiffAdapter]::ExtractStagedChunks($repoRoot)
            Write-Host "   -> Extracted $($this.Chunks.Count) staged added lines for inspection." -ForegroundColor Gray
        }
        else {
            Write-Host " [Security Gate] Mode: Full Repository Scan" -ForegroundColor Cyan
            $this.Chunks = [FileSystemAdapter]::ExtractTrackedChunks($repoRoot, $targetPath)
            Write-Host "   -> Extracted $($this.Chunks.Count) lines across tracked files." -ForegroundColor Gray
        }

        # 2. 状态流转: EvaluatingStrategies
        $this.State = [GateState]::EvaluatingStrategies
        foreach ($chunk in $this.Chunks) {
            if ($chunk.Content -match 'secret-guard:\s*allow') { continue }

            foreach ($strategy in $this.Strategies) {
                $violation = $strategy.Evaluate($chunk)
                if ($violation) {
                    $this.Violations.Add($violation)
                }
            }
        }

        # 3. 状态流转: Triaged
        $this.State = [GateState]::Triaged
        # 4. 状态流转: VerdictRendered
        $this.State = [GateState]::VerdictRendered

        if ($this.Violations.Count -gt 0) {
            Write-Host "`n========================================================" -ForegroundColor Red
            Write-Host " [SECURITY REDLINE VIOLATION] 凭据安全红线检测未通过！" -ForegroundColor Red
            Write-Host " 检测到 $($this.Violations.Count) 处凭据或敏感地址泄漏，已拦截提交！" -ForegroundColor Red
            Write-Host "========================================================`n" -ForegroundColor Red

            foreach ($v in $this.Violations) {
                Write-Host " [FAIL] $($v.RuleName)" -ForegroundColor Red
                Write-Host "   位置: $($v.FilePath):$($v.LineNumber)" -ForegroundColor Yellow
                Write-Host "   命中: $($v.MatchedSnippet)" -ForegroundColor Gray
                Write-Host "   治理建议: $($v.Suggestion)" -ForegroundColor Cyan
                Write-Host "--------------------------------------------------------" -ForegroundColor DarkGray
            }
            Write-Host "`n[操作提示] 请将上述真实凭据替换为标准虚拟占位符，或通过 /env/.envdev 动态加载！`n" -ForegroundColor Yellow
            return 1
        }

        Write-Host "`n [SUCCESS] Security & Credential Governance Check Passed! (0 violations)" -ForegroundColor Green
        return 0
    }
}

# ==============================================================================
# 执行入口
# ==============================================================================
$gateMachine = [SecurityGateStateMachine]::new()
$exitCode = $gateMachine.Run($StagedOnly, $RepoRoot, $TargetPath)
exit $exitCode
