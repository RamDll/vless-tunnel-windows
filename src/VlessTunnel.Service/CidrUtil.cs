using System.Net;

namespace VlessTunnel.Service;

internal static class CidrUtil
{
    public static (IPAddress Address, byte PrefixLength) Parse(string cidr)
    {
        var parts = cidr.Split('/', 2);
        if (parts.Length != 2 || !byte.TryParse(parts[1], out var prefixLength))
            throw new FormatException($"Ожидался адрес в нотации CIDR (\"a.b.c.d/nn\"), получено: {cidr}");
        return (IPAddress.Parse(parts[0]), prefixLength);
    }
}
