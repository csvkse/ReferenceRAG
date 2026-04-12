using System;
using System.Collections.Generic;
using System.Text;

using Microsoft.Extensions.Logging;

namespace ObsidianRAG.Core.Helpers
{
    public static class StaticLogger
    {
        // 全局静态日志工厂
        public static ILoggerFactory LoggerFactory { get; set; }

        // 获取日志实例
        public static ILogger<T> GetLogger<T>()
        {
            return LoggerFactory.CreateLogger<T>();
        }

        // 可选：直接给静态类用
        public static ILogger GetLogger(string categoryName)
        {
            return LoggerFactory.CreateLogger(categoryName);
        }
    }
}
