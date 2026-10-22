using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ReferenceRAG.Core.Helpers
{
    public static class StaticLogger
    {
        // 全局静态日志工厂
        public static ILoggerFactory? LoggerFactory { get; set; }

        // 获取日志实例
        public static ILogger<T> GetLogger<T>()
        {
            try
            {
                return LoggerFactory?.CreateLogger<T>() ?? NullLogger<T>.Instance;
            }
            catch
            {
                return NullLogger<T>.Instance;
            }
        }

        // 可选：直接给静态类用
        public static ILogger GetLogger(string categoryName)
        {
            try
            {
                return LoggerFactory?.CreateLogger(categoryName) ?? NullLogger.Instance;
            }
            catch
            {
                return NullLogger.Instance;
            }
        }
    }
}
