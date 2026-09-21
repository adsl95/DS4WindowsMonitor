# DS4Windows Monitor

一个独立的、低干扰 DS4Windows 状态监控器，目标是帮助记录：

- DS4Windows 进程启动/退出
- 主窗口是否响应 WM_NULL
- UI 短暂变慢、持续无响应
- CPU、工作集、Private Memory、句柄数、线程数
- 在确认 UI 假死时生成独立用户态 DMP
- 假死前约 60 秒的轻量采样和当时的线程状态

## 反作弊/低干扰设计

这个项目的目标是“只监控 DS4Windows，不监控游戏”。它不会：

- 注入 DLL
- Hook 游戏
- 枚举或扫描游戏内存
- 打开游戏进程的内存读取句柄
- 读取游戏输入数据
- 修改 DS4Windows 进程
- 安装驱动或服务

正常监控只观察 DS4Windows.exe 自身。自动 DMP 是唯一会在异常时对 DS4Windows 打开 PROCESS_QUERY_INFORMATION + PROCESS_VM_READ 句柄的步骤，而且只在确认 UI 持续无响应时执行。

无法对任何第三方游戏的反作弊实现作“绝对不会误报”的保证，因为检测规则通常不是公开接口。本程序从设计上避免注入、Hook、游戏内存扫描、游戏进程句柄和 HID 拦截，因此它不会主动对游戏做这些常见的侵入式操作。

**权限注意：** 你的 DS4Windows 历史日志显示它曾经以管理员身份运行。Windows 的 UIPI 会限制低完整性进程向高完整性窗口发送消息，所以如果 DS4Windows 是“管理员运行”而监控器不是，日志会出现 `UI_PING_ACCESS_DENIED`，此时 CPU/内存/线程等监控仍会工作，但 UI 假死检测与自动 DMP 不能正常依赖这个窗口探测。为了完整捕捉 UI 假死，可让监控器与 DS4Windows 使用相同的权限级别；这属于 Windows 权限匹配，不涉及游戏进程。

## 目录

程序所在目录通常会出现：

- `monitor.json`：配置
- `Logs\\`：普通监控日志
- `Events\\`：检测到假死时的事件快照
- `Dumps\\`：监控器生成的 DMP

如果程序目录不可写，会自动改用 `%LOCALAPPDATA%\\DS4WindowsMonitor`，日志里会记录这一点。

## 默认判定

- 每 250 ms 检查一次窗口响应
- 单次超过 200 ms 视为 UI_SLOW
- 连续无响应约 1 秒确认 UI_HANG
- 一次假死只自动抓一次 DMP
- 同一个 DS4Windows PID 最多抓 3 个 DMP

因此一次不到 0.5 秒的轻微卡顿通常只进入日志，不会马上生成 DMP。连续达到约 1 秒无响应后才进入假死流程。

## DMP 与 Windows 系统 CrashDump 的区别

监控器生成的 DMP 永远写入程序自己的 `Dumps\` 目录（程序目录不可写时则写入 `%LOCALAPPDATA%\DS4WindowsMonitor\Dumps\`）。Windows Error Reporting 的系统级 LocalDumps 默认使用 `%LOCALAPPDATA%\CrashDumps`，两者可以按时间戳和 PID 明确区分。

## DMP 类型

默认 `DumpTypeFlags = 0x00001921`，包括线程/进程数据、线程信息、完整内存区域信息、已卸载模块和数据段，但不启用完整内存转储，也不启用 `MiniDumpWithPrivateReadWriteMemory` 的虚拟地址空间扫描。

## 停止程序

托盘图标右键可以退出监控器。也可以运行：

```text
DS4WindowsMonitor.exe --stop
```

## GitHub Actions

推送到 `main` 或 `master` 后，GitHub Actions 会在 Windows runner 上自动构建 x64 单文件程序并生成 artifact：

`DS4WindowsMonitor-win-x64`

程序使用 .NET 8 Desktop Runtime；`--self-contained false` 是为了保持下载体积较小。
