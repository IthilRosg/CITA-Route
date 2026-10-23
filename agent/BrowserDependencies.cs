namespace RoutePilot.Agent;

// Observed initiator-document relationships, never wildcard/CDN route rules.
internal sealed class BrowserDependencies
{
    internal sealed record Edge(string ContextId,string ParentDomain,PolicyKey Target,string Resource,int Seen,int Completed,int Failed,DateTime UpdatedUtc);
    readonly Dictionary<(string Context,string Parent,PolicyKey Target,string Resource),Edge> _edges=new();
    internal int Count=>_edges.Count;
    void Prune(DateTime now)
    {
        foreach(var key in _edges.Where(p=>now<p.Value.UpdatedUtc||now-p.Value.UpdatedUtc>=TimeSpan.FromMinutes(5)).Select(p=>p.Key).ToArray())_edges.Remove(key);
    }
    internal void Observe(string context,string parent,PolicyKey target,string resource,DateTime now,bool? completed=null)
    {
        if(!Guid.TryParseExact(context,"N",out _)||string.IsNullOrEmpty(parent)||parent.Length>253||PolicySafety.Domain(parent)!=parent||PolicySafety.Pinned(parent)||!PolicySafety.Valid(target)||!PolicyBrowserFeedback.Resource(resource)||resource=="Document")return;
        Prune(now);var key=(context,parent,target,resource);var old=_edges.GetValueOrDefault(key);
        // Completion cannot resurrect an evicted/unobserved relationship.
        if(completed!=null&&old==null)return;
        if(old==null&&_edges.Values.Count(e=>e.ContextId==context&&e.ParentDomain==parent&&e.Target.Application==target.Application)>=32)return;
        _edges[key]=new(context,parent,target,resource,Math.Min(1000000,(old?.Seen??0)+(completed==null?1:0)),Math.Min(1000000,(old?.Completed??0)+(completed==true?1:0)),Math.Min(1000000,(old?.Failed??0)+(completed==false?1:0)),now);
        while(_edges.Count>256)_edges.Remove(_edges.MinBy(p=>p.Value.UpdatedUtc).Key);
    }
    internal PolicyKey[] Related(string context,string parent,PolicyKey target,DateTime now)
    {
        Prune(now);
        return _edges.Values.Where(e=>e.ContextId==context&&e.ParentDomain==parent&&e.Target.Epoch==target.Epoch&&e.Target.Application==target.Application&&e.Target!=target)
            .OrderByDescending(e=>e.UpdatedUtc).Select(e=>e.Target).Distinct().Take(4).ToArray();
    }
    internal Edge[] Snapshot(DateTime now){Prune(now);return _edges.Values.OrderByDescending(e=>e.UpdatedUtc).Take(32).ToArray();}
    internal void Clear()=>_edges.Clear();
}
