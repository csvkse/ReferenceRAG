#!/usr/bin/env bash
# Obsidian RAG 功能测试脚本

set -e

echo "========================================"
echo "Obsidian RAG 功能测试"
echo "========================================"
echo ""

# 颜色定义
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

# 测试计数
PASS=0
FAIL=0

# 测试函数
test_case() {
    local name=$1
    local command=$2
    local expected=$3
    
    echo -n "测试: $name ... "
    
    if eval "$command" | grep -q "$expected"; then
        echo -e "${GREEN}✓ PASS${NC}"
        ((PASS++))
    else
        echo -e "${RED}✗ FAIL${NC}"
        ((FAIL++))
    fi
}

# 1. 编译项目
echo ""
echo "1. 编译项目"
echo "----------------------------------------"
dotnet build --configuration Release
echo ""

# 2. 测试向量生成修复
echo ""
echo "2. 测试向量生成"
echo "----------------------------------------"
echo "清理旧数据..."
rm -rf data/*.json 2>/dev/null || true

echo "运行索引..."
dotnet run --project src/ObsidianRAG.CLI -- index --verbose

echo ""
echo "检查向量数据..."
if [ -f "data/vectors.json" ]; then
    # 检查 fileId 是否为空
    if grep -q '"fileId": ""' data/vectors.json; then
        echo -e "${RED}✗ 向量 fileId 为空${NC}"
        ((FAIL++))
    else
        echo -e "${GREEN}✓ 向量 fileId 已正确设置${NC}"
        ((PASS++))
    fi
    
    # 统计向量数量
    VECTOR_COUNT=$(grep -o '"chunkId"' data/vectors.json | wc -l)
    echo "向量数量: $VECTOR_COUNT"
else
    echo -e "${RED}✗ 向量文件不存在${NC}"
    ((FAIL++))
fi

# 3. 测试查询效果
echo ""
echo "3. 测试查询效果"
echo "----------------------------------------"
echo "测试查询: 如何配置系统？"
dotnet run --project src/ObsidianRAG.CLI -- query "如何配置系统？"

echo ""
echo "测试查询: 核心功能"
dotnet run --project src/ObsidianRAG.CLI -- query "核心功能"

# 4. 测试模型管理
echo ""
echo "4. 测试模型管理"
echo "----------------------------------------"
echo "列出可用模型:"
dotnet run --project src/ObsidianRAG.CLI -- model list

echo ""
echo "当前模型:"
dotnet run --project src/ObsidianRAG.CLI -- model current

echo ""
echo "模型推荐:"
dotnet run --project src/ObsidianRAG.CLI -- model recommend --lang zh

# 5. 测试文件监控（可选）
echo ""
echo "5. 测试文件监控"
echo "----------------------------------------"
echo "启动文件监控（5秒后自动停止）..."

# 创建测试文件
TEST_FILE="test-vault/test-monitor.md"
echo "# 测试文件监控" > "$TEST_FILE"
echo "这是一个测试文件，用于测试文件监控功能。" >> "$TEST_FILE"

# 启动监控（后台）
dotnet run --project src/ObsidianRAG.CLI -- watch &
WATCH_PID=$!

# 等待启动
sleep 2

# 修改文件
echo "" >> "$TEST_FILE"
echo "## 新增内容" >> "$TEST_FILE"
echo "这是新增的内容，触发文件变更事件。" >> "$TEST_FILE"

# 等待检测
sleep 3

# 停止监控
kill $WATCH_PID 2>/dev/null || true

# 清理测试文件
rm -f "$TEST_FILE"

echo -e "${GREEN}✓ 文件监控测试完成${NC}"
((PASS++))

# 6. 测试向量查询
echo ""
echo "6. 测试向量查询效果"
echo "----------------------------------------"
echo "运行查询测试..."
dotnet run --project src/ObsidianRAG.CLI -- test query

# 7. 测试基准性能
echo ""
echo "7. 测试基准性能"
echo "----------------------------------------"
dotnet run --project src/ObsidianRAG.CLI -- test benchmark --iterations 5

# 8. 测试向量生成
echo ""
echo "8. 测试向量生成"
echo "----------------------------------------"
dotnet run --project src/ObsidianRAG.CLI -- test vector --text "这是一个测试文本，用于验证向量生成功能。"

# 总结
echo ""
echo "========================================"
echo "测试总结"
echo "========================================"
echo -e "${GREEN}通过: $PASS${NC}"
echo -e "${RED}失败: $FAIL${NC}"
echo ""

if [ $FAIL -eq 0 ]; then
    echo -e "${GREEN}所有测试通过！${NC}"
    exit 0
else
    echo -e "${RED}部分测试失败，请检查日志。${NC}"
    exit 1
fi
