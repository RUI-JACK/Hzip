# 暂停、继续与 ZIP 修复（Hzip 0.3）

## 暂停和继续

压缩等任务进行时，点击主窗口底部的“暂停”；显示“已暂停”后，点击“继续”接着处理。也可打开“任务记录”，选择任务，再点“暂停 / 继续”。暂停时仍可取消任务。暂停的任务会占用其内存和暂存文件，队列中的后续任务会继续等待。

这是保持 Hzip 运行期间的暂停，不保存跨退出或跨重启的续传状态。退出程序会取消未完成任务。原生压缩引擎的工作进程通过 Windows 调试事件暂停全部线程；Hzip 自身的管理代码在文件读写检查点暂停。提交当前文件或调用系统 ISO 组件等阶段需要等待安全边界，届时显示“正在暂停”。若系统策略拒绝暂停，会报告原因并继续原任务，不会把仍在执行的任务标成已暂停。独立的密码恢复窗口尚未增加暂停控制。

验证包含真实原生压缩暂停时的 CPU 时间、加密压缩暂存文件写入停止、暂停中取消、快速切换、修复扫描暂停，以及 GUI 操作后解压内容的独立哈希核对。

## ZIP 修复

选择“ZIP 修复”，指定受损文件和一个新输出路径。如有密码，填写原密码；旧式文件名乱码时可选相应编码。修复后生成独立 ZIP 和同名 `.repair.json` 恢复清单，原文件不被覆盖。**修复结果不加密**，即使源文件有密码；如需保密，可确认恢复内容后重新压缩加密。

恢复器综合中央目录、本地文件头和数据描述符，为每个候选条目核对大小及 CRC；AES AE-2 没有原始 CRC 时，依赖加密认证码。无法验证的条目会跳过。最终生成的 ZIP 会再次校验。清单列出源文件 SHA-256、恢复名称、CRC/认证方式、元数据来源、原始偏移和跳过原因，不保存输入密码。

已验证的损坏情况包括：中央目录截断；部分本地头签名、CRC 和长度字段损坏；自解压前缀导致的偏移变化；32 位和 ZIP64 数据描述符（含无签名且位于文件尾）；坏条目与正常条目混合；Unicode 路径附加字段。已验证 Store、Deflate、BZip2、LZMA，以及提供正确密码的传统 ZipCrypto 和 AES（含 AE-1/AE-2）。其他压缩方法按引擎能力尝试，不保证可恢复。

已经缺失的压缩数据、无法验证的片段、完全破坏的元数据、未知加密方式、分卷重组等仍有明确限制。存在有效外层条目时，不会把嵌套 ZIP 的内部文件误当作顶层条目。路径穿越、已知符号链接和名称冲突会被跳过。不要用“恢复了部分条目”推断整个原归档已完整恢复；请查看清单并检查重要内容。

命令行示例：

```powershell
.\hz.exe repair D:\broken.zip D:\repaired.zip --json
.\hz.exe repair D:\protected.zip D:\repaired-plain.zip --password-file D:\key.txt --encoding 936 --json
```

实现依据：[Windows DebugActiveProcess](https://learn.microsoft.com/en-us/windows/win32/api/debugapi/nf-debugapi-debugactiveprocess)、[ZIP 格式规范](https://pkware.cachefly.net/webdocs/casestudies/APPNOTE.TXT)、[WinZip AES 格式说明](https://www.winzip.com/en/support/aes-encryption/)。这些规范说明机制，测试记录说明本机实际覆盖范围。
