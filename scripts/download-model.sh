#!/bin/bash
# 下载 BGE 中文向量模型

set -e

echo "=== ObsidianRAG 模型下载脚本 ==="
echo ""

# 创建模型目录
mkdir -p models
cd models

# 选择模型
echo "请选择要下载的模型："
echo "1) bge-small-zh-v1.5  (384维, ~100MB) - 推荐"
echo "2) bge-base-zh-v1.5   (768维, ~400MB)"
echo "3) bge-large-zh-v1.5  (1024维, ~1.3GB)"
echo "4) bge-m3             (1024维, ~2GB, 多语言)"
echo ""
read -p "请输入选择 [1-4, 默认1]: " choice
choice=${choice:-1}

case $choice in
    1)
        MODEL_NAME="BAAI/bge-small-zh-v1.5"
        MODEL_DIR="bge-small-zh-v1.5"
        ;;
    2)
        MODEL_NAME="BAAI/bge-base-zh-v1.5"
        MODEL_DIR="bge-base-zh-v1.5"
        ;;
    3)
        MODEL_NAME="BAAI/bge-large-zh-v1.5"
        MODEL_DIR="bge-large-zh-v1.5"
        ;;
    4)
        MODEL_NAME="BAAI/bge-m3"
        MODEL_DIR="bge-m3"
        ;;
    *)
        echo "无效选择，使用默认模型 bge-small-zh-v1.5"
        MODEL_NAME="BAAI/bge-small-zh-v1.5"
        MODEL_DIR="bge-small-zh-v1.5"
        ;;
esac

echo ""
echo "将下载模型: $MODEL_NAME"
echo "保存到目录: $MODEL_DIR"
echo ""

# 检查是否安装了 huggingface-cli
if ! command -v huggingface-cli &> /dev/null; then
    echo "未找到 huggingface-cli，正在安装..."
    pip install huggingface_hub
fi

# 下载模型
echo "开始下载..."
huggingface-cli download $MODEL_NAME \
    --include "model.onnx" "tokenizer.json" "tokenizer_config.json" "vocab.txt" \
    --local-dir $MODEL_DIR

# 检查下载结果
if [ -f "$MODEL_DIR/model.onnx" ]; then
    echo ""
    echo "✅ 模型下载成功!"
    echo ""
    echo "文件列表:"
    ls -lh $MODEL_DIR/
    echo ""
    echo "配置环境变量:"
    echo "  export ModelPath=$(pwd)/$MODEL_DIR/model.onnx"
    echo ""
else
    # 尝试下载 onnx 文件夹中的模型
    echo "尝试下载 onnx 文件夹中的模型..."
    huggingface-cli download $MODEL_NAME \
        --include "onnx/model.onnx" "tokenizer.json" "tokenizer_config.json" "vocab.txt" \
        --local-dir $MODEL_DIR
    
    if [ -f "$MODEL_DIR/onnx/model.onnx" ]; then
        # 移动到根目录
        mv $MODEL_DIR/onnx/model.onnx $MODEL_DIR/model.onnx
        rmdir $MODEL_DIR/onnx 2>/dev/null || true
        
        echo ""
        echo "✅ 模型下载成功!"
        echo ""
        echo "文件列表:"
        ls -lh $MODEL_DIR/
        echo ""
        echo "配置环境变量:"
        echo "  export ModelPath=$(pwd)/$MODEL_DIR/model.onnx"
        echo ""
    else
        echo ""
        echo "❌ 模型下载失败，请手动下载"
        echo ""
        echo "手动下载步骤:"
        echo "1. 访问 https://huggingface.co/$MODEL_NAME"
        echo "2. 下载以下文件到 models/$MODEL_DIR/ 目录:"
        echo "   - model.onnx (或 onnx/model.onnx)"
        echo "   - tokenizer.json"
        echo "   - tokenizer_config.json"
        echo "   - vocab.txt"
        echo ""
    fi
fi
