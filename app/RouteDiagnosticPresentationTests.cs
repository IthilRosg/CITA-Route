using System;

namespace RoutePilot;

internal static class RouteDiagnosticPresentationTests
{
    internal static void Run()
    {
        if(RouteDiagnosticPresentation.Domain("https://Example.com/private?token=secret")!="example.com")throw new Exception("Only domain metadata may cross the diagnostic boundary.");
        foreach(string input in new[]{"127.0.0.1","https://user:secret@example.com/","http://example.com/","https://example.com:8443/","example.com/path","localhost","example.com\r\nInjected"})
        {
            bool failed=false;try{RouteDiagnosticPresentation.Domain(input);}catch(ArgumentException){failed=true;}
            if(!failed)throw new Exception("Invalid diagnostic target accepted.");
        }
    }
}
