/** @file Board.cs
 * @brief SQLite永続化、編集リース、順序制御およびCSV出力。
 */
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace InfoBoard {
  /** @brief カードの編集可能な内容。 */
  public class Content {
    public string Title { get; set; }
    public string Owner { get; set; }
    public string Body { get; set; }
    public string Color { get; set; }
  }

  /** @brief 公開されるカードの保存済み状態。 */
  public sealed class Card : Content {
    public string Id { get; set; }
    public long Position { get; set; }
    public bool Archived { get; set; }
    public long Version { get; set; }
    public string CreatedAt { get; set; }
    public string UpdatedAt { get; set; }
    public bool HasPrevious { get; set; }
    public bool Editing { get; set; }
  }

  /** @brief ボードの一貫したスナップショット。 */
  public sealed class Snapshot {
    public List<Card> Cards { get; set; }
    public long OrderVersion { get; set; }
    public bool Sorting { get; set; }
  }

  /** @brief 変更APIへの入力。 */
  public sealed class Request {
    public string Action { get; set; }
    public string Id { get; set; }
    public string Token { get; set; }
    public long Version { get; set; }
    public Content Content { get; set; }
    public string[] Order { get; set; }
  }

  /** @brief 利用者に通知できる業務エラー。 */
  public sealed class BoardError : Exception {
    public int Status { get; private set; }
    /** @brief エラーを作成する。
     * @param [in] status HTTPステータス
     * @param [in] message 利用者向け説明
     */
    public BoardError(int status, string message) : base(message) { Status = status; }
  }

  /** @brief すべての更新をSQLiteの書き込みトランザクションで直列化する。 */
  public sealed class BoardStore {
    private const int LEASE_SECONDS = 60;
    private const string ORDER_KEY = "order";
    private const string CARD_COLUMNS = "id,title,owner,body,color,position,archived,version,created_at,updated_at,previous";
    private readonly string connectionString;
    private readonly Func<long> clock;
    private static readonly HashSet<string> COLORS = new HashSet<string> { "default", "gray", "red", "orange", "yellow", "green", "blue", "purple" };

    /** @brief 保存先を初期化する。
     * @param [in] path DBファイルの絶対パス
     * @param [in] now テスト用時計。nullならUTC時刻
     */
    public BoardStore(string path, Func<long> now = null) {
      Directory.CreateDirectory(Path.GetDirectoryName(path));
      connectionString = new SQLiteConnectionStringBuilder { DataSource = path, Version = 3, Pooling = false, DefaultTimeout = 5 }.ConnectionString;
      clock = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
      InTransaction(true, db => {
        Execute(db, @"CREATE TABLE IF NOT EXISTS cards(id TEXT PRIMARY KEY,title TEXT NOT NULL,owner TEXT NOT NULL,body TEXT NOT NULL,color TEXT NOT NULL,position INTEGER NOT NULL,archived INTEGER NOT NULL DEFAULT 0,version INTEGER NOT NULL DEFAULT 1,created_at TEXT NOT NULL,updated_at TEXT NOT NULL,previous TEXT);
CREATE TABLE IF NOT EXISTS leases(resource TEXT PRIMARY KEY,token TEXT NOT NULL,expires INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS metadata(id INTEGER PRIMARY KEY CHECK(id=1),order_version INTEGER NOT NULL);
INSERT OR IGNORE INTO metadata(id,order_version) VALUES(1,0);");
        return 0;
      });
    }

    /** @brief トランザクション内で操作し、失敗時はロールバックする。
     * @param [in] write 書き込みトランザクションか
     * @param [in] operation DB操作
     * @return 操作結果
     */
    private T InTransaction<T>(bool write, Func<SQLiteConnection, T> operation) {
      using (var db = new SQLiteConnection(connectionString)) {
        db.Open();
        Execute(db, "PRAGMA busy_timeout=5000");
        Execute(db, write ? "BEGIN IMMEDIATE" : "BEGIN");
        try {
          var result = operation(db);
          Execute(db, "COMMIT");
          return result;
        } catch {
          try { Execute(db, "ROLLBACK"); } catch (SQLiteException) { /* 元の例外を優先する。 */ }
          throw;
        }
      }
    }

    /** @brief パラメーター化したSQLコマンドを構成する。
     * @param [in] db 接続
     * @param [in] sql SQL文
     * @param [in] args 名前と値の組
     * @return SQLコマンド
     */
    private static SQLiteCommand Sql(SQLiteConnection db, string sql, params object[] args) {
      var command = db.CreateCommand();
      command.CommandText = sql;
      for (int i = 0; i < args.Length; i += 2) {
        command.Parameters.AddWithValue((string)args[i], args[i + 1] ?? DBNull.Value);
      }
      return command;
    }

    /** @brief SQLを実行する。
     * @param [in] db 接続
     * @param [in] sql SQL文
     * @param [in] args パラメーター
     * @return 変更行数
     */
    private static int Execute(SQLiteConnection db, string sql, params object[] args) {
      using (var command = Sql(db, sql, args)) return command.ExecuteNonQuery();
    }

    /** @brief 単一の整数を取得する。
     * @param [in] db 接続
     * @param [in] sql SQL文
     * @param [in] args パラメーター
     * @return 整数値
     */
    private static long Scalar(SQLiteConnection db, string sql, params object[] args) {
      using (var command = Sql(db, sql, args)) return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /** @brief カード内容を検証する。
     * @param [in] content 入力内容
     */
    private static void Validate(Content content) {
      if (content == null || string.IsNullOrWhiteSpace(content.Title) || content.Title.Length > 200 || content.Owner == null || content.Owner.Length > 100 || content.Body == null || content.Body.Length > 100000 || !COLORS.Contains(content.Color)) {
        throw new BoardError(400, "タイトル・オーナー・本文・背景色を確認してください。");
      }
    }

    /** @brief UTC時刻をISO 8601形式で取得する。
     * @param [in] now Unix時刻（秒）
     * @return 日時文字列
     */
    private static string Timestamp(long now) => DateTimeOffset.FromUnixTimeSeconds(now).UtcDateTime.ToString("o", CultureInfo.InvariantCulture);

    /** @brief 推測困難なリーストークンを発行する。
     * @return トークン
     */
    private static string NewToken() {
      var bytes = new byte[32];
      using (var rng = RandomNumberGenerator.Create()) { rng.GetBytes(bytes); }
      return Convert.ToBase64String(bytes);
    }

    /** @brief 有効な編集リースの有無を取得する。
     * @param [in] db 接続
     * @param [in] key リース対象
     * @param [in] now 現在時刻
     * @retval true リースが存在する
     * @retval false リースが存在しない
     */
    private static bool Locked(SQLiteConnection db, string key, long now) => Scalar(db, "SELECT count(*) FROM leases WHERE resource=@key AND expires>@now", "@key", key, "@now", now) != 0;

    /** @brief リースが存在しないことを確認する。
     * @param [in] db 接続
     * @param [in] key リース対象
     * @param [in] now 現在時刻
     */
    private static void RequireUnlocked(SQLiteConnection db, string key, long now) {
      if (Locked(db, key, now)) throw new BoardError(409, "他の利用者が操作中です。");
    }

    /** @brief リースの所有権と期限を確認する。
     * @param [in] db 接続
     * @param [in] key リース対象
     * @param [in] token リーストークン
     * @param [in] now 現在時刻
     */
    private static void RequireLease(SQLiteConnection db, string key, string token, long now) {
      if (string.IsNullOrEmpty(token) || Scalar(db, "SELECT count(*) FROM leases WHERE resource=@key AND token=@token AND expires>@now", "@key", key, "@token", token, "@now", now) != 1) {
        throw new BoardError(409, "操作権限の有効期限が切れました。入力を控えてから開き直してください。");
      }
    }

    /** @brief リースを発行する。
     * @param [in] db 接続
     * @param [in] key リース対象
     * @param [in] now 現在時刻
     * @return トークン
     */
    private static string Acquire(SQLiteConnection db, string key, long now) {
      RequireUnlocked(db, key, now);
      string token = NewToken();
      Execute(db, "INSERT OR REPLACE INTO leases(resource,token,expires) VALUES(@key,@token,@expires)", "@key", key, "@token", token, "@expires", now + LEASE_SECONDS);
      return token;
    }

    /** @brief カードのDB行を読み取る。
     * @param [in] row レコード
     * @return カード
     */
    private static Card ReadCard(SQLiteDataReader row) => new Card { Id = row.GetString(0), Title = row.GetString(1), Owner = row.GetString(2), Body = row.GetString(3), Color = row.GetString(4), Position = row.GetInt64(5), Archived = row.GetInt64(6) != 0, Version = row.GetInt64(7), CreatedAt = row.GetString(8), UpdatedAt = row.GetString(9), HasPrevious = !row.IsDBNull(10) };

    /** @brief IDに対応するカードを取得する。
     * @param [in] db 接続
     * @param [in] id カードID
     * @return 保存済みカード
     */
    private static Card GetCard(SQLiteConnection db, string id) {
      using (var command = Sql(db, "SELECT " + CARD_COLUMNS + " FROM cards WHERE id=@id", "@id", id))
      using (var row = command.ExecuteReader()) {
        if (!row.Read()) throw new BoardError(404, "カードが見つかりません。");
        return ReadCard(row);
      }
    }

    /** @brief 編集可能な内容のみを履歴に保存する。
     * @param [in] card カード
     * @return JSON化した直前の内容
     */
    private static string Previous(Card card) => JsonConvert.SerializeObject(new Content { Title = card.Title, Owner = card.Owner, Body = card.Body, Color = card.Color });

    /** @brief 内容を変更し、直前の状態を保持する。
     * @param [in] db 接続
     * @param [in] old 現在のカード
     * @param [in] content 新しい内容
     * @param [in] now 現在時刻
     */
    private static void Save(SQLiteConnection db, Card old, Content content, long now) {
      Validate(content);
      if (old.Title == content.Title && old.Owner == content.Owner && old.Body == content.Body && old.Color == content.Color) return;
      Execute(db, "UPDATE cards SET title=@title,owner=@owner,body=@body,color=@color,previous=@previous,version=version+1,updated_at=@now WHERE id=@id", "@title", content.Title, "@owner", content.Owner, "@body", content.Body, "@color", content.Color, "@previous", Previous(old), "@now", Timestamp(now), "@id", old.Id);
    }

    /** @brief 表示順のバージョンを取得する。
     * @param [in] db 接続
     * @return 順序バージョン
     */
    private static long OrderVersion(SQLiteConnection db) => Scalar(db, "SELECT order_version FROM metadata WHERE id=1");

    /** @brief 一覧の構造変更を記録する。
     * @param [in] db 接続
     */
    private static void BumpOrder(SQLiteConnection db) => Execute(db, "UPDATE metadata SET order_version=order_version+1 WHERE id=1");

    /** @brief 通常一覧の末尾の位置を取得する。
     * @param [in] db 接続
     * @return 追加位置
     */
    private static long NextPosition(SQLiteConnection db) => Scalar(db, "SELECT coalesce(max(position),0)+1 FROM cards WHERE archived=0");

    /** @brief 保存済みカードとリース状態を一貫して取得する。
     * @return スナップショット
     */
    public Snapshot Read() => InTransaction(false, db => {
      long now = clock();
      var cards = new List<Card>();
      using (var command = Sql(db, "SELECT " + CARD_COLUMNS + " FROM cards ORDER BY archived,position,id"))
      using (var row = command.ExecuteReader()) {
        while (row.Read()) { cards.Add(ReadCard(row)); }
      }
      var active = new HashSet<string>();
      using (var command = Sql(db, "SELECT resource FROM leases WHERE expires>@now", "@now", now))
      using (var row = command.ExecuteReader()) {
        while (row.Read()) { active.Add(row.GetString(0)); }
      }
      foreach (var card in cards) { card.Editing = active.Contains(card.Id); }
      return new Snapshot { Cards = cards, OrderVersion = OrderVersion(db), Sorting = active.Contains(ORDER_KEY) };
    });

    /** @brief 変更要求を検証し原子的に実行する。
     * @param [in] request 変更要求
     * @return 操作結果
     */
    public object Apply(Request request) {
      if (request == null || string.IsNullOrEmpty(request.Action)) throw new BoardError(400, "操作が指定されていません。");
      return InTransaction<object>(true, db => {
        long now = clock();
        Execute(db, "DELETE FROM leases WHERE expires<=@now", "@now", now);
        string key = request.Id;
        switch (request.Action) {
          case "sortAcquire":
            return new { token = Acquire(db, ORDER_KEY, now), version = OrderVersion(db), order = ActiveOrder(db) };
          case "sortHeartbeat":
          case "heartbeat":
            key = request.Action == "sortHeartbeat" ? ORDER_KEY : key;
            RequireLease(db, key, request.Token, now);
            Execute(db, "UPDATE leases SET expires=@expires WHERE resource=@key", "@expires", now + LEASE_SECONDS, "@key", key);
            break;
          case "sortRelease":
          case "release":
            key = request.Action == "sortRelease" ? ORDER_KEY : key;
            Execute(db, "DELETE FROM leases WHERE resource=@key AND token=@token", "@key", key, "@token", request.Token);
            break;
          case "sortSave":
            RequireLease(db, ORDER_KEY, request.Token, now);
            var current = ActiveOrder(db);
            if (request.Version != OrderVersion(db) || request.Order == null || request.Order.Length != current.Length || !new HashSet<string>(current).SetEquals(request.Order)) {
              throw new BoardError(409, "一覧が変更されました。並べ替えをやり直してください。");
            }
            for (int i = 0; i < request.Order.Length; ++i) {
              Execute(db, "UPDATE cards SET position=@position WHERE id=@id AND archived=0", "@position", i + 1, "@id", request.Order[i]);
            }
            BumpOrder(db);
            Execute(db, "DELETE FROM leases WHERE resource=@key", "@key", ORDER_KEY);
            break;
          case "create":
            RequireUnlocked(db, ORDER_KEY, now);
            Validate(request.Content);
            var value = request.Content;
            Execute(db, "INSERT INTO cards(id,title,owner,body,color,position,archived,version,created_at,updated_at) VALUES(@id,@title,@owner,@body,@color,@position,0,1,@now,@now)", "@id", Guid.NewGuid().ToString("N"), "@title", value.Title, "@owner", value.Owner, "@body", value.Body, "@color", value.Color, "@position", NextPosition(db), "@now", Timestamp(now));
            BumpOrder(db);
            break;
          case "acquire":
            var card = GetCard(db, key);
            if (card.Archived) throw new BoardError(409, "アーカイブ済みのカードです。");
            return new { token = Acquire(db, key, now), card };
          case "save":
            RequireLease(db, key, request.Token, now);
            var original = GetCard(db, key);
            RequireVersion(original, request.Version);
            if (original.Archived) throw new BoardError(409, "アーカイブ済みのカードです。");
            Save(db, original, request.Content, now);
            Execute(db, "DELETE FROM leases WHERE resource=@key", "@key", key);
            break;
          case "archive":
          case "unarchive":
            RequireUnlocked(db, ORDER_KEY, now);
            RequireUnlocked(db, key, now);
            var target = GetCard(db, key);
            RequireVersion(target, request.Version);
            bool archive = request.Action == "archive";
            if (target.Archived == archive) throw new BoardError(409, "既に状態が変更されています。");
            Execute(db, "UPDATE cards SET archived=@archived,position=@position,version=version+1,updated_at=@now WHERE id=@id", "@archived", archive ? 1 : 0, "@position", archive ? target.Position : NextPosition(db), "@now", Timestamp(now), "@id", key);
            BumpOrder(db);
            break;
          case "delete":
            RequireUnlocked(db, ORDER_KEY, now);
            RequireUnlocked(db, key, now);
            var deleted = GetCard(db, key);
            RequireVersion(deleted, request.Version);
            if (!deleted.Archived) throw new BoardError(409, "完全削除はアーカイブ済みカードのみ可能です。");
            Execute(db, "DELETE FROM cards WHERE id=@id", "@id", key);
            Execute(db, "DELETE FROM leases WHERE resource=@key", "@key", key);
            BumpOrder(db);
            break;
          case "restore":
            RequireUnlocked(db, key, now);
            var latest = GetCard(db, key);
            RequireVersion(latest, request.Version);
            if (latest.Archived || !latest.HasPrevious) throw new BoardError(409, "復元できる状態がありません。");
            string previous;
            using (var command = Sql(db, "SELECT previous FROM cards WHERE id=@id", "@id", key)) { previous = (string)command.ExecuteScalar(); }
            Save(db, latest, JsonConvert.DeserializeObject<Content>(previous), now);
            break;
          default:
            throw new BoardError(400, "未対応の操作です。");
        }
        return new { ok = true };
      });
    }

    /** @brief 保存された通常カードの順序を取得する。
     * @param [in] db 接続
     * @return ID配列
     */
    private static string[] ActiveOrder(SQLiteConnection db) {
      var ids = new List<string>();
      using (var command = Sql(db, "SELECT id FROM cards WHERE archived=0 ORDER BY position,id"))
      using (var row = command.ExecuteReader()) {
        while (row.Read()) { ids.Add(row.GetString(0)); }
      }
      return ids.ToArray();
    }

    /** @brief 古い画面からの上書きを防ぐ。
     * @param [in] card 最新カード
     * @param [in] version クライアントのバージョン
     */
    private static void RequireVersion(Card card, long version) {
      if (card.Version != version) throw new BoardError(409, "カードが更新されました。最新の状態を確認してください。");
    }

    /** @brief CSVセルの引用と数式インジェクション対策を行う。
     * @param [in] value 元の値
     * @return CSVフィールド
     */
    public static string CsvField(string value) {
      value = value ?? "";
      string trimmed = value.TrimStart();
      if ((trimmed.Length > 0 && "=+-@＝＋－＠".Contains(trimmed[0])) || (value.Length > 0 && "\t\r\n".Contains(value[0]))) {
        value = "'" + value;
      }
      return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    /** @brief 保存済みの全カードをUTF-8 BOM付きCSVに変換する。
     * @return CSVバイト列
     */
    public byte[] Export() {
      var csv = new StringBuilder("ID,表示順,タイトル,オーナー,内容,背景色,状態,作成日時,更新日時\r\n");
      foreach (var card in Read().Cards) {
        var fields = new[] { card.Id, card.Position.ToString(CultureInfo.InvariantCulture), card.Title, card.Owner, card.Body, card.Color, card.Archived ? "アーカイブ" : "通常", card.CreatedAt, card.UpdatedAt };
        csv.Append(string.Join(",", fields.Select(CsvField))).Append("\r\n");
      }
      var utf8 = new UTF8Encoding(true);
      return utf8.GetPreamble().Concat(utf8.GetBytes(csv.ToString())).ToArray();
    }
  }
}
