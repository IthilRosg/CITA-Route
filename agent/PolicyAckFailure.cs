namespace RoutePilot.Agent;

internal sealed class PolicyAckFailure
{
    public int Schema{get;set;}=1;
    public string SessionId{get;set;}="";
    public string Route{get;set;}="";
    public string Stage{get;set;}="";
    public string Code{get;set;}="";
    public DateTime UpdatedUtc{get;set;}
}
