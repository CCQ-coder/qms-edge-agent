using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Web.Script.Serialization;

// Optional, site-specific routing for a trusted TCP proxy that forwards multiple machines
// while leaving a stale assets value in their business messages.
internal sealed class KstSourceRoutes
{
    internal readonly string TrustedProxyIp;
    internal readonly Dictionary<string, string> HostToCode;
    internal readonly Dictionary<string, string> AllowedStaleAssets;

    private KstSourceRoutes(string trustedProxyIp, Dictionary<string, string> hostToCode,
        Dictionary<string, string> allowedStaleAssets)
    {
        TrustedProxyIp = trustedProxyIp;
        HostToCode = hostToCode;
        AllowedStaleAssets = allowedStaleAssets;
    }

    internal static KstSourceRoutes Load(string path)
    {
        if (!File.Exists(path))
            return new KstSourceRoutes("", EmptyMap(), EmptyMap());

        Dictionary<string, object> values = new JavaScriptSerializer().DeserializeObject(
            File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
        if (values == null) throw new ArgumentException("科视来源路由配置必须是 JSON 对象。");

        string proxyIp = RequiredText(values, "trusted_proxy_ip");
        ValidateIpv4(proxyIp, "trusted_proxy_ip");
        Dictionary<string, string> routes = ReadMap(values, "source_host_routes", true);
        Dictionary<string, string> stale = ReadMap(values, "allowed_stale_assets", false);

        foreach (KeyValuePair<string, string> route in routes)
        {
            ValidateHost(route.Key);
            ValidateCode(route.Value, "source_host_routes[" + route.Key + "]");
        }
        foreach (KeyValuePair<string, string> alias in stale)
        {
            string routeCode;
            if (!routes.TryGetValue(alias.Key, out routeCode))
                throw new ArgumentException("allowed_stale_assets 中的 Host 未配置路由：" + alias.Key);
            ValidateCode(alias.Value, "allowed_stale_assets[" + alias.Key + "]");
            if (SameCode(routeCode, alias.Value))
                throw new ArgumentException("allowed_stale_assets 不应与该 Host 的设备编号相同：" + alias.Key);
        }
        return new KstSourceRoutes(proxyIp, routes, stale);
    }

    internal void ValidateDeviceMappings(KstMqttSettings mqttSettings)
    {
        if (HostToCode.Count == 0 || !mqttSettings.Enabled) return;
        foreach (KeyValuePair<string, string> route in HostToCode)
            mqttSettings.ResolveDevice(route.Value, false);
        foreach (KeyValuePair<string, string> alias in AllowedStaleAssets)
            mqttSettings.ResolveDevice(alias.Value, false);
    }

    // Check the on-disk queue before constructing the forwarder. Its normal load path may
    // compact legacy items, and it must not replay entries whose original Host was never saved.
    internal void ValidatePendingQueue(KstMqttSettings mqttSettings)
    {
        if (HostToCode.Count == 0 || !mqttSettings.Enabled || !File.Exists(mqttSettings.QueueFile)) return;
        string path = Path.GetFullPath(mqttSettings.QueueFile);
        IList saved = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as IList;
        if (saved == null) throw new InvalidDataException("S1 MQTT 队列必须是 JSON 数组：" + path);
        foreach (object raw in saved)
        {
            Dictionary<string, object> record = raw as Dictionary<string, object>;
            if (record == null) throw new InvalidDataException("S1 MQTT 队列包含无效项目：" + path);
            KstMqttQueueItem item = KstMqttQueueItem.FromDictionary(record);
            string sourceCode;
            if (item.RoutingSource != "trusted_proxy_host" || item.SourceClientIp != TrustedProxyIp
                || string.IsNullOrEmpty(item.RequestHost)
                || !HostToCode.TryGetValue(item.RequestHost, out sourceCode)
                || !SameCode(item.DeviceCode, sourceCode)
                || !string.Equals(item.S1DeviceNumber,
                    mqttSettings.ResolveDevice(sourceCode, false).DeviceNumber, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("S1 MQTT 待发队列含缺少可信入口来源或映射不符的旧数据；先停止服务、备份并审计队列与 .rate-state.json，再处理该队列：" + path);
        }
    }

    private static Dictionary<string, string> EmptyMap()
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> ReadMap(Dictionary<string, object> values, string field, bool required)
    {
        object raw;
        if (!values.TryGetValue(field, out raw))
        {
            if (required) throw new ArgumentException(field + " 不能为空。");
            return EmptyMap();
        }
        Dictionary<string, object> parsed = raw as Dictionary<string, object>;
        if (parsed == null || (required && parsed.Count == 0))
            throw new ArgumentException(field + " 必须是非空的 Host 到设备编号 JSON 对象。");
        Dictionary<string, string> result = EmptyMap();
        foreach (KeyValuePair<string, object> entry in parsed)
        {
            string code = entry.Value as string;
            if (code == null || result.ContainsKey(entry.Key))
                throw new ArgumentException(field + " 包含无效或重复的 Host 映射：" + entry.Key);
            result.Add(entry.Key, code);
        }
        return result;
    }

    private static string RequiredText(Dictionary<string, object> values, string field)
    {
        object raw;
        string result = values.TryGetValue(field, out raw) ? raw as string : null;
        if (string.IsNullOrWhiteSpace(result) || !string.Equals(result, result.Trim(), StringComparison.Ordinal))
            throw new ArgumentException(field + " 必须是无首尾空格的 IPv4 地址。");
        return result;
    }

    private static void ValidateHost(string host)
    {
        if (string.IsNullOrEmpty(host) || !string.Equals(host, host.Trim(), StringComparison.Ordinal))
            throw new ArgumentException("来源 Host 不能为空或包含首尾空格。");
        int colon = host.LastIndexOf(':');
        if (colon <= 0 || colon != host.IndexOf(':'))
            throw new ArgumentException("来源 Host 必须写成 IPv4:端口：" + host);
        ValidateIpv4(host.Substring(0, colon), "来源 Host");
        string portText = host.Substring(colon + 1);
        int port;
        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) ||
            port < 1 || port > 65535 || !string.Equals(portText, port.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            throw new ArgumentException("来源 Host 端口无效：" + host);
    }

    private static void ValidateIpv4(string value, string field)
    {
        IPAddress address;
        if (!IPAddress.TryParse(value, out address) || address.AddressFamily != AddressFamily.InterNetwork ||
            !string.Equals(value, address.ToString(), StringComparison.Ordinal) ||
            value == "0.0.0.0" || value == "255.255.255.255")
            throw new ArgumentException(field + " 必须是完整的 IPv4 地址。");
    }

    private static void ValidateCode(string code, string field)
    {
        if (string.IsNullOrWhiteSpace(code) || !string.Equals(code, code.Trim(), StringComparison.Ordinal))
            throw new ArgumentException(field + " 的设备编号不能为空或包含首尾空格。");
        foreach (char character in code)
            if (!((character >= 'A' && character <= 'Z') || (character >= 'a' && character <= 'z') ||
                (character >= '0' && character <= '9') || character == '-' || character == '_'))
                throw new ArgumentException(field + " 的设备编号只能包含英文字母、数字、减号和下划线。");
    }

    private static bool SameCode(string left, string right)
    {
        return string.Equals(left.Replace("-", ""), right.Replace("-", ""), StringComparison.OrdinalIgnoreCase);
    }
}
