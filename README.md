# lumora-gameserver

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
./install.ps1 -NeoForgeVersion 21.1.77 -AuthUrl https://auth.example.com
```

Linux:

```bash
./install.sh 21.1.77 https://auth.example.com
```

Скрипт:
1. скачивает установщик NeoForge и ставит сервер в `server/`;
2. скачивает `authlib-injector.jar` (с Lumora Auth, если доступен, иначе с официального зеркала);
3. пишет в `server/user_jvm_args.txt` память и `-javaagent:authlib-injector.jar=<AuthUrl>/api/yggdrasil`;
4. копирует `config/server.properties` (если его ещё нет) и принимает EULA.

Запуск: `server/run.bat` или `server/run.sh` (их создаёт NeoForge, они читают `user_jvm_args.txt`).

Версию NeoForge берите ту же, что указана у сборки в админке Lumora Auth.
Серверные моды — из той же сборки (файлы с пометкой `server`/`both`), кладутся в `server/mods/`.
