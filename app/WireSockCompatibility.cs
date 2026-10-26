using System;
using System.Text.RegularExpressions;

namespace RoutePilot;

internal sealed record WireSockAssessment(string Code,string Message,bool CanManage,bool OfferInstall);

// A reviewed stable release is deliberately separate from the vendor's latest
// version. A beta or unknown future CLI must never trigger an automatic downgrade.
internal static class WireSockCompatibility
{
    internal static readonly Version Minimum=new(3,4,4,1);
    internal static readonly Version Reviewed=new(3,6,1,1);
    internal static Version? Parse(string? text)
    {
        if(text==null||!Regex.IsMatch(text.Trim(),@"\A\d+\.\d+\.\d+(?:\.\d+)?\z"))return null;
        return Version.TryParse(text.Trim(),out var value)?new(value.Major,value.Minor,value.Build,Math.Max(0,value.Revision)):null;
    }
    internal static WireSockAssessment Assess(ConnectionInventory inventory)
    {
        var version=Parse(inventory.Version);
        if(inventory.Kind=="NotFound"||inventory.ExecutablePath.Length==0&&inventory.CliPath.Length==0)
            return new("Missing","WireSock не установлен или не найден. Можно установить проверенную версию из CITA Route.",false,true);
        if((version!=null&&version<Minimum)||inventory.Kind=="LegacyClient")
            return new("Unsupported","Найдена несовместимая редакция WireSock"+(version==null?"":" "+version)+". Для управления профилями требуется Secure Connect 3.4.4.1 или новее с wiresock-connect-cli.exe.",false,true);
        if(inventory.CliPath.Length==0)
            return new("Incomplete","WireSock найден, но командный интерфейс отсутствует. Укажи полную установку или восстанови WireSock.",false,true);
        if(!inventory.CliTrusted)
            return new("Untrusted","Подпись или защищённое расположение WireSock CLI не подтверждены. Повтори поиск полной установки в Program Files.",false,false);
        if(!inventory.ManagementReady)
            return new("MissingCapabilities","Необходимые команды WireSock не подтверждены. Обнови или восстанови установленную редакцию.",false,version==null||version<=Reviewed);
        if(version==null)
            return new("UnknownVersion","Версию WireSock не удалось определить. Доступность профилей проверяется отдельно; автоматическая замена отключена.",true,false);
        if(version>Reviewed)
            return new("Unreviewed","WireSock "+version+" новее проверенной версии CITA. Возможности проверяются отдельно; понижение версии не предлагается.",true,false);
        if(version<Reviewed)
            return new("UpdateAvailable","WireSock "+version+" поддерживает управление. Доступно обновление до проверенной стабильной версии "+Reviewed+".",true,true);
        return new("Reviewed","WireSock "+version+": проверенная стабильная версия.",true,false);
    }
}
