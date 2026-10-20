// Disposable lifecycle fixture only: no network or driver code.
Console.OutputEncoding=new System.Text.UTF8Encoding(false);
// Real Russian WireSock CLI output observed in the Windows VM acceptance run.
Console.WriteLine(File.Exists(Path.Combine(AppContext.BaseDirectory,"fixture-ready.txt"))?"Нет подключения":"Fixture service is not ready");
