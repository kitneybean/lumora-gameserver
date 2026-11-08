# lumora-gameserver

## Lumora Agent — управление сервером из админки (рекомендуется)

На машину ставится только агент; дальше всё делается на сайте: установка NeoForge и сборки, запуск и остановка,
консоль, игроки, файлы настроек, бэкапы мира и расписания перезапусков.

1. В админке сайта: **Агенты → Добавить агента**. Сайт покажет команду с одноразовым токеном (действует 30 минут).
2. Выполните её на Linux-сервере (нужны systemd, curl, tar):

   ```bash
   curl -fsSL https://mc.example.com/agent/install.sh | sudo bash -s -- --token lmj_...
   ```

   Скрипт скачивает агента из релизов этого репозитория (проверяет контрольную сумму), создаёт пользователя `lumora`,
   папки `/opt/lumora-agent` (программа), `/var/lib/lumora-agent` (Java, серверы, бэкапы), `/etc/lumora-agent/agent.json`
   (ключ агента, права 640) и службу `lumora-agent`.
3. В админке: **Серверы → сервер → Управление** — выберите агента, примите EULA Minecraft, нажмите «Установить», затем «Запустить».

Безопасность:
- агент сам подключается к сайту по WebSocket (`wss://<сайт>/agent/hub`), входящие порты не нужны (кроме порта игры);
- ключ агента хранится на сайте только как SHA-256 и отзывается в админке одним кликом;
- агент выполняет только типизированные команды (запуск, остановка, консоль Minecraft, текстовые файлы в папке сервера,
  бэкапы) — произвольных команд оболочки нет;
- служба работает от непривилегированного пользователя с `ProtectSystem=strict`.

Журнал агента: `journalctl -u lumora-agent -f`. Удаление: `systemctl disable --now lumora-agent`, затем удалить
`/opt/lumora-agent`, `/etc/lumora-agent`, `/var/lib/lumora-agent` (там миры и бэкапы!).

Выпуск новой версии агента: тег `v*` в этом репозитории (GitHub Actions соберёт linux-x64, linux-arm64, win-x64).

## Ручная установка скриптами
Скрипты и конфиги для NeoForge-сервера Minecraft 1.21.1, подключённого к **Lumora Auth**.

Игроки проходят проверку через Lumora Auth (Yggdrasil API) с помощью
[authlib-injector](https://github.com/yushijinhun/authlib-injector), поэтому:

- `online-mode=true` — сервер проверяет сессии, но не у Mojang, а у Lumora Auth;
- скины игроков берутся с Lumora Auth и видны всем без дополнительных модов;
- `enforce-secure-profile=false` — Lumora не выдаёт сертификаты подписи чата.

## Требования

- Java 21 (`java -version`)
- Запущенный Lumora Auth, доступный с этой машины

## Установка

Windows:

```powershell
./install.ps1 -NeoForgeVersion 21.1.77 -AuthUrl https://auth.example.com -AcceptEula
```

Linux:

```bash
ACCEPT_EULA=1 ./install.sh 21.1.77 https://auth.example.com
```

Скрипт:
1. скачивает установщик NeoForge и ставит сервер в `server/`;
2. скачивает `authlib-injector.jar` (с Lumora Auth, если доступен, иначе с официального зеркала);
3. пишет в `server/user_jvm_args.txt` память и `-javaagent:authlib-injector.jar=<AuthUrl>/api/yggdrasil`;
4. копирует `config/server.properties` (если его ещё нет);
5. пишет `eula=true`, только если передан `-AcceptEula` / `ACCEPT_EULA=1` — передавайте его, если прочитали и
   принимаете [EULA Minecraft](https://aka.ms/MinecraftEULA). Без флага сервер не запустится, пока вы сами
   не поставите `eula=true` в `server/eula.txt`.

Запуск: `server/run.bat` или `server/run.sh` (их создаёт NeoForge, они читают `user_jvm_args.txt`).

Версию NeoForge берите ту же, что указана у сборки в админке Lumora Auth.
Серверные моды — из той же сборки (файлы с пометкой `server`/`both`), кладутся в `server/mods/`.

## На сервере (Linux)

1. Java 21 (`sudo apt install openjdk-21-jre-headless`), пользователь `minecraft`, код в `/opt/lumora-gameserver`.
2. `ACCEPT_EULA=1 ./install.sh <версия NeoForge> https://auth.example.com` — адрес сайта Lumora с HTTPS.
3. Моды и конфиги сервера — из той же сборки в админке (файлы «Везде» и «Только сервер») в `server/mods`, `server/config`.
4. Автозапуск: `deploy/lumora-gameserver.service` (инструкция внутри файла). Откройте порт 25565.

В админке у сервера укажите его домен или IP — лаунчер добавит его в список серверов игры.
