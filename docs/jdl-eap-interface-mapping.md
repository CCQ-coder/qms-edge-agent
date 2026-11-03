# 金大立六轴锣机 EAP 接口与云端返回对照

本文对照《EAP接口文档--金大立--六轴锣机-2024-3-22》的九个 WebAPI 接口与 2026-11-03 已部署的接收器。厂家文档中的 `server:7666` 是示例，不是当前设备应填写的地址。

| 项目 | 地址或要求 |
| --- | --- |
| 厂家文档示例基址 | `http://server:7666/dataService/webapi` |
| 当前云端基址 | `http://106.227.69.144:8604/dataService/webapi` |
| 请求方式 | `POST`，UTF-8 JSON 对象；接口路径大小写须与下表一致 |

将下表路径接在对应基址后，即得到该接口的完整 URL。例如，设备状态的当前地址是 `http://106.227.69.144:8604/dataService/webapi/IO/UploadDeviceStatus`。

## 九个文档接口

| 功能 | PDF 页码 | 接口路径 | 文档返回字段 | 当前 HTTP 返回 |
| --- | ---: | --- | --- | --- |
| 扫码获取任务 | 1–2 | `/Equip/MillingMachineAccessToTask` | `AssetsCode`、`LotCode`、`ProductCode`、`PnlQty`、`PanelLength`、`PanelWidth`、`PanelThickness`、`WprogramNameDir`、`ZDFilePath`、可选 `Description` | 请求落盘后返回 **503**；`Error.Message`、`Error.Code`、`IsSuccess`。目前不下发任务。 |
| 工艺质量参数 | 2–4 | `/IO/UploadProcessQualityParameters` | `Error.Message`、`Error.Code`、`IsSuccess` | 验证并持久入队后 **200**；同文档字段。 |
| 设备状态 | 5–7 | `/IO/UploadDeviceStatus` | `Error.Message`、`Error.Code`、`IsSuccess` | 验证并持久入队后 **200**；同文档字段。 |
| 参数调整 | 7–9 | `/IO/UploadParameterAdjustments` | `Error.Message`、`Error.Code`、`IsSuccess` | 验证并持久入队后 **200**；同文档字段。 |
| 生产计数 | 9–11 | `/IO/UploadProductionCount` | `Error.Message`、`Error.Code`、`IsSuccess` | 验证并持久入队后 **200**；同文档字段。 |
| 稼动率、电量 | 12–14 | `/Equip/UploadJdl` | `Error.Message`、`Error.Code`、`IsSuccess` | 验证并持久入队后 **200**；同文档字段。 |
| 报警开始 | 14–15 | `/UploadAlarmBegin` | `Error.Message`、`Error.Code`、`IsSuccess` | 验证并持久入队后 **200**；同文档字段。 |
| 报警解除 | 16–18 | `/UploadAlarmEnd` | `Error.Message`、`Error.Code`、`IsSuccess` | 验证并持久入队后 **200**；同文档字段。 |
| 断刀信息 | 19–21 | `/uploadbrokenknife` | `TransactionId`、`Code`、`Msg`、`Data` | 验证并持久入队后 **200**；同文档字段。 |

当前七个普通上传接口的成功响应相同：

```json
{"Error":{"Message":"","Code":0},"IsSuccess":true}
```

断刀接口成功时回显请求中的 `TransactionId`；其 `Code` 是**字符串**，普通上传接口的 `Error.Code` 是**整数**：

```json
{"TransactionId":"<请求中的 TransactionId>","Code":"0","Msg":"上传成功","Data":null}
```

扫码接口按当前接入要求只记录请求，不下发任务。有效请求成功存档后的响应为：

```json
{"Error":{"Message":"task source is not configured","Code":1},"IsSuccess":false}
```

厂家文档只给出了扫码成功时的任务字段，没有定义“无任务”或失败响应；当前 503 是接收器明确表示任务来源尚未配置的返回。断刀接口的 `Data` 在 PDF 参数表中标为必填字符串，但 PDF 成功示例及当前实现均为 `null`。

## 错误返回与接收条件

七个普通上传接口失败时仍返回 `Error.Message`、整数 `Error.Code=1`、`IsSuccess=false`；断刀接口失败时返回请求中的 `TransactionId`（未能解析到字符串时为空字符串）、字符串 `Code="1"`、错误说明 `Msg`、`Data=null`。

| 当前条件 | HTTP 状态 | `Message` 或 `Msg` |
| --- | ---: | --- |
| 请求不是 POST | 405 | `POST required` |
| 正文不是 UTF-8 JSON 对象，或缺少必需的设备编号 | 400 | `JSON object required`、`missing AssetsCode` 或 `missing EqpId` |
| 扫码缺少 `VehicleCode`，或断刀缺少 `TransactionId` | 400 | `missing VehicleCode` 或 `missing TransactionId` |
| 文档上传的来源 IP 或设备编号不属于当前 S1 转发配置 | 403 | `untrusted device or source`；先尝试记录原文，存档失败则返回 503 |
| 正文超过 64 KiB | 413 | `body too large` |
| 扫码请求有效，但任务来源未配置 | 503 | `task source is not configured` |
| 非转发请求的原文存储不可用 | 503 | `capture unavailable` |
| 可信文档上传的 S1 待发队列不可用 | 503 | `forward queue unavailable`；公共采样存储失败不阻断已成功入队的上报 |

当前只为 S1 产品“金大立六轴锣机”（产品 ID `34`）下的设备 `E1807091137`（设备 ID `5`）配置了文档上传转发。`8604` 监听端口对所有 IP 开放；转发还要求报文设备编号匹配，并来自已验证的出口 `113.195.85.167/32`。文档中的七个普通上传及断刀接口收到 HTTP 200，表示接收器已将可信上报写入持久队列，不表示 S1 数据库已经入库。

## 现场软件使用的其他入口

以下入口来自现场软件截图，**不在上述 EAP PDF 的九个接口中**：

| 当前入口 | 接收方式 | 当前成功响应 | 后续处理 |
| --- | --- | --- | --- |
| `http://106.227.69.144:8604/httptest.php` | GET 或 POST；HTTP 原文采样 | `{"code":0,"msg":"captured"}` | 保存原文，暂不转发 S1。 |
| `http://106.227.69.144:8604/jdlSave/MchnData` | GET 或 POST；HTTP 原文采样 | `{"code":0,"msg":"captured"}` | 保存原文，暂不转发 S1。 |
| `106.227.69.144:8604` | 原始 MES TCP 字节流 | 尚无厂家分帧与应答规范，当前不发送业务 ACK。 | 按连接保存原始字节，暂不转发 S1。 |
| `https://gongying.duckdns.org:5173/jdlSave/MchnData?key=<私有密钥>` | POST；原有 HTTPS 采样入口 | `{"code":0,"msg":"captured"}` | 要求正确密钥，保存原文，暂不转发 S1。 |

8604 的 HTTP 与 TCP 原文保存在云端 `/var/lib/jdl-s1-bridge/mes-captures/`；5173 的原文保存在 `/var/lib/jdl-s1-bridge/captures/`。8604 HTTP 记录包含时间、来源、路径、方法、查询参数、请求头和原始正文；原始 TCP 记录包含时间、来源和接收字节；5173 记录包含时间、来源、路径、`Content-Type` 和正文。8604 公共采样目录最多保存 10000 条，每条正文最多 64 KiB；满额时普通 HTTP 采样返回 503，原始 TCP 关闭连接，已验证的文档上传仍以包含完整原文的 S1 持久队列为接收依据。

本文依据厂家 PDF 第 1–21 页及本仓库 `tools/jdl-s1-bridge/` 的当前实现；两条现场软件 URL 路径和原始 TCP 的实际业务报文格式未由 PDF 定义。
