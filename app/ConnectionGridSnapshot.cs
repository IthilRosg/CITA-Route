using System.Collections.Generic;
using System.Linq;

namespace RoutePilot;

internal sealed record ConnectionGridRow(string Id,string Host,string Process,string Network,bool IsVpn,bool IsDpi,string Rule,long Upload,long Download,string Address,string Started);
internal static class ConnectionGridSnapshot
{
    internal static bool RefreshInPlace(object? current,IReadOnlyList<ConnectionRow> incoming)
    {
        if(current is not IReadOnlyList<ConnectionRow> displayed||displayed.Count!=incoming.Count)return false;
        for(int i=0;i<incoming.Count;i++)if(displayed[i].Id!=incoming[i].Id)return false;
        for(int i=0;i<incoming.Count;i++)
        {
            var a=displayed[i];var b=incoming[i];a.Host=b.Host;a.Process=b.Process;a.Network=b.Network;
            a.IsVpn=b.IsVpn;a.IsDpi=b.IsDpi;a.Rule=b.Rule;a.Upload=b.Upload;a.Download=b.Download;a.Address=b.Address;a.Started=b.Started;
        }
        var view=System.Windows.Data.CollectionViewSource.GetDefaultView(current);
        if(view.SortDescriptions.Count>0)view.Refresh();
        return true;
    }
    internal static ConnectionGridRow[] Capture(IEnumerable<ConnectionRow> rows)=>rows.Select(r=>new ConnectionGridRow(r.Id,r.Host,r.Process,r.Network,r.IsVpn,r.IsDpi,r.Rule,r.Upload,r.Download,r.Address,r.Started)).ToArray();
}
