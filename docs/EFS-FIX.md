# 修复真实右键解压的系统加密错误

2026-10-20：用户在下载目录通过右键解压时出现“无法加密指定的文件”，路径指向 `.hzip-incoming-*`。此问题已经复现、修复，并更新本机发布版。

## 原因与修复

本机 AppData\Local 启用了 Windows EFS，任务暂存文件会继承此属性。右键菜单从 MSIX 注册的 COM 服务启动 Hzip，进程具有 Hzip.ContextMenu 包身份，使用包对应的 LocalCache\Local\Hzip 数据目录。原来的 `File.Copy(staged, incoming)` 会传播暂存文件的 EFS 加密属性，在这个进程身份下复制到普通目标目录时报错。普通控制台进程没有复现相同报错，但也错误地让输出继承了临时文件的加密属性。

`ArchiveService.AtomicCommit` 现在通过独占新建的 FileStream 复制内容并刷新，然后执行原有的原子重命名/替换；不再把暂存区的 EFS 属性强加到用户输出。没有修改系统加密策略、已有目录属性或用户的加密文件。原有路径检查、同名保护、备份与失败回滚保留。

## 本次证据

- 修复前，通过实际注册的 COM 对象 Invoke，使用用户提供的同一 ZIP，在独立测试目录复现完全相同的加密错误。
- 修复后，同一注册服务及真实加密用户数据目录下的此处/智能/同名目录解压、快捷 ZIP 共 4 项通过；输出逐字核对，普通目标目录中的结果没有 Encrypted 属性。
- 在用户原下载目录实际执行智能解压成功。已有 `index.html` 保留，新结果为 `index (2).html`，SHA-256 与压缩包中的原条目一致，原压缩包不变。
- 103 项核心检查通过，含新增的真实 EFS 文件跨卷提交及覆盖回归；另行运行超过 4 GB 的 ZIP64 回环 1 项通过。
- 当前发布版的 11 项命令行、16 项后台/设置窗口检查、6 项图形流程回归通过。

此前测试把 HZIP_DATA_DIR 放在未加密的验证目录；原生命令测试也主要在验证器进程中加载菜单 DLL，虽然核对了已注册对象的菜单状态，但没有在其 COM 服务里执行操作。因此没有覆盖包身份加 EFS 的真实组合。本次 tests/ShellProbe.cpp 增加 `--registered-invoke-*`，直接在实际注册的服务内执行动作；tools/test_packaged_efs.py 明确不设置 HZIP_DATA_DIR，验证真实包数据目录。

证据由 verification/efs-fix-latest.txt 和 packaged-efs-latest.txt 定位。`before` 相关目录保留修复前证据，`core-final`、`large`、`original-location-results.json` 和注册服务专项结果属于修复后证据。用户文件内容不打包进源码交付，只保留结果、路径和哈希记录。
