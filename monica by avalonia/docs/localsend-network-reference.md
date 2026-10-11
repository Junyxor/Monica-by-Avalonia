# 附近设备连接修复：LocalSend 参考

检查日期：2026-10-09。源码基准：[LocalSend c1ce322f](https://github.com/localsend/localsend/tree/c1ce322fb3acf08e44f329b8b7208b8b0d91e244)，协议参考 [v2.2 文档](https://github.com/localsend/protocol/blob/62bd3406/README.md)。

## 第一方实现依据

- 发现分阶段进行：立即发送多播并探测收藏设备，1 秒没有确认后才扩大到网段扫描。见 [scan_facade.dart:10–38](https://github.com/localsend/localsend/blob/c1ce322fb3acf08e44f329b8b7208b8b0d91e244/app/lib/provider/network/scan_facade.dart#L10-L38)。
- 以设备指纹聚合多个连接通道，记录每个通道是否可达，并根据成功确认记录选择通道。见 [discovery/store.rs:39–111](https://github.com/localsend/localsend/blob/c1ce322fb3acf08e44f329b8b7208b8b0d91e244/packages/core/src/discovery/store.rs#L39-L111)。
- 多播按网卡发送，单张网卡绑定或加入失败时继续使用其余网卡；接收绑定 wildcard，再加入各接口的多播组，发送设置明确的网卡。见 [multicast/socket.rs:20–55](https://github.com/localsend/localsend/blob/c1ce322fb3acf08e44f329b8b7208b8b0d91e244/packages/core/src/multicast/socket.rs#L20-L55) 和 [94–118](https://github.com/localsend/localsend/blob/c1ce322fb3acf08e44f329b8b7208b8b0d91e244/packages/core/src/multicast/socket.rs#L94-L118)。
- 发现确认使用 UDP 包的真实源地址与声明端口，实际连接再检查设备身份；协议支持多种回应路径，当前实现的主动确认使用 HTTP register。见 [discovery/mod.rs:503–563](https://github.com/localsend/localsend/blob/c1ce322fb3acf08e44f329b8b7208b8b0d91e244/packages/core/src/discovery/mod.rs#L503-L563)。
- HTTP 客户端明确禁用系统代理与重定向。见 [http/client/mod.rs:235–241](https://github.com/localsend/localsend/blob/c1ce322fb3acf08e44f329b8b7208b8b0d91e244/packages/core/src/http/client/mod.rs#L235-L241)。Monica 的原生 TCP 连接本身不经过 HTTP 代理。

本次检查的 Android 主 Manifest 和 MainActivity 不能证明 LocalSend 是否在其他依赖中持有 MulticastLock，因此不把这一点当作已确认结论。

## Monica 中已复现的问题

发布包默认邀请只包含排序后的第一个 IPv4 地址。在检查机器上，该地址属于 Dr.COM，另一个有效以太网地址没有进入默认二维码。两个地址都存在时，使用者仍需手动选择地址，失败后不会切换。

诊断命令 .tools/diagnostics/lan-native-probe.py 使用真正发布的原生库和原样生成的邀请，不替换成固定回环链接。增加“默认邀请必须保留可用网卡地址”的断言可以使旧包失败。这个结果确认了多网卡缺陷；不代表已确认用户两台真机上的所有故障原因。

## 采用的修复

1. 协议 2 的默认二维码同时包含最多 8 个有效本地 IPv4 地址。客户端在同一个配对会话里逐一尝试；进入内容交换后不切换目标。旧的单地址邀请仍可读取。
2. 使用实际认证连接的源/目标地址记住设备。以后同时探测已知地址和组播，使用探测返回的新端口，避免依赖上次临时 TCP 端口。
3. 同一进程共享一个发现响应服务，回复所有活动配对监听的信息。这样单播不会被某一配对监听独占；最后一个监听关闭后释放发现端口。逐网卡发送并定期刷新网卡组成员。
4. 失败区分准备本机内容、连接、配对认证、传输和保存；可复制的诊断只包含阶段、错误代码、异常类型与尝试次数，不含密码或配对密钥。

Monica 继续通过 Noise 和每对设备独立的长期密钥认证后交换加密密码变更。发现包仅用于寻找地址，不能授予读取密码的权限。

## 回归方法

- Rust 受控监听仅绑定 127.0.0.1，二维码先给不可连接的 127.0.0.2，再给可用地址；断言真正发生两次连接尝试并完成双向传输。
- Rust 显式关闭组播，仅向已知回环地址发送单播；断言多个活动监听均被发现，回应源地址确实是 127.0.0.1。
- 桌面完整协调器测试使用真实 UDP/TCP，关闭组播后重启一端，并验证离线修改自动补同步。
- 桌面 PNG 二维码编码/解码及 Android 表单测试验证多个地址和配对码一起保留。
- 故障注入验证合并失败被标为 applying，诊断文本不包含邀请密钥。

真实手机与手机、手机与 Windows 的 Wi-Fi 联调仍需设备环境。网络隔离使所有地址都不可达时，应用应给出连接失败诊断，不能将其当成配对码填写错误。
