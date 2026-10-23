# Hzip

Windows 原生压缩文件管理器。解压整个程序包后，双击 **Hzip.exe** 使用；命令行入口是 **hz.exe**。便携包自带运行环境和压缩引擎，无需安装 Bandizip 或 .NET。

## 下载与更新

- [最新版本与更新说明](https://github.com/RUI-JACK/Hzip/releases/latest)
- [Windows x64 安装包](https://github.com/RUI-JACK/Hzip/releases/latest/download/Hzip-Setup-x64.exe)
- [Windows x64 便携版](https://github.com/RUI-JACK/Hzip/releases/latest/download/Hzip-Windows-x64.zip)
- [问题反馈](https://github.com/RUI-JACK/Hzip/issues)

项目更新地址固定为 `https://github.com/RUI-JACK/Hzip/releases/latest`，安装包的 Windows 更新信息也使用该地址。程序包内 `update.json` 记录发布页、最新版本 API 和下载地址。当前采用手动下载安装更新，不会在后台自动替换程序。升级或移动安装位置后，按需重新启用右键菜单。

## 使用

- 将归档拖入窗口，或选择“打开归档”。双击文件夹进入，双击文件进行预览；嵌套归档会在新窗口打开。
- 选择“创建归档”，添加文件/文件夹，设置格式、压缩等级、密码和高级选项。
- 任务进行时，点击底部“暂停 / 继续”，或在“任务记录”中选择任务操作。保持 Hzip 运行即可继续；退出程序或重启电脑后不能续传。文件提交或系统组件调用期间会先等待安全暂停点。
- “解压到…”支持智能目录、同名目录、直接解压；默认遇到同名时比较新旧文件大小和时间，询问替换、跳过或重命名，支持全部应用及取消。右键菜单解压同样生效。详情见 EXTRACTION-CONFLICTS.md。
- ZIP / 7Z / TAR / WIM 支持添加、删除、改名；内置文本预览可编辑并保存回支持修改的归档。
- 左侧工具箱提供密码管理、ZIP 修复与密码恢复。
- 可将包内文件/文件夹拖到桌面或资源管理器：先准备副本，再进行复制拖放。创建归档时可直接选择密码库条目。
- 使用默认应用编辑包内文件后，Hzip 会检测磁盘修改，等你确认再写回；外部编辑副本会保留，失败时不会删除。用法和限制见随附 DAILY-WORKFLOWS.md。
- 在“设置与集成”中可分别启用/移除经典菜单和 Windows 11 新菜单。新版直接显示 Hzip 子菜单，提供打开、智能解压和压缩，支持多选；此本地开发版启用/移除新版菜单时需要 Windows 管理员确认，以管理菜单包的开发证书。默认应用由 Windows 设置界面选择。

快捷键：Ctrl+O 打开，Ctrl+N 创建，Ctrl+E 解压，Ctrl+F 搜索，F5 刷新，F2 改名，Delete 删除归档条目，空格预览，Alt+↑ 返回上级。

## 格式与实际范围

可创建 ZIP、7Z、ZIPX(XZ)、EXE 自解压、TAR、TAR.GZ、TAR.XZ、TAR.BZ2、ISO、LZH(LH0)、GZIP、XZ、BZIP2、WIM。读取能力由随附引擎提供，另支持 RAR/RAR5 等格式。ZIP 和 7Z 支持密码与分卷，ZIP 支持 AES-256 和传统加密；中文 ZIP 密码提供 UTF-8 / 本机旧式编码选择，读取时自动尝试兼容编码。

**这份交付是可运行的 Hzip 0.5，并未证明与 Bandizip 所有功能、算法和边界条件完美等价。** 详细的已验证能力、格式限制和差异见 [功能对照表](功能对照表.md)（源码中为 docs/FEATURES.md）。例如：LZH 创建目前使用 LH0 存储；ZIPX 创建不支持加密；密码恢复不具有 Bandizip 专用算法加速。

0.5 的 Windows 11 右键菜单会按类型切换：普通文件/文件夹显示 ZIP、7Z、新建、打开；压缩包显示此处解压、智能解压、同名目录、其他目录、打开。快捷压缩同名自动加序号，不覆盖已有归档。详情见 WINDOWS11-MENU.md。

右键菜单只有“用 Hzip 打开”会显示主界面。压缩和解压使用独立进度窗口，显示当前阶段、总进度、耗时及结果，支持暂停、继续、取消；完成后保留 100% 结果，点击关闭后退出。“新建压缩包”和“解压到其他文件夹”先显示独立设置窗口，确认后进入进度窗口。密码包只在需要时询问密码，失败和取消会在结果中明确显示。不会自动打开归档或资源管理器；可在结果页点击“打开目标文件夹”，或在自选解压设置中勾选完成后打开目录。详见 SHELL-PROGRESS.md。

0.3 新增任务暂停/继续，并扩展 ZIP 修复：综合中央目录、本地文件头与 32/64 位数据描述符恢复，校验后逐项保留正常内容、跳过坏条目；支持正确密码及更多算法。修复结果另存为**未加密 ZIP**，同时生成 `.repair.json` 恢复清单；原文件保持不变，已丢失的数据无法凭空修复。详细范围见随附 PAUSE-AND-REPAIR.md。新版右键菜单沿用已安装的独立菜单组件，无需因本次更新重新启用。

0.2 加强了源文件覆盖保护、重命名冲突检查、创建/修改前后完整性校验，以及失败回滚。若文件占用导致回滚也失败，会保留原文件备份并显示位置；请按该位置的 recovery.json 恢复文件，不要直接删除备份目录。中文密码 ZIP 读取还支持 BZip2 和 LZMA 条目的兼容解码。

图形界面的“安全扫描”入口已在 0.3.1 移除。命令行仍保留依赖系统 AMSI 的 `scan` 命令；当前开发机器的提供程序不可用，扫描未执行，不能理解为文件安全。

## 数据与卸载

设置、最近记录和 Windows 账户加密的密码库保存在 `%LOCALAPPDATA%\Hzip`；任务暂存文件在其 Temp 子目录。密码库受当前 Windows 账户保护，复制程序文件不会自动迁移密码。归档与预览文件在本机处理，应用未实现账户、广告或遥测上传。

移动程序后需重新启用已使用的右键菜单来更新路径。删除程序前，可在设置中分别移除已启用的新版和经典菜单，再删除程序文件夹。新版菜单移除时也会清理本次安装添加的证书信任。需要清除使用记录或密码时，另行处理上述用户数据目录；Recovery 子目录中若有未恢复的文件，请先处理备份。

## 命令行

```powershell
.\hz.exe create D:\out\files.7z D:\documents --format 7z --level 7
.\hz.exe list D:\out\files.7z --json
.\hz.exe extract D:\out\files.7z D:\unpacked --layout smart
.\hz.exe test D:\out\files.7z
.\hz.exe repair D:\broken.zip D:\repaired.zip
.\hz.exe repair D:\protected-broken.zip D:\repaired-plain.zip --password-file D:\key.txt --json
.\hz.exe hash D:\out\files.7z
```

`hz --help` 查看完整参数。创建中文密码 ZIP 时，如需兼容当前简体中文 Windows 上的旧工具，可指定 `--password-encoding 936`。CLI 默认使用 UTF-8 密码编码。密码也可通过 `--password-file` 或 `HZIP_PASSWORD` 环境变量提供。

## 从源码构建

需要 Windows x64 和 .NET 8 SDK。`build.ps1` 默认生成自带运行环境的 release/Hzip，并运行核心集成测试。`-FrameworkDependent` 生成较小的依赖运行环境版本；`-SkipTests` 只发布。原生菜单还需要 LLVM/MinGW-w64 clang++（可通过 HZIP_CXX 指定；本机使用已有 Windhawk Compiler）及 Windows SDK 打包工具。tools/bootstrap_shell.py 下载并校验固定版本的官方 SDK 构建包。构建脚本仅在当前用户证书库创建本地开发签名证书，私钥不导出；安装时才管理系统的信任。运行程序无需编译器或 SDK。

测试包含真实压缩/解压、中文路径、密码、归档修改、分卷、RAR 读取、损坏 ZIP 恢复、取消回滚及路径检查。0.5 的 102 项核心、11 项发布版命令行，以及新版原生菜单的类型过滤和实际压缩/解压操作均通过。0.4 的 6 项图形流程检查另存为历史证据。新检查覆盖 Shift-JIS 编码贯通、文件导出、密码库真实加密创建、受控外部编辑器保存与确认写回、关闭/冲突/失败时保留副本。Word/Excel 实际版本和资源管理器物理鼠标拖放仍未验收。暂停时原生工作进程的 CPU 增量实测为 0，继续后内容哈希一致；另有此前超过 4 GB 的真实 ZIP64 普通归档回环和 Windows UI Automation 记录。新版菜单此前完成本机注册、COM 激活与实际操作验证。测试结果和截图位于源码的 verification 中。

原始 Hzip 代码采用 MIT；独立 7-Zip 引擎和 ZIP 编解码组件保留各自许可证，详见 THIRD-PARTY-NOTICES.md。
