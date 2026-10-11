# Warden Worker / Vaultwarden 对 Monica 的参考

研究及验证日期：2026-10-09。已完成公开源码对照，并在桌面端落地 Bitwarden 更新版本保护；云端通知与服务部署尚未接入。

源码基准：

- [warden-worker 7a9f6b9e（0.7.2）](https://github.com/qaz741wsd856/warden-worker/tree/7a9f6b9e3cb46449f50df33d7577481ccb9f1b71)
- [Vaultwarden df2cd3c8](https://github.com/dani-garcia/vaultwarden/tree/df2cd3c8693782eeedfd1f70318f6f14033213a8)

## 结论

优先学习 warden-worker 的轻量服务拆分和更新通知，结合 Vaultwarden 核对协议兼容、会话生命周期和异常处理。它们对“设备不在同一局域网也能自动同步”有参考价值；局域网直连仍由现有附近设备通道承担。

Monica 已有自托管 Bitwarden 的登录、令牌刷新、本地待上传队列和远端合并实现。新增通知通道可以复用这些模块。Monica 自有密码的附近同步使用另一套加密副本协议，不能仅更改 URL 就直接接到 Bitwarden 服务端，也不应悄悄改变本地条目的归属。见 [现有边界说明](../../docs/bitwarden-online-sync-boundary.md) 和 [同步协调器](../src/Monica.Data/Bitwarden/BitwardenSyncCoordinator.cs)。

## 已落地：桌面更新版本保护

原先桌面端只在上传前 GET 检查远端 revision，PUT 没有带上服务端支持的 lastKnownRevisionDate。若远端在这两次请求之间修改，服务器就缺少判断本次编辑基于哪个版本的信息；两项目对过期版本返回的 HTTP 400 也会被 Monica 当作普通参数错误。

现在更新请求从待上传队列取得原始 revision，替换请求中可能遗留的同名字段；保留加密内容、未知扩展字段和本地排队载荷。仅对已知的过期版本响应标记冲突，保留真实 HTTP 状态和本地编辑，不自动重试覆盖。普通 400、重试退避、创建、删除和恢复的原有语义继续沿用。

实现：[HTTP 传输](../src/Monica.Platform/Bitwarden/BitwardenMutationHttpTransport.cs)、[队列处理](../src/Monica.Data/Bitwarden/BitwardenMutationProcessor.cs)。

验证：修复前 4 个用例失败；修复后传输与队列回归 31 个通过，全部 Bitwarden 相关回归 178 个通过。覆盖预检后远端发生变化、两种服务器错误格式、旧版本字段替换、原内容保留、上传大小限制、普通错误不误判，以及本地冲突不被自动重试。测试使用合成内容和进程内 HTTP 响应，尚未对真实 warden-worker 部署做端到端验证。

这项保护在桌面端已有 Bitwarden 账户的更新同步中自动生效，无需开关。入口为「同步与备份 → 云端与本地保险库 → Bitwarden 在线保险库」。Windows 05 包包含版本保护及下述复查修复，手机仍使用 03 包；Android 同类更新保护、云端通知和 Monica 自有内容的云端通道仍需后续实现。

客户端携带 revision 只是启用服务端已有检查，不能消除其秒级容差与非原子写入限制，不能将本次改动描述为完整的跨设备原子并发控制。

## 复查发现与修复

对照真实编辑入口、完整上传/拉取流程，以及桌面实际使用的原生存储后，确认原有单元测试漏掉了几条关键路径。

- **冲突漏备份。** 普通编辑可能没有设置 BitwardenLocalModified；上传遭拒后，较新的远端版本会直接覆盖它。拉取现在也使用已同步内容指纹判断本地变化，保留回收站基线的特殊处理。
- **重试状态被重置。** 自动扫描会把相同冲突重新入队，并清空退避次数。自动扫描现在保留相同操作的状态；显式重试仍可重新入队。处理旧备份只结束对应版本的内容冲突，不会取消后来发起的永久删除。
- **上传回调覆盖新编辑。** 等待 HTTP 期间保存的新内容会被上传前的旧对象覆盖。成功回执现在只更新服务器关联和版本，校验当前条目归属，不重写内容，也不重新插入已清除的条目。
- **原生存储关联不完整。** 原先仅保留云端模式标记，没有完整保存账户、条目和版本关联；拉取更新还可能丢失原生 UUID，造成重复条目。现在保留这些关联，远端删除/恢复通过实际回收站操作处理，并保留旧编辑窗口中尚未获知的新关联。
- **旧格式与回收站路径不一致。** 旧封装保存后仍可能优先读取旧数据；真实引擎也拒绝直接更新已删除记录。现在在原封装内更新模型，并通过单次写入操作更新回收站记录而保持其删除状态。恢复需要的自定义字段和历史可按条目 ID 读取，永久清除的墓碑仍被排除。

这次新增 23 个回归用例，其中最初的 6 个流程用例和 2 个存储关联用例均先复现失败。原生用例使用实际 Windows 引擎及隔离的合成保险库，覆盖新旧格式、回收站、未知扩展、再次编辑、重复拉取和远端恢复；没有使用真实账号或用户密码。

最终 413 项相关回归全部通过，涵盖 Bitwarden、原生存储、载荷兼容、平台服务和附近设备同步。日志为工作区 `.tools/research/warden-reference/review-regressions-final.log`，包与源码校验见 `artifacts/device-sync/verification-05.json`。

旧版本已经丢失的账户关联不能靠本次补丁猜测重建，因此不会按标题自动认领或合并现有条目。真实 warden-worker 部署兼容、Android 对应更新和手机 Wi-Fi 联调仍不属于已完成验证。

## warden-worker 最值得借鉴的部分

### 1. 轻量、按职责拆分的托管服务

后端使用 Rust/WASM、worker-rs 与 axum，部署在 Cloudflare Workers；D1 持久化条目，可选 KV/R2 承载附件。通知与高 CPU 工作分别交给 NOTIFY_DO、HEAVY_DO。重任务 DO 复用原有路由，不复制另一套业务实现。

依据：[Cargo.toml](https://github.com/qaz741wsd856/warden-worker/blob/7a9f6b9e3cb46449f50df33d7577481ccb9f1b71/Cargo.toml)、[wrangler.toml](https://github.com/qaz741wsd856/warden-worker/blob/7a9f6b9e3cb46449f50df33d7577481ccb9f1b71/wrangler.toml#L9-L25)、[HeavyDo](https://github.com/qaz741wsd856/warden-worker/blob/7a9f6b9e3cb46449f50df33d7577481ccb9f1b71/src/durable/heavy_do.rs#L8-L61)。

适用于 Monica：可选的加密同步服务可以减少 VPS 运维，也能在直连不可达时提供联网路径。客户端仍保有本地内容、离线队列和解密职责。现有原生文件/线程/TCP 实现不能不经适配直接编译成 Workers 服务。

README 的免费定位不代表任何规模下都没有成本；采用前需要按实际请求、D1、DO、附件存储和当前套餐核算，不以“部署后永不维护”为设计前提。

### 2. 推送更新提示，收到提示后再同步

publish_cipher_update 发送条目 ID、用户/组织信息、revision 与上下文 ID，没有把完整密码条目塞入通知。NotifyDo 校验访问令牌、按用户标签管理连接，并用 Durable Object WebSocket 接口保留连接状态。

依据：[通知载荷](https://github.com/qaz741wsd856/warden-worker/blob/7a9f6b9e3cb46449f50df33d7577481ccb9f1b71/src/notifications.rs#L311-L345)、[WebSocket 认证与连接](https://github.com/qaz741wsd856/warden-worker/blob/7a9f6b9e3cb46449f50df33d7577481ccb9f1b71/src/durable/notify_do.rs#L110-L143)。

适用于 Monica：收到变更提示后，合并短时间内的重复事件，触发现有同步协调器；忽略或合并本机操作产生的回声；重连、解锁时主动补拉，防止漏掉通知。定时模式可以只标记“有更新”，到间隔后处理。

当前实现把通知发送给名为 global 的 DO，再按标签区分用户，不能描述成“已实现每用户独立 DO 分片”。若做多用户服务，需要另行验证隔离和容量。依据：[send_ws_to_do](https://github.com/qaz741wsd856/warden-worker/blob/7a9f6b9e3cb46449f50df33d7577481ccb9f1b71/src/notifications.rs#L172-L191)。

### 3. 认证、设备记录与会话失效的联动

HTTP 认证检查 JWT 签名和有效期，并核对 security_stamp、设备记录。全局退出删除设备记录并更新 security_stamp，从服务端阻止旧凭据继续访问。设备授权请求也检查设备类型、访问码、批准状态等。

依据：[访问令牌验证](https://github.com/qaz741wsd856/warden-worker/blob/7a9f6b9e3cb46449f50df33d7577481ccb9f1b71/src/auth.rs#L97-L137)、[退出会话](https://github.com/qaz741wsd856/warden-worker/blob/7a9f6b9e3cb46449f50df33d7577481ccb9f1b71/src/handlers/accounts.rs#L1060-L1111)、[设备授权请求](https://github.com/qaz741wsd856/warden-worker/blob/7a9f6b9e3cb46449f50df33d7577481ccb9f1b71/src/handlers/auth_requests.rs)。

适用于 Monica：云端设备撤销应同时影响后续 API、令牌与通知连接；不能只把设备从界面移除。现有 auth-request 创建流程要求已知设备记录，不可直接当作全新设备首次扫码配对。相关源码也指出退出时移动注销推送可能因设备记录已经删除而跳过，因此客户端必须以服务端鉴权结果为准。

### 4. 附件与主同步请求分开处理

附件有待完成记录、上传完成校验和短期授权 URL，可选 KV/R2。适合借鉴其“先准备、再上传、确认后发布”的流程，避免每次密码同步都重新传大文件。

依据：[附件处理](https://github.com/qaz741wsd856/warden-worker/blob/7a9f6b9e3cb46449f50df33d7577481ccb9f1b71/src/handlers/attachments.rs#L150-L313)、[存储后端选择](https://github.com/qaz741wsd856/warden-worker/blob/7a9f6b9e3cb46449f50df33d7577481ccb9f1b71/src/handlers/attachments.rs#L29-L60)。

## 不应直接照搬的部分

- **不是现成的增量协议。** 当前 /api/sync 查询该用户全部 ciphers、folders、sends，再构造完整响应；优化主要是减少 JSON 解析/重序列化和规避 SQL 大 JSON 限制。它不能证明“只下载变化的数据”。Monica 应保留本地队列与版本追踪，并评估按条目获取、合并通知与全量校准的组合。[源码](https://github.com/qaz741wsd856/warden-worker/blob/7a9f6b9e3cb46449f50df33d7577481ccb9f1b71/src/handlers/sync.rs#L75-L161)
- **时间戳检查不等于完整冲突方案。** lastKnownRevisionDate 检查可选，且有秒级容差；该方法先读取再更新，UPDATE 条件只有 id/user_id，没有原子 revision 条件。Monica 已有的守卫写入、版本向量和冲突副本不能被简单时间戳覆盖替代。[源码](https://github.com/qaz741wsd856/warden-worker/blob/7a9f6b9e3cb46449f50df33d7577481ccb9f1b71/src/handlers/ciphers.rs#L154-L233)
- **手机推送需要单独接入。** README 所述移动推送通过 Bitwarden 中继面向官方客户端，并需要安装 ID/密钥；不能据此保证自定义 Monica APK 也能收到。需要选择 Monica 自己的通知通道，并处理后台限制与解锁补同步。[README](https://github.com/qaz741wsd856/warden-worker/blob/7a9f6b9e3cb46449f50df33d7577481ccb9f1b71/README.md#live-sync-and-push-notifications)、[push.rs](https://github.com/qaz741wsd856/warden-worker/blob/7a9f6b9e3cb46449f50df33d7577481ccb9f1b71/src/push.rs)
- **成熟度有明确边界。** 项目声明功能未完整，不支持组织、共享、部分二次验证等；安全说明明确尚未正式审计。网页前端来自上游，显示功能不代表后端已经支持。[状态](https://github.com/qaz741wsd856/warden-worker/blob/7a9f6b9e3cb46449f50df33d7577481ccb9f1b71/README.md#current-status)、[安全说明](https://github.com/qaz741wsd856/warden-worker/blob/7a9f6b9e3cb46449f50df33d7577481ccb9f1b71/SECURITY.md)
- **许可分开看。** Worker 后端为 MIT，直接复用时保留许可和版权声明；其打包的上游前端另行核对。Vaultwarden 为 AGPL-3.0，借用实际实现时需评估对应义务。[Worker LICENSE](https://github.com/qaz741wsd856/warden-worker/blob/7a9f6b9e3cb46449f50df33d7577481ccb9f1b71/LICENSE)、[Vaultwarden LICENSE](https://github.com/dani-garcia/vaultwarden/blob/df2cd3c8693782eeedfd1f70318f6f14033213a8/LICENSE.txt)

## Vaultwarden 的参考定位

Vaultwarden 适合作为较完整的 Bitwarden 协议和行为参照：WebSocket 认证、心跳超时、连接清理、设备记录和令牌操作、条目 revision 检查与删除/恢复语义。其通知同样以 ID/revision/context 为主。这里是源码参考，不能理解为本轮做过全面安全审计。

依据：[WebSocket 生命周期](https://github.com/dani-garcia/vaultwarden/blob/df2cd3c8693782eeedfd1f70318f6f14033213a8/src/api/notifications.rs#L129-L220)、[条目更新通知](https://github.com/dani-garcia/vaultwarden/blob/df2cd3c8693782eeedfd1f70318f6f14033213a8/src/api/notifications.rs#L458-L506)、[设备和令牌](https://github.com/dani-garcia/vaultwarden/blob/df2cd3c8693782eeedfd1f70318f6f14033213a8/src/db/models/device.rs)、[revision 检查](https://github.com/dani-garcia/vaultwarden/blob/df2cd3c8693782eeedfd1f70318f6f14033213a8/src/api/core/ciphers.rs#L454-L465)。

## 建议的实施顺序

1. 在本轮本地 HTTP 回归基础上，用隔离的合成账号建立 Monica ↔ warden-worker 的服务端兼容测试，覆盖登录、令牌刷新、增删改、重复重试、回收站、锁定和撤销。源码与伪造响应不能替代真实服务端验证。
2. 为现有 Bitwarden 账号通道接入 WebSocket 变化提示，统一触发现有同步调度器；恢复网络后补同步。这是最短的复用路径。
3. 若为 Monica 自有密码增加云端通道，另外定义加密记录/设备授权协议，让直连与云端共用本机版本和冲突逻辑。服务配置一次，日常只保留自动同步、频率和设备状态；不让用户管理服务端数据库，也不自动迁移本地记录到外部账号。
4. 再扩展附件续传和恢复能力，持续用兼容性与故障测试核对 Vaultwarden 行为。

源码缓存位于工作区 .tools/research/warden-reference，均为公开仓库文件。本轮未访问示例服务、未注册账号、未读取用户密码。
