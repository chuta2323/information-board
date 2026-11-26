<%@ WebHandler Language="C#" Class="InfoBoardHandler" %>
/** @file api.ashx
 * @brief 標準ASP.NETによる情報共有API。外部DLLを使用しない。
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web;
using System.Web.Script.Serialization;

/** @brief APIの利用者向けエラー。 */
public sealed class BoardError : Exception {
  public int Status { get; private set; }
  public BoardError(int status, string message) : base(message) { Status = status; }
}

/** @brief 保存可能なカード内容。 */
public class CardContent {
  public string title { get; set; }
  public string owner { get; set; }
  public string body { get; set; }
  public string color { get; set; }
}

/** @brief カードの永続化レコード。 */
public sealed class Card : CardContent {
  public string id { get; set; }
  public long position { get; set; }
  public bool archived { get; set; }
  public long version { get; set; }
  public string createdAt { get; set; }
  public string updatedAt { get; set; }
  public CardContent previous { get; set; }
}

/** @brief 操作リース。トークンは取得者以外に公開しない。 */
public sealed class Lease {
  public string token { get; set; }
  public long expires { get; set; }
}

/** @brief JSONファイルに保存する状態。 */
public sealed class BoardState {
  public int schemaVersion { get; set; }
  public List<Card> cards { get; set; }
  public Dictionary<string, Lease> leases { get; set; }
  public long orderVersion { get; set; }
  public string siteName { get; set; }
  public long siteNameVersion { get; set; }
}

/** @brief 変更要求。 */
public sealed class BoardRequest {
  public string action { get; set; }
  public string id { get; set; }
  public string token { get; set; }
  public long version { get; set; }
  public CardContent content { get; set; }
  public string[] order { get; set; }
  public string siteName { get; set; }
  public BoardRequest() { version = -1; }
}

/** @brief ファイルロックと原子的置換で状態を管理する。 */
public sealed class BoardStore {
  private const string ORDER_KEY = "order";
  private const int LEASE_SECONDS = 60;
  private const int MAX_JSON_CHARS = 16000000;
  private const long MAX_FILE_BYTES = 64000000;
  private static readonly UTF8Encoding UTF8 = new UTF8Encoding(false, true);
  private static readonly HashSet<string> COLORS = new HashSet<string> { "default", "gray", "red", "orange", "yellow", "green", "blue", "purple" };
  private readonly string directory;
  private readonly Func<long> clock;

  /** @brief データ保存先を設定する。
   * @param [in] directory App_Dataの絶対パス
   * @param [in] clock テスト用時計
   */
  public BoardStore(string directory, Func<long> clock = null) {
    this.directory = directory;
    this.clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
  }

  /** @brief 標準JSONシリアライザを生成する。 */
  public static JavaScriptSerializer Serializer() {
    return new JavaScriptSerializer { MaxJsonLength = MAX_JSON_CHARS, RecursionLimit = 32 };
  }

  /** @brief プロセスをまたぐ排他ロックを取得する。 */
  private FileStream AcquireLock() {
    Directory.CreateDirectory(directory);
    var timer = Stopwatch.StartNew();
    while (true) {
      try {
        return new FileStream(Path.Combine(directory, "board.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
      } catch (IOException error) {
        int code = error.HResult & 0xffff;
        if (code != 32 && code != 33) throw;
        if (timer.ElapsedMilliseconds >= 5000) throw new BoardError(503, "保存処理が混み合っています。再試行してください。");
        Thread.Sleep(50);
      }
    }
  }

  /** @brief 状態を読み取る。破損や旧形式を空のデータで上書きしない。 */
  private BoardState Load() {
    string path = Path.Combine(directory, "board.json");
    if (!File.Exists(path)) {
      if (File.Exists(Path.Combine(directory, "board.previous.json")) || File.Exists(Path.Combine(directory, "board.db"))) {
        throw new BoardError(503, "既存データが見つかりました。管理者が復旧または移行してください。");
      }
      return new BoardState { schemaVersion = 1, cards = new List<Card>(), leases = new Dictionary<string, Lease>(), siteName = "InfoBoard" };
    }
    if (new FileInfo(path).Length > MAX_FILE_BYTES) throw new BoardError(503, "データ容量上限を超えています。管理者へ連絡してください。");
    var state = Serializer().Deserialize<BoardState>(File.ReadAllText(path, UTF8));
    if (state == null || state.schemaVersion != 1 || state.cards == null || state.leases == null || string.IsNullOrWhiteSpace(state.siteName)) {
      throw new InvalidDataException("Invalid InfoBoard state.");
    }
    return state;
  }

  /** @brief 一時ファイルをフラッシュ後に同一ボリューム内で置換する。
   * @param [in] state 保存する状態
   */
  private void Persist(BoardState state) {
    string path = Path.Combine(directory, "board.json");
    string backup = Path.Combine(directory, "board.previous.json");
    string temp = Path.Combine(directory, "board." + Guid.NewGuid().ToString("N") + ".tmp");
    string json = Serializer().Serialize(state);
    byte[] bytes = UTF8.GetBytes(json);
    if (bytes.LongLength > MAX_FILE_BYTES) throw new BoardError(507, "データ容量上限に達しました。");
    try {
      using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) {
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(true);
      }
      if (File.Exists(path)) {
        File.Replace(temp, path, backup);
      } else {
        File.Move(temp, path);
      }
    } finally {
      if (File.Exists(temp)) { File.Delete(temp); }
    }
  }

  /** @brief 状態を排他ロック中に処理する。
   * @param [in] write 書き込み要求か
   * @param [in] operation 状態に対する処理
   * @return 処理結果
   */
  private T Transaction<T>(bool write, Func<BoardState, long, T> operation) {
    using (AcquireLock()) {
      var state = Load();
      long now = clock();
      foreach (string key in state.leases.Where(x => x.Value.expires <= now).Select(x => x.Key).ToArray()) {
        state.leases.Remove(key);
      }
      T result = operation(state, now);
      if (write || !File.Exists(Path.Combine(directory, "board.json"))) { Persist(state); }
      return result;
    }
  }

  /** @brief 日時をUTC ISO 8601形式にする。 */
  private static string Timestamp(long now) { return DateTimeOffset.FromUnixTimeSeconds(now).UtcDateTime.ToString("o", CultureInfo.InvariantCulture); }

  /** @brief 暗号学的乱数でリーストークンを生成する。 */
  private static string NewToken() {
    var bytes = new byte[32];
    using (var rng = RandomNumberGenerator.Create()) { rng.GetBytes(bytes); }
    return Convert.ToBase64String(bytes);
  }

  /** @brief カードの内容を検証する。 */
  private static void Validate(CardContent content) {
    if (content == null || string.IsNullOrWhiteSpace(content.title) || content.title.Length > 200 || content.owner == null || content.owner.Length > 100 || content.body == null || content.body.Length > 100000 || !COLORS.Contains(content.color)) {
      throw new BoardError(400, "タイトル・オーナー・本文・背景色を確認してください。");
    }
  }

  /** @brief 指定IDのカードを取得する。 */
  private static Card GetCard(BoardState state, string id) {
    var card = state.cards.FirstOrDefault(c => c.id == id);
    if (card == null) throw new BoardError(404, "カードが見つかりません。");
    return card;
  }

  /** @brief 他の利用者の操作ロックがないことを確認する。 */
  private static void RequireUnlocked(BoardState state, string key) {
    if (string.IsNullOrEmpty(key)) throw new BoardError(400, "対象が指定されていません。");
    if (state.leases.ContainsKey(key)) throw new BoardError(409, "他の利用者が操作中です。");
  }

  /** @brief 指定されたリースの所有権を確認する。 */
  private static Lease RequireLease(BoardState state, string key, string token) {
    Lease lease;
    if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(token) || !state.leases.TryGetValue(key, out lease) || lease.token != token) {
      throw new BoardError(409, "操作権限の有効期限が切れました。入力を控えてから開き直してください。");
    }
    return lease;
  }

  /** @brief 新しいリースを取得する。 */
  private static string Acquire(BoardState state, string key, long now) {
    RequireUnlocked(state, key);
    string token = NewToken();
    state.leases.Add(key, new Lease { token = token, expires = now + LEASE_SECONDS });
    return token;
  }

  /** @brief 古いバージョンからの変更を拒否する。 */
  private static void RequireVersion(Card card, long version) {
    if (card.version != version) throw new BoardError(409, "カードが更新されました。最新の状態を確認してください。");
  }

  /** @brief カードの編集可能な内容を複製する。 */
  private static CardContent CopyContent(CardContent card) {
    return new CardContent { title = card.title, owner = card.owner, body = card.body, color = card.color };
  }

  /** @brief 内容を更新し直前の状態を保存する。 */
  private static void Save(Card card, CardContent content, long now) {
    Validate(content);
    if (card.title == content.title && card.owner == content.owner && card.body == content.body && card.color == content.color) return;
    card.previous = CopyContent(card);
    card.title = content.title;
    card.owner = content.owner;
    card.body = content.body;
    card.color = content.color;
    card.version++;
    card.updatedAt = Timestamp(now);
  }

  /** @brief 公開カード情報を生成する。内部履歴・リーストークンは含めない。 */
  private static object PublicCard(BoardState state, Card card) {
    return new { card.id, card.title, card.owner, card.body, card.color, card.position, card.archived, card.version, card.createdAt, card.updatedAt, hasPrevious = card.previous != null, editing = state.leases.ContainsKey(card.id) };
  }

  /** @brief 一覧の表示順を取得する。 */
  private static IEnumerable<Card> Ordered(BoardState state) {
    return state.cards.OrderBy(c => c.archived).ThenBy(c => c.position).ThenBy(c => c.id, StringComparer.Ordinal);
  }

  /** @brief 通常一覧の末尾位置を取得する。 */
  private static long NextPosition(BoardState state) {
    return state.cards.Where(c => !c.archived).Select(c => c.position).DefaultIfEmpty(0).Max() + 1;
  }

  /** @brief 最新の公開スナップショットを返す。 */
  public object Read() {
    return Transaction(false, (state, now) => new { cards = Ordered(state).Select(c => PublicCard(state, c)).ToArray(), state.orderVersion, sorting = state.leases.ContainsKey(ORDER_KEY), state.siteName, state.siteNameVersion });
  }

  /** @brief 変更要求を排他ロック内で実行する。
   * @param [in] request 変更要求
   * @return 操作結果
   */
  public object Apply(BoardRequest request) {
    if (request == null || string.IsNullOrEmpty(request.action)) throw new BoardError(400, "操作が指定されていません。");
    return Transaction<object>(true, (state, now) => {
      string key = request.id;
      switch (request.action) {
        case "renameSite":
          string name = request.siteName == null ? null : request.siteName.Trim();
          if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.Any(char.IsControl)) throw new BoardError(400, "サイト名は1〜100文字で入力してください。");
          if (state.siteNameVersion != request.version) throw new BoardError(409, "他の利用者がサイト名を変更しました。最新の名前を確認してください。");
          state.siteName = name;
          state.siteNameVersion++;
          break;
        case "sortAcquire":
          return new { token = Acquire(state, ORDER_KEY, now), version = state.orderVersion, order = Ordered(state).Where(c => !c.archived).Select(c => c.id).ToArray() };
        case "sortHeartbeat":
        case "heartbeat":
          key = request.action == "sortHeartbeat" ? ORDER_KEY : key;
          RequireLease(state, key, request.token).expires = now + LEASE_SECONDS;
          break;
        case "sortRelease":
        case "release":
          key = request.action == "sortRelease" ? ORDER_KEY : key;
          Lease existing;
          if (!string.IsNullOrEmpty(key) && state.leases.TryGetValue(key, out existing) && existing.token == request.token) { state.leases.Remove(key); }
          break;
        case "sortSave":
          RequireLease(state, ORDER_KEY, request.token);
          var active = state.cards.Where(c => !c.archived).ToDictionary(c => c.id);
          if (request.version != state.orderVersion || request.order == null || request.order.Length != active.Count || !new HashSet<string>(active.Keys).SetEquals(request.order)) throw new BoardError(409, "一覧が変更されました。並べ替えをやり直してください。");
          for (int i = 0; i < request.order.Length; i++) { active[request.order[i]].position = i + 1; }
          state.orderVersion++;
          state.leases.Remove(ORDER_KEY);
          break;
        case "create":
          RequireUnlocked(state, ORDER_KEY);
          Validate(request.content);
          var value = request.content;
          state.cards.Add(new Card { id = Guid.NewGuid().ToString("N"), title = value.title, owner = value.owner, body = value.body, color = value.color, position = NextPosition(state), version = 1, createdAt = Timestamp(now), updatedAt = Timestamp(now) });
          state.orderVersion++;
          break;
        case "acquire":
          var card = GetCard(state, key);
          if (card.archived) throw new BoardError(409, "アーカイブ済みのカードです。");
          return new { token = Acquire(state, key, now), card = PublicCard(state, card) };
        case "save":
          RequireLease(state, key, request.token);
          var original = GetCard(state, key);
          RequireVersion(original, request.version);
          if (original.archived) throw new BoardError(409, "アーカイブ済みのカードです。");
          Save(original, request.content, now);
          state.leases.Remove(key);
          break;
        case "archive":
        case "unarchive":
        case "delete":
          RequireUnlocked(state, ORDER_KEY);
          RequireUnlocked(state, key);
          var target = GetCard(state, key);
          RequireVersion(target, request.version);
          if (request.action == "delete") {
            if (!target.archived) throw new BoardError(409, "完全削除はアーカイブ済みカードのみ可能です。");
            state.cards.Remove(target);
            state.leases.Remove(key);
          } else {
            bool archive = request.action == "archive";
            if (target.archived == archive) throw new BoardError(409, "既に状態が変更されています。");
            if (!archive) { target.position = NextPosition(state); }
            target.archived = archive;
            target.version++;
            target.updatedAt = Timestamp(now);
          }
          state.orderVersion++;
          break;
        case "restore":
          RequireUnlocked(state, key);
          var latest = GetCard(state, key);
          RequireVersion(latest, request.version);
          if (latest.archived || latest.previous == null) throw new BoardError(409, "復元できる状態がありません。");
          Save(latest, latest.previous, now);
          break;
        default:
          throw new BoardError(400, "未対応の操作です。");
      }
      return new { ok = true };
    });
  }

  /** @brief CSVフィールドを引用し数式インジェクションを抑止する。 */
  public static string CsvField(string value) {
    value = value ?? "";
    string trimmed = value.TrimStart();
    if ((trimmed.Length > 0 && "=+-@＝＋－＠".Contains(trimmed[0])) || (value.Length > 0 && "\t\r\n".Contains(value[0]))) {
      value = "'" + value;
    }
    return "\"" + value.Replace("\"", "\"\"") + "\"";
  }

  /** @brief 全保存カードをUTF-8 BOMのCSVとして返す。 */
  public byte[] Export() {
    return Transaction(false, (state, now) => {
      var csv = new StringBuilder("ID,表示順,タイトル,オーナー,内容,背景色,状態,作成日時,更新日時\r\n");
      foreach (var card in Ordered(state)) {
        var fields = new[] { card.id, card.position.ToString(CultureInfo.InvariantCulture), card.title, card.owner, card.body, card.color, card.archived ? "アーカイブ" : "通常", card.createdAt, card.updatedAt };
        csv.Append(string.Join(",", fields.Select(CsvField))).Append("\r\n");
      }
      var utf8 = new UTF8Encoding(true);
      return utf8.GetPreamble().Concat(utf8.GetBytes(csv.ToString())).ToArray();
    });
  }
}

/** @brief 同一オリジン専用のHTTPハンドラー。 */
public sealed class InfoBoardHandler : IHttpHandler {
  public bool IsReusable { get { return false; } }

  /** @brief JSONレスポンスを返す。
   * @param [in] response HTTPレスポンス
   * @param [in] value JSON化する値
   */
  private static void Json(HttpResponse response, object value) {
    response.ContentType = "application/json";
    response.ContentEncoding = Encoding.UTF8;
    response.Write(BoardStore.Serializer().Serialize(value));
  }

  /** @brief HTTPメソッドと変更要求を検証して処理する。
   * @param [in] context ASP.NET HTTPコンテキスト
   */
  public void ProcessRequest(HttpContext context) {
    var request = context.Request;
    var response = context.Response;
    response.TrySkipIisCustomErrors = true;
    response.Cache.SetCacheability(HttpCacheability.NoCache);
    response.Cache.SetNoStore();
    try {
      var store = new BoardStore(context.Server.MapPath("~/App_Data"));
      if (request.HttpMethod == "GET") {
        if (request.QueryString["export"] == "1") {
          byte[] csv = store.Export();
          response.ContentType = "text/csv";
          response.ContentEncoding = Encoding.UTF8;
          response.AddHeader("Content-Disposition", "attachment; filename=InfoBoard_" + DateTime.UtcNow.AddHours(9).ToString("yyyyMMdd_HHmm") + ".csv");
          response.BinaryWrite(csv);
        } else {
          Json(response, store.Read());
        }
      } else if (request.HttpMethod == "POST") {
        string origin = request.Headers["Origin"];
        if (request.Headers["X-InfoBoard"] != "1" || !string.Equals((request.ContentType ?? "").Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase) || (origin != null && !string.Equals(origin, request.Url.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))) {
          throw new BoardError(403, "不正な変更要求です。");
        }
        if (request.ContentLength > 2097152) throw new BoardError(413, "入力が大きすぎます。");
        BoardRequest input;
        try {
          using (var reader = new StreamReader(request.InputStream, new UTF8Encoding(false, true))) { input = BoardStore.Serializer().Deserialize<BoardRequest>(reader.ReadToEnd()); }
        } catch (ArgumentException) {
          throw new BoardError(400, "入力形式が正しくありません。");
        } catch (InvalidOperationException) {
          throw new BoardError(400, "入力形式が正しくありません。");
        }
        Json(response, store.Apply(input));
      } else {
        throw new BoardError(405, "未対応のHTTPメソッドです。");
      }
    } catch (BoardError error) {
      response.Clear();
      response.StatusCode = error.Status;
      Json(response, new { message = error.Message });
    } catch (Exception error) {
      Trace.TraceError("InfoBoard: {0}", error);
      response.Clear();
      response.StatusCode = 500;
      Json(response, new { message = "サーバー処理に失敗しました。管理者へ連絡してください。" });
    }
  }
}
