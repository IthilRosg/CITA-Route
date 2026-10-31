# Проверка пользовательского API Freakhosting

Проверено по полной OpenAPI-схеме, извлечённой из предоставленного API Reference.html, а не по отдельным скриншотам меню.

Версия: 1.0.0; база: https://cloud.freakhosting.com/api; операций: 29.

| Метод | Путь | Назначение |
|---|---|---|
| GET | `/account/sshKeys` | SSH Keys |
| POST | `/account/sshKeys` | Add SSH Key |
| DELETE | `/account/sshKeys/{sshKeyId}` | Delete SSH Key |
| GET | `/account` | Settings |
| POST | `/server/{serverId}/build` | Build |
| GET | `/server/{serverId}/operatingSystemTemplates` | Operating System Templates |
| GET | `/server/{serverId}/swap` | Swap Options |
| POST | `/server/{serverId}/vnc` | Enable/Disable |
| GET | `/server/{serverId}/vnc` | Settings |
| POST | `/server/{serverId}/restart` | Restart |
| POST | `/server/{serverId}/boot` | Boot |
| POST | `/server/{serverId}/shutdown` | Shutdown |
| POST | `/server/{serverId}/powerOff` | Power Off |
| GET | `/server/{serverId}/tasks` | Tasks |
| GET | `/server/{serverId}/task/{taskId}` | Task |
| POST | `/server/{serverId}/iso` | ISO |
| GET | `/server/{serverId}/isos` | ISOs |
| GET | `/server` | Servers |
| GET | `/server/{serverId}` | Server |
| POST | `/server/{serverId}/resetPassword` | Reset Password |
| PUT | `/server/{serverId}/name` | Change Name |
| POST | `/server/{serverId}/rescue` | Rescue |
| PUT | `/server/{serverId}/settings` | Settings |
| POST | `/server/{serverId}/bootOrder` | Boot Order |
| GET | `/resourcePack` | Resource Packs |
| GET | `/resourcePack/{resourcePackId}` | Resource Pack |
| POST | `/resourcePack/{resourcePackId}/{createId}` | Create Server |
| DELETE | `/resourcePack/{serverId}` | Delete Server |
| GET | `/connect` | Verify Connection |

Отдельных документированных операций Firewall, DDoS, traffic policy, packet-rate limit или изменения параметров сетевого интерфейса в этой схеме нет. PUT /server/{serverId}/settings задаёт только bootType и autoConfiguration; это не управление фильтрацией. Изменения не выполнялись.

GET /server/{serverId}?state=true возвращает состояние и сетевые счётчики. Результат для DE-2: runtime/de2-panel-live-state.json; сервер running, suspended=false. Значение network.primary.limit=null не доказывает отсутствия внешнего policer. В примере документации limit содержит строку "20000 GB", то есть его нельзя интерпретировать как packet-rate или скорость.

Для проверки конкретного внешнего фильтра требуется отдельный интерфейс защиты провайдера или его оператор. Рабочий пользовательский ключ VirtFusion сам по себе не предоставляет сведения о таких фильтрах. Неустановленные внешние правила не считаются доказанно отсутствующими.
