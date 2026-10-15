# lt_database

A fully working database connector that connects to the seperate database api that is hosted external to save and read persitent data for servers.

Built and managed with the BlackICE Server Panel (Bundles → Projects). The build result is written into `data/ugc/<name>` of the game server. Do not copy a config.toml into subfolders of that bundle folder: REServer refuses bundles that contain nested manifests.

Local development: `dotnet build -c Release` creates `dist/` with `config.toml`, `sv.wasm` and `cl.wasm`.
