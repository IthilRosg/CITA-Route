# Ускоренный порядок приёмки CITA Route

44 сценария остаются полным доказательным реестром. Для работы они объединены в восемь gate-пачек; каждая пачка выпускает один receipt и не повторяет уже подтверждённые строки без изменения кандидата.

| Gate | Сценарии | Результат пачки |
|---|---|---|
| G1 | A01–A05 | source/package/managed CI и единый manifest |
| G2 | B01–B07 | один пользовательский цикл: import → connect → cancel → relaunch → autoconnect |
| G3 | C01–C06 | forced intent + CalibrationId + revision/TTL |
| G4 | D01–D06 | fault matrix и bounded recovery |
| G5 | E01–E05 | очередь, cold start, CPU/RAM/handles |
| G6 | F01–F06 | страницы, CDN, QUIC/UDP, YouTube/Discord |
| G7 | G01–G06 | установка/обновление/удаление и runtime |
| G8 | H01–H03 | candidate receipt, smoke, private preview |

Параллельно можно выполнять G1, G5 и подготовку G7; физические G2–G4 идут последовательно на одной рабочей станции, чтобы не смешивать поколения и маршруты. G6 начинается после стабилизации G2–G4. Один и тот же candidate hash передаётся между gate-пачками; старые receipts остаются baseline и не засчитываются автоматически.

Ускорение достигается параметризацией harness по `AgentSha`, `ManifestSha`, `CoreSha`, helper и transport, bounded retry только для кратких file-lock, сохранением промежуточного evidence и пропуском неизменённых gate-пачек. Число строк реестра не уменьшается, но ручной цикл и повторные сборки убираются.

Текущая точка: canonical private-preview stage `235f7600`, lineage `df7c36b7`; G1 закрыт, installer integration закрыт, D02/D03 закрыты на этом же кандидате. Следующая пачка — G2/G3: file picker/profile switch и forced-policy/CalibrationId.
