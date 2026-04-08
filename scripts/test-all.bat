@echo off
REM Obsidian RAG 功能测试脚本 (Windows)

echo ========================================
echo Obsidian RAG 功能测试
echo ========================================
echo.

REM 测试计数
set PASS=0
set FAIL=0

REM 1. 编译项目
echo.
echo 1. 编译项目
echo ----------------------------------------
dotnet build --configuration Release
if %ERRORLEVEL% neq 0 (
    echo 编译失败！
    exit /b 1
)
echo.

REM 2. 测试向量生成修复
echo.
echo 2. 测试向量生成
echo ----------------------------------------
echo 清理旧数据...
if exist data\*.json del /q data\*.json

echo 运行索引...
dotnet run --project src\ObsidianRAG.CLI -- index --verbose

echo.
echo 检查向量数据...
if exist data\vectors.json (
    findstr /C:"fileId" data\vectors.json > nul
    if %ERRORLEVEL% equ 0 (
        echo [32m✓ 向量数据已生成[0m
        set /a PASS+=1
    ) else (
        echo [31m✗ 向量数据异常[0m
        set /a FAIL+=1
    )
) else (
    echo [31m✗ 向量文件不存在[0m
    set /a FAIL+=1
)

REM 3. 测试查询效果
echo.
echo 3. 测试查询效果
echo ----------------------------------------
echo 测试查询: 如何配置系统？
dotnet run --project src\ObsidianRAG.CLI -- query "如何配置系统？"

echo.
echo 测试查询: 核心功能
dotnet run --project src\ObsidianRAG.CLI -- query "核心功能"

REM 4. 测试模型管理
echo.
echo 4. 测试模型管理
echo ----------------------------------------
echo 列出可用模型:
dotnet run --project src\ObsidianRAG.CLI -- model list

echo.
echo 当前模型:
dotnet run --project src\ObsidianRAG.CLI -- model current

echo.
echo 模型推荐:
dotnet run --project src\ObsidianRAG.CLI -- model recommend --lang zh

REM 5. 测试文件监控
echo.
echo 5. 测试文件监控
echo ----------------------------------------
echo 创建测试文件...
echo # 测试文件监控 > test-vault\test-monitor.md
echo 这是一个测试文件，用于测试文件监控功能。 >> test-vault\test-monitor.md

echo 启动文件监控（5秒后自动停止）...
start /B dotnet run --project src\ObsidianRAG.CLI -- watch

REM 等待启动
timeout /t 2 /nobreak > nul

REM 修改文件
echo. >> test-vault\test-monitor.md
echo ## 新增内容 >> test-vault\test-monitor.md
echo 这是新增的内容，触发文件变更事件。 >> test-vault\test-monitor.md

REM 等待检测
timeout /t 3 /nobreak > nul

REM 停止监控
taskkill /F /FI "WINDOWTITLE eq ObsidianRAG*" > nul 2>&1

REM 清理测试文件
del /q test-vault\test-monitor.md 2>nul

echo [32m✓ 文件监控测试完成[0m
set /a PASS+=1

REM 6. 测试向量查询
echo.
echo 6. 测试向量查询效果
echo ----------------------------------------
echo 运行查询测试...
dotnet run --project src\ObsidianRAG.CLI -- test query

REM 7. 测试基准性能
echo.
echo 7. 测试基准性能
echo ----------------------------------------
dotnet run --project src\ObsidianRAG.CLI -- test benchmark --iterations 5

REM 8. 测试向量生成
echo.
echo 8. 测试向量生成
echo ----------------------------------------
dotnet run --project src\ObsidianRAG.CLI -- test vector --text "这是一个测试文本，用于验证向量生成功能。"

REM 总结
echo.
echo ========================================
echo 测试总结
echo ========================================
echo [32m通过: %PASS%[0m
echo [31m失败: %FAIL%[0m
echo.

if %FAIL% equ 0 (
    echo [32m所有测试通过！[0m
    exit /b 0
) else (
    echo [31m部分测试失败，请检查日志。[0m
    exit /b 1
)
