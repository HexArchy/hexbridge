# Релей на VPS

Релей нужен только когда прямой путь `Mac → белый IP хоста` не работает.
Он ничего не расшифровывает: видит 24-байтный заголовок, сводит участников по
`room` и пересылает датаграммы байт в байт.

## Отдельным compose-файлом

Самый быстрый вариант, не трогает существующий стек:

Ниже `vps` — ваш ssh-алиас для сервера.

```bash
rsync -a relay/ vps:/opt/hexbridge-relay/
ssh vps 'cd /opt/hexbridge-relay && docker compose up -d --build'
ssh vps 'ufw allow 47702/udp comment "hexbridge relay"'
```

Проверка:

```bash
ssh vps 'docker logs -f hexbridge-relay'
# hexbridge-relay listening on [::]:47702
# rooms=1 endpoints=2 forwarded=648 dropped=3
```

`endpoints=2` означает, что обе стороны нашлись. `endpoints=1` — вторая сторона
до релея не достучалась.

## Внутри основного стека

Если релей должен жить вместе с остальным `~/Workspace/vps`:

1. Скопируйте `relay/` в репозиторий как `hexbridge/`.
2. Добавьте сервис в `docker-compose.yml`:

```yaml
  hexbridge-relay:
    build: ./hexbridge
    container_name: hexbridge-relay
    restart: unless-stopped
    ports:
      - "47702:47702/udp"
    logging:
      driver: json-file
      options:
        max-size: "10m"
        max-file: "3"
```

3. Добавьте порт в `setup-firewall.sh` рядом с остальными правилами:

```bash
ufw allow 47702/udp comment "hexbridge relay"
```

4. `./deploy.sh` — он сам сделает rsync и `docker compose up -d`.

## Почему не через существующий Hysteria2

Hysteria2 на UDP/443 — это прокси для клиентского трафика, туда голосовой поток
заворачивать незачем: он добавит собственный конгестион-контроль и шифрование
поверх уже зашифрованного потока, то есть только задержку. Релей делает ровно
одно действие — пересылку датаграммы — и стоит около нуля по CPU.

## Ограничения

* 512 комнат, 4 адреса на комнату, TTL адреса 60 секунд.
* 2000 пакетов/с с адреса: голос это 51 пакет/с, но проброшенный геймпад
  добавляет по пакету на каждый HID-репорт.
* Только IPv4/IPv6 UDP, без TLS-обёртки: полезная нагрузка уже под AES-256-GCM.
