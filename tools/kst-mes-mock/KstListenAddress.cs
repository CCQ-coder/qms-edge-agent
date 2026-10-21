using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

internal sealed class KstListenCandidate
{
    internal readonly string Address;
    internal readonly bool HasGateway;

    internal KstListenCandidate(string address, bool hasGateway)
    {
        Address = address;
        HasGateway = hasGateway;
    }
}

internal static class KstListenAddress
{
    private const string PreviousMesAddress = "192.168.1.71";

    private sealed class Choice
    {
        internal readonly IPAddress Address;
        internal bool HasGateway;

        internal Choice(IPAddress address, bool hasGateway)
        {
            Address = address;
            HasGateway = hasGateway;
        }
    }

    internal static string DefaultAddress()
    {
        return DefaultAddress(Discover());
    }

    internal static string DefaultAddress(IEnumerable<KstListenCandidate> candidates)
    {
        List<Choice> choices = Sort(candidates);
        return choices.Count == 0 ? "" : choices[0].Address.ToString();
    }

    internal static string DisplayAddress(string listenIp)
    {
        return DisplayAddress(listenIp, Discover());
    }

    internal static string DisplayAddress(string listenIp, IEnumerable<KstListenCandidate> candidates)
    {
        List<Choice> choices = Sort(candidates);
        IPAddress address;
        if (!TryIPv4(listenIp, out address)) return "";
        if (address.Equals(IPAddress.Any)) return choices.Count == 0 ? "" : choices[0].Address.ToString();
        foreach (Choice choice in choices)
            if (address.Equals(choice.Address)) return choice.Address.ToString();
        return "";
    }

    internal static string ValidationError(string listenIp)
    {
        return ValidationError(listenIp, Discover());
    }

    internal static string ValidationError(string listenIp, IEnumerable<KstListenCandidate> candidates)
    {
        List<Choice> choices = Sort(candidates);
        IPAddress address;
        if (!TryIPv4(listenIp, out address))
            return "监听 IP 必须是有效的 IPv4 地址。" + CandidateHint(choices);
        if (choices.Count == 0)
            return "本机没有可用于接收设备请求的 IPv4 地址，请先检查网络连接。";
        if (address.Equals(IPAddress.Any)) return null;
        foreach (Choice choice in choices)
            if (address.Equals(choice.Address)) return null;
        return "监听 IP " + address + " 不属于本机活动网卡，无法启动服务。" + CandidateHint(choices);
    }

    internal static string[] CandidateAddresses()
    {
        List<Choice> choices = Sort(Discover());
        string[] addresses = new string[choices.Count];
        for (int i = 0; i < choices.Count; i++) addresses[i] = choices[i].Address.ToString();
        return addresses;
    }

    private static string CandidateHint(List<Choice> choices)
    {
        if (choices.Count == 0) return "本机没有可用的 IPv4 地址。";
        string[] addresses = new string[choices.Count];
        for (int i = 0; i < choices.Count; i++) addresses[i] = choices[i].Address.ToString();
        return "本机可用地址：" + String.Join("、", addresses) + "；也可填 0.0.0.0 监听所有网卡。";
    }

    private static bool TryIPv4(string text, out IPAddress address)
    {
        return IPAddress.TryParse((text ?? "").Trim(), out address)
            && address.AddressFamily == AddressFamily.InterNetwork;
    }

    private static bool IsUsable(IPAddress address)
    {
        if (address == null || address.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] bytes = address.GetAddressBytes();
        return bytes[0] != 0 && bytes[0] != 127 && bytes[0] < 224
            && !(bytes[0] == 169 && bytes[1] == 254);
    }

    private static int PrivateRank(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        if (bytes[0] == 192 && bytes[1] == 168) return 3;
        if (bytes[0] == 10) return 2;
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return 1;
        return 0;
    }

    private static int Score(Choice choice)
    {
        if (choice.Address.ToString() == PreviousMesAddress) return 1000;
        int privateRank = PrivateRank(choice.Address);
        if (privateRank > 0) return (choice.HasGateway ? 500 : 300) + privateRank;
        return choice.HasGateway ? 200 : 100;
    }

    private static List<Choice> Sort(IEnumerable<KstListenCandidate> candidates)
    {
        Dictionary<string, Choice> unique = new Dictionary<string, Choice>(StringComparer.Ordinal);
        if (candidates != null)
        {
            foreach (KstListenCandidate candidate in candidates)
            {
                IPAddress address;
                if (candidate == null || !TryIPv4(candidate.Address, out address) || !IsUsable(address)) continue;
                string key = address.ToString();
                Choice choice;
                if (unique.TryGetValue(key, out choice)) choice.HasGateway |= candidate.HasGateway;
                else unique.Add(key, new Choice(address, candidate.HasGateway));
            }
        }
        List<Choice> choices = new List<Choice>(unique.Values);
        choices.Sort(delegate(Choice left, Choice right)
        {
            int score = Score(right).CompareTo(Score(left));
            if (score != 0) return score;
            byte[] leftBytes = left.Address.GetAddressBytes();
            byte[] rightBytes = right.Address.GetAddressBytes();
            for (int i = 0; i < leftBytes.Length; i++)
            {
                int comparison = leftBytes[i].CompareTo(rightBytes[i]);
                if (comparison != 0) return comparison;
            }
            return 0;
        });
        return choices;
    }

    private static KstListenCandidate[] Discover()
    {
        List<KstListenCandidate> candidates = new List<KstListenCandidate>();
        try
        {
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                IPInterfaceProperties properties;
                try { properties = adapter.GetIPProperties(); }
                catch (NetworkInformationException) { continue; }
                bool hasGateway = false;
                foreach (GatewayIPAddressInformation gateway in properties.GatewayAddresses)
                    if (IsUsable(gateway.Address)) { hasGateway = true; break; }
                foreach (UnicastIPAddressInformation unicast in properties.UnicastAddresses)
                    if (IsUsable(unicast.Address)) candidates.Add(new KstListenCandidate(unicast.Address.ToString(), hasGateway));
            }
        }
        catch (NetworkInformationException) { }
        return candidates.ToArray();
    }
}
