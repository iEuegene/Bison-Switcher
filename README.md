# Bison Switcher (BS)

Автоперемикач і автовиправлення розкладки **UA ↔ EN** для Windows: набрав `ghbdsn` — отримав `привіт`.

## Завантажити

**[⬇ BisonSwitcher.exe — остання версія](https://github.com/iEuegene/Bison-Switcher/releases/latest/download/BisonSwitcher.exe)**

Один файл, встановлювати нічого не треба: завантажте й запустіть, програма з'явиться в треї (іконка **BS**).

- Windows може показати «Windows захистив ваш ПК»: файл не підписаний сертифікатом. Натисніть **Докладніше → Виконати однаково**.
- Щоб запускалась із системою: Налаштування → Система → «Запускати разом з Windows».

## Для розробки

- Код, збірка й опис функцій: [`Windows/README-UA.md`](Windows/README-UA.md)
- Зібрати локально: `Windows\build-ua.cmd` (потрібен .NET 8 SDK)
- Нова версія для колег: `git tag v1.2.1 && git push origin v1.2.1` — GitHub сам збере exe і опублікує Release.

Зроблено на основі [Kright](https://github.com/walteryaron/Kright) — Yaron Walter / Walter Technologies LTD, ліцензія MIT
(див. [`LICENSE`](LICENSE)). Оригінальний README: [`README.Kright.md`](README.Kright.md).
Частотні словники — [wordfreq](https://github.com/rspeer/wordfreq), CC BY-SA 4.0.
Папка `Mac/` успадкована від Kright і в Bison Switcher не змінювалась.
