# 科视光学 MES Mock

`KstMesMock.exe` 是独立的 Windows 图形化 HTTPS 测试服务，参考森泉 MES Mock 的操作方式。它接收科视光学设备主动发送的上报，按 HTTP 路径匹配以下接口：

| 路径 | 上报内容 |
| --- | --- |
| `/Status` | 设备状态 |
| `/OEE` | 设备效率 |
| `/Alarm` | 报警信息 |
| `/ExposureResult` | 曝光结果 |
| `/Heart` | 心跳 |
| `/Mode` | 设备模式 |
| `/Log` | 科视代理或设备日志；`function=result` 的 JSON 同时映射为 S1 曝光结果 |

程序启动后从本机活动网卡选择监听地址，优先使用本机存在的 `192.168.1.71`，端口默认 `4524`；不会自动启动服务。确认地址后点击“启动服务”。输入不属于本机的 IP 时，界面会列出本机可用地址；也可填 `0.0.0.0` 监听所有网卡，服务 URL 会显示可供设备访问的本机 IP。服务在同一端口自动识别 TLS 1.2 和明文 HTTP。程序只接收 POST；六个业务接口要求 JSON 对象，`/Log` 接受 UTF-8 JSON 或纯文本，其中 JSON 的 `function=result` 会进入 S1 MQTT 曝光结果队列，其他 `/Log` 仅写本地日志。未知路径返回 HTTP 404，其他方法返回 405，业务接口的非法 JSON 返回 400。窗口可查看请求和响应、编辑已配置接口的应答 JSON 并立即生效；每次请求同时追加到 `logs\yyyy-MM-dd.jsonl`。日志分别保存传输层原始字节和 TLS 解密后的 HTTP 报文，解析失败时还会保存异常原因。清空窗口日志不会删除磁盘日志。旧版 `kst_server_config.json` 即使没有 `Log` 配置，`/Log` 也会返回默认成功 JSON；如需自定义应答，在该配置中增加 `Log` 模板。

窗口中的“S1 MQTT配置”可启用正式转发。需要转发的合法科视报文先写入 `data\pending-s1-mqtt.json`，后台再按 MQTT 3.1.1、QoS 1 发布到 S1。Broker 或 S1 暂时不可用时队列会保留，收到 `PUBACK` 后才删除。发送成功与重试原因同时写入 `logs\mqtt-yyyy-MM-dd.log`。

每台设备首次收到合法业务报文或心跳后即建立自己的 MQTT 连接，即使该条心跳仍在 60 秒的待发缓冲中，或重复状态不需要再次上报。每台设备独立维护连接；空闲期间约每 22.5 秒发送一次 MQTT PINGREQ（默认保活 30 秒），收到 PINGRESP 后保持原连接，断线后按重试间隔重连。一台设备发送积压或连接超时，不会阻塞另一台的 MQTT 保活。MQTT PING 不会产生 S1 属性数据，OEE/心跳的 60 秒上报间隔保持不变。设备从未向 Mock 发送有效报文时，不会建立它的 MQTT 连接。窗口显示“S1 MQTT 运行中”仅表示转发线程已启动，不能代替 S1 的连接状态。S1 显示“未激活”时，先在当天的 HTTP 日志中查该设备编号，再在 MQTT 日志中查对应设备的已连接、发送成功或待重试记录；显示“离线”时，还需查看 Broker 中该设备客户端 ID 的断开原因，并确认现场只运行一个使用该客户端 ID 的 Mock 实例。

Mock 服务仍需手动启动；停止服务或退出程序会断开两台设备的 MQTT 连接，S1 随后显示离线。S1 在线表示 Mock 与 Broker 的 MQTT 连接存活；曝光机本身的运行状态应结合 `/Heart`、`/Status` 报文判断。若 2 号 `/Heart` 因设备编号冲突仍返回 500，先按下文核对并启用来源路由，否则不会为 KST002 建立连接。

“OEE/心跳上报间隔秒”控制向 S1 上报的频率，默认 `60` 秒，可设置为 `1` 至 `3600` 秒；配置文件字段为 `telemetry_interval_seconds`。每台设备的 OEE 按 `StartTime`/`EndTime` 班次分别合并，心跳单独合并；每个间隔内只保留最新一条，首条约一个间隔后发送，默认约 60 秒，不会随每个 HTTP 请求立即上报。S1 断线时继续合并，只保留各设备对应 OEE 班次和心跳的最新快照，恢复连接后再发送。`Status` 和 `Mode` 的值发生变化时才上报；`Alarm` 和 `ExposureResult` 仍逐条入队上报。

旧版配置没有 `telemetry_interval_seconds` 时也按 60 秒运行，保存配置时会写入该字段。升级后首次启动会自动压缩旧的待发送队列，并在队列文件旁生成 `.before-rate-limit-*.json` 备份。`retry_interval_seconds` 只控制发送失败后的重试，`keep_alive_seconds` 只控制 MQTT 连接保活，调整它们不会降低正常上报频率。

在“S1 MQTT配置”中修改“本地队列文件”路径时，先停止服务。保存配置会把原队列及同名 `.rate-state.json` 状态文件复制到新路径，旧文件原样保留作为备份；新路径已有队列或状态文件时会拒绝覆盖。若配置未能保存，本次复制的新文件会被清理，原队列仍可继续使用。只改配置路径而不迁移这两个文件，会遗留待发消息和状态记录。

## 构建和使用

1. 在安装 .NET Framework 4.x 编译器及 PowerShell 7 的 Windows 电脑上运行 `build.cmd`。发布目录为仓库根目录的 `outputs\kst-mes-mock`。首次构建会生成自签名联调证书 `kst_server.pfx` 和可分发公钥证书 `kst_server.cer`，证书包含 IP `192.168.1.71`，有效期三年。
2. 双击发布目录中的 `start-mock.cmd` 或 `KstMesMock.exe`，确认监听 IP 是这台电脑的地址、端口为 `4524`，然后点击“启动服务”。
3. 把科视代理的 MES 服务器地址改为界面显示的 IP 和端口，例如迁移到 `192.168.1.98` 后使用 `192.168.1.98:4524`，对应路径为 `https://192.168.1.98:4524/Status` 等业务接口以及 `/Log`。无需修改“报文编码”的 UTF-8 设置。
4. 在路径列表中选择接口，修改应答 JSON 后点击“保存修改”。当前配置保存在 `kst_server_config.json`；“恢复默认”读取 `kst_server_defaults.json`，仍需点击保存。
5. 需要转发 S1 时，先保持服务停止，点击“S1 MQTT配置”，填写 S1 设备连接参数并启用。保存后再启动服务。

## S1 MQTT 物模型

发布包中的 `s1-kst-mqtt-template.json` 列出了需要在 S1 产品中创建的标识符：

- 属性：`kst_status`、`kst_oee`、`kst_exposure_result`、`kst_heartbeat`、`kst_mode`
- 事件：`kst_alarm`

属性主题为 `/{productId}/{deviceNumber}/property/post`，事件主题为 `/{productId}/{deviceNumber}/event/post`。正文是 UTF-8 JSON 数组，每项含 `id`、`value`、`ts`、`remark`。程序使用 S1 简单认证客户端 ID `S&设备编号&产品ID&用户ID`。MQTT 主题使用 S1 设备序列号，`remark` 保留现场编号。`kst_mqtt_config.json` 默认关闭且不含凭据；填写 S1 页面给出的产品 ID、用户 ID、MQTT 用户名和完整密码后再将 `enabled` 设为 `true`。

`kst_oee` 上报到 S1 前四舍五入到小数点后两位，按科视原值的 0–100 百分比口径发送，不再乘以 100；例如原值 `45.441587085902029` 上报为 `45.44`。每日 `logs\yyyy-MM-dd.jsonl` 中的原始请求报文仍保留完整精度。

科视现场的曝光结果可能发往 `/Log`，JSON 中的 `function` 为 `result`。Mock 保留原始 `/Log` 记录，并把 `CRESULT`（如 `OK`、`NG`）作为 `kst_exposure_result` 属性逐条上报；`CITEM_NO`、`CSTART_TIME`、`CEND_TIME` 等信息保留在 MQTT `remark`。设备归属优先使用已验证的来源路由；未配置来源路由时优先使用 `CMACHINE_CODE`，不会用 2 号报文中可能陈旧的 `assets=KST-001` 改投 1 号。若 `MachineNO` 与 `CMACHINE_CODE` 明确冲突，Mock 返回 HTTP 400；若启用 S1 转发但结果缺少 `CRESULT`，返回 HTTP 500。此映射只处理升级后收到的新报文，不自动重传历史 `/Log` 日志。

同一台 Mock 接收两台科视设备时，在发布目录的 `kst_mqtt_config.json` 中保留第一台顶层配置，并增加第二台映射。例如，现有 S1 产品 ID 为 `17`，两台设备序列号分别为 `KST001` 和 `KST002`：

```json
{
  "device_number": "KST001",
  "source_device_code": "KST-001",
  "additional_devices": [
    {"source_device_code": "KST-002", "device_number": "KST002"}
  ]
}
```

第二台默认继承顶层 `product_id`、`user_id`、`username`、`password`；若其 MQTT 用户名或密码不同，可在该映射项中分别添加 `username`、`password`。每台设备使用自己的客户端 ID 和 MQTT 连接，队列项记录对应的 S1 设备编号，重启后补传仍会发往原设备。一台设备连接失败时，它的逐条数据保留待重试，OEE/心跳只保留最新快照；另一台设备的消息仍可发送。已有单设备配置无需增加这些字段。未启用来源路由时，现场编号从 `MachineNO`、`assets`、`CMACHINE_CODE` 的非空值读取；在多设备模式下，缺少编号、编号未配置，或同一报文中 `MachineNO` 与 `assets` 编号冲突时，报文不会入队，服务会返回错误。编辑 JSON 时先停止服务，保存后重新启动；窗口中的配置对话框会保留其他设备映射。

## 两台设备共用代理时的来源路由

2026-10-20 的现场报文显示：代理的 `9000` 入口对应 `KST-001`，`9001` 对应 `KST-002`；两边的业务报文 `assets` 却都写成 `KST-001`。因此只靠业务报文的 `assets` 会把 2 号的 `/OEE`、`/Status` 等归给 1 号，而 2 号 `/Heart` 中的 `MachineNO=KST-002` 与 `assets=KST-001` 冲突。优先在科视代理或设备端将 2 号业务报文的 `assets` 修正为 `KST-002`。若现场暂时无法修改，可启用下面的代理来源路由。

发布包带有 `kst_source_routes.example.json`，**示例文件不会自动启用**。核对现场网络后，在服务停止时复制为同目录的 `kst_source_routes.json`，再启动服务。示例配置如下：

```json
{
  "trusted_proxy_ip": "192.168.1.151",
  "source_host_routes": {
    "192.168.10.1:9000": "KST-001",
    "192.168.10.1:9001": "KST-002"
  },
  "allowed_stale_assets": {
    "192.168.10.1:9001": "KST-001"
  }
}
```

`trusted_proxy_ip` 是 Mock 实际收到连接的代理 IP；`source_host_routes` 的键是代理转发时保留的 HTTP `Host` 值，两者不是同一个地址。程序只接受来自指定代理 IP 且 `Host` 精确匹配的请求。`allowed_stale_assets` 仅允许 `9001` 上已确认的旧 `assets=KST-001`，将这类报文归属 `KST-002`；其他相互矛盾的设备编号仍会被拒绝。启用路由后，从其他来源直接发送到 Mock 的测试请求也会被拒绝。`Host` 本身可由发送方填写，所以要确保只有可信代理能连接 Mock 的监听端口。若代理的地址或 `Host` 改变，先核对新报文，再更新配置并重启服务；移除 `kst_source_routes.json` 并重启则关闭此路由。

启用前先停止服务，按 `kst_mqtt_config.json` 的 `queue_file` 路径备份待发队列、同名 `.rate-state.json` 和 `.before-rate-limit-*` 备份，并保留当天的 HTTP/MQTT 日志。来源路由只影响之后收到的请求；旧队列里已按 `KST001` 归属的 2 号消息不会自动改成 `KST002`，已经发往 S1 的历史数据也不会自动修正。启用路由时，若待发队列存在缺少可信代理 IP/Host 来源记录或与当前映射不一致的条目，程序会拒绝启动转发，且不会修改队列。请在副本中结合 `logs\yyyy-MM-dd.jsonl`、`logs\mqtt-yyyy-MM-dd.log` 与 S1 数据核对历史影响；将待发队列和 `.rate-state.json` 安全隔离到审计目录后，才用空队列启动新路由。不要直接删除或把旧项凭 `assets` 改投 2 号。

不需要图形界面时，可以从命令行运行 `KstMesMock.exe 127.0.0.1 4523`，传入监听 IPv4 和端口；此模式在启动时读取配置，修改配置后需重启。

上述 `192.168.10.1:9000` 和 `:9001` 来自 2026-10-20 的现场日志；若现场网络配置变化，须重新核对。文档没有规定 MES 的应答正文，因此本工具默认使用已有科视接入程序采用的 `{"success":true,"message":"accepted","method":"..."}` 形式；现场需要其他应答时，直接在界面中编辑。该进程可把科视数据直接转发到 S1；QMS 转发仍由主设备监听器负责。

如果不同设备需要不同应答，可编辑 `kst_server_device_config.json`，用现场报文的 `assets` 作为键，同时兼容 `MachineNO`（曝光结果兼容 `CMACHINE_CODE`），例如：

```json
{
  "MACHINE001": {
    "Status": {"success": true, "message": "accepted for MACHINE001"}
  }
}
```

运行 `test-mock.cmd` 可验证业务路径和 `/Log`、TLS 1.2、错误状态、UTF-8 原始报文、S1 MQTT 模板、认证、主题和 QoS 1 发布；运行 `test-layout.cmd` 可验证本机地址选择、手动启动状态、窗口控件和 MQTT 配置弹窗。程序使用 TCP 监听，无需配置 HTTP.sys URL ACL；跨电脑联调时按现场网络规则开放 TCP 4524 和到 S1 Broker 的 1883 出站连接。

若迁移后出现“在其上下文中，该请求的地址无效”，说明输入的监听 IP 不属于当前电脑。按界面列出的本机地址修改，同时更新科视代理的 MES 服务器地址。发布包自带的测试证书仅包含 `192.168.1.71`；如果科视代理校验 TLS 证书的 IP，迁移到其他地址后需在新电脑上重新生成匹配地址的证书，例如：

```powershell
pwsh -NoProfile -File .\new-test-certificate.ps1 -OutputPath .\kst_server.pfx -IpAddress 192.168.1.98
```

重新生成证书会更换自签名证书的公钥；若客户端校验证书信任链，需要重新信任新的 `kst_server.cer`。也可把 `kst_server.pfx` 替换为科视认可的服务端证书，文件名和 PFX 密码 `kst-mes-mock` 保持不变。
