using System.Security.Cryptography;
using System.Text.Json;

namespace RoutePilot.Agent;

internal static class EngineTestAssets
{
    internal static string Router(string root)
    {
        string image=Path.Combine(root,"runtime","router-engine-build","smart-router.exe");
        if(!File.Exists(image))return Path.Combine(root,"runtime","smart-router.exe");
        using var receipt=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"runtime","router-engine-build.json")));
        using var file=File.OpenRead(image);
        if(!Convert.ToHexString(SHA256.HashData(file)).Equals(receipt.RootElement.GetProperty("sha256").GetString(),StringComparison.OrdinalIgnoreCase))
            throw new IOException("Current router test image differs from its build receipt.");
        return image;
    }
}
