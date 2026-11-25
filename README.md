# InfoBoard

既存のIIS上で動作する単ページ型の情報共有ボード。サーバー側のアクセス制御を利用し、Webアプリ独自のログイン機能は持たない。

ヘッダーの鉛筆ボタンで全利用者共通のサイト名を変更できる（初期値InfoBoard）。

## ビルド・配備

1. GitHub Actionsの **Build InfoBoard IIS package** を実行する（mainへのpushでも起動）。
2. 成功した実行のArtifactsから `InfoBoard-IIS` をダウンロードし、内部の `InfoBoard-IIS.zip` を展開する。
3. [IIS配備手順](docs/deployment.md) に従って専用のアプリケーションとして配置する。

Windows開発環境でも .NET SDK 10 / Node.js 24 を使って `pwsh ./build.ps1` で同じZIPを生成できる。これらはビルド専用で、本番サーバーへのインストールは不要。

- [基本仕様・詳細設計](docs/specification.md)
- [配備・バックアップ・受入検証](docs/deployment.md)

## 状態

本文はプレーンテキストで保存・表示し、改行と空白を維持する。既存の本文も変換せずに表示する。完全削除はアーカイブ一覧からのみ可能。Windows上のビルド、SQLiteのスモークテスト、IIS実機の受入確認を通過してから業務利用すること。業務データ、実在サーバー名、認証情報はリポジトリに登録しない。

## 主な依存関係

ASP.NET Web API 2 5.3.0、.NET Framework 4.7.2、System.Data.SQLite.Core 1.0.119、Newtonsoft.Json 13.0.3。依存ライブラリはそれぞれのライセンスに従う。更新時は脆弱性と互換性を確認する。
