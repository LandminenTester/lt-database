# Install

## What you need

* A BlackICE game server you control (`REServer`), able to run UGC bundles.
* A running [lt-database-api](https://github.com/LandminenTester/lt-database-api) in front of your database, reachable over
  HTTPS from the game server (plain HTTP only for loopback and local tests). Its documentation covers installation with Docker,
  Windows or Linux, the database accounts and TLS.
* On the API side an **API key** for this game server: a key id and a secret of at least 32 random characters, allowed for the
  namespaces your bundles use (and ideally restricted to the game server's address).

## 1. Get the bundle

Either download `lt_database-<version>.zip` from the releases page, or build it yourself ([Build](BUILD.md)). It contains

```
config.toml        the manifest
sv.wasm            the server component
vfs/database.example.json
```

There is intentionally no client component.

## 2. Put it on the server

The folder name becomes the bundle name. Unpack into the server's UGC directory:

```
<server>/data/ugc/lt_database/
    config.toml
    sv.wasm
    vfs/
```

### Folder name

Install under `lt_database`; the helper `Db.Bundle` already points at it. If an installation deliberately uses a custom
folder such as `my_database`, set that name once at start-up of every consumer bundle:

```csharp
LtDatabaseClient.Db.Bundle = "my_database";
```

Server and client consoles list bundles by folder name, so use the same name in `start <name>`.

### Allow the API host

Open `config.toml` and replace the example host. The connector may only reach hosts listed here, and nothing else:

```toml
[Networking]
allowed = ["db.example.com"]            # https://db.example.com and http://db.example.com
# allowed = ["https://db.example.com"]  # https only
# allowed = ["http://127.0.0.1:8081"]   # local test
```

Entries are `host`, `host:port`, `scheme://host` or `scheme://host:port`, matched exactly, without wildcards. Redirects are
checked against the same list.

## 3. Configure it

The bundle reads two files from its private `vfs/` folder (`data/ugc/lt_database/vfs/`). That folder is not sent to players.

`vfs/database.json`

```json
{
  "url": "https://db.example.com",
  "keyId": "gameserver",
  "secretFile": "database.secret",
  "allowedNamespaces": ["mygame"]
}
```

`vfs/database.secret`: the key's secret on one line (the same value the API has for `gameserver`).

All options are explained in [Configuration](CONFIGURATION.md). **Protect these files with operating-system permissions**:
anyone who can read the secret can act as that key.

## 4. Start it

Start the bundle from the server console (or the panel's console) *before* the bundles that use it:

```
start lt_database
```

If the server does not know the bundle yet, make sure `config.toml` is written **after** `sv.wasm` when you copy the folder
(a file watcher discovers new folders when the manifest appears), or restart the server. The console's `bundles` command lists
what is known.

To start it automatically, add it to `server.cfg` before the bundles that use it:

```
start lt_database
start mygame
```

## 5. Check it

In the server console:

```
lt-db-health
```

Expected output in the log:

```
[lt_database] ready -> https://db.example.com as 'gameserver'
[lt_database] health: ok status=200  {"status":"ok","provider":"postgres","database":true,"writeBuffer":{...}}
```

This proves the whole chain: the host is allowed, the key and secret are right, the signature of the answer verified, and the
API reaches the database. `lt-db-status` shows the configuration (never the secret). Problems are listed in
[Operations](OPERATIONS.md#troubleshooting).

Optionally run the 38-step functional test, which creates and removes its own namespace `smoke_test` (add it to
`allowedNamespaces` and to the API key first): see [`examples/smoke`](../examples/smoke/README.md).

## 6. Use it

Reference the connector's shared project and compile the helper into your own server bundle's `.csproj`:

```xml
<ProjectReference Include="../lt_database/sh/sh.csproj" />
<Compile Include="../lt_database/import/LtDatabase.Server.cs" Link="LtDatabase.Server.cs" />
```

If your project already has a different project that is also called `sh` (for example another bundle's shared project), the two
assemblies collide. Compile the sources instead:

```xml
<Compile Include="../lt_database/sh/Contracts.cs" Link="LtDatabase/Contracts.cs" />
<Compile Include="../lt_database/sh/Pure/*.cs" Link="LtDatabase/Pure/%(Filename)%(Extension)" />
<Compile Include="../lt_database/import/LtDatabase.Server.cs" Link="LtDatabase.Server.cs" />
```

Then see [Usage](USAGE.md).

## Local test with Docker

To try everything on one machine, run the API against all three engines with the compose file in the API repository:

```bash
git clone https://github.com/LandminenTester/lt-database-api
cd lt-database-api
cp .env.example .env            # set LDB_KEY_MAIN and DB_PASSWORD to long random values
docker compose -f docker-compose.all.yml up -d --build
# PostgreSQL http://127.0.0.1:8081   MySQL http://127.0.0.1:8082   MongoDB http://127.0.0.1:8083
```

Then allow these hosts in `config.toml`:

```toml
[Networking]
allowed = ["http://127.0.0.1:8081", "http://127.0.0.1:8082", "http://127.0.0.1:8083"]
```

and point `vfs/database.json` at one of them (`"url": "http://127.0.0.1:8081"`, key id `gameserver`, the secret from your `.env`).
Plain HTTP to a non-loopback host is refused by the connector unless `"allowInsecureHttp": true` is set. To switch the database,
change `url` and run `lt-db-reload`.
