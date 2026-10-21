# 飞针机文件采集程序

`FlyingProbeCollector.exe` 是带窗口的独立飞针机采集程序。它只读取飞针机数据目录，将每个设备目录中的 TXT 测试计数作为快照提交给 QMS Telemetry v1。它与其他监听器的进程、配置、队列和日志互不共用。

## 构建与部署

在已安装 .NET Framework 4.x 编译器的 Windows 电脑上运行 `tools\flying-probe-collector\build.cmd`。发布目录为 `outputs\flying-probe-collector`；将整个目录复制到现场电脑。SCBGX 上的采集目录使用本地路径 `E:\飞针机`，QMS 地址为 `http://192.168.1.98:8080`；在其他电脑部署时按实际位置修改。现场运行账户需要能读取采集目录，并能写入程序目录下的 `data` 和 `logs`。

双击 `FlyingProbeCollector.exe` 打开窗口，在“采集配置”中填写数据目录、QMS 地址和真实网关令牌，然后点击“保存配置”和“启动采集”。生产 QMS 需要服务账号认证时，填写不带 `Bearer ` 前缀的 `BearerToken`。令牌框会遮挡内容；请勿将含真实令牌的 INI 提交到仓库。每个设备子目录名必须在 QMS 的“设备网关映射”中映射到对应设备的 `machineName`。

也可双击 `start-collector.cmd` 启动窗口，或在命令行执行 `FlyingProbeCollector.exe --config <配置文件路径>`。配置未填写完整时，窗口仍会打开并显示“待配置”；配置有效时自动开始采集。点击“停止采集”会完成当前 TXT 文件处理或等待当前 QMS 请求结束，然后停止并允许修改配置；关闭窗口即停止采集。另有 `FlyingProbeCollector.Console.exe` 供无人值守命令行运行，支持相同的 `--config` 参数，按 Ctrl+C 可退出。同一队列只能有一个程序运行。首次启动会采集现有 TXT 文件的当前快照，此后文件更新时上报新快照。文件需在连续两轮扫描中保持长度和修改时间不变才会被处理。

## 数据与故障恢复

只读取 `<SourceRoot>\<设备名>\<料号>.txt`，不递归深层目录。文件创建时间为开始时间，最后修改时间为 QMS `deviceTime` 和更新时间。`Test_Counters 2 2 0 01:27:09` 依次表示已测板数、好板数、坏板数、上次测试时间；已测板数写入 QMS `machiningCount`，其余值连同开始时间写入 `attributes`。QMS 当前把好板、坏板保存在原始事件里，现有汇总界面不会自动显示这两个数。

`data\pending-events.json` 保存待发送事件；QMS 返回 HTTP `2xx` 且成功写入本地同步记录后才删除。`data\scan-checkpoints.json` 保存每个文件最后入队的快照编号。两者损坏时程序会报错停止，避免静默丢失数据；请保留文件并修复后重启。`data\history\yyyy-MM-dd.jsonl` 保留入队、成功、失败和重试记录，不自动删除；窗口下方可按日期查看，选中一行查看请求事件和响应摘要。历史文件不保存认证头，令牌会从响应摘要中遮盖。`logs\flying-probe-collector.log` 记录扫描和发送结果，不写入令牌。共享目录或 QMS 暂时不可用时，程序保留本地状态并重试；不要在程序运行中删除或修改这些数据文件。

程序运行时独占 `data\pending-events.json.lock`，避免重复启动造成队列覆盖。若第二份程序提示 `already in use`，先检查现有采集进程；进程退出后锁会自动释放，锁文件本身留在磁盘上。QMS 返回重定向时会保留事件并重试，不把登录页的成功响应算作上报成功。

## 现场验证

1. 用运行程序的 Windows 账户打开共享目录，确认能看到设备子目录和 TXT 文件。
2. 在 QMS 配置设备目录名对应的网关映射，并填好 QMS 地址和令牌。
3. 启动程序，等待两轮扫描；确认窗口的“同步记录”出现“已入队”和“同步成功”，QMS 遥测历史中出现 `FLYING_PROBE_TEST_COUNTERS` 事件。
4. 修改一份测试 TXT 的 `Test_Counters` 后再次观察；应新增一条快照，未改动的文件不重复上报。现场测试时请只使用允许修改的样本文件。
5. 临时断开 QMS 后检查 `pending-events.json` 保留待发事件，恢复 QMS 后确认补传。无需为了测试断开飞针机共享目录。
