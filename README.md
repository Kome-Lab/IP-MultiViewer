# ip-multiviewer

Windows Desktop App を主役にした汎用 IP stream MultiViewer です。AutoStream とは独立したプロジェクトとして扱い、必要な場合だけ webhook / API で任意連携します。

## 構成

- `apps/desktop-windows`: C# / WPF / LibVLCSharp ベースの正式Windows desktop app
- `apps/server`: 設定同期、alert 集約、通知、client heartbeat 用の Go lightweight server
- `apps/agent`: 軽量 reachability probe を行う Go headless monitor
- `packages/contracts`: API / event / alert の wire format
- `packages/analyzer-core`: 検知ロジック
- `packages/notification-core`: Webhook 通知 helper
- `packages/stream-utils`: stream URL validation と credential masking
- `packages/desktop-core`: UIから分離した音声meter scale

## Windows UI

- 通常リリースの実行ファイルはZIP直下の `IPMultiViewer.exe` 1本です。利用者向けのファイル名やVersionInfoにはpreview時代のsuffixを出しません。
- 正式アプリと回帰検証用のUI QA / runtime smokeは、`apps/desktop-windows`配下で一体管理します。検証projectはリリースZIPへ同梱しません。
- 正式版は設定 `local-config.json` とログ `desktop.log` を使います。preview版の `local-config.next.json` が新しい場合は、初回起動時に設定内容を正式ファイルへ自動昇格します。
- 現行UIでは再生中タイルの左右にチャンネル別meterを表示し、ヘッダーで `VU`（RMS、-18 dBFS基準）と `PEAK`（peak dBFS）を切り替えます。`PLAYING`横に小型meterは表示しません。
- SRT/FFmpegのmeterは、表示済みvideo frameのPTSを基準に、同じPTS以下の最新audio levelを選びます。audioだけが映像より先へ進むことを防ぎ、PTS同期済みの立ち上がり・下降を即時に反映します。150msを超えて古いaudio levelは表示せず、PTS欠落時もvideo readerを継続します。`stats_enc_pre`非対応の旧FFmpegは`showinfo`へ自動fallbackします。FFmpegのaudio統計はchannel別RMS/Peakだけへ限定し、metadata処理量を抑えます。
- FFmpeg receiver開始時はlayoutとDPI込みviewportから表示解像度を選びます。基準は`2x2=960x540`、`3x3=640x360`、`4x4=480x270`、広い高DPI画面ではそれぞれ最大`1280x720`、`960x540`、`640x360`です。bicubicで入力の縦横比を保ち、余白は黒で補います。再生中のlayout切替ではSRTを切断せず、次のreceiver開始時に新tierを適用します。
- 現行alert表示は、SRT/FFmpegのWPF映像ではタイル内へ直接描画し、LibVLCのnative surfaceだけMainWindow所有overlay Windowを使います。alertの追加・解消でも監視映像のviewportと再生sessionは変わりません。MainWindowが非アクティブまたは最小化中はnative overlayを必ず隠すため、警告だけが別アプリの上へ浮いたり、MainWindowが勝手にforegroundへ移動したりしません。
- alert更新時のconfig保存はcoalescing付きbackground writerへ渡し、resolved / ignoredのlocal履歴はactive alertを除いて新しい1,000件を保持します。既存configがこの上限を超える初回移行では、同じdirectoryへ`*.history-before-retention.json`を1回だけ退避します。
- SettingsはStreams / Alert profiles / Sync and audioの3タブ、Alert Historyは検索・status/severity filter・KPI・詳細paneを備えます。履歴一覧は最大500行をvirtualizeして表示します。
- ヘッダーの`DECODE / OUTPUT`はdecode backendと表示経路を分けて示します。`GPU · <backend>`はFFmpegのruntime evidenceを検出し、かつframeを画面へ提示した場合だけ表示します。`REQUESTED`や`UNKNOWN`はGPU動作の確認を意味しません。

## Desktop standalone alert behavior

- Main window は監視画面専用です。Settings と Alert History は別 window で開きます。
- MV タイルは映像面積を優先し、常時表示は短い stream 名、状態 badge、上部 alert overlay に絞ります。overlayは映像viewportを再配置しません。
- stream のタイル割当は MV タイル上の `Assign` / `Change` から行います。
- 監視対象は現在の layout で表示されている tile の stream だけです。4x4 から 3x3 へ切り替えて表示外になった stream は receiver と検知対象から外します。
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

# ローカル/対象Windows: 実FFmpegセッションとライブ入力fixtureを含む
./scripts/verify-windows.ps1

# GitHub Actions相当: 通信系を除外し、build/UI/publishを検証
./scripts/verify-windows.ps1 -SkipMediaIntegrationQa -PublishDryRun
```

GitHub Actionsでは`-SkipMediaIntegrationQa`を使い、runnerのネットワーク状態やFFmpeg起動タイミングに左右される実メディア検証を必須ゲートにしません。ActionsにはGo/.NET build、desktop-core self-test、offline WPF/UI smoke、publish/package検証を残します。引数なしのローカル実行では実メディア検証も行いますが、現時点のライブfixtureはSRT中心であり、対応する全入力protocolの動作証明とは扱いません。

検証後の `bin` / `obj` / 一時publish / 古い中間成果物を整理する場合:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/clean-generated.ps1 -IncludeRuntimeCache
```

通常実行ではrepo内の再生成可能な成果物をすべて削除します。ローカルのリリースやQA画像を意図的に残す場合だけ `-KeepReleaseArtifacts` または `-KeepQaEvidence` を明示します。`-IncludeRuntimeCache` はIP MultiViewer終了後に.NET単一EXE展開cacheとQA一時ファイルも削除します。Windows package処理自身の大容量work directoryは成功・失敗にかかわらず自動削除されます。

standard版とGPU版のWindowsリリースZIPをローカル生成する場合:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/package-windows.ps1 -GitTag v2.3.0-rc.1
```

Server / Agent の配布パッケージを作る場合:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/package-server.ps1 -Version v0.0.0-local -Runtime linux-x64,linux-arm64 -OutputDir artifacts/server
```

タグ `v*` または GitHub Actions の手動実行では `.github/workflows/release-server.yml` が `ip-multiviewer_<version>_server_linux_x64.zip` と `ip-multiviewer_<version>_server_linux_arm64.zip`、それぞれの `.sha256` を作成します。パッケージには `ip-multiviewer-server`、`ip-multiviewer-agent`、`systemd/*.service.example`、`deploy/*.env.example`、`.env.example`、`docker-compose.prod.yml` が含まれます。

Windows desktop release は `.github/workflows/release-windows.yml` が既存のGit tagを唯一のversion sourceとして、通常版 `IPMultiViewer-<tag>-windows-x64.zip` と GPU 描画向け `IPMultiViewer-<tag>-windows-x64-gpu.zip` を作成します。たとえば`v2.3.0-rc.1`は、画面とProductVersionでは`2.3.0-rc.1`、Windowsの数値FileVersionでは`2.3.0.0`になります。手動workflowも既存tagをcheckoutし、tagとHEADが一致しなければ失敗します。各ZIPの実行ファイルは直下の `IPMultiViewer.exe` 1本で、`README-WINDOWS.md` と `VERSION.txt` を同梱します。package処理はEXEの製品名・versionとZIP内の許可ファイルを検査し、SHA-256ファイルも生成します。GPU版は`EnableGpuRendering=true`でhardware decodeを要求し、RTSP/RTMP/HTTP/HLSなどの非SRT streamはLibVLCの埋め込みD3D9/DXVA2経路、SRT streamはFFmpeg/D3D11VA 30fps playbackを使います。ただしbuild設定や要求だけではGPU動作を保証しません。`DECODE / OUTPUT`で、`GPU · <backend>`というruntime確認済み表示と`WPF <width>x<height>`などの出力経路を確認してください。黒味・フリーズ検知は低解像度FFmpeg sidecar monitorまたはplayback snapshotで行います。

## Windows icon and signing

- App icon は `apps/desktop-windows/Assets/AppIcon.ico` です。Window icon にも同じ icon を指定し、タスクバー表示もこの icon に揃えます。
- Assembly metadata は `Jumala9163, Kome-Lab` を authors / copyright に設定しています。
- Authenticode 署名には実証明書が必要です。開発用 self-signed 証明書は `apps/desktop-windows/signing/create-dev-cert.ps1` で作成できます。
- 発行後の exe 署名は `apps/desktop-windows/signing/sign-file.ps1`、または `dotnet publish -p:SignOutput=true -p:SignToolPath=<signtool.exe>` を使います。
- PFX、秘密鍵、証明書パスワードは repository にコミットしません。

## Security

- stream URL credentials、webhook URL、client token は raw のまま UI / API response / log に出しません。
- Server は heavy media decoding、video compositing、大量 transcoding を実装しません。
- 実 secret は repository にコミットしません。
