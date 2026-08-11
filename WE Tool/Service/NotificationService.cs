using Microsoft.Win32;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Serilog;
using System;

namespace WE_Tool.Service
{
    /// <summary>
    /// 系统通知(AppNotification,Windows App SDK)封装。
    /// 非打包应用:启动时在注册表注册 AUMID(HKCU\Software\Classes\AppUserModelId\WE_Tool,
    /// 与开始菜单快捷方式等效的通知归属注册),再 Register()。
    /// 提取等事件仅在主窗口不在焦点时弹通知(IsWindowFocused 由 MainWindow 激活事件维护)。
    /// </summary>
    public static class NotificationService
    {
        public const string Aumid = "WE_Tool";

        private static bool _isWindowFocused = true;
        private static bool _initialized;

        /// <summary>主窗口是否处于焦点(由 MainWindow 的 Activated 事件维护;启动默认视为有焦点)</summary>
        public static bool IsWindowFocused
        {
            get => _isWindowFocused;
            set
            {
                if (_isWindowFocused == value) return;
                _isWindowFocused = value;
                Log.Debug("[通知] 主窗口焦点变化: {Focused}", value);
            }
        }

        /// <summary>App 启动时调用一次:注册 AUMID + 通知平台注册;失败只记日志,不阻断启动</summary>
        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            try
            {
                EnsureAumidRegistered();
                AppNotificationManager.Default.Register();
                Log.Information("[通知] AppNotification 注册成功(AUMID: {Aumid})", Aumid);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[通知] AppNotification 注册失败,系统通知不可用");
            }
        }

        /// <summary>仅当主窗口不在焦点时发送通知(提取事件的主要入口)</summary>
        public static void NotifyIfUnfocused(string title, string body)
        {
            if (IsWindowFocused) return;
            Show(title, body);
        }

        public static void Show(string title, string body)
        {
            try
            {
                var notification = new AppNotificationBuilder()
                    .AddText(title)
                    .AddText(body)
                    .BuildNotification();
                notification.Tag = "extract"; // 同 Tag 新通知替换旧通知,避免操作中心堆积
                AppNotificationManager.Default.Show(notification);
                Log.Information("[通知] 已发送: {Title} - {Body}", title, body);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[通知] 发送失败: {Title}", title);
            }
        }

        /// <summary>非打包应用必须在系统注册 AUMID(注册表方式,与开始菜单快捷方式等效的通知归属注册)</summary>
        private static void EnsureAumidRegistered()
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{Aumid}");
            key.SetValue("DisplayName", "WE Tool");
            key.SetValue("IconUri", Environment.ProcessPath ?? "");
        }
    }
}
