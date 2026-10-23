# 发布 Hzip

仓库：https://github.com/RUI-JACK/Hzip

固定更新页面：https://github.com/RUI-JACK/Hzip/releases/latest

## 版本与文件

1. 更新 `Directory.Build.props` 中的产品版本，保留用户确认的应用界面。
2. 发布自带运行环境的 Windows x64 程序，包含 `update.json`、7-Zip 引擎、许可证和对应源码。
3. 使用 `tools/build_installer.ps1` 生成安装包，实际验证安装、启动、压缩解压、冲突处理、重新安装和卸载。
4. 创建对应源码提交的版本标签，例如 `v0.5.0`。
5. 在 GitHub Releases 上传安装包、便携 ZIP 和 `SHA256SUMS.txt`，核对下载文件后发布。

每个正式版本必须提供以下稳定资产名称，供固定更新地址使用：

- `Hzip-Setup-x64.exe`：与该版带版本号安装包内容完全相同。
- `Hzip-Windows-x64.zip`：完整便携程序文件夹。
- `SHA256SUMS.txt`：覆盖上述文件和带版本号安装包的 SHA-256。

程序项目、安装包元数据和 `update.json` 均指向同一仓库。`latestReleaseApi` 为 `https://api.github.com/repos/RUI-JACK/Hzip/releases/latest`；其成功响应需要先存在已公开发布且不是预发布的 Release。

## 仓库范围

提交源码、测试固定样本、必要第三方组件、许可证与构建脚本。开发 SDK、工具缓存、私人签名密钥、机器设置、真实用户归档和本机验证临时目录不提交。第三方组件的对应源码与许可证随发行文件附带；Hzip 原创代码采用 MIT。

当前没有后台自动替换或自动安装功能，更新地址用于访问发布说明及手动下载。安装包未使用公众信任的商业代码签名；Windows 11 原生菜单使用单独的开发签名组件，启用时会请求管理员确认。
