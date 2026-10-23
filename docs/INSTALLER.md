# Hzip 中文安装包

安装文件：`release/installer/Hzip-Setup-0.5.1-x64.exe`。用于 Windows x64，包含完整运行环境和解压引擎，无需额外安装 .NET。0.5.1 新增启动更新提醒，详见 [UPDATES.md](UPDATES.md)。

安装包直接封装用户美化后的 `release/Hzip` 程序目录，不重新设计或修改应用的界面、布局、样式。默认安装到当前用户的 `%LOCALAPPDATA%\Programs\Hzip`，可另选目录，提供开始菜单和可选桌面快捷方式、Windows 已安装应用中的卸载入口。同版本可以重复安装。

可选经典右键菜单；Windows 11 原生右键菜单选项调用随附的既有菜单管理功能，需要管理员确认开发菜单证书。两个菜单选项默认不选，亦可在应用“设置与集成”中启用；不改变 ZIP 等格式的默认打开程序。卸载仅清理仍指向这个安装副本的菜单注册；另一便携副本接管的注册保留。卸载保留个人设置和用户后来创建的文件。

## 构建

先确保美化后的程序已经发布到 `release/Hzip`，然后运行：

```powershell
.\tools\build_installer.ps1
```

可用 `-RuntimeDirectory` 指定要封装的完整程序目录，`-OutputDirectory` 指定安装包输出位置。每次创建新的验证/载荷目录，避免混入历史产物。安装辅助组件在 `installer/Hzip.SetupHelper`，安装定义在 `installer/Hzip.iss`；辅助组件不改动现有应用程序集。

构建使用 Inno Setup 7.1.0。`tools/bootstrap_installer.ps1` 通过解包固定版本 NuGet 工具归档准备编译器，检查固定 SHA-256、NuGet 仓库签名和官方发布者签名，不运行编译器本身的安装程序。工具包来源为 NuGet 的 `Tools.InnoSetup` 重新分发包，编译器发布者为 Pyrsys B.V.。工具上游与文档：[Inno Setup](https://jrsoftware.org/isinfo.php)、[安装命令行](https://jrsoftware.org/ishelp/topic_setupcmdline.htm)。

## 本机验证

2026-10-23，实际执行安装、运行、重新安装及卸载，11 项通过：逐文件验证安装载荷哈希；确认桌面和开始菜单快捷方式、经典菜单及已安装应用入口；核对程序更新配置与安装后的 Windows 更新信息均指向 GitHub 发布页；关闭全局 .NET 查找后启动美化界面；创建、测试、解压中文密码 ZIP；真实同名弹窗重命名；保留其他副本接管的注册；重装；卸载清理及用户文件保留；恢复并核对测试前的注册和快捷方式，确认默认关联及原有原生菜单记录不变。

源界面文件和发布版 Hzip.dll 的哈希在打包前后保持一致。当前证据目录见 `verification/installer-latest.txt`；最终测试子目录见其中 `test-run-latest.txt`，包含安装/重装/卸载日志、`results.json`、`installed-home.png` 和 `installed-conflict.png`。

Windows 11 原生菜单首次导入开发证书的安装选项没有在另一台全新电脑上验收；本次实际测试选择经典菜单，保留现有原生菜单。安装 EXE 没有公众信任的商业代码签名。
