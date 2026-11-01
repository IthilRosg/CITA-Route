# CITA Route — аудит завершения проекта

Дата: 1 ноября 2026 года.

## Зафиксированный кандидат

Канонический private-preview кандидат: `3279a92b`.
Финальный пересобранный ZIP: `dist/CITA-Route-0.6.9-win-x64-framework.zip`.
Финальный установщик: `dist/CITA-Route-0.6.9-win-x64-setup.exe`.

Проверки происхождения и упаковки прошли. Native source receipt привязан к core
`4D6EA4F55DE6388EE951C6F45C633366ED52D7D6256B4CDADA3B2B91897DAEE9`, manifest
и native archive пересобраны из той же цепочки. `Verify-FastRelease.ps1` вернул
`PASS` для stage `3279a92b` с теми же исходниками и receipt.
Managed CI run `41257404364` для коммита `a2cf59b` завершился успешно.
Последующий runner physical-gates `a8bff25` также прошёл CI: run `41257846640`.

## Что закрыто

- .NET 10 agent build: PASS.
- WPF app build: PASS после удаления только старого build-lock.
- native core rebuild из закреплённого source receipt: PASS.
- source/package verification: PASS.
- native installer build: PASS.
- fast package verification: PASS.
- предыдущая managed CI и installer integration сохранены как baseline; D02/D03
  для freshly rebuilt `3279a92b` ещё не повторены и не считаются PASS этого кандидата.
- helper управления РМ получил переменный `CITA_STATION_ADDRESS`; транспорт больше
  не обязан навсегда использовать один LAN-адрес.
- physical-cycle defaults обновлены на agent/app/manifest hashes кандидата
  `3279a92b`, чтобы следующий прогон не использовал старые pins.
- локальные ProfileSync, Storage, RuleFeeds, ComponentLiveness, ComponentRecovery,
  SessionRecovery, AgentPackage, ConnectionHandoff, WireSockRecovery,
  InstallationGuard и InstallationTransaction прошли.
- NativeAgentLab собран и self-test прошёл; shared agent compilation завершилась
  без ошибок, с четырьмя прежними analyzer/nullable warnings.

## Открытые gates

Кандидат остаётся private preview. Для публичного стабильного релиза не закрыты:

- физический UI-прогон выбора профиля и полного lifecycle;
- десятиминутный smoke с реальным профилем;
- durable forced policy C01–C06;
- DNS/NIC/sleep/update fault cases;
- длительные resource и media проверки;
- IPv6/QUIC/UDP и реальный Discord voice/share;
- полная матрица установки, обновления, отката и удаления.

Локальная физическая приёмка сейчас не запускается автоматически: рабочая машина
перешла с `192.168.1.117` на `10.228.246.23`, а РМ осталась на старом сегменте.
Старый host-route к `192.168.1.124` распознан как устаревший; его удаление требует
повышенного системного подтверждения. До восстановления общего LAN-сегмента нельзя
честно объявлять физические UI и smoke gates пройденными.

DesktopRuntime и Optimization self-tests в текущем окружении остановлены защитой
стенда на создании junction/системных pipe; эти результаты не считаются PASS.
Go native-awg source tests также имеют отдельные host-only отказы: DNS fixture
получает `ReadTimeout`, а proxy fixture возвращает WSAEACCES `10013`; native
guard unit package при этом проходит. Эти результаты не используются как
доказательство сетевой работоспособности.

## Ограничения текущего пакета

Установщик не содержит внутренний Zapret и маркирован как native preview. Это
преднамеренно исключает неподтверждённую внешнюю компоненту из публичного payload.
Публичный релиз нельзя объявлять только по локальной упаковке или старым receipt.
