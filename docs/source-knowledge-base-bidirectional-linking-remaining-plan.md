# 源 ↔ 知识库双向关联：剩余工作计划与执行流程（P2 + 脚本修复 + 遗漏项）

> 本文档为 `source-knowledge-base-bidirectional-linking-feasibility-and-architecture-design.md` 的**续篇执行清单**。
> 设计、P0、P1 的详细论证见主文档；本文档只记录**尚未落地的内容与可照搬的执行步骤**，供后续会话直接照做。

---

## 0. 当前进度快照（2026-11-13 已落地）

| 阶段 | 状态 | 提交 | 部署 |
| :--- | :--- | :--- | :--- |
| 调研 / 架构设计 | ✅ 已交付文档 | — | — |
| P0 写侧去重 + link/unlink + 删除语义 | ✅ 已落地 | `11246f6` | 正式 `referencerag:prod-local` @ 5123 |
| P1 删除级联 / 未挂载标签 / 批量挂载（弹窗层） | ✅ 已落地 | `11246f6` | 同上 |
| P2 事件 / 对账 / 漂移治理 + 脚本修复 + 遗漏 UI 项 | ✅ 已落地并验收（工作区未提交） | 待提交 | 开发 `referencerag-dev` @ 5124（正式未动） |

**已验证能力**：同一 KB 幂等挂载、跨 KB 挂载、解绑（retain）、删源级联（409→cascade 204）、未挂载标签、批量挂载弹窗、连接器行只读化。

**P2 新增已验证能力**（详见主文档 §15.6）：手动/定时对账（Orphaned / Unlinked / Drifted 三分类）、候选重绑（迁移源投影、保留索引）、孤儿一键清理（连带清理失效投影）、行级漂移徽标、源↔知识库双向跳转与高亮、关联事件观测接口、`deploy-linux.ps1` 三模式闭环。

**未完成范围**：见下文 §3.1（抽象类遗留）与主文档 §15.6.6（正式环境部署、`AutoRebind` 开关）。

---

## 1. P2：事件驱动与漂移治理

主文档 §4.6 / §7.3。目标：让「连接器为事实来源、源为投影」的模型具备**自愈与可观测性**，避免长期运行后出现孤儿/漂移且无人知晓。

### 1.1 领域事件（3 个）

| 事件 | 触发点 | 订阅者动作 |
| :--- | :--- | :--- |
| `SourceLinked` | `EnsureConnectorAsync` 返回 `created=true` | 失效源管理缓存、刷新 KB 连接器数 |
| `ConnectorDetached` | `DELETE /link` 或删源级联解绑成功 | 标记源为 `Unlinked`、取消进行中的索引任务 |
| `SourceRemoved` | 源被物理删除（非解绑） | 级联解绑其连接器、回收索引 |

**约束**：当前无事件总线，不引入 MediatR。使用进程内 `System.Threading.Channels.Channel<T>` 单例，或在同一事务后同步调用处理器。优先选**同步调用**（简单、可测），`Channel` 仅在需要跨进程/异步解耦时引入。

### 1.2 定时对账任务 `ReconciliationJob`

- **输入**：`config.Sources`（投影）vs `connectors` 表（事实）。
- **输出**：两类漂移报告：
  - `Orphaned`：存在 `connectors` 但对应物理路径已不存在（目录被删/改名）。
  - `Unlinked`：存在 `config.Sources` 但无任何连接器指向其路径（被解绑后残留）。
- **落点**：`KnowledgeBaseService` 新增 `ReconcileAsync()`；通过 `IHostedService` 每 N 分钟触发（建议 30 min，首版可手动触发 `POST /api/knowledge-bases/reconcile`）。
- **复用**：`checkDrift` / `PurgeConnectorDataAsync`（主文档 §6）可直接对接 `Drifted` / `Delete` 语义。

### 1.3 路径迁移检测与自动重绑

- **场景**：用户把 `/lwt/foo` 重命名为 `/lwt/bar`，连接器 `path_key` 失效 → 进入 `Drifted`。
- **检测**：`ReconciliationJob` 中发现 connector 的 `TargetPath` 在文件系统不存在、但存在「大小/名称相似」的新目录时，标记为 `Drifted` 并产出候选重绑。
- **自动重绑（首版保守）**：默认**只报告不自动改**，由用户在 UI「孤儿连接器」面板确认重绑；后续可加 `AutoRebind=true` 开关。
- **注意**：重绑本质是更新 connector 的 `target_path` + `path_key`，受 `ux_connectors_kb_path` 唯一索引约束，需先 detatch 旧再 attach 新。

### 1.4 P2 验收门禁

- [x] 触发 `SourceLinked` → 源管理列表 `linkedKnowledgeBases` 立即反映，无全量重算卡顿（投影缓存 + 版本失效）；
- [x] 手动删除一连接器物理目录 → `ReconcileAsync` 产出 `Orphaned` 报告，源行显示「漂移」标记（行级标签本轮补齐）；
- [x] 解绑后残留源 → `ReconcileAsync` 产出 `Unlinked` 报告；
- [x] 目录改名模拟 → 产出 `Drifted` + 候选重绑，确认后连接器恢复 `Linked`（并同步迁移源投影）；
- [x] `npm test` + `dotnet build` 0 错误。

> 实测记录见主文档 §15.6.3。

---

## 2. 部署脚本缺陷修复：`deploy-linux.ps1` 临时密钥清理

### 2.1 根因

脚本在 `try { ... } finally { Remove-Item $tempKeyPath -Force }` 中：
1. `icacls.exe $tempKeyPath /inheritance:r /grant:r "$($env:USERNAME):(R)"` 把文件 ACL 收紧为**仅当前用户读**；
2. `finally` 中 `Remove-Item -Force` 报**访问被拒绝**（删文件需要 Delete 权限，而 `(R)` 不含 Delete；且 OpenSSH 要求该收紧，但收紧后同进程删除失败）；
3. 该异常中断脚本后续步骤（重建/健康检查），导致 `-Mode Dev` / `-FrontendOnly` / `-Mode Official` 三个模式在重建阶段均中断。

### 2.2 修复方案（二选一，推荐 A）

**方案 A（最小改动）**：在 `finally` 删除前先恢复 ACL，再删除：

```powershell
finally {
    if (Test-Path $tempKeyPath) {
        # 恢复删除权限后再清除，避免 "访问被拒绝"
        & icacls.exe $tempKeyPath /reset | Out-Null
        try { Remove-Item -Path $tempKeyPath -Force -ErrorAction Stop }
        catch {
            # 兜底：直接调用 BCL 删除
            [System.IO.File]::Delete($tempKeyPath)
        }
    }
}
```

**方案 B（避免收紧）**：不调用 `icacls /inheritance:r`，改为写入仅当前用户可读的临时子目录（由目录 ACL 继承），OpenSSH 在 Windows 上通常接受用户专有目录内的 key。改动更大，且不同 Windows 版本行为不一，**不推荐首版采用**。

### 2.3 验证

- [x] 本机 PowerShell 直接运行 `scripts/deploy-linux.ps1 -Mode Dev`（或 `-FrontendOnly`）至完成，无「访问被拒绝」；
- [x] 确认 `%TEMP%` 下无残留 `tmp*.key` 文件；
- [x] 远端容器按脚本流程重建成功、健康检查 200。

> 附带修复：启动时合并大小写重复环境变量（`HTTPS_PROXY` / `https_proxy` 会让 `Start-Process` 抛「已添加相同键」），以及 `Invoke-RemoteSsh` 不再把 `docker build` 的 stderr 当终止错误。Dev 模式实测：`-Mode Dev` 1 分 11 秒完成，`-FrontendOnly` 约 3 秒。

---

## 3. P1 计划但遗漏的代码 / UI 项

主文档 §7.2 列了 P1 的 5 项，其中 2 项只做了一半或没做，需补：

| 项 | 现状 | 处理结果 |
| :--- | :--- | :--- |
| 源行「已挂载 N」点击跳转 | 标签已显示，但**不可点击** | ✅ 已改可点击，跳 `/knowledge-bases?kb=&connector=&path=`，目标行绿色高亮并滚入视口 |
| 连接器行「来源」标识 | 仅显示「知识库连接器」标签 | ✅ 已加「来源：源文件管理 / 仅知识库」+「在源管理中查看」反向入口（插槽修正为 `#header-extra`） |
| 孤儿连接器提示与一键清理 | 未做（属 P2 漂移治理，但 UI 入口应放此处） | ✅ 源管理页新增「漂移检查」面板：统计行 + 行级徽标 + 逐项「一键清理 / 重绑」 |
| 批量挂载实际提交 | 仅验证到弹窗，未真正提交 | ✅ 已真实提交并验证 `created/skipped/failed` 统计（重复提交返回 `already-linked` 计入 skipped） |
| `EnsureConnectorAsync` 单测 | 仅端到端 API 验证 | ✅ `SourceLinkReconciliationTests` 覆盖同 KB 幂等、跨 KB 新建、并发唯一冲突回查 |

### 3.1 设计文档提及但**尚未实现**的抽象

以下在主文档 §4 有完整设计，P0/P1 仅落地了最小子集，后续若需要可补齐：
- `SourceMountAdapter`（§4.1）：把 connector/source 统一投影为 `SourceMount`；
- `LinkPolicy`（Explicit / FirstEnabled / ProjectionOnly，§4.2）；
- `ReclaimPolicy`（Retain / Delete / Purge，§4.2，对标 K8s PV/PVC）；
- `MountState` 状态机（Unlinked / Linked / Indexed / **Drifted / Detached / Orphaned**，§4.3）——目前只用到 Unlinked/Linked。

> 决策建议：除非出现真实的多策略需求（如不同 KB 想用不同回收策略），**不必为抽象而抽象**，优先把 §1 的对账/漂移做出来。

---

## 4. 后续执行顺序（建议）

```
1. [代码] 补 EnsureConnectorAsync 单测 + 批量挂载真实提交验证      （低风险，先锁正确性）
2. [代码] 源行「已挂载 N」可点击跳转 + 连接器「来源」标识          （纯前端，独立 PR）
3. [脚本] §2 部署脚本临时密钥清理修复                              （解锁正式流程自动化）
4. [后端] §1.2 ReconciliationJob（手动触发先上，定时后上）          （中风险，先读后写）
5. [后端] §1.1 领域事件（同步调用版）接入 SourceLinked/Detached     （与 4 同批）
6. [前端] §1.3 孤儿/漂移面板 + 一键清理/重绑                       （依赖 4/5 的 API）
7. [部署] 走通 §2 修复后的 `deploy-linux.ps1 -Mode Official`        （验证脚本闭环）
8. [文档] 主文档 §15 追加 P2 实施与验收记录
```

---

## 5. 环境与凭证速查（续执行必读）

| 项 | 值 |
| :--- | :--- |
| 分支 | `feature/v2-knowledgebase-dualchannel` |
| 最新提交 | `11246f6 feat(reader,sources): 双向续读、搜索折行与源-知识库双向关联` |
| 开发环境 | 主机 `127.0.0.1:45612`（SSH）→ 容器 `referencerag-dev` :5124；密钥 `env/.envdev` 的 `DEPLOY_SSH_KEY`；无 API Key 认证 |
| 正式环境 | 主机 `127.0.0.1:22`（SSH）→ 容器 `reference-rag` :5123；密钥 `env/.envprod` 的 `DEPLOY_SSH_KEY`；**启用 `X-API-Key` 认证** |
| 正式容器 env | 已导出 `/tmp/referencerag-prod.env`（chmod 600），重建须复用 |
| 正式挂载 | `/mnt/disk1/domains/Docker/dc/referencerag/{logs,data,models}`、`/lwt:/lwt:rslave`、`/lwt:/app/sources` |
| 回滚镜像 | `referencerag:dev-cpu` 仍在正式主机，可用于快速回滚 |
| 构建命令 | 开发：`docker build -f support/deploy/docker/Dockerfile.dev`；正式：`docker build -f support/deploy/docker/Dockerfile` |
| 等价手动部署 | `tar` 源码 → `ssh` 传输 → 远端 `docker build` → `docker rm -f` + `docker run`（复用 env-file 与挂载）。本机 `deploy-linux.ps1` 三个模式在重建阶段均因 §2 缺陷中断，修复前继续用手动等价流程 |

### 5.1 手动部署命令模板（正式）

```bash
# 1) 本机打源码包（排除构建产物/数据/密钥）
cd E:/WorkProject/CSharp/PrivateProject/Web/ReferenceRAG
tar -czf prod-bundle.tar.gz --exclude=.git --exclude=bin --exclude=obj \
  --exclude=models --exclude=data --exclude=data-dev --exclude=logs \
  --exclude=artifacts --exclude=test-vault --exclude='*.onnx' --exclude='*.gguf' \
  --exclude='*.bin' --exclude='*.tmp' --exclude='*.tar.gz' --exclude='*.zip' \
  Directory.Build.props Directory.Packages.props nuget.config packages \
  Infrastructure Business Host support

# 2) 传输 + 远端构建（密钥仅从 env/.envprod 读取）
SSH="ssh -i <prod_key> -p 22 -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR root@127.0.0.1"
$SSH "mkdir -p /opt/referencerag-build && tar -xzf - -C /opt/referencerag-build" < prod-bundle.tar.gz
$SSH "cd /opt/referencerag-build && docker build -f support/deploy/docker/Dockerfile -t referencerag:prod-local ."

# 3) 导出原容器 env（仅首次/变更时需重新导出）→ 重建
$SSH "docker inspect reference-rag --format '{{range .Config.Env}}{{println .}}{{end}}' > /tmp/referencerag-prod.env && chmod 600 /tmp/referencerag-prod.env"
$SSH "docker rm -f reference-rag && docker run -d --name reference-rag --restart unless-stopped -p 5123:5000 \
  --env-file /tmp/referencerag-prod.env \
  -v /mnt/disk1/domains/Docker/dc/referencerag/logs:/app/logs \
  -v /lwt:/lwt:rslave -v /mnt/disk1/domains/Docker/dc/referencerag/data:/app/data \
  -v /mnt/disk1/domains/Docker/dc/referencerag/models:/app/models -v /lwt:/app/sources \
  referencerag:prod-local"

# 4) 健康检查（正式需带 X-API-Key；/api/system/health 通常免认证）
curl -s -o /dev/null -w "%{http_code}\n" --max-time 10 http://127.0.0.1:5123/api/system/health
```

---

## 6. 风险与注意

- 正式环境 **API Key 认证**已启用：任何 `/api/...` 调用（除 `/api/system/health` 等白名单）须带 `X-API-Key`，否则 401；dev 无此限制，调试时注意区分。
- 正式容器环境变量**硬编码在容器配置**，重建必须复用 `/tmp/referencerag-prod.env`，否则丢失 `REFERENCERAG_API_KEY` 等。
- 批量挂载真实提交会写 `connectors` 并触发索引，**先在 dev 验证**，确认 `created/skipped/failed` 统计无误后再上正式。
- 对账任务首版**只报告不自动改**，避免误删用户语料。

---

*本文档为执行清单，不含新论证。设计细节、接口契约、数据模型见主文档 `source-knowledge-base-bidirectional-linking-feasibility-and-architecture-design.md`。*
