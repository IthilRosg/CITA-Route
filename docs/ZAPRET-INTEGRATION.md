# Windows Zapret: интеграция CITA Route

Текущий статус 30 октября 2026: связь selector → publisher → session-owned
connector → WinDivert/Zapret внедрена в защищённый production-agent внутреннего
кандидата. На физической РМ подтверждены подготовка connector (Available) и
попытка автоматической пробы после TLS timeout Direct. Успешная публикация и
передача пользовательского тестового соединения через Zapret ещё не приняты:
прогон fb1afe29d795448dae84f771f7083738 завершил probe timeout/ошибку и сбросы
состояния. Все 12 HTTP-ответов этого прогона и exit proof относятся к базовому
VPN-пути; они не подтверждают обход DPI или медиасвязь. Актуальные критерии и
доказательства: `docs/GOAL-2-RESILIENT-CLIENT.md` и
`runtime/native-routing-sample-summary-fb1afe29d795448dae84f771f7083738.json`.

Ниже сохранена хронология компонентных и лабораторных проверок. Ограничение
текущей стратегии — IPv4/TCP443; public redistribution остаётся закрыта.

## Реализовано

- `tools/zapret/Fetch-Zapret.ps1` закрепляет commit Windows upstream
  `4840909b1a26389cb72c46be5301d610b3768928`, проверяет Git blob, размер,
  SHA-256 всех шести файлов и Windows Authenticode драйвера. Нужны winws2,
  Cygwin, WinDivert DLL/driver и два Lua-файла. Загрузка не является разрешением
  на распространение: receipt явно содержит `redistributionReady=false`.
- `NativeZapretPlan` создаёт **полный** WinDivert-фильтр конкретного сокета:
  IPv4, физический ifIdx, local/remote IP/port, оба направления, исключение
  loopback/impostor. Не используются общие `--wf-tcp-out=443`. Исключены
  локальные, FakeIP, CGNAT, Radmin-адреса и явные адреса VPN/управления.
  Первая стратегия — только TLS/TCP 443; UDP сейчас отказывается, а не
  выдаётся за работающий Zapret-путь.
- `NativeZapretBundle` проверяет все файлы, включая Lua; не допускает reparse
  paths. Ожидаемые хеши должны поступать из защищённого manifest установщика.
  Одна проверка хеша не заменяет проверку ACL и доверия драйверу.
- `NativeZapretWorker` ограничивает одновременно работающие handshake-workers
  четырьмя, время — 12 сек, commit каждого процесса — 128 MiB. Состояние живого
  процесса — `StartedUnverified`; успешность ещё требует связанного probe.
  Согласование с ядром не реализовано, поэтому эти ограничения пока не
  являются измеренной характеристикой пользовательского маршрутизатора.
- `NativeProcessJob.StartOwned` назначает job **при создании** процесса через
  Windows process attributes, наследует только NUL, не создаёт консоль.
  Контроллер не оставляет окно между запуском и прикреплением к job.
- Отмена и создание сериализованы; callbacks регистрируются до создания.
  Повторно проверяется отмена после проверки файлов и перед запуском.
  Квота возвращается только после подтверждения active-processes=0 и exit.
  `RetirementConfirmed` обязателен до освобождения порта. Если очистка при
  ошибке Start не подтверждена, специальное исключение сохраняет worker для
  recovery; ошибку нельзя считать разрешением освободить/повторно выдать порт.

## Проверено

`NativeZapretTests` входит в agent self-tests: адресные/протокольные исключения,
epoch/interface/application, отсутствие широкого фильтра, изменённый Lua,
отмена до запуска и внутри двух проверок lease, отсутствие утечки квоты,
реальный дочерний PowerShell с quoting и его завершение через Windows Job.
Проверки используют локальные fixtures, не включают перехват на основном ПК.

На РМ `USER`, UUID `F8798200-12FB-11F1-8F47-A104BEE59100`:

- подпись драйвера — Valid, хеши после передачи совпали;
- winws2 сообщил v1.0.6, исходный revision
  `221d7dff24bfec0cd5ac0a5070b8acef2008af3d`, Lua compatibility 6;
- реальный `WinDivertHelperCompileFilter` принял фильтр;
- реальный `WinDivertHelperEvalFilter` принял оба нужных направления и
  отклонил чужие порт/назначение/интерфейс, loopback и impostor (7 проверок);
- `--dry-run` и инициализация Lua завершились exit 0.

**Обнаруженный побочный эффект:** `--intercept=0` всё равно инициализирует
Windows WinDivert и загружает драйвер. Нельзя называть это проверкой без
изменений ОС. Служба отсутствовала до запуска, появилась с точным тестовым
путём; её остановили только после проверки отсутствия workers и владения
путём. После остановки служба автоматически исчезла; отдельный `sc delete`
вернул отсутствие службы. Независимая проверка подтвердила drivers/workers=[]
(`runtime/zapret-physical-preflight-cleanup.json`). Wrapper теперь отказывается
при существующем чужом WinDivert и имеет cleanup в finally.

Артефакты: `runtime/zapret-physical-preflight.json`,
`runtime/goal2-zapret-agent-tests.log`, `runtime/zapret-upstream-4840909b1a26/upstream-receipt.json`.
Эта пачка не проверяет доступность сайтов, TUN/WFP/Zapret вместе или голос.

## Следующая обязательная передача управления ядро ↔ агент

### Уточнение владельца сокета

Выбран connector-owned .NET Socket: доверенный коннектор агента удерживает
эксклюзивно bound socket до connect, ядро передаёт ему разрешённый поток по
закрытому каналу. Это избегает отдельной реализации Windows TCP/overlapped I/O
в Go и разрыва reserve-close-rebind. Это внутренний коннектор CITA, без WireSock;
он ещё не подключён к core/publisher. Авторизация приложения/назначения,
одноразового ticket и поколения обязательна до предоставления этого пути.

1. Доверенный коннектор создаёт эксклюзивно связанный socket **до connect/SYN**; ядро выбирает путь
   и сообщает точный tuple, application, session/epoch и одноразовый ticket
   через аутентифицированный канал. Предварительный probe выполняется через
   ту же lease; произвольный `Reachable=true` и ticket-корреляция не заменяют
   проверку источника и свежести результата.
2. Агент проверяет текущий core/process, manifest/ACL/driver ownership,
   запускает точный worker и подтверждает готовность; только затем ядро
   разрешает connect и последующие TLS-записи. Одного процесса Alive мало.
   Нельзя запускать WinDivert уже после SYN и считать conntrack корректным.
3. При завершении/отмене сокета: закрыть admission, остановить worker,
   получить подтверждение retirement и только затем освободить local port.
   Ошибка завершения удерживает lease/квоту и требует recovery. Отдельно
   проверить внезапную смерть core/контроллера, повторное использование порта,
   network generation и невозможность захвата чужого приложения.
4. Включить это в publisher и селектор с отдельной проверкой Direct/Zapret/VPN,
   ограниченными пробами и сохранением действующих потоков. Добавить UDP-стратегии
   отдельно; не переносить TCP-результат на Discord/QUIC.
5. Проверить контролируемый перехват на РМ с WFP/TUN, готовность драйвера,
   отмену/смерть/зависание/отказ kill, ACL/TOCTOU, реальное освобождение handles,
   CPU/RAM и сетевые сценарии. Затем закрыть лицензии Cygwin/WinDivert,
   соответствующие исходники и notices перед включением в установщик.

Источники: [Windows bundle](https://github.com/bol-van/zapret-win-bundle),
[Zapret manual](https://github.com/bol-van/zapret2/blob/master/docs/manual.en.md),
[WinDivert API](https://github.com/basil00/WinDivert/blob/master/include/windivert.h).

## Сокетная часть коннектора: реализована, адаптер WinDivert ещё обязателен

`NativeBoundTcpLease` держит реальный эксклюзивный TCP socket с явным
IP_UNICAST_IF и локальным адресом. `INativeSocketInterception` сохраняется
**до вызова** startup, чтобы и обычная ошибка запуска не теряла частичный
ресурс. Только успешная проверка готовности разрешает connect; есть
неизменяемый снимок tuple/epoch, независимо от mutable IPEndPoint вызывающего.
Это транспорт, не подтверждение HTTPS, DPI или качества приложения.

Отмена не передаётся в системный Socket.ConnectAsync: исходники .NET
подтверждают закрытие сокета при отмене этой операции, что нарушило бы порядок
retire → release. Отменяется только ожидание; закрытие socket разрешено лишь
после подтверждённого retirement.

Startup и retirement ограничены ожиданием, но неизвестная задача не объявляется
остановленной. При зависании сохраняются задача/owner/socket/квота; retirement
не запускается параллельно с незавершённым startup, повтор не дублирует pending
retirement. До 64 сокетов удерживаются в ограниченном реестре. Карантин
доступен для обнаружения/повторной очистки только по соответствующему epoch.
Если драйвер/код не возвращается вообще, socket остаётся в fail-closed
карантине; это требует наблюдаемого recovery, не ложного успеха отключения.

Проверено реальными loopback-сокетами: исключительное владение портом,
отсутствие соединения до readiness, неизменность передаваемых байтов,
отмена до connect, отказ cleanup и повтор, обычная ошибка частичного startup,
зависшие startup/retirement, отсутствие параллельной повторной остановки,
лимит 64 и отказ 65-му, подмена mutable destination. Перехватчик в этих тестах
управляемый fixture, **не реальный DPI**.

На физической РМ полный agent self-test завершился exit 0 за 41.12 с,
сборка SHA-256 `E3DA3CC05A62A1A8E3F39A2F1FBBD81BE55FBAAC54E49C460C182C747204B748`.
Обе группы socket/Zapret тестов отмечены, временные localhost firewall-правила
удалены. Артефакт: `runtime/goal2-physical-agent-tests.json`. Эти данные
относятся к указанной сборке; последующая защита file leases проверяется отдельно.

`NativeZapretTrust` повторно использует ACL-политику NativeTrustedBundle,
проверяет цепочку каталогов и удерживает read-only file handles до подтверждения
завершения процесса. Expected hashes копируются в immutable snapshot. Обычная
пользовательская папка не становится trust root. Положительный сценарий в
защищённой установленной папке и готовность настоящего kernel filter ещё
нуждаются в проверке перед включением маршрута.

## Новые результаты: реальный фильтр и закрытый data plane

На физической РМ положительно проверены protected ACL/hash/file locks,
настоящий REFLECT с сопоставлением PID, timestamp, слоя/флагов и полного
канонического tuple. Connect происходит после подтверждения kernel handle.
После завершения подготовки отмена службы по-прежнему останавливает worker:
linked CTS сохраняется до подтверждения retirement. Проверены TLS с валидацией
сертификата, нулевые retained sockets/worker slots и очистка owned драйвера.

`NativeCoreConnector` добавляет локальные named pipes с DACL, удерживаемым
handle конкретного core PID, случайной capability, epoch, ограниченным
nonce/replay-окном и callback авторизации точного app/domain/address/TCP443.
16 постоянных pipe instances исключают окно потери имени между потоками.
Заголовок JSON ограничен 4096 байт; data frames — 16384 байт; нулевой data frame
означает EOF только в одном направлении. Это сохраняет полный ответ после
TCP half-close. TLS не расшифровывается коннектором.

Ревью исправило продолжение пятисекундного startup timer во время relay и
обрыв второго направления при EOF первого. Self-tests проверили PID/epoch/
capability/scope/replay, bounded framing, настоящий loopback half-close,
обратный ответ после задержки 5.2 с и остановку. Timeout cleanup сохраняет те
же pending workers и pipe handles; DisposeAsync повторно наблюдает их,
статус RetirementPending не подменяется Closed. Неочищенный внешний socket
даёт SocketQuarantined, admission закрывается, порт остаётся в реестре lease.

Физическая проверка через **настоящий pipe + Zapret + socket + TLS** прошла.
Роль core в ней выполнял процесс .NET live-lab, а не sing-box. Ответ сайта —
HTTP 403: это транспортная проверка, не подтверждение доступности сайта.
Точный бинарный хеш и результаты сохранены в
runtime/zapret-physical-connector-result.json. Этот файл относится именно к
проверенной сборке, не автоматически ко всем последующим правкам.

Go-патчи добавляют отдельный route `zapret`, не переименовывая экспериментальный
`dpi`. Конфигурация pipe принимается только по private stdin с owner/session/
generation; закрытие/карантин очищают capability. Windows client сверяет PID
сервера до отправки capability, снимает startup cancellation после ACK,
поддерживает framed half-close. При отказе Zapret dial возвращает ошибку без
скрытого Direct/VPN fallback. Policy parser требует app + public IPv4 + TCP443;
publication без установленного connector запрещена. Новое ядро собрано,
policy/connector unit tests и go vet прошли: runtime/native-core-lab-build.json.

Настоящий Windows Go connector ↔ pipe ↔ Zapret ↔ TLS на РМ подтверждён
отдельным контрольным HEAD (HTTP 403, не доказательство доступности сайта).
Артефакт: runtime/zapret-physical-go-interop-result.json. Это тот же dial-код,
но лабораторный Go peer, не весь TUN/publisher/selector рабочего клиента.

**Ещё не закрыто:** полноценный TUN/core end-to-end на РМ, stale generation
во время dial/register, производственный WFP permit именно процессу агента,
связь controller/publisher/селектор, внедрение bundle в защищённый установщик
с лицензиями, UDP-стратегии и пользовательский цикл. Production publisher
пока не включает `zapret`; успешные component tests не снимают этот gate.

29 октября, 17:26 МСК: реальная WFP/Zapret interop на физической РМ прошла
с лабораторным dynamic System companion для точного удерживаемого socket
tuple и physical LUID. Без companion та же финальная сборка воспроизводит
Connecting timeout. После hard-kill helper его правило исчезло; отменённый
и повреждённый ACK больше не мешают независимой проверке удаления.
Другой исходный порт остаётся заблокированным, все собственные ресурсы очищены.
Хеши/результаты: runtime/goal2-system-companion-receipt.json.

Ограничения: System-разрешение ограничено защищённым physical-lab helper,
20 s watchdog подходит только для опыта. Требуются штатный dynamic lease
owner, наблюдение за companion, долгие/конкурентные потоки, чужая системная
инъекция и controller crash до включения в production; HTTP403 не доказывает
доступность содержимого. Более ранняя запись «совместная WFP проверка не
пройдена» сохраняет историю исходного отказа, но эта лабораторная группа
теперь подтверждена отдельными артефактами.

Общий agent factory теперь имеет opt-in dynamic socket owner с socket ID/epoch,
продлениями и проверкой kernel-правила. Потеря пути делает ShutdownBoth без
освобождения исходного порта до worker/rule retirement. Helper включён в
KILL_ON_JOB_CLOSE Job до передачи управляющего запроса. Этот протокол пока
advertised исключительно физическому lab fixture, publisher отключён.

Два физических прогона подтвердили повторный HEAD по тому же TLS-потоку через
25 s; финальный Job-вариант: runtime/zapret-continuous-job-result.json.
Убраны абсолютные 12/20 s таймеры для opt-in flow mode, ограниченные пробы
сохраняют прежние сроки. Агент unit tests проверяют malformed/stale heartbeat
и сохранение exclusive port после fail-closed shutdown. Controller hard-kill,
удаление правил, NIC/epoch change и конкурентные потоки пока не приняты.

Уточнение после crash audit: continuous mode теперь использует протокол 2
и размещает agent+System правила ВМЕСТЕ в dynamic session. Persistent agent
permit больше не участвует в этом режиме. Это закрывает обнаруженную возможность
пережить контроллер и освобождение порта. Проверка absence подтверждает оба
правила; старые и новые протоколы не смешиваются.

На финальной сборке пройдены 4 сценария: 25 s same TLS reuse, потеря System
rule, потеря agent rule и controller hard-kill. Для rule loss получены реальные
EOF и SystemLeaseLost; для hard-kill точная пара отсутствовала, а прежний deny
оставался ДО rescue Cleanup. Сводка: runtime/goal2-dynamic-pair-acceptance-receipt.json.
Это заменяет прежнее «controller hard-kill/удаление правил не приняты» в рамках
указанного physical fixture. Полная production-приёмка и остальные gates открыты.
