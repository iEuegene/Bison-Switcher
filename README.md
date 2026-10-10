# Bison Switcher (BS)

Автоперемикач і автовиправлення розкладки **UA ↔ EN** для Windows: набрав `ghbdsn` — отримав `привіт`.

## Завантажити

**[⬇ Інсталятор BisonSwitcherSetup.exe](https://github.com/iEuegene/Bison-Switcher/releases/latest/download/BisonSwitcherSetup.exe)** — рекомендовано

Встановлюється за кілька секунд без прав адміністратора: ярлик у меню «Пуск», автозапуск з Windows, видалення через «Параметри → Програми».

[Portable-версія BisonSwitcherPortable.exe](https://github.com/iEuegene/Bison-Switcher/releases/latest/download/BisonSwitcherPortable.exe) — один файл без встановлення.

Windows може показати «Windows захистив ваш ПК»: файли не підписані платним сертифікатом. Натисніть **Докладніше → Виконати однаково**.

Програма сама перевіряє оновлення раз на день (вимикається в налаштуваннях → Система) і оновлюється в один клік.

Пропозиції та побажання: [y.buivol@inweb.ua](mailto:y.buivol@inweb.ua)

## Для розробки

- Код, збірка й опис функцій: [`Windows/README-UA.md`](Windows/README-UA.md)
- Зібрати локально: `Windows\build-ua.cmd` (потрібен .NET 8 SDK)
- Нова версія для колег: Releases → **Draft a new release** → тег `v1.2.1` → **Publish**. GitHub сам збере інсталятор і portable-exe та прикріпить до релізу.

Зроблено на основі [Kright](https://github.com/walteryaron/Kright) — Yaron Walter / Walter Technologies LTD, ліцензія MIT
(див. [`LICENSE`](LICENSE)). Оригінальний README: [`README.Kright.md`](README.Kright.md).
Частотні словники — [wordfreq](https://github.com/rspeer/wordfreq), CC BY-SA 4.0.
Папка `Mac/` успадкована від Kright і в Bison Switcher не змінювалась.
