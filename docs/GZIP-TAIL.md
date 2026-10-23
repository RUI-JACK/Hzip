# GZIP 固件附加数据兼容修复

2026-10-20，当前 Windows x64 本机。已更新发布目录中的 Hzip.Core.dll、Hzip.dll 和 hz.dll。

## 原因与处理

用户提供的 OpenWrt `.img.gz` 在有效 GZIP 数据后附有 262 字节数据，其中含 OpenWrt 版本、设备名称等元数据及固件尾部。原生 7-Zip 26.04 将其报告为 `There are some data after the end of the payload data`，Hzip 原先把这个结果作为解压失败。

现由独立 GZIP 读取器逐个解析压缩成员，检查头部、可选头部 CRC、Deflate 数据、CRC32 与原始长度；支持拼接成员。全部成员通过后，才把解压文件从暂存区提交到目标位置。外部尾部不写入解压文件，作为明确提示返回；原始压缩包保持不变，附加数据本身不作为已验证内容。测试归档操作也返回该提示，图形界面和命令行均展示。其他归档格式继续使用现有逻辑，没有普遍忽略 7-Zip 的错误码。

CRC 错误、长度不匹配、头部或数据截断、损坏的后续成员仍然失败，并且不提交部分输出。GZIP 管理代码保留暂停、继续和取消检查点；TAR.GZ 仍解开内部 TAR 成员。针对用户目录中的同名 `.img` 文件夹，自动改名策略现也处理文件夹冲突；不会覆盖或删除原文件夹。

## 当前验证

- 核心回归 123 项通过，其中 GZIP 专项 20 项；涵盖普通与空流、拼接流、可选头部/头部 CRC、外部尾部、TAR 包装、损坏拒绝、暂停/取消和目录冲突。
- 加载当前发布程序集的 6 项图形流程回归通过；另实际点击主界面“测试”，确认弹窗展示附加数据提示。
- 用户原文件经 Python zlib 独立解码并验证 GZIP CRC 与长度，解压结果与 Hzip 的 SHA-256 一致。
- 实际安装的 Windows 11 COM 右键命令执行“解压到此处”成功；只显示独立进度窗口，两个进度条完成时均为 100%。结果文本包含 262 字节附加数据提示。输出未继承 COM 配置目录中的 EFS 加密。
- 默认自动改名生成 `D:\RAY\download\Compressed\openwrt-25.12.5-rockchip-armv8-hinlink_h28k-squashfs-sysupgrade (2).img`，176,160,768 字节；已有文件、目录和原始 GZIP 均保持。

原文件 SHA-256：`50d362174c531ac7fa9cd7bd0466d23642c48afdeb9e58e31905e49907057e47`。

解压文件 SHA-256：`5eaafa7158bde21be91340b657ae6e97de24d53ce7fe86c8bdc03d81b64afcb8`。

结构化证据在 `verification/gzip-tail-latest.txt` 所指目录：`analysis.json`、`core-final-current/results.json`、`packaged-results.json`、`original-extraction-completed.png`。这里只确认归档解码完整性，不是固件来源、签名、硬件适配或刷机可用性的验证；未改写原固件，也未执行升级。

格式依据：[RFC 1952](https://www.rfc-editor.org/rfc/rfc1952)。独立校验器使用 Python 标准库 zlib，Hzip 使用随附独立编译的 SharpZipLib Deflate 解码器，避免用同一解码路径互相证明。
