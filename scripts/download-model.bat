@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion

echo === ObsidianRAG 模型下载脚本 ===
echo.

:: 创建模型目录
if not exist "models" mkdir models
cd models

:: 选择模型
echo 请选择要下载的模型：
echo 1) bge-small-zh-v1.5  (384维, ~100MB) - 推荐
echo 2) bge-base-zh-v1.5   (768维, ~400MB)
echo 3) bge-large-zh-v1.5  (1024维, ~1.3GB)
echo 4) bge-m3             (1024维, ~2GB, 多语言)
echo.
set /p choice="请输入选择 [1-4, 默认1]: "
if "%choice%"=="" set choice=1

if "%choice%"=="1" (
    set MODEL_NAME=BAAI/bge-small-zh-v1.5
    set MODEL_DIR=bge-small-zh-v1.5
) else if "%choice%"=="2" (
    set MODEL_NAME=BAAI/bge-base-zh-v1.5
    set MODEL_DIR=bge-base-zh-v1.5
) else if "%choice%"=="3" (
    set MODEL_NAME=BAAI/bge-large-zh-v1.5
    set MODEL_DIR=bge-large-zh-v1.5
) else if "%choice%"=="4" (
    set MODEL_NAME=BAAI/bge-m3
    set MODEL_DIR=bge-m3
) else (
    echo 无效选择，使用默认模型 bge-small-zh-v1.5
    set MODEL_NAME=BAAI/bge-small-zh-v1.5
    set MODEL_DIR=bge-small-zh-v1.5
)

echo.
echo 将下载模型: !MODEL_NAME!
echo 保存到目录: !MODEL_DIR!
echo.

:: 检查 Python
python --version >nul 2>&1
if errorlevel 1 (
    echo 错误: 未找到 Python，请先安装 Python 3.8+
    echo 下载地址: https://www.python.org/downloads/
    pause
    exit /b 1
)

:: 检查 huggingface-cli
huggingface-cli --version >nul 2>&1
if errorlevel 1 (
    echo 未找到 huggingface-cli，正在安装...
    pip install huggingface_hub
)

:: 下载模型
echo 开始下载...
huggingface-cli download !MODEL_NAME! --include "model.onnx" "tokenizer.json" "tokenizer_config.json" "vocab.txt" --local-dir !MODEL_DIR!

:: 检查结果
if exist "!MODEL_DIR!\model.onnx" (
    echo.
    echo ✅ 模型下载成功!
    echo.
    echo 文件列表:
    dir /b !MODEL_DIR!
    echo.
    echo 配置环境变量:
    echo   set ModelPath=%CD%\!MODEL_DIR!\model.onnx
    echo.
) else if exist "!MODEL_DIR!\onnx\model.onnx" (
    move "!MODEL_DIR!\onnx\model.onnx" "!MODEL_DIR!\model.onnx" >nul
    echo.
    echo ✅ 模型下载成功!
    echo.
    echo 文件列表:
    dir /b !MODEL_DIR!
    echo.
    echo 配置环境变量:
    echo   set ModelPath=%CD%\!MODEL_DIR!\model.onnx
    echo.
) else (
    echo.
    echo ❌ 模型下载失败，请手动下载
    echo.
    echo 手动下载步骤:
    echo 1. 访问 https://huggingface.co/!MODEL_NAME!
    echo 2. 下载以下文件到 models\!MODEL_DIR!\ 目录:
    echo    - model.onnx ^(或 onnx/model.onnx^)
    echo    - tokenizer.json
    echo    - tokenizer_config.json
    echo    - vocab.txt
    echo.
)

pause
