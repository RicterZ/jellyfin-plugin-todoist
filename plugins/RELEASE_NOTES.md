修复 Jellyfin 12 中插件后台授权返回 HTTP 401。

- 后台请求改为标准 Authorization: MediaBrowser 认证头，不再使用 Jellyfin 12 已移除的 X-Emby-Token。
- 同时修复后台状态、项目列表、保存关联和解除关联的认证方式。
- 会话失效时提示重新登录。
- 保留仅新入库建任务及“剧名 S01E01”标题行为。

请在 Jellyfin 更新 Todoist 到 1.0.3.0 后重启，并强制刷新插件后台，再点击“授权 Todoist”。不要直接在地址栏打开 /Todoist/Authorize，它需要携带 Jellyfin 会话的 POST 请求。
