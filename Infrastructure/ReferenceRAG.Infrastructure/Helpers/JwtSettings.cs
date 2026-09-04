using System;
using System.Collections.Generic;
using System.Text;

namespace ReferenceRAG.Core.Helpers
{
    /// <summary>
    /// JWT 配置
    /// </summary>
    public class JwtSettings
    {
        public string SecretKey { get; set; } = "";
        public string Issuer { get; set; } = "";
        public string Audience { get; set; } = "";
        public int ExpireMinutes { get; set; } = 60;
    }
}
