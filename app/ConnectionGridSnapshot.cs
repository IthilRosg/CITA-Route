using System.Collections.Generic;
using System.Linq;

namespace RoutePilot;

internal sealed record ConnectionGridRow(string Id,string Host,string Process,string Network,bool IsVpn,bool IsDpi,string Rule,long Upload,long Download,string Address,string Started);
internal static class ConnectionGridSnapshot
{
    internal static ConnectionGridRow[] Capture(IEnumerable<ConnectionRow> rows)=>rows.Select(r=>new ConnectionGridRow(r.Id,r.Host,r.Process,r.Network,r.IsVpn,r.IsDpi,r.Rule,r.Upload,r.Download,r.Address,r.Started)).ToArray();
}
