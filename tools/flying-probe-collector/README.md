# 飞针机文件采集程序

`FlyingProbeCollector.exe` 是带窗口的独立飞针机采集程序。它只读取飞针机共享目录，将每个设备目录中的 TXT 测试计数作为快照提交给 QMS Telemetry v1。它与其他监听器的进程、配置、队列和日志互不共用。

## 构建与部署

在已安装 .NET Framework 4.x 编译器的 Windows 电脑上运行 `tools\flying-probe-collector\build.cmd`。发布目录为 `outputs\flying-probe-collector`；首次安装时可将整个目录复制到现场电脑。也可运行 `powershell -NoProfile -File tools\flying-probe-collector\package.ps1` 生成仅含程序、说明和空白配置示例的 ZIP。打包脚本不会把 `outputs` 下的现场 INI、设备映射、队列或日志放入 ZIP。现场运行账户需要能读取 `\\Scbgx\数字化\飞针机`，并能写入程序目录下的 `data` 和 `logs`。

从 ZIP 首次安装时，将 `FlyingProbeCollector.ini.example` 复制为 `FlyingProbeCollector.ini`，将 `FlyingProbeCollector.hosts.json.example` 复制为 `FlyingProbeCollector.hosts.json`，再双击 `FlyingProbeCollector.exe` 填写并保存现场配置。若尚未复制 INI 就双击窗口程序，窗口仍会打开并显示“待配置”，保存配置时会创建 INI；控制台程序应先复制示例配置。

双击 `FlyingProbeCollector.exe` 打开窗口，在“采集配置”中填写共享目录、QMS 地址和真实网关令牌，然后点击“保存配置”和“启动采集”。网关模式只使用 `X-Gateway-Token`；旧 `Bearer Token` 字段不参与网关请求，建议清空。令牌框会遮挡内容，模板占位值也会显示为圆点；请勿将含真实令牌的 INI 提交到仓库。每个设备子目录名必须在 QMS 的“设备网关映射”中映射到对应设备的 `machineName`。

在“飞针机设备状态”中给每个设备目录填写控制电脑的局域网主机名。默认每 60 秒使用 ICMP Ping 探测；探测间隔可设为 1–240 秒，最大值比 QMS 默认 300 秒的状态有效期少 60 秒。现场禁 Ping 时，可选 TCP 并填写经现场确认能代表控制电脑在线的端口。设备使用动态 IP 时，应填写稳定的主机名，而不是当前 IP；采集器每轮重新解析主机名。点击“保存设备映射”将设置写到与当前 INI 同目录的 `FlyingProbeCollector.hosts.json`，保存后重启采集生效。构建程序不会覆盖发布目录里已有的映射文件。主机名可以暂时留空，此时设备显示“未确认”，TXT 数据继续同步。映射文件损坏或端口无效也只影响状态探测，不影响 TXT 采集。

“在线”仅表示对应控制电脑网络可达，不代表飞针机机械本体或测试软件正在工作；连续 3 次探测失败后显示“离线”。在线且最近 10 分钟实际观察到 TXT 文件长度或修改时间变化时显示“运行中”，否则保持“在线”。共享目录无法读取、采集器或 QMS 超过 5 分钟没有新状态时显示“未确认”。刚启动时旧 TXT 文件只建立变化基线，不会被当作正在运行。设备在线状态独立于测试计数补传，旧数据补传不会把离线设备改为在线。

也可双击 `start-collector.cmd` 启动窗口，或在命令行执行 `FlyingProbeCollector.exe --config <配置文件路径>`。配置未填写完整时，窗口仍会打开并显示“待配置”；配置有效时自动开始采集。点击“停止采集”会完成当前 TXT 文件处理或等待当前 QMS 请求结束，然后停止并允许修改配置；关闭窗口即停止采集。另有 `FlyingProbeCollector.Console.exe` 供无人值守命令行运行，支持相同的 `--config` 参数，按 Ctrl+C 可退出。同一队列只能有一个程序运行。首次启动会采集现有 TXT 文件的当前快照，此后文件更新时上报新快照。文件需在连续两轮扫描中保持长度和修改时间不变才会被处理。

窗口将配置保存到当前 EXE 同目录的 `FlyingProbeCollector.ini`；使用 `--config` 时保存到指定文件。可以先保存未填完令牌的配置，窗口会提示“配置已保存，尚不能启动”；补齐令牌并再次保存后才能启动。若提示“保存失败”，文件并未更新。修改输入框后必须先保存再启动；重开时也要使用同一目录的 EXE。现场升级时先停止采集，可直接解压新版 ZIP；其中两个配置示例使用 `.example` 后缀，不会覆盖原 `FlyingProbeCollector.ini`、`FlyingProbeCollector.hosts.json`、`data` 和 `logs`。使用构建目录升级时只替换两个 EXE 与需要更新的说明文件。

## 数据与故障恢复

只读取 `<SourceRoot>\<设备名>\<料号>.txt`，不递归深层目录。文件创建时间为开始时间，最后修改时间为 QMS `deviceTime` 和更新时间。`Test_Counters 2 2 0 01:27:09` 依次表示已测板数、好板数、坏板数、上次测试时间；已测板数写入 QMS `machiningCount`，其余值连同开始时间写入 `attributes`。QMS 当前把好板、坏板保存在原始事件里，现有汇总界面不会自动显示这两个数。

`data\pending-events.json` 保存待发送事件；QMS 返回 HTTP `2xx` 且成功写入本地同步记录后才删除。`data\scan-checkpoints.json` 保存每个文件最后入队的快照编号。两者损坏时程序会报错停止，避免静默丢失数据；请保留文件并修复后重启。`data\history\yyyy-MM-dd.jsonl` 保留入队、成功、失败和重试记录，不自动删除；窗口下方可按日期查看，选中一行查看请求事件和响应摘要。“已入队”只表示本地保存成功，不代表 QMS 已入库。设备状态通过独立请求上报，不写入测试数据队列或同步历史；窗口设备状态区会显示状态上报结果。历史文件不保存认证头，令牌会从响应摘要中遮盖。`logs\flying-probe-collector.log` 记录扫描和发送结果，不写入令牌。共享目录或 QMS 暂时不可用时，程序保留本地测试计数队列并重试；不要在程序运行中删除或修改这些数据文件。

程序运行时独占 `data\pending-events.json.lock`，避免重复启动造成队列覆盖。若第二份程序提示 `already in use`，先检查现有采集进程；进程退出后锁会自动释放，锁文件本身留在磁盘上。QMS 返回重定向时会保留事件并重试，不把登录页的成功响应算作上报成功。

## 现场验证

1. 用运行程序的 Windows 账户打开共享目录，确认能看到设备子目录和 TXT 文件。
2. 部署含飞针机状态接口和 V34 运行通道表的新版 QMS，设置服务端固定遥测租户、网关令牌，并配置设备目录名对应的网关映射。采集器填好 QMS 地址及相同网关令牌，清空旧 Bearer Token。
3. 在采集电脑上逐台用 `ping <主机名>` 或指定 TCP 端口验证控制电脑可达，再保存主机名映射。目录名不一定是主机名，未验证前允许留空。
4. 启动程序，等待两轮扫描；确认窗口的“同步记录”出现“已入队”和“同步成功”，QMS 遥测历史中出现 `FLYING_PROBE_TEST_COUNTERS` 事件；同时在设备状态区确认状态 POST 已上报、QMS 实时监控页显示在线。
5. 修改一份测试 TXT 的 `Test_Counters` 后再次观察；应新增一条快照并显示“运行中”，未改动的文件不重复上报。现场测试时请只使用允许修改的样本文件。
6. 验证 TXT 停止更新 10 分钟后回到“在线”，网络连续 3 次探测失败后变为“离线”，采集器关闭或网关不可达 5 分钟后 QMS 显示“未确认”。
7. 临时断开 QMS 后检查 `pending-events.json` 保留待发事件，恢复 QMS 后确认补传。无需为了测试断开飞针机共享目录。
