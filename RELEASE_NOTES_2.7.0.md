# MJJsteamtools v2.7.0

## OpenSteamTool 中国区更新优化

- OST 自动安装与更新增加中国区 GitHub 加速源回退。
- 更新接口遇到超时、HTML 错误页或无效 JSON 时自动切换备用源。
- 下载包先校验并暂存，再替换 Steam 根目录 DLL，减少半包安装问题。
- 保留清单多源回退与 `config/lua` 自动配置，改善虚拟入库游戏的下载连接。

## 全新 Windows 安装体验

- 新增 `MJJsteamtools-Setup-2.7.0-x64.exe` 传统安装向导。
- 支持自定义安装目录，默认安装到 `%LocalAppData%\Programs\MJJsteamtools`。
- 安装完成后自动创建开始菜单与桌面快捷方式。
- 支持勾选安装完成后立即启动 MJJsteamtools。
- MSI 安装包继续保留，适合已有部署流程；EXE 安装器适合普通用户直接双击安装。
- 安装器采用自包含发布，不需要额外安装 .NET 运行时。

## 构建版本

- MJJsteamtools 版本号更新为 `2.7.0`。
- `build-installer.ps1` 现在会同时生成 MSI 与 EXE 两种安装包。
