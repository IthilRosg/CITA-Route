using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json;

namespace RoutePilot;

internal static class RouteDiagnosticPresentation
{
    internal static string Domain(string input)
    {
        input=input.Trim();
        if(input.Contains("://",StringComparison.Ordinal))
        {
            if(!Uri.TryCreate(input,UriKind.Absolute,out var uri)||uri.Scheme!="https"||!uri.IsDefaultPort||uri.UserInfo.Length>0)throw new ArgumentException("Укажи домен или HTTPS-ссылку без данных входа и нестандартного порта.");
            input=uri.IdnHost;
        }
        string domain=new IdnMapping().GetAscii(input.TrimEnd('.')).ToLowerInvariant();
        if(domain.Length>253||IPAddress.TryParse(domain,out _)||!System.Text.RegularExpressions.Regex.IsMatch(domain,@"\A(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9-]{2,63}\z"))
            throw new ArgumentException("Нужен публичный домен, например example.com.");
        return domain;
    }
    internal static string Describe(JsonElement report,string domain)
    {
        if(report.GetProperty("Schema").GetInt32()!=1||report.GetProperty("Domain").GetString()!=domain)throw new ArgumentException("Ответ проверки относится к другому запросу.");
        if(!report.GetProperty("Current").GetBoolean())return report.GetProperty("Code").GetString() switch
        {
            "Busy"=>"Другая проверка домена уже выполняется.",
            "SessionChanged"=>"Подключение изменилось во время проверки. Результаты отброшены; повтори проверку после восстановления.",
            _=>"Сначала подключись и дождись подтверждения VPN. Во время восстановления сравнение путей не запускается."
        };
        var paths=report.GetProperty("Paths").EnumerateArray().ToArray();
        if(paths.Length!=3)throw new ArgumentException("Неполный ответ проверки путей.");
        string Label(string code)=>code switch
        {
            "ContentConfirmed"=>"две проверки содержимого прошли",
            "DnsUnavailable"=>"DNS не дал пригодного ответа",
            "UnsafeAddress"=>"DNS вернул непубличный адрес; запрос не отправлен",
            "TlsAuthenticationError"=>"сертификат TLS не подтверждён",
            "HttpRestricted"=>"сервер дважды вернул HTTP 451",
            "BodyStalled"=>"передача содержимого останавливается",
            "TransportUnavailable"=>"соединение не подтверждено",
            "BudgetExceeded"=>"истёк срок проверки",
            _=>"недостаточно данных для подтверждения доступа"
        };
        return "Проверен корневой адрес https://"+domain+"/\n"+string.Join("\n",paths.Select(p=>
        {
            string route=p.GetProperty("Route").GetString() switch{"direct"=>"Напрямую","dpi"=>"DPI","vpn"=>"VPN",_=>throw new ArgumentException("Неизвестный путь.")};
            string status=p.TryGetProperty("HttpStatus",out var http)&&http.ValueKind==JsonValueKind.Number?" · HTTP "+http.GetInt32():"";
            return route+": "+Label(p.GetProperty("Outcome").GetString()??"")+status+" · "+p.GetProperty("Milliseconds").GetInt64()+" мс";
        }))+"\nМаршруты не изменены. Проверяется HTTPS/TCP по IPv4. Корневой адрес не проверяет авторизацию, все ресурсы страницы, приложение, IPv6 или QUIC. Отказ сервера сам по себе не доказывает блокировку.";
    }
}
