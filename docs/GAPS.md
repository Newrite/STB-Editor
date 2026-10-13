# Чего не хватает до CK и xEdit

Сверка от 2026-10-13 (STB Editor 0.6.1). Источники: меню и все 426 окон `CreationKit.exe` (Skyrim SE 1.5.97,
вытащены из ресурсов exe), меню, фильтры и настройки `SSEEdit 4.1.5f` (формы Delphi из exe), документация
xEdit (tes5edit.github.io/docs). Что уже есть — в `README.md`; здесь только то, чего нет.

Сделанное помечено ✅ с версией.

Важность: **★★★** без этого CK не заменить, **★★** часто нужно, **★** редко или есть внешние программы.
Объём: **S** — день-два, **M** — неделя, **L** — больше.

## 1. Базовые операции с записями

| Что | Где есть | Важность | Объём |
|---|---|---|---|
| ✅ 0.7.0 **Новая запись любого типа** («+ Новая» в Object Window; квесты и диалоги — со своими редакторами) | CK: Object Window → New; xEdit: Copy as new record | ★★★ | S |
| ✅ 0.7.0 **Удалить запись / убрать override из своего плагина** | CK: Delete; xEdit: Remove | ★★★ | S |
| ✅ 0.8.0 **Use Info / Referenced By** — кто ссылается на запись (рефы, списки, скрипты, условия) | CK: Use Info; xEdit: вкладка Referenced By | ★★★ | M |
| ✅ 0.9.0 **Редактор условий (CTDA)**: выбор функции, параметры, Run On, OR/AND, копировать/вставить условия | CK: Condition Item, Select Function Parameters, Copy/Paste Conditions | ★★★ | M |
| ✅ Search & Replace: 0.8.0 заменить базовый объект у рефов; 0.19.0 текст в записях, Find Text по всем строкам | CK: Ctrl+F, Find Text | ★★ | S–M |
| ✅ 0.8.0 Copy as override в активный файл (as new record = Duplicate); deep copy, перенос между плагинами — нет | xEdit | ★★ | S |
| ✅ 0.18.0 (поля верхнего уровня) Правка прямо в сравнении: перетащить значение из колонки в колонку, «Copy to selected records» | xEdit | ★★ | M |
| ✅ 0.8.0 Одно поле сразу у нескольких записей («Править вместе»); таблицы оружия/брони/боеприпасов и др. | xEdit | ★★ | M |
| ✅ 0.7.0 Фильтр Object Window: только записи плагина X / активного файла / только конфликты | xEdit: Apply Filter, by conflict status | ★★ | S |
| ✅ 0.25.0 (Use Report алиаса; ссылки из условий на записи находит Use Info с 0.8.0) Use Report для алиасов квеста, «Find use in conditions» | CK | ★ | S |

## 2. Квесты, диалоги, скрипты

| Что | Где есть | Важность | Объём |
|---|---|---|---|
| ✅ 0.10.0 **Квесты**: Quest Data, стадии (лог, флаги), цели (Objectives, цели на алиасы), **алиасы** (Reference / Location Alias: заполнение, условия, пакеты, inventory). Нет: скрипты алиасов, фрагменты (п. «Papyrus»), Quest Stages/Objectives export | CK: Quest, Reference Alias, Location Alias | ★★★ | L |
| ✅ 0.12.0 (темы, ветки, реплики, ответы, условия, фрагменты; нет — граф Dialogue Views, удаление реплик, порядок перетаскиванием) **Диалоги**: темы (DIAL), ветки (DLBR), реплики (INFO), ответы с эмоциями, Shared Info, Player Dialogue, Dialogue Views (граф) | CK: Topic, Info, Edit Response, Branch, Dialogue Connections | ★★★ | L |
| ✅ 0.11.0 / 0.12.0 / 0.24.0 / 0.27.0 (компиляция, новые скрипты; фрагменты стадий квеста, реплик, пакетов, сцен, перков) **Компиляция Papyrus и фрагменты** (стадий квеста, реплик, пакетов, сцен, перков): без этого квест или диалог со скриптом не сделать. Компилятор уже лежит в `C:\Skyrim\Papyrus Compiler` | CK: Papyrus Manager, Compile, Edit Papyrus Script | ★★★ | M |
| ✅ 0.11.0 **SEQ-файл** для квестов «Start Game Enabled» с диалогами (без него диалоги не работают) | xEdit: Create SEQ File | ★★ | S |
| ✅ 0.23.0 / 0.24.0 (таймлайн, актёры, фазы, действия, тема реплики с репликами, фрагменты) Сцены (SCEN): фазы, актёры, действия (Dialogue / Package / Timer) на таймлайне | CK: Scene Data, Phase, Dialogue Scene Action | ★★ | L |
| ✅ 0.22.0 (шаблон и его входы, расписание, цели, дерево процедур — просмотр и поля веток, 0.24.0 фрагменты; добавлять ветки в дерево — нет) AI-пакеты: шаблон, дерево процедур, расписание, цели | CK: Package, Package Target, Choose Procedure | ★★ | M |
| ✅ 0.36.0 (дерево и переход к формам; перетаскивание узлов — нет, порядок правится полями формы) Story Manager: дерево веток и событий (SMBN / SMQN / SMEN) | CK: Story Manager | ★ | M |
| Голос и LIP: список нужных звуковых файлов по актёрам, генерация .lip, Export Dialogue | CK: Quest Voice Assets, Export Dialogue | ★ | M |

## 3. Персонажи

| Что | Где есть | Важность | Объём |
|---|---|---|---|
| ✅ 0.37.0 (экспорт головы и тинта как у CK, сверено с его файлами; ползунки лица — следующая строка) **FaceGen**: экспорт лица NPC (меш + тинт), иначе «тёмное лицо» после правок внешности | CK: Ctrl+F4 | ★★★ | L |
| Лицо: ползунки морфов, части головы, слои тинта с превью | CK: Actor → Character Gen Parts / Face | ★★ | L |
| Дерево перков (AVIF) графически | CK: Actor Values; xEdit-скрипт PerkTreeUI | ★ | M |
| Дерево idle-анимаций (IDLE) | CK: Animations | ★ | M |
| ✅ 0.31.0 (тинт тела: проверка и исправление; Filtered Dialogue, Export Relationship Data — нет) Update NPC Body Tint Colors, Filtered Dialogue, Export Relationship Data | CK | ★ | S |

## 4. Мир

| Что | Где есть | Важность | Объём |
|---|---|---|---|
| ✅ 0.13.0 **Свойства существующей ячейки**: освещение, вода, акустика, музыка, encounter zone, локация, регионы, владелец, флаги | CK: Cell | ★★★ | M |
| ✅ 0.7.0 **Копировать / вырезать / вставить рефы** (в т.ч. в другую ячейку), Paste in Place | CK: Ctrl+C / X / V, Ctrl+Shift+V | ★★★ | S |
| ✅ 0.13.0 Свойства мира: родитель, климат, вода, карта, LOD, флаги | CK: World Spaces | ★★ | M |
| ✅ 0.21.0 (показ с маркерами; правка размера и вида — «Ещё свойства» рефа; Validate room/portal — нет) Примитивы: триггер-боксы, активаторы-зоны, room bounds и порталы, multibound, occlusion planes | CK: Primitives, Validate room/portal | ★★ | M |
| ✅ 0.8.0 Reference Batch Action: поменять что-то у всех выделенных рефов; замена базы | CK | ★★ | S |
| ✅ 0.38.0 (BepuPhysics, тела — выпуклые оболочки мешей, опора — меши и ландшафт; коллизия Havok из NIF — нет) Run Havok Sim — «уронить» clutter физикой, а не только F (на землю) | CK: Alt+H | ★★ | M |
| ✅ 0.9.0 Остальные свойства рефа: activate parents, emittance, уровень лвл-актёра, map marker, location ref type, persist location, encounter zone рефа | CK: Reference | ★★ | M |
| ✅ 0.20.0 (Show/Hide по типам, Current Cell Only; радиус света был; коллизия и изометрия — нет) Show/Hide по типам, Current Cell Only, световые и звуковые маркеры, радиус света, коллизия (F4), изометрия | CK: View | ★ | S |
| Регионы (REGN): области на карте, генерация объектов, травы, погоды и звуков | CK: Region Editor | ★ | L |
| ✅ 0.34.0 Object Palette — «кисть» объектов со случайным поворотом и масштабом | CK: Object Palette | ★ | M |
| ✅ 0.35.0 (слои редактора: скрыть / заморозить, хранятся вне плагина) Слои (Layers): группы рефов, скрыть/заморозить слой | CK: Layers | ★ | M |
| ✅ 0.32.0 / 0.33.0 (карта высот загруженных ячеек: экспорт / импорт .r16; кисть цвета вершин) Ландшафт: vertex color, импорт/экспорт карты высот | CK: Heightmap Editor | ★ | M |
| Предпросмотр погоды и времени суток в Render Window | CK: Bright Light / Sky | ★ | M |
| Локальные карты, LOD (террейн / объекты) | CK: Create Local Maps, World LOD; xLODGen / DynDoLOD | ★ (внешние программы) | L |

## 5. Навмеш

| Что | Где есть | Важность | Объём |
|---|---|---|---|
| ✅ 0.14.0 **Finalize** (NAVI, связи между ячейками, ребра у дверей) — без него NPC не проходят через двери и границы | CK: Finalize Cell / WorldSpace | ★★★ | M |
| Автогенерация (Recast / Havok), Check NavMeshes, cover edges, pathing tests | CK: NavMesh → Generation, Check, Find Cover Edges | ★★ | L |
| ✅ 0.28.0 (вынос в отдельный навмеш, выбор по номеру; флаги треугольников — с 0.4) Move Selection to Separate NavMesh, Select Triangle by Index, preferred/non-connector вершины | CK | ★ | S |

## 6. Файлы и проверки

| Что | Где есть | Важность | Объём |
|---|---|---|---|
| ✅ 0.15.0 **Check for Errors** (несуществующие ссылки, неверные данные), циклические leveled list | xEdit; CK: Validate Loaded Data | ★★ | M |
| ✅ 0.15.0 (удалённые навмеши — нет) **Очистка**: ITM (Identical to Master), UDR (удалённые рефы → отключить), удалённые навмеши — как Quick Auto Clean | xEdit | ★★ | M |
| ✅ 0.7.0 Мастера: Add / Clean Masters (Sort — нет) | xEdit | ★★ | S |
| ✅ 0.17.0 (Compact + light; смена одного FormID — нет) Change FormID, Renumber, **Compact FormIDs for ESL**, Convert to Light Master | CK + xEdit | ★★ | M |
| ✅ 0.16.0 (упаковка; список используемых ассетов и проверка отсутствующих — нет) Упаковка в **BSA** (Create Archive), список ассетов плагина, проверка отсутствующих мешей и текстур | CK: Create Archive, Test Models; xEdit: Assets manager | ★★ | M |
| ✅ 0.29.0 (сравнение и список полей; delta-патч — нет) Compare to: сравнить с другой версией того же плагина, delta-патч | xEdit | ★ | M |
| ✅ 0.26.0 (экспорт / импорт текстов в JSON; запись .strings у локализованных плагинов — нет) Строки локализации (.strings): правка, экспорт / импорт для перевода | xEdit: Localization; CK: Export | ★ | M |
| ✅ 0.30.0 (левел-листы предметов, NPC, заклинаний; контейнеры, инвентарь NPC и прочее — нет) Merged Patch (слияние leveled lists и т.п.) | xEdit | ★ | M |
| Скрипты пользователя (у xEdit ~190 .pas в `Edit Scripts`) — свой язык или C#-скрипты | xEdit: Apply Script | ★ | L |
| ModGroups, экспорт данных (имена, книги, GMST, сообщения) | xEdit / CK: Export | ★ | S |

## Предлагаемый порядок

1. ✅ 0.7.0 **Мелкие ежедневные операции**: новая запись, удаление записи и override, копирование и вставка рефов,
   фильтр «только плагин X / только конфликты», Add/Clean Masters.
2. ✅ 0.8.0 **Use Info / Referenced By**: нужен везде, в том числе перед удалением записи.
3. ✅ 0.9.0 **Редактор условий** (M): нужен квестам, диалогам, перкам, пакетам, эффектам.
4. ✅ 0.10.0 **Квесты** (L): стадии, цели, алиасы.
5. ✅ 0.11.0 **Papyrus: компиляция и фрагменты** (M), **SEQ** (S). Фрагменты реплик — вместе с диалогами.
6. ✅ 0.12.0 **Диалоги** (L): темы, ветки, реплики, ответы.
7. ✅ 0.13.0 / 0.14.0 **Свойства ячейки и мира** (M), **Finalize навмеша** (M).
8. **FaceGen** (L), очистка ITM/UDR и Check for Errors (M), упаковка BSA (M).
9. Остальное по запросам.
