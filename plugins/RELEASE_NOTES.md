Jellyfin 10.11.x 原生 Todoist 插件首次发布。

- 可通过 Jellyfin 插件仓库安装，无需 webhook 或 Python 服务。
- 原生后台：OAuth 应用配置、项目关联、同步状态和用户关联列表。
- 每个 Jellyfin 用户独立授权 Todoist，按剧集建任务，观看后自动完成。
- 持久化去重、失败重试、令牌刷新，以及空分区归档和恢复。

插件仓库地址：

```text
https://github.com/RicterZ/jellyfin-plugin-todoist/releases/latest/download/manifest.json
```

Jellyfin 控制台 → 插件 → 仓库 → 添加上述地址；在目录中安装 Todoist 并重启。

迁移前停用旧 webhook 服务。旧 Python SQLite 映射不自动迁移，建议选择新的 Todoist 项目。
