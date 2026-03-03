using Microsoft.Windows.ApplicationModel.Resources;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WE_Tool.Helper
{
    internal class LanguageHelper
    {
        private static readonly ResourceLoader _loader = new();
        // 线程安全的字典，速度极快
        private static readonly ConcurrentDictionary<string, string> _cache = new();

        static LanguageHelper()
        {
            try
            {
                _loader = new ResourceLoader();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "语言资源文件初始化出现异常。");
            }
        }
        public static string GetString(string prefix, string value)
        {
            if (string.IsNullOrEmpty(value)) return "未知";

            string sanitizedValue = value.Replace(" ", "").Replace("-", "");

            if (prefix == "Type" && sanitizedValue.Length > 0)
            {
                sanitizedValue = char.ToUpper(sanitizedValue[0]) + sanitizedValue.Substring(1).ToLower();
            }

            string key = $"{prefix}_{sanitizedValue}";

            return _cache.GetOrAdd(key, k =>
            {
                try
                {
                    string result = _loader.GetString(k + "/Content");
                    return string.IsNullOrEmpty(result) ? value : result;
                }
                catch
                {
                    return value;
                }
            });
        }
        public static string GetResource(string key)
        {
            if (string.IsNullOrEmpty(key)) return key ?? string.Empty;

            return _cache.GetOrAdd(key, k =>
            {
                try
                {
                    string lookupKey = k.Contains('.') ? k.Replace(".", "/") : k;

                    string result = _loader.GetString(lookupKey);

                    if (string.IsNullOrEmpty(result) && lookupKey != k)
                    {
                        result = _loader.GetString(k);
                    }

                    return string.IsNullOrEmpty(result) ? k : result;
                }
                catch
                {
                    return k;
                }
            });
        }
        public static void ClearCache()
        {
            _cache.Clear();
        }
    }
}
