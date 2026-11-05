# QuietRunner

用于开发和测试子进程的 Windows 执行器。它不在正常 CDir 进程中启用。

```powershell
QuietRunner.exe --exe C:\Tools\dotnet.exe --workdir C:\Project --timeout-seconds 300 -- build Source/CDir.csproj -p:UseSharedCompilation=false -nodeReuse:false
```

- 将挂起的新进程先放入 Job Object，再恢复执行。默认不限制 CPU 使用率、不绑定 CPU 核心，也不注入 `DOTNET_PROCESSOR_COUNT` 或限制子进程内部并行；只有显式传入 `--cpu-percent 1..100` 时才启用整棵子进程树的 CPU 硬上限，`0` 表示不限。
- 执行器进入后台模式，并对子进程设置 Idle 和很低的 I/O 优先级，关闭常驻 MSBuild/编译服务器。子进程使用正常内存页优先级，避免 WPF 在后台内存策略下反复压缩工作集、拖慢启动；测试程序不会再次进入后台内存模式。子进程环境带有 `CDIR_QUIET_RUNNER=1`，供内部构建/打包入口核验执行路径。
- `--temp-root` 只覆盖该子进程及后代的 TEMP/TMP。目录必须已经存在，所有权与清理由调用方负责。
- 子进程继承当前 Windows 桌面。独立桌面选项、桌面创建和桌面标记已删除；界面测试通过 `Source/Scripts/Test.ps1 -UIGroup ...` 按需启动，窗口、菜单和对话框可能出现在当前桌面。
- 设置 CPU 硬上限或降低子进程 I/O 优先级失败时拒绝启动，不会绕过保护继续运行。
- 只继承三个标准流句柄。使用原生 Windows 参数转义，不经 shell 重解释。
- 当前用户、当前会话的所有执行器共用命名互斥量排队，同一时刻只启动一棵子进程树；等待期间也可用 Ctrl+C 取消。超时时间从子进程恢复运行时计算。
- 子进程退出码原样返回；超时为 124，取消为 130，执行器配置/启动错误为 2。退出时只终止本次创建的进程树并关闭 Job；不枚举、停止或改变其他应用。

适用于 Windows x64 与 .NET 10。UI 验证需由调用方显式选择；普通构建只编译，不启动测试窗口。
