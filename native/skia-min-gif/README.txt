WE Tool 精简版 libSkiaSharp.dll — 构建存档(2026-08-24)
============================================
文件:      libSkiaSharp.dll
大小:      5,723,648 字节 (5.72 MB)
SHA256:    abeea584b8c247eaabe5bc0e0d341ffd7f32ce0c26ab9aa266865a8dd7cb2264
来源:      自编 mono/skia @ bdd0c3a(与官方 4.151.1 同提交)
           SkiaSharp 绑定层 v4.151.1 / C 包装层(src/c)
编译器:    Clang 22.1.3 (VS 组件 VC/Tools/Llvm/x64)
构建系统:  gn(自编)+ ninja 1.13 + MSVC vcvars64(头文件/库路径)
配置:      out/min-gif/args.gn(与原文件一致)— 仅 GIF 解码(Wuffs)+ skottie,
           裁掉 PNG/JPEG/WebP/PDF/字体/Vulkan/D3D 等
导出符号:  937 个,与官方包 100% 一致(官方 libSkiaSharp.dll 同数对比)
性能:      SkOpts AVX2 指令 3082 处(官方 3019),大图缩放基准 472 次/秒
           (官方 448,MSVC 旧版仅 20)— 与官方持平
体积收益:  dll 11.72MB -> 5.46MB;发布包 zip 18.03 -> 15.59MB
           setup.exe 14.20 -> 12.52MB
复现:      见同目录 build.bat(需先搭好 gn/ninja/Clang 工具链)
