# 金大立六轴锣机 S1 接收器

独立 Go 程序，接收厂家 HTTP POST JSON；文档中的八个上传接口可持久入队后按 S1 MQTT QoS 1 转发，两版软件的单 URL 接口和原始 TCP 先保存原文。无需额外 Go 依赖。

设备生产软件截图展示单个 `http://.../jdlSave/MchnData` 地址，而 PDF 描述另外九个 WebAPI 路由。由于两者报文契约不同，程序可先以**采样模式**接收该单地址的原始请求，确认格式后再启用下述 S1 转发模式。

每个文档接口的页码、地址及当前返回字段见 [金大立 EAP 接口对照](../../docs/jdl-eap-interface-mapping.md)。

## 首条报文采样

将 `capture.example.json` 复制为 `config.json`，用 `openssl rand -hex 24` 生成至少 32 位的 `capture_token`，并设置私有的 `queue_dir`。原有 5173 HTTPS 入口只接收 `/jdlSave/MchnData?key=<capture_token>`，原文位于 `queue/captures/*.json`。8604 HTTP 入口接收下表九个文档接口；每个请求的路径、方法、查询参数、请求头、来源、时间、原始正文及预定应答状态先持久落盘。文档中的八个上传接口在保存成功后返回其成功应答，非法请求或磁盘不可写时返回错误。扫码取任务接口会记录请求，但在缺少真实生产任务来源时返回 HTTP 503，不能用虚构任务驱动设备生产。原文的 `body` 字段是 Base64 编码的原始字节。默认只记录；确认设备编号、来源和 S1 凭据后，可启用 `forward_mes` 转发八个上传接口。

另一版软件可设置 `"mes_listen":"0.0.0.0:8604"`，在同一端口采样 HTTP GET/POST `/httptest.php`、`/jdlSave/MchnData`、下表九个文档接口，以及其他原始 MES TCP 字节流；未知 HTTP 路径和错误方法也会存档，再返回 404/405。省略 `mes_capture_until` 时持续监听，也可填写未来 30 分钟内的 RFC3339 时间作限时观察。8604 原文单独保存在 `queue/mes-captures`，**不自动删除**；默认最多保存 10000 个文件，可用 `mes_max_files` 调整。达到上限时普通采样 HTTP 返回 503、原始 TCP 关闭；已验证的设备上报仍可写入包含完整原文的持久 MQTT 队列。每条正文最多 64 KiB；单 IP 同时最多 4 条连接、每分钟最多 30 条成功采样；全局同时最多 16 条连接、每分钟最多 300 条成功采样。公网入口没有强设备认证；启用 `forward_mes` 后，仅匹配 `source_ids` 和 `allowed_cidrs` 的八个文档上传接口转发 S1，扫码、`/httptest.php`、`/jdlSave/MchnData` 和原始 TCP 继续只记录。原始 TCP 缺少厂家分帧与应答规范，采样不代表生产软件认为上传成功。

## 接口

基址：`http://<服务器>:<端口>/dataService/webapi`。支持文档中的八个上传路径：

| 路径 | S1 事件标识 |
| --- | --- |
| `/IO/UploadProcessQualityParameters` | `jdl_quality` |
| `/IO/UploadDeviceStatus` | `jdl_status` |
| `/IO/UploadParameterAdjustments` | `jdl_adjustment` |
| `/IO/UploadProductionCount` | `jdl_production_count` |
| `/Equip/UploadJdl` | `jdl_utilization` |
| `/UploadAlarmBegin` | `jdl_alarm_begin` |
| `/UploadAlarmEnd` | `jdl_alarm_end` |
| `/uploadbrokenknife` | `jdl_broken_knife` |

`/Equip/MillingMachineAccessToTask` 需要同步提供真实任务资料；目前没有任务来源配置，因此明确返回 HTTP 503，**不能将本程序用于扫码取任务生产流程**。厂家文档没有定义无任务应答。

每个原始 JSON 字段作为一条 S1 事件项，原字段名、JSON 类型、批次、分段序号写在 `remark` JSON 中。长值按 64 字符分段，同批各项放在一条 MQTT 消息。S1 事件时间是 S1 服务端接收时间；设备原始时间戳会作为普通字段保留。`queue/*.json` 保存原始报文和待发 MQTT 数据，收到 PUBACK 后移入 `queue/sent/` 留作核对及人工重放；PUBACK 并不代表 S1 数据库已落库。归档会持续增长，运维需核对 S1 入库后定期清理并监控磁盘空间。

## 构建与配置

```sh
cd tools/jdl-s1-bridge
go test ./...
go build -o ../../outputs/jdl-s1-bridge .
cp config.example.json config.json
chmod 600 config.json
```

编辑 `config.json`：

- `listen`：主监听器只接受数字形式的本机回环地址，例如 `127.0.0.1:7666`；通过 HTTPS 反向代理或私有隧道供设备访问。文档中的 `7666` 仅是厂家示例，不保证云端空闲。单独的 `mes_listen` 已按用户要求在公网 8604 开放，作为有界原文采样和已验证设备的文档接口入口。
- `allowed_cidrs`：本程序看到的可信来源 IP，例如本机反向代理为 `127.0.0.1/8`；启用 `forward_mes` 时加入已验证设备出口的 `/32`，只允许这些来源转发到 S1。公网 8604 仍记录其他来源。空值和全网段会被拒绝；本程序不信任 `X-Forwarded-For`。
- `source_ids`：**同一台实体设备**在常规上报中使用的真实 `AssetsCode`，以及断刀报文中的 `EqpId`（如果不同）。未列出的编号返回 403。多台实体设备须分别创建 S1 设备及接收器实例。
- `queue_dir`：只有服务账号可读写的持久目录；请监控磁盘空间，勿放在临时目录。
- `mes_max_files`：8604 公共采样目录的文件上限，默认 10000、最大 100000；达到上限时拒收普通采样报文，但已验证设备的文档上传可在持久 MQTT 队列保存完整原文后继续处理。旧记录不自动删除。每条正文上限 64 KiB，加上 Base64 与请求头开销，默认公共采样上限约占 1 GiB。
- `forward_mes`：仅与 `capture_only=true`、`mes_listen` 同用；启用后需填写 `source_ids` 和 S1 MQTT 字段。只有设备编号及真实来源 IP 均匹配的八个文档上传接口入队；其他请求照常记录。队列及 `sent/` 归档无自动清理，请监控磁盘。
- `broker` 及其余 MQTT 字段：从已发布的 S1 产品、设备连接信息中取得。MQTT 凭据通过明文 MQTT 3.1.1 传输，`broker` 只允许本机回环地址；若 S1 在别的主机，先建立受保护的 SSH/VPN 隧道。产品 ID、用户 ID 必须是数字，S1 设备编号仅用字母数字。

纯采样模式只需 `listen`、`allowed_cidrs`、`queue_dir`、`capture_only` 和 `capture_token`；启用 `forward_mes` 时还需真实设备编号、来源网段和 S1 连接参数。

启动：`./jdl-s1-bridge -config config.json`。程序会在启动和新报文入队后重试待发文件，S1 不可用时保持队列。部署前确认指定端口未被别的进程占用，并在 HTTPS 反向代理上限制设备出口 IP。Linux 用 systemd 或 1Panel 进程守护运行；Windows 可用服务管理器。

## S1 配置

在 S1 后台创建金大立六轴锣机产品，传输方式选 MQTT，发布产品后创建设备并取 MQTT 连接信息。事件接收不依赖物模型，但建议创建上表八个事件标识以便界面识别。实际产品/设备必须使用现场编号，不能用 PDF 示例 `E1030ROUT017`。

## 当前云端部署（2026-11-03）

- Ubuntu x86_64 上的 `jdl-s1-bridge.service` 以独立 `jdlbridge` 账号运行，程序/私有配置位于 `/opt/jdl-s1-bridge`，原文位于 `/var/lib/jdl-s1-bridge/captures`。
- OpenResty 的 `/data/1panel/www/conf.d/qms-browser-https-internal.conf` 将 `https://gongying.duckdns.org:5173/jdlSave/MchnData` 精确代理到本机 `127.0.0.1:7666`；修改前副本为同目录的 `.jdl-backup-20261103` 文件。
- S1 产品“金大立六轴锣机”（ID `34`）已发布，首台设备 `E1807091137`（设备 ID `5`）已创建，云端 MQTT 连接验证成功。该设备网络在 13:40 的人工测试请求来自 `113.195.85.167`；`forward_mes` 已允许该出口 `/32` 与设备编号同时匹配时转发八个文档上传接口。这个公网 IP 与先前另一版软件的测试 IP 相同，须继续用实际报文中的设备编号区分。扫码请求只记录并返回 503；两条软件 URL 路径和原始 TCP 仍只记录，待真实报文确定格式后再映射。凭据只在云端私有配置中，不提交到仓库。
- 按用户要求，8604 持续对所有 IP 开放：UFW 允许 IPv4/IPv6，程序无截止时间，且无待执行的关闭 timer。公网九个文档接口、两条软件 URL 路径、未知 HTTP 路径与原始 TCP 人工测试均通过；非可信来源或设备编号的文档上报会记录后返回 403，不进入 S1 队列。公共采样位于 `/var/lib/jdl-s1-bridge/mes-captures`，上限 10000 条且不自动删除；可信设备上报另在待发队列及 `sent/` 中保存完整原文。截至 2026-11-03 13:56（北京时间），尚无真实设备报文。
