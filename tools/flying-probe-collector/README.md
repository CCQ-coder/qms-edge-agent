# 飞针机文件采集程序

`FlyingProbeCollector.exe` 是独立于精德、森泉、科视监听器的 Windows 程序。它只读取飞针机共享目录，将每个设备目录中的 TXT 测试计数作为快照提交给 QMS Telemetry v1。两套程序的进程、配置、队列和日志互不共用。

## 构建与部署

在已安装 .NET Framework 4.x 编译器的 Windows 电脑上运行 `tools\flying-probe-collector\build.cmd`。发布目录为 `outputs\flying-probe-collector`；将整个目录复制到现场电脑。现场运行账户需要能读取 `\\Scbgx\数字化\飞针机`，并能写入程序目录下的 `data` 和 `logs`。

先修改发布目录中的 `FlyingProbeCollector.ini`：填写真实 `QmsBaseUrl` 和 `GatewayToken`；生产 QMS 需要服务账号认证时，填写不带 `Bearer ` 前缀的 `BearerToken`。请勿将真实令牌提交到仓库。每个设备子目录名必须在 QMS 的“设备网关映射”中映射到对应设备的 `machineName`。

运行 `start-collector.cmd`，或执行 `FlyingProbeCollector.exe --config <配置文件路径>`。程序持续运行，按 `ScanIntervalSeconds` 轮询；按 Ctrl+C 可退出。首次启动会采集现有 TXT 文件的当前快照，此后文件更新时上报新快照。文件需在连续两轮扫描中保持长度和修改时间不变才会被处理。

## 数据与故障恢复

只读取 `<SourceRoot>\<设备名>\<料号>.txt`，不递归深层目录。文件创建时间为开始时间，最后修改时间为 QMS `deviceTime` 和更新时间。`Test_Counters 2 2 0 01:27:09` 依次表示已测板数、好板数、坏板数、上次测试时间；已测板数写入 QMS `machiningCount`，其余值连同开始时间写入 `attributes`。QMS 当前把好板、坏板保存在原始事件里，现有汇总界面不会自动显示这两个数。

`data\pending-events.json` 保存待发送事件；只有 QMS 返回 HTTP `2xx` 才删除。`data\scan-checkpoints.json` 保存每个文件最后入队的快照编号。两者损坏时程序会报错停止，避免静默丢失数据；请保留文件并修复后重启。`logs\flying-probe-collector.log` 记录扫描和发送结果，不写入令牌。共享目录或 QMS 暂时不可用时，程序保留本地状态并重试；不要在程序运行中删除或修改这些数据文件。

## 现场验证

1. 用运行程序的 Windows 账户打开共享目录，确认能看到设备子目录和 TXT 文件。
2. 在 QMS 配置设备目录名对应的网关映射，并填好 QMS 地址和令牌。
3. 启动程序，等待两轮扫描；确认日志出现 `Queued` 和 `QMS accepted`，QMS 遥测历史中出现 `FLYING_PROBE_TEST_COUNTERS` 事件。
4. 修改一份测试 TXT 的 `Test_Counters` 后再次观察；应新增一条快照，未改动的文件不重复上报。现场测试时请只使用允许修改的样本文件。
5. 临时断开 QMS 后检查 `pending-events.json` 保留待发事件，恢复 QMS 后确认补传。无需为了测试断开飞针机共享目录。
