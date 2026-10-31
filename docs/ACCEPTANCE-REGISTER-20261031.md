# Реестр приёмки CITA Route

Версия реестра 1.0, 31 октября 2026. План: PROJECT-REVIEW-20261031.md.
Все сценарии ниже обязательные для текущего объёма. В каждой строке отдельно
фиксируются реализация, локальное доказательство и приёмка конкретного кандидата.
Изначально новая приёмка открыта; прежние свидетельства хранятся как baseline.
PASS относится к сценарию и хешам, не ко всему приложению. BLOCKED/FAIL/OPEN
не считаются PASS. Изменение знаменателя требует новой версии реестра.

| Этап | ID | Сценарий |
|---|---|---|
| A | A01 | Исходники и отличие от установленной сборки идентифицированы |
| A | A02 | Native managed CI: clean-checkout build и agent self-tests |
| A | A03 | Все три native PE воспроизводятся из corresponding source |
| A | A04 | Пакет с CalibrationId собран; agent/app/updater self-tests |
| A | A05 | Manifest пакета и исходных inputs совпадает до/после проверок |
| B | B01 | Обычный file picker и импорт WG/AWG |
| B | B02 | Ошибочный профиль сохраняет прежний; два разных валидных профиля |
| B | B03 | Connect/Cancel/Stop, включая Stop во время подготовки |
| B | B04 | Закрытие/выход/relaunch; один управляющий процесс |
| B | B05 | Автоподключение при запуске и через автозагрузку |
| B | B06 | Конфликт/повтор команды и потерянный ответ |
| B | B07 | UI/служба/экспорт согласованы, включая CoreUpdateRequired |
| C | C01 | Durable intent: загрузка, RPC, рестарт, конфликт ревизий |
| C | C02 | Повреждение/удаление/откат intent не ослабляет правило |
| C | C03 | Forced VPN/Direct/Blocked/Zapret; отказ неподдерживаемой области |
| C | C04 | Отозванная калибровка не воскресает после другой публикации |
| C | C05 | Fresh proof разрешает новую калибровку; отрицательный/потерянный ACK |
| C | C06 | TTL/clock/overflow/профиль/epoch; старые потоки и retirement |
| D | D01 | Отказ Zapret ограничен своим путём |
| D | D02 | Смерть и зависание ядра; минимальный repair и исчерпание бюджета |
| D | D03 | Потеря guard и смерть службы; отсутствие утечки |
| D | D04 | DNS timeout/ошибочный ответ/отказ VPN endpoint |
| D | D05 | IP/NIC change и sleep/resume |
| D | D06 | Stop/update одновременно с reconnect; управляющий доступ/cleanup |
| E | E01 | Baseline 1/20/100+ направлений и несколько приложений |
| E | E02 | p50/p95/max очереди, первого доступа, повторного доступа |
| E | E03 | Ограниченный параллелизм, дедупликация, справедливость |
| E | E04 | TTL/refresh/negative cache не ослабляют защиту |
| E | E05 | CPU/RAM/handles/helper churn до/после; отсутствие роста |
| F | F01 | Корпус 30–50 страниц, включая 10 holdout |
| F | F02 | Зависимости, изображения, redirects, Range, partial body |
| F | F03 | DNS/TLS/HTTP/client-blocker классифицируются раздельно |
| F | F04 | IPv6/DoH/ECH/неизвестный процесс/QUIC имеют явное поведение |
| F | F05 | Двусторонний UDP под TCP-нагрузкой; late/loss/jitter/bursts |
| F | F06 | YouTube; реальный Discord voice+share либо явный BLOCKED |
| G | G01 | Чистая/существующая установка, runtime отсутствует/есть |
| G | G02 | Версия новая/та же/старая и другой каталог |
| G | G03 | Отмена/нет прав/нет места/отказ скачивания |
| G | G04 | Сбой замены пары, откат и повтор обновления |
| G | G05 | Удаление/reinstall, профили и остатки автозапуска/маршрутов |
| G | G06 | Доверие обновления, лицензии/source/драйвер и права распространения |
| H | H01 | Все обязательные результаты привязаны к финальному пакету |
| H | H02 | 10-минутный smoke, очистка и инструкция восстановления |
| H | H03 | Известные ограничения и готовый private EXE; публикация отдельно |

Всего: A=5, B=7, C=6, D=6, E=5, F=6, G=6, H=3; 44 сценария.
Процент этапа = PASS / его знаменатель. Общий процент = PASS / 44, при этом
критические отказы блокируют выпуск независимо от числа. Реальный Discord нельзя
заменить synthetic PASS; если стенда нет, F06 остаётся незакрытым или объём
кандидата явно пересматривается с пользователем.

## Начало выполнения

31 октября: цель создана по новому плану. Новый stage a7e8994e собран из текущих
исходников с verified source receipt CalibrationId. Self-tests выполняются.
Managed CI добавлена, но GitHub run ещё не выполнялся, A02 не принят.
Полная clean-checkout native build pipeline пока не закрыта.

### Пачка A, результат

A01/A03/A04/A05 приняты как build/source gates кандидата a7e8994e:
277 файлов manifest и 271 managed build input совпали после всех проверок;
проверенный corresponding source соответствует текущему native inventory.
Agent 19A096B3..., app 2F1F0D61..., updater EDF17B1D...: self-tests exit 0.
Закрытый framework-dependent EXE собран. Полные хеши и размер установщика:
`runtime/plan-a-candidate-a7e8994e.json`.

A02 открыт: добавлен отдельный managed workflow, но он не запускался в GitHub;
полная native clean build остаётся отдельной незавершённой частью этого gate.
Этап A: 4/5 = 80%. Общая приёмка: 4/44 = 9,09%; это пока только build gates,
пользовательские и сетевые сценарии нового кандидата не приняты.
Состояния: `runtime/acceptance-state-20261031.json` (хеш документа после добавления
этой заметки обновляется отдельно).

РМ снова доступна по LAN: совпал аппаратный UUID, служба Running, Explorer
в session 2. Это подтверждает доступ к станции; установленный пакет пока прежний.
Далее: приёмка установки нового пакета с сохранением профиля/намерения и
CalibrationId/fault-сценарии на РМ. Сетевые изменения в этой пачке не выполнялись.

### Физическая установка и первый fault test

Позже в этой же рабочей сессии выполнены разрешённые изменения только на РМ:
установщик C92A36C5... успешно установил публичные файлы, same-version решение
зафиксировано. Run 5e9086a94dd849edbc9ab1fbd532916f; после очистки actors/tasks/root=0.
Штатный producer ee8bfcd543da4385aeea75c8a7dc5bc3 обновил agent 3B7BCA8D→19A096B3
и core 65376C51→93E9FCFB, сохранив профиль и false intent gen50.
Installed manifest 903FBE36...; upgrade c410e14bfd2a4adb8ade52cdfccf8c14 Committed.
Это частичные свидетельства B/G, не полное закрытие соответствующих сценариев.

D02 — FAIL на a7e8994e. Контролируемое завершение точно проверенного ядра
8704 привело к новому core PID7884/Active/repairs1, но сессия получила
PolicyRecoveryUnconfirmed / RecoveryFailed. Нельзя считать восстановлением
сессии сам факт запуска нового процесса. NetworkResets=0, 16 address events
имели unchanged stamp; прежняя гипотеза о смене физической сети не доказана.
Причина внутри координации publisher/core ещё требует подтверждения; вероятную
гонку текущей калибровки с заменой ядра не считать установленным диагнозом.

Run 6b6a446158f74cc180cfccffb2e2831f, CID1841e647f5e046f3834cf684e542b9e7.
Оба rescue подтвердили ExactConnection/Stopped; falseDisconnected gen51,
actors0, tasks/root отсутствуют. Полный результат сохранён до cleanup:
`runtime/native-client-cycle-inspection-6b6a446158f74cc180cfccffb2e2831f.json`.
Следующий приоритет — устранить этот воспроизводимый блокирующий дефект,
затем повторить изменённый сценарий и nominal smoke. Счёт приёмки остаётся 4/44.

### Разбор восстановления — следующая пачка

Добавлена запись подтверждённого retired PID после успешного Dispose под core
gate: поздняя ошибка старой policy не закрывает новую binding. Неизвестный PID
и неподтверждённая очистка по-прежнему дают отказ. Добавлены отрицательные тесты;
адресное ревью наблюдателя не обнаружило ослабления этих границ.

Кандидат358b4bb0/agent588F5FB0 установлен и прошёл self-tests. Два fault-прогона
вернули HTTPS, но automaticRouting осталось Unavailable; третий вновь дал
PolicyRecoveryUnconfirmed. Первые два harness Passed НЕ считаются приёмкой:
критерий был слишком слабым. Полная история:
`runtime/plan-d-core-regression-358b4bb0.json`, Accepted=false.
Показанные там RecoveryMs 2721/2784 отсчитывались ПОСЛЕ отдельной exit-пробы,
поэтому это не полное время от crash. Таймер исправлен: старт ДО Kill.

Следующий кандидат6030da34/agent7C15B878 добавил fallback к scoped owner вместо
нулевого cleanup PID и ACK пустой policy при привязке нового ядра, сохраняя
revision при уже завершившемся reset. Self-tests и ревью прошли; установщик
4DD2388E... установлен на РМ; upgrade8e2d501cde3d496488d99548e262c4e5
Committed, профиль и false gen54 сохранены, временный payload/tasks удалены.

Усиленный physical тест3bc99d50465c4f64ae1b8d3e7dadf53b — FAIL:
Core Active/Connected, но automaticRouting Unavailable, coreResets0. Теперь
controller требует активного scheduler и coreResets>0, а не только HTTPS.
Результат: `runtime/plan-d-core-regression-results.json`; оба rescueStopped,
falseDisconnectedgen55, actors/tasks/root0. Новая схема state.json также
сохраняет automaticRouting при раннем отказе теста.

В исходниках дополнительно исправлена некорректная трактовка неатомарного
снимка State/PID: ResetForCoreAsync теперь возвращает false БЕЗ изменения
политики, если наблюдение уже устарело; scheduler повторяет согласование.
Неизвестная ошибка guard/revoke не подавляется. Эта последняя правка ещё
не установлена на РМ и не является доказанным устранением сбоя.
Следующий шаг: локальная проверка и повтор того же усиленного fault-теста,
при необходимости точечная диагностика причины завершения scheduler.
Полный D02 остаётся FAIL; отсутствие зависания ядра и остальные faults также
не приняты. Общая приёмка по-прежнему 4/44=9,09%.

### Повтор после исправления dead-owner

Кандидат df7c36b7 установлен на РМ после локальных self-tests. Три физических
прогона core-crash прошли: `e0c0a159...`, `d4f38e501...`, `9e1fd7ee...`.
Каждый сохранил тот же connection ID и desired intent, перешёл через Recovering
в Active, возобновил automatic routing и завершился HTTPS-проверкой. Время от
завершения ядра до восстановления: 3358, 4334 и 3359 мс. Для каждого прогона
rescue остановлен, processes=0, tasks/root отсутствуют. Полный журнал:
`runtime/plan-d-core-regression-df7c36b7.log`; структурированные данные:
`runtime/plan-d-core-regression-results.json`.

D02 теперь PASS как ограниченная проверка смерти ядра и восстановления policy.
После исправления SYSTEM harness D03 также PASS как bounded-проверка потери
службы/guard и восстановления с сохранением intent. Evidence:
`runtime/service-cycle-result-5e841b9088a840eebb3cbe41ff11e490.json` и
`runtime/service-cycle-cleanup-5e841b9088a840eebb3cbe41ff11e490.json`.
Задачи и временная папка после evidence удалены; RoutePilotRescue осталась
Running, intent/agent/manifest не изменились. Это не закрывает D01/D04–D06 и
не доказывает восстановление после смены NIC, сна, DNS или зависшего процесса.
Общая приёмка: 6/44 = 13,64%.

Текущее состояние РМ после прогонов: UUID подтверждён, RoutePilotRescue Running,
намерение отключено, generation 59, установлен агент кандидата df7c36b7. Активных
тестовых задач и рабочих процессов не осталось. Следующий сценарий D04 должен
проверить DNS timeout/ошибочный VPN endpoint; отдельные длительные сетевые и
медиапроверки пока не запускались.

### A02 managed CI принят

31 октября 2026 GitHub Actions run `41219677919` на commit `5c9e154` прошёл clean-checkout сборку .NET 10 и полный набор agent self-tests. Предыдущий сбой был ошибкой теста: сценарий передавал запрещённую минимальную ревизию `0`; исправление перевело его на валидную ревизию `1`. Новый private-preview stage: `fd2490f6`; обновлённые managed hashes и CI-ссылка сохранены в `runtime/private-preview-candidate-092276ed.json` и `runtime/managed-build-result-20261031.json`. Локальная попытка самотестов по-прежнему ограничена политикой хоста для loopback TCP/named pipe и не считается дефектом клиента.

A02 теперь PASS для текущего кандидата, этап A — 5/5 (100%), общий реестр — 7/44 (15,91%). Публичный релиз остаётся закрыт: installer integration, physical acceptance и пользовательские/сетевые сценарии ещё не приняты.

### Native installer integration принят

После привязки к stage `fd2490f6` `Verify-Installer.ps1 -NativeStandalone -IsolatedTest` прошёл полный dedicated fixture-цикл. Он подтвердил чистую установку, upgrade старой версии, same-version decline/confirm, downgrade и invalid-version refusal, ярлыки и uninstall registration, отказ при активном маршруте, interrupted-copy rollback, uninstall/reinstall и сохранение пользовательских данных. В harness добавлен bounded retry чтения transaction journal; краткий файл-lock больше не превращает корректный rollback в ложный FAIL. Receipt: `runtime/installer-integration-result-fd2490f6.json`. Это закрывает installer integration gate кандидата; отдельные G01–G06 acceptance-сценарии остаются открыты до полной матрицы.
