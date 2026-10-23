# Hzip 验证记录（当前版本 0.5）

验证日期：2026-10-20。环境：当前 Windows x64 主机，200% DPI。此记录说明实际测试范围，不代表所有系统、所有格式变体或全部 Bandizip 功能均已验收。

## 0.5 独立进度窗口

- 右键压缩和解压增加独立进度、暂停/继续、取消及保留的完成结果页，不打开主界面。设置窗口确认后再显示进度。
- 16 项进度/输出/取消/密码流程、真实注册 COM 服务中的 256 MiB 暂停/恢复任务、4 项真实包身份/EFS 操作及原有 6 项图形工作流程验证，见 SHELL-PROGRESS.md。
- 未修改核心引擎和 EFS 提交代码，历史核心检查未重复计数。

## 0.5 真实包身份与 EFS 修复

- 已在实际注册的 COM 服务中复现并修复“无法加密指定的文件”。临时文件使用内容流提交，不传播暂存目录的 EFS 加密属性。
- 103 项核心（含新增 EFS 回归）、单独的超过 4 GB 回环、11 项命令行、16 项后台窗口检查、6 项图形流程通过。
- 实际注册服务的 4 项压缩/解压专项通过，且用户原下载目录中的失败任务已成功完成，原同名文件保留。见 EFS-FIX.md。

## 0.5 右键后台与独立设置窗口

- 真实发布版 16 项专项检查：快捷操作无窗口；创建/解压只显示设置；密码输入、错误重试、取消及损坏包错误不显示主界面。通过窗口显示事件、UI Automation、内容核对和进程退出共同验证。
- 已安装原生菜单的实际动作和原有 6 项图形流程另做回归，详见 SHELL-BACKGROUND.md。压缩核心和菜单 DLL 未修改，历史核心检查不算作本次重跑。

## 0.5 整体复查

- 2026-10-20 重新完成 103 项核心检查（包含超过 4 GB 的真实 ZIP64 回环）、11 项发布版命令行、6 项加载发布版实际程序集的图形流程，以及当前已注册菜单的 9 项实际动作，均通过。
- 另行用发布版命令行解压 8 卷 7Z 并逐文件核对哈希；缺少末卷时失败且不提交部分文件。
- 修正两个测试工程共用构建目录及从引擎路径推断样本目录的问题。应用运行代码不变。
- 当前证据见 verification/audit-latest.txt 和 docs/CURRENT-AUDIT.md；历史验收记录保留在下方。

## 0.5 按类型切换右键菜单

- verification/release-0.5-final/results.json：102 项通过，0 失败，包含 7 项快捷压缩命名、ZIP/7Z 实际回环、多选、同名保护及非法输入检查。
- verification/menu-0.5-latest.txt 指向本次独立测试目录。其中 results.json 包含 9 项原生命令实际结果：ZIP、7Z、混合多选、同名保护、此处/智能/同名目录解压、自定义创建窗口生成归档、自选目录窗口解压；内容由独立 ZIP 读取器或解压后逐字核验。
- 同目录 ui-results.json：文件夹打开入口实际弹出文件选择窗口，初始目录为选择的文件夹；压缩包打开入口加载真实 sample.txt 条目。registration.json 记录本机菜单身份包 0.5.1.0。
- tests/ShellProbe.cpp 从实际 Shell 选择对象与注册 COM surrogate 核对：普通文件/文件夹四项，压缩包五项；多选过滤；同名目录动态标题；隐藏压缩命令在归档选择上不可调用。内部八个动作不代表用户同时看到八项。
- 此次不把历史 0.2 的三命令菜单记录或菜单缺失的 PrintWindow 图像用作新版视觉证据。Windows 11 原生菜单外观交由系统绘制；经典静态兼容入口另行管理。

## 0.4 日常流程补全

- verification/release-0.4-final/results.json：95 项通过，0 失败。含原 84 项和 11 项新流程检查：Shift-JIS 预览/修改、选择导出、空目录/重叠选择/缓存冲突保护，外部编辑原子保存、来源标记、快照、占用/取消/写回失败和同条目冲突保护。
- verification/ui-0.4-release-final/results.json：真实 WPF 操作，包括密码库选择后实际加密创建、旧式日文编码预览/编辑写回、受控默认应用保存后的检测与确认、关闭后保留未写回副本、Windows FileDrop 物理文件数据对象。
- ui-0.4-release-final 中 PNG 是实际 WPF 视觉树渲染记录；screenshots/home-0.4.png 是发布程序 PrintWindow 截图。没有把控件存在当作写回成功证据，归档实际内容另行读取核验。
- Word/Excel 实际版本、资源管理器的物理鼠标拖放尚未验收；已完成的导出与数据对象检查不能替代这两项。独立受控编辑程序不修改用户已有默认关联，临时专用扩展名的测试注册在结束时删除。
- 用法与数据保留策略见 DAILY-WORKFLOWS.md；外部编辑副本保留为明文，不自动删除。

## 0.3.1 界面修正

- 移除安全扫描按钮及其图形事件处理入口；其他功能入口检查未发现未实现的占位操作。
- verification/ui-0.3.1-results.json：发布版实际启动，UI Automation 确认不存在“安全扫描”控件，密码管理、ZIP 修复、密码恢复仍可见，窗口正常响应。
- verification/screenshots/toolbox-0.3.1.png：实际窗口截图已检查，工具箱显示三个已实现入口，无空白占位。
- 本次仅调整图形入口、说明与版本号；下方 84 项核心结果来自 0.3，未将其描述为本次重新运行。

## 0.3 验收

- verification/release-0.3-final/results.json：84 项通过，0 失败，含此前 57 项、6 项真实暂停检查、21 项增强修复检查。
- verification/release-0.3-final/native-pause-proof.json：67,108,864 字节随机文件，原生工作进程暂停 600 毫秒期间 CPU 时间增量为 0；继续后压缩解压内容 SHA-256 一致。另测管理代码加密压缩停止写入、暂停中取消、快速继续/再暂停、启动前暂停以及修复扫描暂停。
- verification/cli-final/results.json：当前发布版 11 项通过，含加密 ZIP 修复、JSON 恢复清单、错密码和既有输出保护。
- verification/ui-0.3-results.json 与 screenshots/pause-0.3.png、pause-jobs-0.3.png：真实 GUI 暂停、任务窗口继续；实际 7Z 创建后独立 SHA-256 核对一致。
- screenshots/repair-dialog-0.3.png、repair-result-0.3.png：真实修复对话框与结果；损坏样本中的 bad.txt 被跳过，good.txt 的内容由 Python ZIP 读取器独立核对一致，原件哈希与清单相符。

ZIP64 修复测试针对 64 位描述符及异常尾记录的结构样本；下方历史 4 GB 测试是普通压缩/解压，不应将其表述为 4 GB 损坏 ZIP 修复已实测。未覆盖所有杀毒软件对 Windows 调试接口的策略、所有硬件及所有损坏组合。

## 0.2 增量验收

- verification/priority-delivery/results.json：57 项通过，0 失败；包含下方历史核心项及第三方 ZIP 编码/算法兼容、源文件覆盖拦截、命名冲突、被占用文件回滚和回滚失败保留备份。
- 原 8 项命令行基础检查现包含在 verification/cli-final/results.json 的当前 11 项检查中，使用自带运行环境。
- verification/shell-probe-results.json：三个原生命令及四类真实 Shell 选择对象通过；Windows 注册的 COM surrogate 激活成功。
- verification/modern-menu-registration.json：真实删除菜单包、清理安装者添加的证书信任并重新安装通过，默认文件关联未变。
- verification/modern-menu-workflow.json：资源管理器的 Hzip 菜单及子命令经 UI Automation 检测；实际打开归档；同一原生命令接口触发创建与解压后，独立读取/哈希核对通过。

当前桌面锁屏，不能提供新菜单弹出层的可靠像素截图。菜单结构和动作已由真实 UI Automation 及运行结果验证；窗口截图只用于可见的 Hzip 程序窗口。详见 WINDOWS11-MENU.md。

## 0.1 历史基线

| 项目 | 实际结果 | 证据 |
| --- | --- | --- |
| 核心归档流程 | 36 项通过，0 失败；含格式回环、中文路径与密码、分卷覆盖、归档修改、修复、取消与路径保护 | verification/core-final/results.json |
| 兼容与系统集成 | 2 项通过；Bandizip 旧式中文文件名/密码互操作，当前用户菜单注册和移除 | verification/integration-final/results.json |
| 超过 4 GB 的 ZIP64 | 4,294,975,488 字节真实压缩和解压，SHA-256 一致；大体积临时文件已清理 | verification/core-large/zip64-proof.json |
| 发布版命令行 | 原 8 项基础检查现包含在当前 11 项报告中；运行环境隔离、创建/读取/解压/测试、参数错误保护与中文加密 | verification/cli-final/results.json |
| 图形界面工作流 | 真实窗口创建归档、预览编辑保存、解压并核对内容 | verification/ui-workflow-results.json |
| 发布版图形界面 | 实际从 release/Hzip/coreclr.dll 加载运行环境；归档浏览、PNG 预览、940×630 与 1240×810 逻辑尺寸截图；关闭主窗口时出现未保存提示，取消后窗口及编辑内容保留 | verification/ui-release-results.json 与 screenshots |
| AMSI | 系统提供程序初始化失败 0x80070103；程序明确报告扫描未执行，未声称恶意样本检出成功 | verification/core-final/amsi-report.json |

截图通过目标窗口 PrintWindow 采集。未覆盖其他计算机、ARM64/x86、不同系统编解码器、所有 DPI/多屏组合或任意历史损坏归档。更完整的能力边界见 FEATURES.md。

便携包包含运行环境、独立压缩引擎和第三方许可证。源码包包含构建脚本、对应第三方源码、上述结构化结果与窗口截图。release-manifest.json 列出交付目录中每个文件的 SHA-256。

后续图标更新使用用户提供的 SVG，原样保留为 Assets/Hzip.svg，生成透明 PNG 和十种尺寸的 ICO，应用于可执行文件、窗口及界面标志。已重新发布并实际启动主窗口和设置窗口，截图为 screenshots/icon-home.png 和 icon-settings.png；icon-executable.png 来自发布版可执行文件内嵌资源。桌面快捷方式已指向新 ICO，并发送系统图标变更通知。其余截图记录此前功能验收时的外观。
