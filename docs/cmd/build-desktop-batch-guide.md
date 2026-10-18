# build-desktop.bat 命令与语法说明

本文以 [build-desktop.bat](../../support/tools/scripts/build-desktop.bat) 的当前实现为例，介绍 Windows CMD 批处理的命令、变量、参数解析和按键重试流程。脚本由 `cmd.exe` 解释执行；其中的 `%变量%`、`goto`、`if errorlevel` 等都是 CMD 语法，不是 PowerShell 语法。

## 1. 脚本用途与使用方式

脚本发布 `ReferenceRAG.DesktopHost.csproj`，默认使用 `Release`、`win-x64`、独立部署，成功后删除发布目录中的 PDB 文件并复制图标，最后等待用户按键。日志追加到 `support/artifacts/logs/desktop-build.log`，不是完整的构建输出日志。

在仓库根目录的 CMD 中运行：

```bat
support\tools\scripts\build-desktop.bat
support\tools\scripts\build-desktop.bat --debug --pdb
support\tools\scripts\build-desktop.bat -c Release -r win-x64 -o "D:\Build Output\ReferenceRAG"
```

在 PowerShell 中运行：

```powershell
.\support\tools\scripts\build-desktop.bat --debug
```

这些命令用于说明调用方式。脚本实际执行时会自动强制结束同名桌面端进程；`--clean` 会递归删除输出目录；默认成功后也会递归删除该目录内的 `*.pdb`。自定义输出目录应专用于发布产物，避免指向需要保留文件的目录。

### 参数速查

| 参数 | 当前行为 |
| --- | --- |
| `-c VALUE`、`--config VALUE` | 设置构建配置，默认 `Release` |
| `--debug` | 设置 `CONFIG=Debug`，同时禁止删除 PDB |
| `-r VALUE`、`--runtime VALUE` | 设置运行时标识，默认 `win-x64` |
| `-o PATH`、`--output PATH` | 设置自定义输出目录；相对路径按调用时的当前目录解析 |
| `--framework-dependent`、`--no-self-contained` | 将独立部署设置为 `false` |
| `--clean`、`-clean` | 每次构建前删除旧的输出目录 |
| `-v`、`--verbose` | 将发布日志详细程度从 `m` 改为 `n` |
| `--pdb` | 禁用 PDB 瘦身，既不添加禁用调试符号的发布参数，也不在成功后删除 PDB |
| `--no-retry`、`--batch` | 关闭 R 重试选项，但仍等待按 Q 退出，并非无人值守模式 |

参数从左到右处理，后面的赋值覆盖前面的相同变量。例如 `--debug -c Release` 最终是 `Release`，但 `--debug` 设置的 `STRIP_PDB=false` 仍然保留。单独使用 `-c Debug` 不会将 `STRIP_PDB` 改为 `false`，成功后仍会执行 PDB 删除步骤；需要保留符号时使用 `--debug` 或 `--pdb`。

## 2. 执行流程与标签跳转

```text
初始化路径和默认配置
    ↓
:parse_args → 解析参数，shift 移除已处理参数
    ↓
:after_args
    ↓
:build_loop ←───────────────────────┐
    ↓                              │
设置输出目录、检查项目与 dotnet       │
    ↓                              │
结束同名进程、按需清理输出目录         │
    ↓                              │
dotnet publish → 保存退出码          │
    ↓                              │
成功后整理产物、记录成功或失败日志     │
    ↓                              │
:finish → 按 R ─────────────────────┘
    ↓ 按 Q
:exit_script → 退出
```

`:名称` 定义标签，`goto 名称` 跳转到标签后的代码。标签本身不是函数，也不会阻止顺序执行；脚本顺序经过 `:after_args` 后，会直接进入 `:build_loop`。

项目不存在或找不到 `dotnet` 时，脚本设置 `EXIT_CODE=1` 并 `goto finish`，仍可选择重试。这两种提前结束不会经过构建结果日志的写入代码。

重试跳回 `:build_loop`，不再解析参数，因此保留之前的配置；同时重置 `EXIT_CODE=0`，重新检查环境、进程和输出目录。`--clean` 也会在每次重试时再次生效。

## 3. 回显、注释与字符编码

```bat
@echo off
chcp 65001 >nul
REM 这里是注释
echo 正在构建桌面端...
echo.
```

| 写法 | 含义 |
| --- | --- |
| `@` | 不回显当前这一行命令 |
| `echo off` | 关闭后续命令本身的回显，显式的 `echo` 输出仍会显示 |
| `echo 文本` | 输出文本 |
| `echo.` | 输出一个空行 |
| `REM 文本` | 批处理注释 |
| `chcp 65001` | 将控制台活动代码页切换为 UTF-8 |
| `>nul` | 丢弃标准输出 |

`chcp` 不会转换脚本文件的实际编码，也不保证所有外部程序都使用同一种编码。`setlocal` 管理环境变量等局部状态，不负责恢复控制台代码页。

## 4. 变量与局部作用域

### 4.1 推荐的 set 写法

```bat
set "CONFIG=Release"
set "VERBOSE="
echo 配置: %CONFIG%
```

`set "名称=值"` 外层引号不会存入变量，可以避免意外把行末空格写入值。`set "VERBOSE="` 清除变量，不是将它设置为空字符串对象。

`true`、`false`、`1` 在这里都是字符串。脚本通过字符串比较或变量是否存在实现开关：

```bat
if "%CLEAN%"=="true" echo 需要清理
if defined RETRY_READY echo 支持重试
if not defined VERBOSE echo 使用简略日志
```

`if defined` 只检查变量是否定义，不解释布尔值。因此 `set "RETRY_READY=false"` 仍会启用重试；当前脚本通过清除变量来关闭它。

### 4.2 setlocal、endlocal 与延迟展开

```bat
setlocal EnableDelayedExpansion
```

`setlocal` 开始局部环境，`endlocal` 恢复此前的环境变量。这样脚本设置的 `CONFIG`、`OUT_DIR` 等变量通常不会遗留给调用者。[setlocal 官方说明](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/setlocal)

`%NAME%` 通常在命令解析时展开；括号代码块里的变量可能在整个块执行前就被展开。`!NAME!` 在启用延迟展开时可读取执行到当前命令时的值：

```bat
set "VALUE=old"
if defined VALUE (
    set "VALUE=new"
    echo %VALUE%
    echo !VALUE!
)
```

以上示例先输出 `old`，再输出 `new`。当前脚本虽启用了延迟展开，但没有使用 `!变量!`；以后在同一块中修改再读取变量时，需要留意这个区别。开启延迟展开还会使含 `!` 的路径或参数面临被解释、改变的风险。

## 5. 参数、路径修饰符与 for

```bat
set "SCRIPT_DIR=%~dp0"
for %%I in ("%SCRIPT_DIR%..\..\..") do set "ROOT_DIR=%%~fI"
```

| 写法 | 含义与用途 |
| --- | --- |
| `%0` | 批处理脚本自身的调用路径或名称 |
| `%1`、`%2` | 第一个、第二个位置参数 |
| `%~1` | 去掉第一个参数的外层引号 |
| `%~dp0` | 脚本路径的盘符和目录，通常带末尾反斜杠 |
| `%~f2` | 将第二个参数展开为完全限定路径 |
| `%%I` | 批处理文件中的 `for` 循环变量 |
| `%%~fI` | 将循环变量中的路径展开为完全限定路径 |
| `..` | 路径的上一级目录 |

本例的 `for` 只遍历一个路径项，将脚本目录向上三级得到仓库根目录；不会递归遍历文件。完全限定路径展开不代表目标目录存在，也不等于已验证路径安全边界。在交互式 CMD 中写同类 `for` 命令时，循环变量用 `%I`；在批处理文件中才用 `%%I`。[for 官方说明](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/for)

```bat
cd /d "%ROOT_DIR%"
```

`cd` 切换工作目录，`/d` 允许同时切换盘符。路径外层引号用于处理空格。当前脚本不恢复调用前的工作目录；在已有 CMD 中运行后，当前目录可能停留在仓库根目录。

## 6. 参数解析：if、括号块与 shift

```bat
:parse_args
if "%~1"=="" goto after_args
if /i "%~1"=="-c" (
    set "CONFIG=%~2"
    shift
    shift
    goto parse_args
)
```

处理 `-c Debug` 时：

1. `%~1` 是 `-c`，`%~2` 是 `Debug`。
2. `if /i` 忽略大小写进行比较。
3. 保存第二个参数到 `CONFIG`。
4. 两次 `shift` 使后面的参数向前移动，消费选项名和选项值。
5. 跳回入口处理剩余参数。

无值开关，例如 `--clean`，只执行一次 `shift`。所有参数消费完后，`%~1` 为空，跳到 `:after_args`。当前实现对未知参数仅执行 `shift`，没有报错；需要值的选项也没有缺值校验，调用时应提供完整参数。

括号把多条命令组成一个条件块，带分支时通常写作：

```bat
if defined CUSTOM_OUTPUT (
    set "OUT_DIR=%CUSTOM_OUTPUT%"
) else (
    set "OUT_DIR=%ROOT_DIR%\Host\ReferenceRAG.DesktopHost\bin\%CONFIG%\net10.0-windows\publish"
)
```

将 `) else (` 放在同一行，避免 CMD 将其拆成不同命令。

## 7. 环境、进程与文件命令

| 命令 | 脚本中的作用 | 关键参数 |
| --- | --- | --- |
| `mkdir "路径"` | 创建日志目录 | 可配合 `if not exist` 判断 |
| `where dotnet` | 查找可解析的 `dotnet` 程序 | 找不到时使用失败退出码 |
| `tasklist /fi "imagename eq ..."` | 按映像名称筛选进程 | `/fi` 指定过滤条件 |
| `findstr /i "文本"` | 在进程列表中匹配名称 | `/i` 忽略大小写 |
| `taskkill /F /IM 名称` | 强制结束匹配名称的进程 | `/F` 强制；`/IM` 按映像名称 |
| `timeout /t 1 /nobreak` | 等待约一秒 | `/nobreak` 不因普通按键提前结束 |
| `rd /s /q "路径"` | 删除输出目录及其内容 | `rd` 等同于 `rmdir`；`/s` 递归；`/q` 不提示确认 |
| `del /s /q "路径\*.pdb"` | 递归删除匹配的 PDB 文件 | `*` 是通配符；此处只删除文件 |
| `copy /y "来源" "目标"` | 复制托盘图标 | `/y` 覆盖时不询问 |

当前 `where dotnet` 检查只能证明命令可找到，不证明已安装所需的 .NET 10 SDK；实际 SDK 或项目兼容问题仍可能在发布时暴露。

进程筛选和终止均按名称进行，不区分程序所在目录，可能影响多个同名实例。脚本没有检查进程终止、目录删除、PDB 删除和图标复制的结果；成功提示主要依据 `dotnet publish` 的退出码。

## 8. 管道、重定向与日志

```bat
tasklist /fi "imagename eq ReferenceRAG.DesktopHost.exe" 2>nul | findstr /i "ReferenceRAG.DesktopHost.exe" >nul
```

`|` 将左侧命令的标准输出交给右侧命令作为输入。这里 `tasklist` 列出匹配进程，`findstr` 检查是否包含目标名称。后面的判断使用右侧匹配命令的结果，不能代表管道中每个命令均执行成功。

| 写法 | 含义 |
| --- | --- |
| `> 文件` | 将标准输出写入文件，覆盖原内容 |
| `>> 文件` | 将标准输出追加到文件 |
| `2> 文件` | 重定向标准错误 |
| `>nul 2>nul` | 分别丢弃标准输出与标准错误 |
| `2>&1` | 将标准错误接到标准输出当前的去向；当前脚本未使用，常用于合并日志 |

`nul` 是 Windows 的空设备。`>nul` 只丢弃普通输出，不会自动隐藏错误；重定向也不会自动把失败变为成功。有关特殊字符、引号和重定向，参见 [CMD 官方说明](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/cmd)。

脚本用以下形式追加结果：

```bat
echo [%date% %time%] 桌面端构建成功 - %CONFIG% %RUNTIME% >> "%LOG_FILE%"
```

`%date%`、`%time%` 展开为系统当前日期和时间，其格式依赖系统区域设置。`dotnet publish` 本身没有重定向到该文件，因此文件中只有摘要，没有完整编译诊断。括号块内的时间变量也受到提前展开的影响。

## 9. 发布参数的组装

```bat
set "PUBLISH_ARGS=-c %CONFIG% -r %RUNTIME% --nologo -p:OS=Windows_NT -o "%OUT_DIR%""
dotnet publish "%PROJECT%" %PUBLISH_ARGS%
```

第一行外层引号用于 `set` 赋值，`-o` 后的内部引号保留在变量值中，用于保护带空格的输出目录。第二行展开参数串；不应把整个 `%PUBLISH_ARGS%` 再包成一个带引号的参数。

| 参数 | 在当前脚本中的用途 |
| --- | --- |
| `-c %CONFIG%` | 选择构建配置 |
| `-r %RUNTIME%` | 选择目标运行时标识 |
| `-o "目录"` | 指定发布输出目录 |
| `--nologo` | 隐藏相关启动标识信息 |
| `-p:OS=Windows_NT` | 向 MSBuild 显式传入属性；不代表可将任意平台项目转换为 Windows 项目 |
| `--self-contained true/false` | 控制是否按独立部署方式发布 |
| `-p:DebugType=none -p:DebugSymbols=false` | Release 且启用瘦身时传入，禁用相关调试符号生成 |
| `-v m`、`-v n` | 分别请求 minimal、normal 级别的日志；`--verbose` 在本脚本中对应 normal |

这些是传给外部程序 `dotnet` 的选项，不是 CMD 内置语法。参数串还会经过 CMD 解析，因此不适合直接拼接不可信输入。

## 10. ERRORLEVEL 与构建结果保存

```bat
dotnet publish "%PROJECT%" %PUBLISH_ARGS%
set "EXIT_CODE=%ERRORLEVEL%"
```

`ERRORLEVEL` 表示命令退出状态，通常 `0` 表示成功，非零表示失败。紧接发布命令保存结果，可以避免后续 `choice`、文件操作等命令覆盖发布状态。

脚本使用了三类判断：

```bat
if errorlevel 1 echo 退出码至少为 1
if %ERRORLEVEL% equ 0 echo 当前退出码恰好为 0
if "%EXIT_CODE%"=="0" echo 已保存的构建结果为成功
```

`if errorlevel N` 表示“大于或等于 N”，不是“恰好等于 N”；`equ` 是数值相等比较。字符串比较两侧加引号，可避免变量为空时形成不完整表达式。不要自行定义名为 `ERRORLEVEL` 的环境变量，以免遮蔽动态状态。[if 官方说明](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/if)

## 11. choice 实现按键重试

```bat
:finish
echo.
if defined RETRY_READY (
    choice /C RQ /N /M "Press R to rebuild, Q to quit: "
    if errorlevel 2 goto exit_script
    if errorlevel 1 goto build_loop
) else (
    choice /C Q /N /M "Press Q to quit: "
)
```

| 选项 | 含义 |
| --- | --- |
| `/C RQ` | 仅接受 R、Q 两个选项 |
| `/N` | 隐藏自动生成的选项列表，不隐藏 `/M` 提示 |
| `/M "文本"` | 显示自定义提示 |

不需要按回车，默认不区分大小写。R 是第一个选项，返回 `1`；Q 是第二个，返回 `2`。由于 `if errorlevel` 是大于或等于判断，必须先检查 `2` 再检查 `1`，否则按 Q 也会命中 `1` 而重建。这里的条件直接检查运行时状态，不使用可能在括号块开始时已展开的 `%ERRORLEVEL%`。

`choice` 与 `pause` 的差别在于：`pause` 等待任意键，`choice` 限定有效选项并返回选项序号。异常时 `choice` 可返回 `255`，当前代码会按退出分支处理；官方还说明 Ctrl+C / Ctrl+Break 可返回 `0`，当前代码没有专门处理中断分支。参见 [choice 官方说明](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/choice)。

## 12. 当前退出逻辑需要注意的地方

### 12.1 --batch 仍然等待输入

`--batch` 和 `--no-retry` 都只是清除 `RETRY_READY`。它们仍会进入 `choice /C Q`，不会在构建结束后自动退出。用于 CI 等无人值守环境时，不能仅凭参数名称判断它们已经支持非交互执行。

### 12.2 endlocal 后再读取局部退出码

脚本当前末尾是：

```bat
:exit_script
endlocal
exit /b %EXIT_CODE%
```

`endlocal` 恢复外层环境后，局部 `EXIT_CODE` 已经丢失。下一行读取到的可能是外层同名变量，或空值，因此不能保证将发布退出码传递给调用者。[endlocal 官方说明](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/endlocal)

常见的保留方式是把两条命令放到同一行，在环境恢复前完成变量展开：

```bat
endlocal & exit /b %EXIT_CODE%
```

这是改进示例，本文没有修改脚本。`&` 表示依次执行命令，不以第一条是否成功为条件。`exit /b 数值` 退出当前批处理或子程序并设置返回码；这里没有直接使用 `exit`，避免结束整个调用 CMD 会话。

## 13. 学习顺序与核对方式

建议先理解 `set`、`%变量%`、参数和路径修饰符，再阅读 `if`、`shift` 与 `goto`，最后结合退出码理解 `choice` 重试和发布结果处理。后续修改括号块时，重点检查变量是在解析时还是执行时取值。

可在 CMD 中使用 `命令 /?` 查看本机帮助，例如 `set /?`、`for /?`、`if /?`、`shift /?` 和 `choice /?`。本文依据当前脚本逐段核对，并参考微软命令文档；未执行真实发布、进程终止或文件清理操作。
