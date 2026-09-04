using System;
using System.Collections.Generic;
using System.Text;

using Microsoft.Extensions.Logging;

namespace ReferenceRAG.Core.Helpers
{
    public static class StaticLogger
    {
        // 全局静态日志工厂
        public static ILoggerFactory? LoggerFactory { get; set; }

        // 获取日志实例
        public static ILogger<T> GetLogger<T>()
        {
            return LoggerFactory?.CreateLogger<T>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<T>.Instance;
        }

        // 可选：直接给静态类用
        public static ILogger GetLogger(string categoryName)
        {
            return LoggerFactory?.CreateLogger(categoryName) ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        }
    }
}
