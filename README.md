# ip-multiviewer

Windows Desktop App を主役にした汎用 IP stream MultiViewer です。AutoStream とは独立したプロジェクトとして扱い、必要な場合だけ webhook / API で任意連携します。

## 構成

- `apps/desktop-windows`: C# / WPF / LibVLCSharp ベースの Windows desktop app
- `apps/server`: 設定同期、alert 集約、通知、client heartbeat 用の Go lightweight server
- `apps/agent`: 軽量 reachability probe を行う Go headless monitor
- `packages/contracts`: API / event / alert の wire format
- `packages/analyzer-core`: 検知ロジック
- `packages/notification-core`: Webhook 通知 helper
- `packages/stream-utils`: stream URL validation と credential masking

## Desktop standalone alert behavior

- Main window は監視画面専用です。Settings と Alert History は別 window で開きます。
- MV タイルは映像面積を優先し、常時表示は短い stream 名、状態 badge、上部 alert overlay に絞ります。
- stream のタイル割当は MV タイル上の `Assign` / `Change` から行います。
- Playback を開始していない tile は `OK` ではなく `standby / playback stopped` と表示します。
- 手動 Stop、未再生、standalone idle では `video.no_signal` を出しません。
- `video.decode_error` と `video.no_signal` は別 alert です。decode error だけで no-signal timer は進めません。
- `video.black` は ffmpeg 共有解析から平均輝度と暗色 pixel ratio を見て、黒味フレームの初回検知で alert を出します。
- `video.freeze` は ffmpeg 解析 fallback または hidden `VideoLumaMonitor` からフレーム差分、安定フレーム数、持続秒数で判定します。
- `video.no_signal`、`video.black`、`video.freeze`、`video.low_fps`、`audio.silence`、`audio.clipping` は同じ stream 上で同時 active alert として扱えます。
- Alert 発生中の MV タイルは severity に応じて赤または黄で点滅します。
- VLC の `TakeSnapshot` fallback は使いません。PiP サイズ画像の一瞬表示や playback surface 干渉を避けるためです。
- 解析 monitor から snapshot を取得できない場合は、推測で黒味・フリーズ alert を出しません。`ffmpeg` が PATH にある場合は ffmpeg 解析を優先します。
- Alert profile で黒味、フリーズ、無信号、低 FPS、無音、クリッピングの閾値を調整できます。

## Alert sync behavior

- `open` / `acknowledged` / `snoozed` は active alert として扱います。
- `resolved` / `ignored` は terminal state として扱います。
- Desktop は `stream_id + type` 単位で active alert を dedupe し、別 type の alert は潰しません。
- `snoozed_until` は Desktop と Server の両方で保持します。
- Server sync 有効時、Desktop は local unsynced alert を upload した後、`GET /alerts` で Server alert を pull します。
- 同じ alert ID は `updated_at` が新しい方を採用します。

## 再生バックエンド

現時点では WPF / LibVLCSharp を継続します。FFmpeg、GStreamer、Media Foundation などの比較は `ip-multiviewer-docs` repository にまとめています。

## 検証

```powershell
go test ./...
go build ./...
dotnet build apps/desktop-windows/IPMultiViewer.Desktop.csproj
```

single-file 成果物を Desktop に出す場合:

```powershell
powershell -ExecutionPolicy Bypass -File apps/desktop-windows/publish-single-file.ps1
```

Server / Agent の配布パッケージを作る場合:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/package-server.ps1 -Version v0.0.0-local -Runtime linux-x64
```

タグ `v*` または GitHub Actions の手動実行では `.github/workflows/release-server.yml` が `ip-multiviewer_<version>_server_linux_x64.zip` と `ip-multiviewer_<version>_server_linux_arm64.zip`、それぞれの `.sha256` を作成します。パッケージには `ip-multiviewer-server`、`ip-multiviewer-agent`、`systemd/*.service.example`、`deploy/*.env.example`、`.env.example`、`docker-compose.prod.yml` が含まれます。

## Windows icon and signing

- App icon は `apps/desktop-windows/Assets/AppIcon.ico` です。
- Assembly metadata は `Jumala9163, Kome-Lab` を authors / copyright に設定しています。
- Authenticode 署名には実証明書が必要です。開発用 self-signed 証明書は `apps/desktop-windows/signing/create-dev-cert.ps1` で作成できます。
- 発行後の exe 署名は `apps/desktop-windows/signing/sign-file.ps1`、または `dotnet publish -p:SignOutput=true -p:SignToolPath=<signtool.exe>` を使います。
- PFX、秘密鍵、証明書パスワードは repository にコミットしません。

## Security

- stream URL credentials、webhook URL、client token は raw のまま UI / API response / log に出しません。
- Server は heavy media decoding、video compositing、大量 transcoding を実装しません。
- 実 secret は repository にコミットしません。
