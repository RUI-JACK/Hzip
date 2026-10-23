# Hzip 0.3.1 未完成能力调查

历史快照说明：0.4 已实现本报告中的常规拖出解压、创建时密码库选择、外部编辑检测与确认写回，并贯通编码参数。具体能力、限制和证据以 FEATURES.md、DAILY-WORKFLOWS.md、VERIFICATION.md 为准；其他尚缺项目仍保留在本报告中供后续规划。

调查日期：2026-10-20。当前发布程序集 0.3.1；本机 Bandizip 7.46.0.1。对照当前源码、随附引擎真实格式清单、既有验收记录和 Bandizip 官方 Windows 功能帮助。本次未修改程序功能；以下不是逐个格式的全量运行测试，也不是全部二进制函数审计。

## 确认尚未实现的用户操作

| 项目 | 现在的具体限制 | 源码 / 证据 |
|---|---|---|
| 从归档列表拖出文件解压 | 可以拖入文件/文件夹；没有把归档条目拖到桌面或文件夹的拖出流程，也没有直接向拖放目标解压的快速拖放 | MainWindow.xaml 只有 Drop/DragOver 接收；MainWindow.xaml.cs 的 Window_Drop；应用无 DoDragDrop / 虚拟文件拖出实现 |
| 归档内图片缩略图网格 | 当前列表只显示通用图片符号；双击后可以查看单张图片、缩放旋转，不等于缩略图浏览 | MainWindow.xaml 的 DataGrid 模板；Models.cs 的 Glyph；PreviewWindow.cs |
| 外部编辑器修改后写回 | “用默认应用打开”只有 Process.Start；Word、Excel 等外部编辑后的文件没有变更跟踪、回写确认或写回生命周期管理；关闭预览还会释放临时文件 | PreviewWindow.cs 的 external.Click、Closed；内置文本编辑的 save 回调另行存在 |
| ZIP/RAR 注释查看 | ArchiveEntry 没有注释字段；引擎目录解析不向模型传递注释；界面没有注释展示。内部 ZIP 重写保留 entry.Comment 不等于能查看注释 | Models.cs；Engine.cs 的 ParseListing；ManagedZip.cs |
| 更多读取格式 | 至少 ALZ、EGG、ACE、BR/Brotli、LZ4、ZPAQ、PEA、DAA、ISZ 没有可用处理器；不能把“更多格式”理解为 Bandizip 全格式兼容 | verification/gap-audit-0.3.1/engine-inventory.txt 是随附 7-Zip 26.04 的实际 `i` 输出；Hzip 没有对应额外解码器。结论依据处理器缺失，不是逐格式样本实测 |
| 智能高速压缩策略 | 没有根据内容可压缩性自动中止无收益压缩、切换存储的 Hzip 策略；手动选择压缩等级 0 不是自动决策 | CreateOptions、ArchiveService.CreateWithEngine、ManagedZip.CreateAsync；底层引擎可能有自身优化，未据此声称完全等价或更慢 |
| 单窗口批量任务并行调度 | 当前批量解压 foreach 排队，同一主窗口由 SemaphoreSlim(1,1) 串行执行；没有并发数设置 | MainWindow.xaml.cs 的 worker、EnqueueAsync、Batch_Click。此项不表示底层引擎不能多线程，也不把 Bandizip 单 ZIP 条目并行与多归档任务并行混为一谈 |
| 密码库的完整交互 | 新建归档不能直接从密码库选择；密码条目不能原位编辑、排序，没有主密码锁定和迁移导入导出。解密提示窗口已经能选择保存的密码，不能说密码库未接入 | DialogKit.cs 的 CreateWindow、PasswordWindow；ToolsWindows.cs 的 VaultWindow；UserData.cs |
| 设置导入导出、主题、多语言 | 只有少量默认项；没有界面导入导出、主题切换和完整语言资源系统 | ToolsWindows.SettingsWindow；UserSettings；App.xaml |
| 公共发行与升级流程 | 只有 Windows x64 便携发布；没有 x86/ARM64 原生版本、标准安装卸载及自动升级流程。Windows 11 菜单已有本机开发签名安装，不等于公共受信任签名发行 | 项目构建目标、build.ps1、ModernShellIntegration.cs、shell 管理脚本 |

以上是本次确认的主要缺口，并非穷尽 Bandizip 的每一个设置、命令行开关和格式变体。

## 已实现，但范围还有限

| 项目 | 已有能力 | 尚缺 / 需要区分 |
|---|---|---|
| LZH 创建 | LH0 存储型封装可读写 | 没有 LH7 压缩；当前 GBK 路径及文件大小限制见 FEATURES.md |
| ZIPX | XZ 内容创建、读取与基础回环 | 当前写入器不支持密码/分卷；与现有 ZIP、7Z 加密能力分开看；不能直接推断 Bandizip 支持所有 ZIPX 加密组合 |
| 密码恢复 | 数字范围和字典候选，受控 CPU 并发，真实归档验证 | 没有通用字符集/掩码搜索、专用算法加速；独立恢复窗口尚无暂停；没有性能等价证据 |
| 文件名编码 | 手动选 GBK、Shift-JIS、Big5、OEM、UTF-8；默认交给引擎 | Hzip 没有自身的多语言文件名概率识别；密码字节多编码重试不能算文件名自动识别 |
| 暂停/继续 | 保持程序运行期间可暂停并继续，已验证 | 退出/重启后没有断点续作；这里是 Hzip 的附加限制，未据此声称 Bandizip 必然支持跨重启续作 |
| ZIP 修复 | 已交叉解析多种记录、逐项校验、恢复正常条目并生成报告 | 不支持分卷重组、保持原有加密输出；数据已丢失不能保证修复。Bandizip 官方同样不保证任意损坏可恢复，不能把“不能全部修好”算作 Hzip 独有缺失 |
| 图片/文本预览 | 单张系统图片解码、基础文本编辑 | 内置预览 32 MiB，文本编辑 4 MiB；不自带完整 WebP/PSD 等解码器，具体取决于系统 |
| 安全扫描 | CLI 仍有 AMSI 适配接口 | 本机已有记录显示不可用、未执行；GUI 已移除按钮，不能列作本机可用功能 |
| 元数据与系统映像 | 普通文件/目录内容、时间信息；WIM 和 ISO 有基础回环 | 不承诺完整 ACL/ADS/链接、启动镜像或所有 UDF 边界等价；不能将有限元数据支持误写成完全没有 |

## 另外发现的集成缺口

用户手动选择文件名编码后，普通“解压到”会传 CodePage；但 PreviewAsync 构造 ExtractOptions 时没有 CodePage 参数，预览/嵌套归档可能重新使用默认编码。Batch_Click 和启动参数智能解压也未传主窗口 codePage。本次通过源码确认参数未贯通，未复现所有语言的错误结果；这是需要定向验证修正的问题，不应当作普通预览功能完全没实现。

## 已有实现，不应再列作缺失

- 暂停/继续、增强 ZIP 修复、Windows 11 右键菜单、中文密码、分卷和归档文本保存已有实现及既有测试记录。
- 密码提示窗已经支持从密码库选择密码；缺口主要在创建、编辑、主密码和迁移等操作。
- 压缩后把源文件送回收站已经存在，不能说未实现。
- 普通解压已有 Zone.Identifier 来源标记复制。本次在独立自建 ZIP 上写入 ZoneId=3，经当前发布版 CLI 解压后逐字核对标记一致，证据：verification/gap-audit-0.3.1/runtime-proof.json。本次不据此替预览、修复及所有文件系统作保证。
- RAR 创建不列作替代缺口：Hzip 与 Bandizip 均以 RAR 读取为目标。

## 已做但还缺验收证据

EXE 自解压 GUI 真正启动操作、所有 CAB/DMG/ARJ 等格式变体、超大 ZIPX/复杂 ISO/WIM 元数据、各杀毒软件对原生暂停机制的策略、其他设备和 DPI 组合仍没有充分验收。已有 84 项核心、11 项 CLI 以及部分 GUI 记录不能换算成所有场景都通过。

## 建议顺序

1. 先补编码参数贯通，再做拖出解压、外部编辑后写回；这是基础操作的一致性与内容保存问题。
2. 增加新建归档的密码库选择、图片缩略图、ZIP/RAR 注释和更多便捷设置。
3. 按真实文件需求补格式与高性能能力；需要 EGG/ALZ/LZ4 时再确定独立编解码方案，不依赖原 Bandizip 私有组件。
4. 跨重启续作、所有平台和完整商业发行流程另作需求，不把它们混入本机常用功能已完成的承诺。

## 官方对照来源

- [Windows 功能与格式清单](https://en.bandisoft.com/bandizip/)
- [完整帮助索引](https://en.bandisoft.com/bandizip/help/)
- [快速拖放](https://en.bandisoft.com/bandizip/help/fastdnd/)
- [外部编辑后写回](https://en.bandisoft.com/bandizip/help/how-to-edit-and-save-file-in-archive-directly/)
- [图片缩略图](https://en.bandisoft.com/bandizip/help/image-preview-in-archive/)
- [密码管理](https://en.bandisoft.com/bandizip/help/how-to-use-password-manager/)
- [高速压缩](https://en.bandisoft.com/bandizip/help/fastarchiving/)
- [单 ZIP 条目并行解压及其限制](https://en.bandisoft.com/bandizip/help/parallel-extraction/)
- [文件名编码自动识别及其限制](https://en.bandisoft.com/bandizip/help/codepage-autodetect-problem/)
- [来源标记复制](https://en.bandisoft.com/bandizip/help/zone-identifier/)

官方页面用于确认对方功能；Hzip 的状态来自当前源码、真实引擎清单和指定测试，不能由官方页面替代证明。
