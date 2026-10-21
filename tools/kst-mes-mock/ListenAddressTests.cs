using System;

internal static class ListenAddressTests
{
    private static int Main()
    {
        try
        {
            KeepsConfiguredMesAddressWhenItIsLocal();
            PrefersPrivateAddressWithGatewayOnAnotherComputer();
            ExcludesLoopbackAndLinkLocalAddresses();
            HandlesNoUsableNetworkAddress();
            UsesReachableAddressForAllInterfacesUrl();
            RejectsAnAddressNotAssignedToThisComputer();
            Console.WriteLine("KST listen-address selection passed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.GetType().FullName + ": " + error.Message);
            return 1;
        }
    }

    private static void KeepsConfiguredMesAddressWhenItIsLocal()
    {
        KstListenCandidate[] local = {
            new KstListenCandidate("192.168.3.40", true),
            new KstListenCandidate("192.168.1.71", false)
        };
        Equal("192.168.1.71", KstListenAddress.DefaultAddress(local), "preserve .71 when assigned locally");
    }

    private static void PrefersPrivateAddressWithGatewayOnAnotherComputer()
    {
        KstListenCandidate[] local = {
            new KstListenCandidate("172.18.0.1", false),
            new KstListenCandidate("10.9.0.8", true),
            new KstListenCandidate("192.168.1.98", true),
            new KstListenCandidate("192.168.1.200", false)
        };
        Equal("192.168.1.98", KstListenAddress.DefaultAddress(local), "select the active private LAN address with a gateway");
    }

    private static void ExcludesLoopbackAndLinkLocalAddresses()
    {
        KstListenCandidate[] local = {
            new KstListenCandidate("127.0.0.1", true),
            new KstListenCandidate("169.254.20.30", true),
            new KstListenCandidate("192.168.1.98", false)
        };
        Equal("192.168.1.98", KstListenAddress.DefaultAddress(local), "do not advertise loopback or APIPA");
        NotNull(KstListenAddress.ValidationError("127.0.0.1", local), "do not accept loopback as LAN listener");
    }

    private static void HandlesNoUsableNetworkAddress()
    {
        KstListenCandidate[] local = { new KstListenCandidate("169.254.20.30", false) };
        Equal("", KstListenAddress.DefaultAddress(local), "no unusable default URL");
        Equal("", KstListenAddress.DisplayAddress("0.0.0.0", local), "no unusable wildcard URL");
        NotNull(KstListenAddress.ValidationError("0.0.0.0", local), "fail clearly with no usable IPv4");
    }

    private static void UsesReachableAddressForAllInterfacesUrl()
    {
        KstListenCandidate[] local = {
            new KstListenCandidate("192.168.1.98", true),
            new KstListenCandidate("172.18.0.1", false)
        };
        Equal("192.168.1.98", KstListenAddress.DisplayAddress("0.0.0.0", local), "wildcard listener advertises a real IP");
        IsNull(KstListenAddress.ValidationError("0.0.0.0", local), "wildcard listener accepted when LAN IP exists");
    }

    private static void RejectsAnAddressNotAssignedToThisComputer()
    {
        KstListenCandidate[] local = { new KstListenCandidate("192.168.1.98", true) };
        string error = KstListenAddress.ValidationError("192.168.1.71", local);
        NotNull(error, "reject another computer's IPv4");
        Contains(error, "192.168.1.98", "show an actionable local candidate");
        Equal("", KstListenAddress.DisplayAddress("192.168.1.71", local), "do not advertise nonlocal URL");
        Equal("192.168.1.98", KstListenAddress.DisplayAddress("192.168.1.98", local), "advertise assigned address");
    }

    private static void Equal(string expected, string actual, string description)
    {
        if (!String.Equals(expected, actual, StringComparison.Ordinal))
            throw new Exception(description + ": expected '" + expected + "', got '" + actual + "'");
    }

    private static void IsNull(string actual, string description)
    {
        if (actual != null) throw new Exception(description + ": " + actual);
    }

    private static void NotNull(string actual, string description)
    {
        if (actual == null) throw new Exception(description + ": unexpectedly accepted");
    }

    private static void Contains(string actual, string part, string description)
    {
        if (actual.IndexOf(part, StringComparison.Ordinal) < 0)
            throw new Exception(description + ": " + actual);
    }
}
