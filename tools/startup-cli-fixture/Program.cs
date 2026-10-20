// Disposable lifecycle fixture only: no network or driver code.
Console.WriteLine(File.Exists(Path.Combine(AppContext.BaseDirectory,"fixture-ready.txt"))?"Not connected":"Fixture service is not ready");
