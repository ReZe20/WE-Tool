using System;
using System.Runtime.InteropServices;

namespace WE_Tool.Service
{
    /// <summary>
    /// 任务栏图标进度条(ITaskbarList3)。
    /// 提取类长任务运行时在任务栏按钮上盖一条进度条:绿色正常 / 黄色暂停 / 红色失败。
    /// 
    /// 实现说明(NativeAOT 安全):不用 [ComImport] + Marshal.GetObjectForIUnknown
    /// (built-in COM interop 在 NativeAOT 不受支持,运行时会抛 NotSupportedException)。
    /// 这里用 CoCreateInstance 拿原始 IUnknown 指针,再手动按 vtable 偏移调用
    /// SetProgressValue/SetProgressState(纯函数指针,零 COM marshaling,100% AOT 兼容)。
    /// </summary>
    public static class TaskbarProgressService
    {
        // ---- 原生声明 ----
        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int CoCreateInstance(
            [In] ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext,
            [In] ref Guid riid, out IntPtr ppv);

        // ---- ITaskbarList3 vtable 槽位(0 基) ----
        // IUnknown:     QueryInterface(0) AddRef(1) Release(2)
        // ITaskbarList: HrInit(3) AddTab(4) DeleteTab(5) ActivateTab(6) SetActiveAlt(7)
        // ITaskbarList2:MarkFullscreenWindow(8)
        // ITaskbarList3: SetProgressValue(9) SetProgressState(10)
        private const int VTBL_SetProgressValue = 9;
        private const int VTBL_SetProgressState = 10;

        // ---- TBPFLAG ----
        private const int TBPF_NOPROGRESS = 0x0;
        private const int TBPF_INDETERMINATE = 0x1;
        private const int TBPF_NORMAL = 0x2;
        private const int TBPF_ERROR = 0x4;
        private const int TBPF_PAUSED = 0x8;

        private static readonly Guid CLSID_TaskbarList = new("56fdf344-fd6d-11d0-958a-006097c9a090");
        private static readonly Guid IID_ITaskbarList3 = new("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf");
        private const uint CLSCTX_INPROC_SERVER = 1;

        private static IntPtr _taskbarPtr;
        private static readonly object _gate = new();

        /// <summary>惰性创建 ITaskbarList3 原始指针(失败返回 0,调用方静默跳过)。</summary>
        private static IntPtr GetTaskbar()
        {
            if (_taskbarPtr != IntPtr.Zero) return _taskbarPtr;
            lock (_gate)
            {
                if (_taskbarPtr != IntPtr.Zero) return _taskbarPtr;
                try
                {
                    // 静态只读 Guid 不能直接 ref 传,先拷贝到本地(CS0199)
                    Guid clsid = CLSID_TaskbarList;
                    Guid iid = IID_ITaskbarList3;
                    if (CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out var pv) != 0)
                        return IntPtr.Zero;
                    _taskbarPtr = pv;
                    return _taskbarPtr;
                }
                catch
                {
                    return IntPtr.Zero; // 任务栏不可用(如无桌面 shell)时静默降级
                }
            }
        }

        /// <summary>窗口句柄;获取失败返回 0(调用方静默跳过)。</summary>
        private static IntPtr GetHwnd()
        {
            try
            {
                return App.MainWindowInstance is null
                    ? IntPtr.Zero
                    : WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance);
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        /// <summary>SetProgressValue(HWND, completed, total):按 vtable 槽位 9 调用。</summary>
        private static unsafe void CallSetProgressValue(IntPtr hwnd, ulong completed, ulong total)
        {
            IntPtr p = GetTaskbar();
            if (p == IntPtr.Zero) return;
            IntPtr* vtbl = *(IntPtr**)p;
            if (vtbl == null || vtbl[VTBL_SetProgressValue] == IntPtr.Zero) return;
            // [MemberFunction] 约定下 this 是第一个显式参数(与 runtime 源码 ComWrappers 用法一致)
            var fn = (delegate* unmanaged[MemberFunction]<IntPtr, IntPtr, ulong, ulong, int>)vtbl[VTBL_SetProgressValue];
            fn(p, hwnd, completed, total);
        }

        /// <summary>SetProgressState(HWND, TBPFLAG):按 vtable 槽位 10 调用。</summary>
        private static unsafe void CallSetProgressState(IntPtr hwnd, int state)
        {
            IntPtr p = GetTaskbar();
            if (p == IntPtr.Zero) return;
            IntPtr* vtbl = *(IntPtr**)p;
            if (vtbl == null || vtbl[VTBL_SetProgressState] == IntPtr.Zero) return;
            // [MemberFunction] 约定下 this 是第一个显式参数(与 runtime 源码 ComWrappers 用法一致)
            var fn = (delegate* unmanaged[MemberFunction]<IntPtr, IntPtr, int, int>)vtbl[VTBL_SetProgressState];
            fn(p, hwnd, state);
        }

        /// <summary>任务栏进度:普通(绿色)状态,value 0~100。</summary>
        public static void SetProgress(double value)
        {
            var hwnd = GetHwnd();
            if (hwnd == IntPtr.Zero) return;
            ulong v = (ulong)Math.Clamp((long)value, 0, 100);
            CallSetProgressValue(hwnd, v, 100UL);
            CallSetProgressState(hwnd, TBPF_NORMAL);
        }

        /// <summary>任务栏进度:不确定(绿色流动)状态,用于无百分比的阶段。</summary>
        public static void SetIndeterminate()
        {
            var hwnd = GetHwnd();
            if (hwnd == IntPtr.Zero) return;
            CallSetProgressState(hwnd, TBPF_INDETERMINATE);
        }

        /// <summary>任务栏进度:暂停(黄色)。</summary>
        public static void SetPaused()
        {
            var hwnd = GetHwnd();
            if (hwnd == IntPtr.Zero) return;
            CallSetProgressState(hwnd, TBPF_PAUSED);
        }

        /// <summary>任务栏进度:失败(红色)。</summary>
        public static void SetError()
        {
            var hwnd = GetHwnd();
            if (hwnd == IntPtr.Zero) return;
            CallSetProgressState(hwnd, TBPF_ERROR);
        }

        /// <summary>任务栏进度:清除(进度条消失)。</summary>
        public static void Clear()
        {
            var hwnd = GetHwnd();
            if (hwnd == IntPtr.Zero) return;
            CallSetProgressState(hwnd, TBPF_NOPROGRESS);
        }
    }
}
