using RoutePilot.Agent;

internal static class RepairBudgetTests
{
    sealed class Clock:TimeProvider
    {
        internal long Seconds;
        public override long TimestampFrequency=>1;
        public override long GetTimestamp()=>Seconds;
    }
    internal static void Run()
    {
        var clock=new Clock();var budget=new NativeRepairBudget(clock);
        static void Require(bool value){if(!value)throw new Exception("Native rolling repair budget failed.");}
        for(int window=0;window<3;window++)
        {
            clock.Seconds=window*300;
            Require(budget.TryAcquire()&&budget.TryAcquire()&&!budget.TryAcquire());
        }
        clock.Seconds=900;Require(!budget.TryAcquire());
        clock.Seconds=3599;Require(!budget.TryAcquire());
        clock.Seconds=3600;Require(budget.TryAcquire()&&budget.TryAcquire()&&!budget.TryAcquire());
        clock.Seconds=3500;Require(!budget.TryAcquire());
        clock.Seconds=3900;Require(budget.TryAcquire());
        Console.WriteLine("PASS: monotonic repair windows, hourly cap, boundary expiry and clock rollback.");
    }
}
