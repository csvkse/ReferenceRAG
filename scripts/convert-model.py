#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
ObsidianRAG 模型转换与量化脚本

功能:
1. 下载 HuggingFace 模型
2. 转换为 ONNX 格式
3. FP16 量化（显存减半，速度提升）
4. 可选 TensorRT 转换

使用方法:
    pip install optimum onnx onnxruntime-gpu transformers

    # 下载并转换为 ONNX
    python scripts/convert-model.py --model bge-small-zh-v1.5

    # FP16 量化
    python scripts/convert-model.py --model bge-small-zh-v1.5 --fp16

    # 完整流程（ONNX + FP16）
    python scripts/convert-model.py --model bge-small-zh-v1.5 --fp16 --output models/bge-small-zh-v1.5
"""

import argparse
import os
import sys
from pathlib import Path

# Windows 控制台 UTF-8 支持
if sys.platform == 'win32':
    sys.stdout.reconfigure(encoding='utf-8')
    sys.stderr.reconfigure(encoding='utf-8')

def check_dependencies():
    """检查依赖"""
    missing = []
    try:
        import transformers
    except ImportError:
        missing.append("transformers")

    try:
        import onnx
    except ImportError:
        missing.append("onnx")

    try:
        import onnxruntime
    except ImportError:
        missing.append("onnxruntime")

    if missing:
        print(f"❌ 缺少依赖: {', '.join(missing)}")
        print(f"\n安装命令:")
        print(f"  pip install {' '.join(missing)}")
        return False

    return True


def download_and_convert(model_name: str, output_dir: str, fp16: bool = False):
    """下载并转换模型"""
    try:
        from optimum.onnxruntime import ORTModelForFeatureExtraction
        from transformers import AutoTokenizer
    except ImportError:
        print("❌ 需要安装 optimum")
        print("  pip install optimum")
        return False

    print(f"\n{'='*50}")
    print(f"模型: {model_name}")
    print(f"输出: {output_dir}")
    print(f"FP16: {'是' if fp16 else '否'}")
    print(f"{'='*50}\n")

    # 创建输出目录
    output_path = Path(output_dir)
    output_path.mkdir(parents=True, exist_ok=True)

    # 下载并转换
    print("📥 下载模型...")
    model = ORTModelForFeatureExtraction.from_pretrained(
        model_name,
        export=True  # 自动转换为 ONNX
    )
    tokenizer = AutoTokenizer.from_pretrained(model_name)

    print("✅ 模型下载完成")

    # FP16 量化
    if fp16:
        print("\n⚡ FP16 量化...")
        try:
            import onnx
            from onnxruntime.transformers import optimizer

            # 获取 ONNX 模型路径
            onnx_path = output_path / "model.onnx"

            # 先保存原始模型
            model.save_pretrained(output_path)
            tokenizer.save_pretrained(output_path)

            # 使用 onnx 进行 FP16 转换
            onnx_model = onnx.load(onnx_path)

            # 转换为 FP16
            from onnxconverter_common import float16
            onnx_model_fp16 = float16.convert_float_to_float16(onnx_model, keep_io_types=True)

            # 保存 FP16 模型
            fp16_path = output_path / "model_fp16.onnx"
            onnx.save(onnx_model_fp16, fp16_path)

            # 替换原模型
            os.replace(fp16_path, onnx_path)

            print("✅ FP16 量化完成")

        except ImportError:
            print("⚠️ FP16 转换需要 onnxconverter-common")
            print("  pip install onnxconverter-common")
            print("  使用 FP32 模型...")

            # 保存 FP32 模型
            model.save_pretrained(output_path)
            tokenizer.save_pretrained(output_path)
    else:
        # 保存模型
        print("\n💾 保存模型...")
        model.save_pretrained(output_path)
        tokenizer.save_pretrained(output_path)

    print(f"\n✅ 转换完成: {output_path}")

    # 显示文件
    print("\n文件列表:")
    for f in output_path.iterdir():
        size = f.stat().st_size / (1024 * 1024)
        print(f"  {f.name}: {size:.2f} MB")

    return True


def optimize_onnx(input_path: str, output_path: str):
    """优化 ONNX 模型"""
    try:
        from onnxruntime.transformers import optimizer
    except ImportError:
        print("❌ 需要安装 onnxruntime")
        return False

    print(f"\n🔧 优化 ONNX 模型...")
    print(f"  输入: {input_path}")
    print(f"  输出: {output_path}")

    optimized_model = optimizer.optimize_model(
        input_path,
        model_type='bert',
        num_heads=12,  # BGE-small
        hidden_size=384,
    )

    optimized_model.save_model_to_file(output_path)
    print("✅ 优化完成")

    return True


def convert_to_tensorrt(onnx_path: str, output_path: str, fp16: bool = True):
    """转换为 TensorRT 引擎（需要 TensorRT 环境）"""
    print(f"\n🚀 TensorRT 转换...")
    print(f"  输入: {onnx_path}")
    print(f"  输出: {output_path}")

    # 检查 trtexec
    import subprocess
    import shutil

    trtexec_path = shutil.which('trtexec')
    if not trtexec_path:
        print("❌ 未找到 trtexec")
        print("\n安装 TensorRT 方法:")
        print("  1. 下载: https://developer.nvidia.com/tensorrt/download")
        print("  2. 安装后添加到 PATH:")
        print("     set PATH=%PATH%;C:\\Program Files\\NVIDIA Corporation\\TensorRT\\bin")
        print("\n或者使用 Python API:")
        print("  pip install tensorrt")
        print("\n当前已生成 FP16 ONNX 模型，可直接使用:")
        print(f"  {onnx_path}")
        return False

    # 构建命令
    cmd = [
        'trtexec',
        f'--onnx={onnx_path}',
        f'--saveEngine={output_path}',
        '--minShapes=input_ids:1x1,attention_mask:1x1',
        '--optShapes=input_ids:1x512,attention_mask:1x512',
        '--maxShapes=input_ids:64x512,attention_mask:64x512',
        '--memPoolSize=workspace:1024',
    ]

    if fp16:
        cmd.append('--fp16')

    print(f"  命令: {' '.join(cmd)}")

    result = subprocess.run(cmd, capture_output=True, text=True)

    if result.returncode == 0:
        print("✅ TensorRT 转换完成")
        return True
    else:
        print(f"❌ TensorRT 转换失败:")
        print(result.stderr)
        return False


def main():
    parser = argparse.ArgumentParser(
        description='ObsidianRAG 模型转换与量化工具',
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
示例:
  # 下载并转换为 ONNX
  python scripts/convert-model.py --model BAAI/bge-small-zh-v1.5

  # FP16 量化（推荐）
  python scripts/convert-model.py --model BAAI/bge-small-zh-v1.5 --fp16

  # 指定输出目录
  python scripts/convert-model.py --model BAAI/bge-small-zh-v1.5 --fp16 --output models/bge-small-fp16

  # TensorRT 转换（需要 TensorRT 环境）
  python scripts/convert-model.py --model BAAI/bge-small-zh-v1.5 --tensorrt
        """
    )

    parser.add_argument(
        '--model', '-m',
        default='BAAI/bge-small-zh-v1.5',
        help='HuggingFace 模型名称 (默认: BAAI/bge-small-zh-v1.5)'
    )

    parser.add_argument(
        '--output', '-o',
        default=None,
        help='输出目录 (默认: models/<模型名>)'
    )

    parser.add_argument(
        '--fp16',
        action='store_true',
        help='启用 FP16 量化（显存减半，速度提升）'
    )

    parser.add_argument(
        '--tensorrt',
        action='store_true',
        help='转换为 TensorRT 引擎'
    )

    parser.add_argument(
        '--optimize',
        action='store_true',
        help='优化 ONNX 模型'
    )

    args = parser.parse_args()

    # 检查依赖
    if not check_dependencies():
        sys.exit(1)

    # 确定输出目录
    model_short_name = args.model.split('/')[-1]
    output_dir = args.output or f"models/{model_short_name}"

    # 执行转换
    success = download_and_convert(
        model_name=args.model,
        output_dir=output_dir,
        fp16=args.fp16
    )

    if not success:
        sys.exit(1)

    # 优化
    if args.optimize:
        onnx_path = os.path.join(output_dir, "model.onnx")
        opt_path = os.path.join(output_dir, "model_optimized.onnx")
        optimize_onnx(onnx_path, opt_path)

    # TensorRT
    if args.tensorrt:
        onnx_path = os.path.join(output_dir, "model.onnx")
        trt_path = os.path.join(output_dir, "model.engine")
        convert_to_tensorrt(onnx_path, trt_path, fp16=args.fp16)

    print("\n" + "="*50)
    print("🎉 全部完成!")
    print("="*50)
    print(f"\n配置路径:")
    print(f"  ModelPath: {os.path.abspath(output_dir)}/model.onnx")


if __name__ == '__main__':
    main()
